using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ManagedCuda;
using ManagedCuda.BasicTypes;
using ManagedCuda.NPP;
using SkiaSharp;

namespace CardChooser.Services
{
    /// <summary>
    /// GPU-accelerated image pipeline using CUDA NPP (NVIDIA Performance Primitives).
    /// Runs resize, crop, Gaussian denoise, and Laplacian sharpen entirely on the RTX
    /// GPU's CUDA cores, replacing the equivalent SkiaSharp CPU operations.
    ///
    /// <b>Requirements:</b> NVIDIA CUDA Toolkit 12.3+ must be installed, which provides
    /// <c>nppig64_13.dll</c>, <c>nppif64_13.dll</c>, etc.  The NVIDIA display driver for
    /// RTX 3080 already bundles the CUDA driver (<c>nvcuda.dll</c>).
    /// If initialisation fails for any reason, <see cref="TryCreate"/> returns <c>null</c>
    /// and the caller silently falls back to the SkiaSharp CPU pipeline.
    /// </summary>
    internal sealed class CudaNppImagePipeline : IDisposable
    {
        // ── NPP DLL discovery ────────────────────────────────────────────────
        // DLLs required by our pipeline (CUDA Toolkit 12.2+, NPP API 13).
        // nppidei64_13 is listed even though we bypass its broken Copy import,
        // because ManagedCuda imports other functions (Set, etc.) from it.
        private static readonly string[] RequiredNppDlls =
        {
            "nppisu64_13",   // stream-context utilities  (NppStreamContext)
            "nppig64_13",    // geometry transforms       (Resize, nppiCopy_8u_C3R)
            "nppif64_13",    // image filtering           (FilterGaussBorder, FilterBorder)
            "nppidei64_13",  // data exchange / init      (ManagedCuda import target for Set etc.)
        };

        // Standard root of CUDA Toolkit installs on Windows.
        private const string CudaToolkitRoot =
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";

        // ── Multi-stream parallelism ─────────────────────────────────────────
        // Number of concurrent CUDA streams (= max images processed in parallel).
        // RTX 3080 has 68 SM units — 4 concurrent streams saturate the GPU while
        // keeping VRAM usage well within the 10 GB budget per pipeline slot:
        //   Resize buffer (37 MB) + crop buffer (14 MB) + work buffers (~15 MB) ≈ 66 MB/stream
        //   4 streams × 66 MB = ~264 MB — negligible on 10 GB VRAM.
        private const int GpuStreamCount = 4;

        private readonly PrimaryContext _ctx;

        // Pool of (stream, NPP stream context) pairs.  The pool always contains exactly
        // GpuStreamCount entries; the semaphore ensures we never over-subscribe it.
        private readonly ConcurrentQueue<(CudaStream Stream, NppStreamContext Ctx)> _streamPool = new();

        // Owned stream objects — disposed when the pipeline is disposed.
        private readonly List<CudaStream> _ownedStreams = new(GpuStreamCount);

        // Semaphore(N, N) allows up to GpuStreamCount concurrent ProcessOnGpu calls.
        private readonly SemaphoreSlim _gpuLock = new SemaphoreSlim(GpuStreamCount, GpuStreamCount);

        private bool _disposed;

        /// <summary>
        /// Number of images that can be processed simultaneously on the GPU.
        /// Pass this to <see cref="System.Threading.Tasks.ParallelOptions.MaxDegreeOfParallelism"/>
        /// when driving the pipeline from <c>Parallel.ForEachAsync</c>.
        /// </summary>
        public int Parallelism => GpuStreamCount;

        private CudaNppImagePipeline(PrimaryContext ctx)
        {
            _ctx = ctx;
        }

        // ════════════════════════════════════════════════════════════════════
        // Factory
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Attempts to initialise a CUDA NPP pipeline on GPU <paramref name="gpuIndex"/>.
        /// Returns <c>null</c> if CUDA is unavailable or any required NPP DLL is missing.
        /// All exceptions are silently swallowed so the caller can fall back to SkiaSharp.
        /// </summary>
        public static CudaNppImagePipeline? TryCreate(int gpuIndex = 0)
        {
            try
            {
                // ── Step 1: pre-check unmanaged NPP DLL presence ──────────────────────
                // We probe the DLLs BEFORE accessing any NPP managed type, because
                // accessing NppStreamContext / NPPImage_8uC3 triggers CLR type-init which
                // can throw DllNotFoundException before our try/catch is in scope.
                //
                // Strategy:
                //   a) Try PATH / system dirs first (works when CUDA\vX.Y\bin is in PATH).
                //   b) If any DLL is missing via PATH, scan well-known CUDA Toolkit install
                //      directories (the installer often skips adding them to PATH).
                //      Load each DLL from its full absolute path so the CLR can find it.
                if (!EnsureNppDllsLoaded(out string nppSource))
                {
                    // Print the diagnostic here so the caller sees WHY CUDA is unavailable.
                    // This appears slightly before the "GPU image processing: OFF" line in
                    // ExtractArtsAsync but is far more informative than a silent fallback.
                    Console.WriteLine($"    CUDA NPP : {nppSource}");
                    return null;
                }
                Console.WriteLine($"    CUDA NPP DLLs : loaded from {nppSource}");

                // ── Step 2: check that a CUDA device is present ───────────────────────
                // GetDeviceCount() calls cuInit() implicitly via nvcuda.dll (ships with
                // the NVIDIA display driver; always present on RTX systems).
                if (CudaContext.GetDeviceCount() == 0) return null;

                // ── Step 3: create CUDA primary context ───────────────────────────────
                // NVIDIA recommends PrimaryContext (not CudaContext) when using NPP.
                var ctx = new PrimaryContext(gpuIndex);
                ctx.SetCurrent();

                // ── Step 4: create the stream pool ────────────────────────────────────
                // Each stream allows one image to be in-flight on the GPU concurrently.
                // NppStreamContext is created per-stream so NPP operations are enqueued
                // on their respective streams and can run in parallel on the GPU.
                var pipeline = new CudaNppImagePipeline(ctx);
                for (int i = 0; i < GpuStreamCount; i++)
                {
                    var stream    = new CudaStream();
                    var streamCtx = new NppStreamContext(stream.Stream);
                    pipeline._streamPool.Enqueue((stream, streamCtx));
                    pipeline._ownedStreams.Add(stream);
                }
                return pipeline;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Returns the GPU device name, e.g. "NVIDIA GeForce RTX 3080".</summary>
        public static string GetDeviceName(int gpuIndex = 0)
        {
            try { return CudaContext.GetDeviceName(gpuIndex); }
            catch { return "unknown GPU"; }
        }

        // ════════════════════════════════════════════════════════════════════
        // NPP DLL discovery helpers
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Ensures all three required NPP DLLs are loaded into the process before any NPP
        /// managed type is first accessed.
        ///
        /// <para>Strategy (two passes):</para>
        /// <list type="number">
        ///   <item>Try <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/> for each DLL —
        ///         succeeds when the CUDA Toolkit <c>bin\</c> directory is in <c>PATH</c>.</item>
        ///   <item>If any DLL is not found via PATH, walk <see cref="CudaBinSearchPaths"/> and
        ///         attempt to load each DLL by its full absolute path — succeeds when the
        ///         Toolkit is installed but its directory was not added to <c>PATH</c>
        ///         (common with the silent/default installer on Windows).</item>
        /// </list>
        /// </summary>
        /// <param name="source">
        ///   On success: <c>"PATH"</c> or the absolute CUDA <c>bin\</c> directory that was used.
        ///   On failure: a human-readable explanation of which DLL was not found.
        /// </param>
        /// <returns><c>true</c> if every DLL was loaded; <c>false</c> otherwise.</returns>
        private static bool EnsureNppDllsLoaded(out string source)
        {
            // ── Pass 1: PATH / system-directory search ────────────────────────
            // Works when the CUDA Toolkit bin\ directory is in PATH.
            if (RequiredNppDlls.All(dll => NativeLibrary.TryLoad(dll, out _)))
            {
                source = "PATH";
                return true;
            }

            // ── Pass 2: dynamic scan of installed CUDA Toolkit versions ───────
            // The CUDA Toolkit installer often does NOT add its bin\ to PATH.
            // We scan every installed version under the standard install root,
            // ordered newest-first (highest version number wins).
            IEnumerable<string> binDirs = Enumerable.Empty<string>();
            if (Directory.Exists(CudaToolkitRoot))
            {
                binDirs = Directory
                    .GetDirectories(CudaToolkitRoot, "v*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(d => d)           // v12.6 > v12.3 > v12.0, etc.
                    .Select(d => Path.Combine(d, "bin"))
                    .Where(Directory.Exists);
            }

            foreach (string dir in binDirs)
            {
                bool allFound = RequiredNppDlls.All(dll =>
                {
                    string fullPath = Path.Combine(dir, dll + ".dll");
                    return File.Exists(fullPath) && NativeLibrary.TryLoad(fullPath, out _);
                });

                if (allFound)
                {
                    source = dir;
                    return true;
                }
            }

            // ── Not found — build an actionable diagnostic ────────────────────
            // Tell the user exactly which DLL is missing and where we looked.
            bool rootExists = Directory.Exists(CudaToolkitRoot);
            var missingDlls = RequiredNppDlls
                .Where(dll => !NativeLibrary.TryLoad(dll, out _))
                .Select(dll => dll + ".dll")
                .ToList();

            if (!rootExists)
            {
                source = $"CUDA Toolkit not found at '{CudaToolkitRoot}'. " +
                         $"Install from https://developer.nvidia.com/cuda-downloads " +
                         $"(requires Toolkit 12.2+ for NPP API v13 DLLs)";
            }
            else if (missingDlls.Count > 0)
            {
                string versions = string.Join(", ",
                    Directory.GetDirectories(CudaToolkitRoot, "v*")
                             .Select(Path.GetFileName));
                source = $"Found CUDA installs [{versions}] but none contain " +
                         $"{string.Join(", ", missingDlls)}. " +
                         $"Upgrade to CUDA Toolkit 12.2+ (NPP API 13).";
            }
            else
            {
                source = "DLLs found but could not be loaded — check DLL architecture or corruption.";
            }

            return false;
        }

        // ════════════════════════════════════════════════════════════════════
        // Public synchronous GPU API
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Executes the full image pipeline on the GPU and returns a new
        /// <see cref="SKBitmap"/> (caller must Dispose it).
        ///
        /// GPU stages:
        /// <list type="number">
        ///   <item><b>Resize</b> → (<paramref name="targetW"/> × <paramref name="targetH"/>)
        ///         using Cubic (Catmull-Rom equivalent) interpolation</item>
        ///   <item><b>Crop</b> ROI: origin (<paramref name="cropX"/>, <paramref name="cropY"/>),
        ///         size <paramref name="cropW"/> × <paramref name="cropH"/></item>
        ///   <item><b>Gaussian denoise</b> — 3×3 mask, Reflect border</item>
        ///   <item><b>Laplacian sharpen</b> — custom 3×3 float <paramref name="sharpenKernel3x3"/>,
        ///         Reflect border</item>
        /// </list>
        /// Thread-safe via internal SemaphoreSlim lock.
        /// </summary>
        public SKBitmap ProcessOnGpu(
            SKBitmap source,
            int targetW, int targetH,
            int cropX,   int cropY,
            int cropW,   int cropH,
            float[] sharpenKernel3x3)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The semaphore limits concurrent GPU users to GpuStreamCount.
            // Because the pool has exactly GpuStreamCount entries, TryDequeue
            // is guaranteed to succeed immediately after acquiring the semaphore.
            _gpuLock.Wait();
            try
            {
                _ctx.SetCurrent(); // Bind CUDA context to this CPU thread

                _streamPool.TryDequeue(out var entry);
                try
                {
                    return RunGpuPipeline(source,
                                          targetW, targetH,
                                          cropX, cropY, cropW, cropH,
                                          sharpenKernel3x3,
                                          entry.Stream, entry.Ctx);
                }
                finally
                {
                    _streamPool.Enqueue(entry); // return stream to pool
                }
            }
            finally
            {
                _gpuLock.Release();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Core GPU pipeline (runs while _gpuLock is held)
        // ════════════════════════════════════════════════════════════════════

        private SKBitmap RunGpuPipeline(
            SKBitmap source,
            int targetW, int targetH,
            int cropX,   int cropY,
            int cropW,   int cropH,
            float[] kernel,
            CudaStream stream, NppStreamContext streamCtx)
        {
            // ── 1. SKBitmap (BGRA 8888) → contiguous BGR byte[] ──────────────
            byte[] srcBgr = BgraToRgb(source);

            // ── 2. Upload BGR to GPU ──────────────────────────────────────────
            using var gpuSrc = new NPPImage_8uC3(source.Width, source.Height);
            gpuSrc.CopyToDevice(srcBgr);

            // ── 3. Resize on GPU (named stream — runs concurrently with other streams) ──
            using var gpuResized = new NPPImage_8uC3(targetW, targetH);
            gpuSrc.Resize(gpuResized, InterpolationMode.Cubic, streamCtx);

            // Flush stream before DtoH transfer.
            // cuMemcpyDtoH is synchronous only with the null stream; for named
            // streams we must explicitly wait for queued GPU work to finish.
            stream.Synchronize();

            // ── 4. Crop via CPU round-trip ────────────────────────────────────
            //    nppiCopy_8u_C3R is absent from both nppidei64_13 and nppig64_13
            //    on CUDA 12.x — avoid the function entirely.
            //    While this thread waits for the CPU crop, other streams continue
            //    their own GPU work on different SM units (true GPU parallelism).
            byte[] resizedBgr = new byte[targetW * targetH * 3];
            gpuResized.CopyToHost(resizedBgr);

            byte[] croppedBgr = ExtractRoiCpu(resizedBgr, targetW, cropX, cropY, cropW, cropH);

            using var gpuCropped = new NPPImage_8uC3(cropW, cropH);
            gpuCropped.CopyToDevice(croppedBgr);

            // ── 5. Gaussian denoise on GPU ────────────────────────────────────
            using var gpuBlurred = new NPPImage_8uC3(cropW, cropH);
            gpuCropped.FilterGaussBorder(
                gpuBlurred, MaskSize.Size_3_X_3,
                NppiBorderType.Replicate, streamCtx,
                new NppiRect(0, 0, cropW, cropH));

            // ── 6. Laplacian sharpen on GPU ───────────────────────────────────
            using var gpuSharpened = new NPPImage_8uC3(cropW, cropH);
            using var gpuKernel    = new CudaDeviceVariable<float>(kernel.Length);
            gpuKernel.CopyToDevice(kernel);
            gpuBlurred.FilterBorder(
                gpuSharpened, gpuKernel,
                new NppiSize(3, 3), new NppiPoint(1, 1),
                NppiBorderType.Replicate, streamCtx,
                new NppiRect(0, 0, cropW, cropH));

            // Flush before final DtoH transfer.
            stream.Synchronize();

            // ── 7. Download from GPU ──────────────────────────────────────────
            byte[] dstBgr = new byte[cropW * cropH * 3];
            gpuSharpened.CopyToHost(dstBgr);

            // ── 8. BGR → SKBitmap (BGRA 8888, Alpha = 255) ───────────────────
            return RgbToSKBitmap(dstBgr, cropW, cropH);
        }

        // ════════════════════════════════════════════════════════════════════
        // Pixel format & image helpers
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extracts a sub-rectangle from a packed BGR24 byte array.
        /// Used for the Resize→Crop step because <c>nppiCopy_8u_C3R</c> cannot be
        /// reliably located across CUDA Toolkit versions: ManagedCuda 2.12.0 imports
        /// it from <c>nppidei64_13</c>, but it may be absent there <em>and</em> from
        /// <c>nppig64_13</c> depending on the installed Toolkit revision.
        /// The CPU round-trip (37 MB ↓ + 14 MB ↑) takes ~3 ms on PCIe 4.0 — negligible
        /// beside the per-card AI SR time (~10 s).
        /// </summary>
        private static byte[] ExtractRoiCpu(
            byte[] src, int srcWidth, int x, int y, int w, int h)
        {
            byte[] dst        = new byte[w * h * 3];
            int srcRowBytes   = srcWidth * 3;
            int dstRowBytes   = w * 3;
            for (int row = 0; row < h; row++)
            {
                Array.Copy(src, (y + row) * srcRowBytes + x * 3,
                           dst, row * dstRowBytes,
                           dstRowBytes);
            }
            return dst;
        }

        /// <summary>BGRA 8888 (SkiaSharp) → BGR 24-bit for NPP C3.</summary>
        private static byte[] BgraToRgb(SKBitmap bmp)
        {
            byte[] pixels = bmp.Bytes; // B G R A per pixel
            byte[] bgr    = new byte[bmp.Width * bmp.Height * 3];
            for (int s = 0, d = 0; s < pixels.Length; s += 4, d += 3)
            {
                bgr[d]     = pixels[s];
                bgr[d + 1] = pixels[s + 1];
                bgr[d + 2] = pixels[s + 2];
                // pixels[s + 3] = Alpha — discarded
            }
            return bgr;
        }

        /// <summary>
        /// BGR 24-bit → BGRA 8888 SKBitmap (Alpha = 255, fully opaque).
        ///
        /// <b>Why not <c>InstallPixels</c> + <c>GCHandle</c>?</b>
        /// <c>InstallPixels</c> does NOT copy the pixels — it stores the raw pointer.
        /// Calling <c>handle.Free()</c> immediately after unpins the managed byte array;
        /// the GC is then free to move it, and any later access (e.g. <c>SKImage.FromBitmap</c>
        /// on a different thread) causes a 0xC0000005 access violation.
        ///
        /// The safe pattern: let <c>SKBitmap(info)</c> allocate its own native pixel buffer,
        /// then copy our BGRA bytes into it with <c>Marshal.Copy</c>.  The bitmap owns the
        /// memory and it never moves.
        /// </summary>
        private static SKBitmap RgbToSKBitmap(byte[] bgr, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var bmp  = new SKBitmap(info); // allocates its own native pixel buffer

            // Build the BGRA array and copy into the bitmap's own buffer.
            byte[] bgra = new byte[width * height * 4];
            for (int s = 0, d = 0; s < bgr.Length; s += 3, d += 4)
            {
                bgra[d]     = bgr[s];
                bgra[d + 1] = bgr[s + 1];
                bgra[d + 2] = bgr[s + 2];
                bgra[d + 3] = 255; // Alpha = fully opaque
            }

            // Copy into the bitmap's own native allocation — no GCHandle needed.
            IntPtr pixPtr = bmp.GetPixels();
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, pixPtr, bgra.Length);
            bmp.NotifyPixelsChanged();
            return bmp;
        }

        // ════════════════════════════════════════════════════════════════════
        // IDisposable
        // ════════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (!_disposed)
            {
                _gpuLock.Dispose();
                // Dispose all owned CUDA streams (releases GPU stream handles)
                foreach (CudaStream s in _ownedStreams)
                    s.Dispose();
                _ownedStreams.Clear();
                _ctx.Dispose();
                _disposed = true;
            }
        }
    }
}
