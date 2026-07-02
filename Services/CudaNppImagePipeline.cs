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
        private readonly PrimaryContext _ctx;
        private readonly NppStreamContext _streamCtx;

        // A single mutex ensures the GPU is used by exactly one CPU thread at a time,
        // which matters when Parallel.ForEachAsync calls ProcessOnGpu concurrently.
        private readonly SemaphoreSlim _gpuLock = new SemaphoreSlim(1, 1);

        private bool _disposed;

        private CudaNppImagePipeline(PrimaryContext ctx, NppStreamContext streamCtx)
        {
            _ctx       = ctx;
            _streamCtx = streamCtx;
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
                // We use NativeLibrary.TryLoad BEFORE accessing any NPP managed types
                // (NppStreamContext, NPPImage_8uC3, etc.).  Accessing those types would
                // immediately trigger a DllNotFoundException that can escape a try/catch
                // if the CLR fails to load the assembly-bound unmanaged DLLs at type-init
                // time.  TryLoad is silent: it returns false instead of throwing.
                //
                // DLLs required by our pipeline (CUDA Toolkit 12.x, installed separately
                // from the NVIDIA display driver):
                //   nppisu64_13  — NPP stream-context utilities  (NppStreamContext)
                //   nppig64_13   — NPP geometry transforms       (Resize, Copy)
                //   nppif64_13   — NPP image filtering           (FilterGaussBorder, FilterBorder)
                if (!NativeLibrary.TryLoad("nppisu64_13", out _)) return null;
                if (!NativeLibrary.TryLoad("nppig64_13",  out _)) return null;
                if (!NativeLibrary.TryLoad("nppif64_13",  out _)) return null;

                // ── Step 2: check that a CUDA device is present ───────────────────────
                // GetDeviceCount() calls cuInit() implicitly via nvcuda.dll (ships with
                // the NVIDIA display driver; always present on RTX systems).
                if (CudaContext.GetDeviceCount() == 0) return null;

                // ── Step 3: create CUDA primary context ───────────────────────────────
                // NVIDIA recommends PrimaryContext (not CudaContext) when using NPP.
                var ctx = new PrimaryContext(gpuIndex);
                ctx.SetCurrent();

                // ── Step 4: create NPP stream context ─────────────────────────────────
                // NullStream = synchronous execution; all NPP calls block until done.
                var streamCtx = new NppStreamContext(CUstream.NullStream);
                return new CudaNppImagePipeline(ctx, streamCtx);
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

            _gpuLock.Wait();
            try
            {
                _ctx.SetCurrent(); // Bind CUDA context to this CPU thread
                return RunGpuPipeline(source,
                                      targetW, targetH,
                                      cropX, cropY, cropW, cropH,
                                      sharpenKernel3x3);
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
            float[] kernel)
        {
            // ── 1. SKBitmap (BGRA 8888) → contiguous BGR byte[] ──────────────
            //    NPPImage_8uC3 is 3-channel BGR; we strip the Alpha byte here.
            byte[] srcBgr = BgraToRgb(source);

            // ── 2. Upload BGR to GPU (cuMemcpy2D handles pitch alignment) ─────
            using var gpuSrc = new NPPImage_8uC3(source.Width, source.Height);
            gpuSrc.CopyToDevice(srcBgr);

            // ── 3. Resize on GPU: Cubic ≈ Catmull-Rom ────────────────────────
            using var gpuResized = new NPPImage_8uC3(targetW, targetH);
            gpuSrc.Resize(gpuResized, InterpolationMode.Cubic, _streamCtx);

            // ── 4. Crop on GPU: Copy(dst, srcOffsetX, srcOffsetY) ────────────
            //    Copies cropW×cropH pixels starting at (cropX, cropY) in gpuResized.
            using var gpuCropped = new NPPImage_8uC3(cropW, cropH);
            gpuResized.Copy(gpuCropped, cropX, cropY);

            // ── 5. Gaussian denoise on GPU (3×3, Reflect boundary) ────────────
            using var gpuBlurred = new NPPImage_8uC3(cropW, cropH);
            gpuCropped.FilterGaussBorder(
                gpuBlurred, MaskSize.Size_3_X_3,
                NppiBorderType.Replicate, _streamCtx,
                new NppiRect(0, 0, cropW, cropH));

            // ── 6. Laplacian sharpen on GPU (custom 3×3 float kernel) ─────────
            //    Uploads kernel to GPU device memory, then runs FilterBorder.
            using var gpuSharpened = new NPPImage_8uC3(cropW, cropH);
            using var gpuKernel    = new CudaDeviceVariable<float>(kernel.Length);
            gpuKernel.CopyToDevice(kernel);
            gpuBlurred.FilterBorder(
                gpuSharpened, gpuKernel,
                new NppiSize(3, 3), new NppiPoint(1, 1),
                NppiBorderType.Replicate, _streamCtx,
                new NppiRect(0, 0, cropW, cropH));

            // ── 7. Download from GPU ─────────────────────────────────────────
            byte[] dstBgr = new byte[cropW * cropH * 3];
            gpuSharpened.CopyToHost(dstBgr);

            // ── 8. BGR → SKBitmap (BGRA 8888, Alpha = 255) ──────────────────
            return RgbToSKBitmap(dstBgr, cropW, cropH);
        }

        // ════════════════════════════════════════════════════════════════════
        // Pixel format helpers
        // ════════════════════════════════════════════════════════════════════

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

        /// <summary>BGR 24-bit → BGRA 8888 SKBitmap (Alpha = 255, fully opaque).</summary>
        private static SKBitmap RgbToSKBitmap(byte[] bgr, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var bmp  = new SKBitmap(info);

            byte[] bgra = new byte[width * height * 4];
            for (int s = 0, d = 0; s < bgr.Length; s += 3, d += 4)
            {
                bgra[d]     = bgr[s];
                bgra[d + 1] = bgr[s + 1];
                bgra[d + 2] = bgr[s + 2];
                bgra[d + 3] = 255; // Alpha = fully opaque
            }

            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
                return bmp;
            }
            finally
            {
                handle.Free();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // IDisposable
        // ════════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (!_disposed)
            {
                _gpuLock.Dispose();
                _ctx.Dispose();
                _disposed = true;
            }
        }
    }
}
