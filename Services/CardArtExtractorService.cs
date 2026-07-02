using System.Diagnostics;
using System.Threading.Channels;
using SkiaSharp;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Extracts and enhances card art from Scryfall card images using SkiaSharp (Google Skia engine).
    ///
    /// Pipeline per image:
    ///   1. [Optional] AI Super-Resolution — if <c>realesrgan-ncnn-vulkan.exe</c> is present,
    ///      upscale 4× on the GPU using Vulkan compute (CUDA cores on RTX 3080; explicit GPU
    ///      selection, auto tile-size to maximise 10 GB VRAM, 4 GPU threads).
    ///   2. Resize to 3000 × 4159 px using Catmull-Rom cubic resampling.
    ///   3. Crop to the standard M15 art frame (2527 × 1827 px @ 1200 DPI, verified).
    ///   4. Denoise — Gaussian blur (σ = 0.5) to suppress JPEG artefacts.
    ///   5. Sharpen — 3×3 Laplacian unsharp-mask.
    ///   6. Encode as JPEG (quality 95) with 1200 DPI JFIF header.
    ///
    /// Concurrency strategy:
    ///   • AI SR ON  → <b>GPU + CPU pipeline</b>: a bounded <see cref="Channel{T}"/> overlaps GPU
    ///     work (AI SR, one image at a time) with CPU work (resize→crop→denoise→sharpen→save),
    ///     so GPU and CPU are never both idle at the same time.
    ///   • AI SR OFF → <b>CPU parallel</b>: <see cref="Parallel.ForEachAsync"/> distributes all
    ///     images across <see cref="CpuParallelism"/> cores simultaneously.
    /// </summary>
    public class CardArtExtractorService : ICardArtExtractorService
    {
        // ── Target canvas (Proxyshop PSD template) ───────────────────────────
        private const int TargetCardWidth  = 3000;
        private const int TargetCardHeight = 4159;
        private const int TargetDpi        = 1200;

        // ── Art frame crop ────────────────────────────────────────────────────
        // Calibrated against Photoshop Image Size dialog (2527 × 1827 px @ 1200 DPI = 2.106" × 1.523").
        // Boundaries measured on a 672×936 Scryfall 'large' JPEG source.
        private const double ArtFrameLeft   = 0.077;   //  7.70 % →  231 px (left edge of art)
        private const double ArtFrameTop    = 0.1169;  // 11.69 % →  486 px (shifted 75 px up from 561)
        private const double ArtFrameWidth  = 0.8425;  // 84.25 % → 2527 px ← Photoshop verified
        private const double ArtFrameHeight = 0.4394;  // 43.94 % → 1827 px ← Photoshop verified (bottom @ 2313 px)

        // Expected dimensions — printed before enhancement as a sanity check.
        private const int ExpectedArtWidth  = 2527;
        private const int ExpectedArtHeight = 1827;

        // ── Enhancement ──────────────────────────────────────────────────────
        // Gaussian sigma for JPEG-artefact reduction (0.5 = gentle, preserves detail).
        private const float DenoiseSigma = 0.5f;

        // 3×3 Laplacian unsharp-mask kernel (sum = 1, brightness-preserving).
        private static readonly float[] SharpenKernel =
        {
            -0.25f, -0.5f, -0.25f,
            -0.5f,   4.0f, -0.5f,
            -0.25f, -0.5f, -0.25f
        };

        private const int JpegQuality = 95;

        // ── AI Super-Resolution (realesrgan-ncnn-vulkan) ─────────────────────
        // Set to empty string to disable AI SR entirely.
        private const string AiSrToolPath    = "realesrgan-ncnn-vulkan.exe";
        private const string AiSrModelsFolder = "realesrgan-models";

        // Model selection — change to any model in realesrgan-models/:
        //   realesr-animevideov3    — fast, anime/cel-shading (1.2 MB) — over-smooths painted art
        //   realesrgan-x4plus-anime — higher-quality illustration (8.9 MB)
        //   realesrgan-x4plus       — photo-realistic / digital painting (33 MB) ← best for MTG art
        private const string AiSrModelName = "realesrgan-x4plus";
        private const int    AiSrScale     = 4;

        // ── GPU optimisation flags ────────────────────────────────────────────
        // -g  GPU index  →  0 = first GPU (RTX 3080). Use -1 for auto-detect.
        // -t  tile size  →  0 = auto, uses as much VRAM as available.
        //                   RTX 3080 has 10 GB VRAM → very large tiles → fewer GPU passes.
        // -j  threads    →  load:proc:save — 4 GPU compute threads for RTX 3080.
        private const int    AiSrGpuId        = 0;
        private const int    AiSrTileSize     = 0;       // 0 = auto
        private const string AiSrThreadConfig = "1:4:1"; // 4 GPU compute threads

        // ── Concurrency ───────────────────────────────────────────────────────
        // Channel capacity = 2: GPU can be 2 images ahead of the CPU consumer,
        // ensuring the GPU is never stalled waiting for the CPU to catch up.
        private const int SrChannelCapacity = 2;

        // CPU-only parallelism: half the logical cores avoids thermal/memory pressure.
        private static readonly int CpuParallelism = Math.Max(1, Environment.ProcessorCount / 2);

        // ── JFIF DPI header offsets ───────────────────────────────────────────
        private const int JfifUnitsOffset    = 13;
        private const int JfifXDensityOffset = 14;
        private const int JfifYDensityOffset = 16;
        private const int JfifMinLength      = 18;

        private const string JpegExtension = ".jpg";

        // ── Pipeline token (carries GPU SR result to CPU consumer) ───────────
        private sealed record SrResult(
            string SourcePath,
            string InputForProcessing, // temp upscaled path, or original if SR failed
            string CardFileName,
            bool   WasUpscaled,
            string StatusPrefix);

        // ════════════════════════════════════════════════════════════════════
        // Public API
        // ════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task ExtractArtsAsync(string sourceImagesFolder, string artOutputFolder)
        {
            if (!Directory.Exists(sourceImagesFolder))
            {
                Console.WriteLine($"Art extraction skipped: source folder '{sourceImagesFolder}' does not exist.");
                return;
            }

            EnsureDirectoryExists(artOutputFolder);
            IReadOnlyList<string> imageFiles = DiscoverJpegs(sourceImagesFolder);

            if (imageFiles.Count == 0)
            {
                Console.WriteLine("Art extraction skipped: no JPEG images found in the Scryfall images folder.");
                return;
            }

            bool aiSrAvailable = IsAiSrToolAvailable();

            // Initialise the CUDA NPP image pipeline (requires CUDA Toolkit 12.3+).
            // Returns null silently if CUDA is unavailable; all methods fall back to SkiaSharp.
            using CudaNppImagePipeline? cuda = CudaNppImagePipeline.TryCreate(AiSrGpuId);
            string gpuImgProc = cuda != null
                ? $"ON  (CUDA NPP — {CudaNppImagePipeline.GetDeviceName(AiSrGpuId)})"
                : "OFF (CUDA Toolkit 12.3+ not found — using SkiaSharp CPU)";

            Console.WriteLine($"Extracting card art from {imageFiles.Count} image(s)...");
            Console.WriteLine($"  AI super-resolution : {(aiSrAvailable
                ? $"ON  — GPU {AiSrGpuId}, model={AiSrModelName}, tile=auto (10 GB VRAM), threads={AiSrThreadConfig}"
                : "OFF (realesrgan-ncnn-vulkan.exe not found)")}");
            Console.WriteLine($"  GPU image processing: {gpuImgProc}");
            Console.WriteLine($"  Denoise + sharpen   : ON (always)");
            Console.WriteLine($"  Concurrency         : {(aiSrAvailable
                ? $"GPU+CPU pipeline (channel depth {SrChannelCapacity})"
                : $"CPU parallel ({CpuParallelism} cores, Parallel.ForEachAsync)")}");
            Console.WriteLine($"  Output folder       : {artOutputFolder}");
            Console.WriteLine();

            SKRectI cropRect = ComputeArtCropRectangle();
            int successCount, failureCount;

            if (aiSrAvailable)
            {
                (successCount, failureCount) =
                    await RunPipelineWithSrAsync(imageFiles, artOutputFolder, cropRect, cuda);
            }
            else
            {
                (successCount, failureCount) =
                    await RunParallelCpuAsync(imageFiles, artOutputFolder, cropRect, cuda);
            }

            Console.WriteLine();
            Console.WriteLine($"Art extraction complete: {successCount} succeeded, {failureCount} failed.");
            Console.WriteLine();
        }

        // ════════════════════════════════════════════════════════════════════
        // Concurrency strategies
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// GPU + CPU pipeline using a bounded <see cref="Channel{T}"/>.
        /// <para>
        /// A producer <see cref="Task"/> runs AI SR sequentially on GPU 0 (one image at a time)
        /// and writes <see cref="SrResult"/> tokens to the channel.  The consumer (this method's
        /// <c>await foreach</c>) reads tokens and runs the CPU pipeline concurrently, so GPU and
        /// CPU are busy at the same time:
        /// </para>
        /// <code>
        ///   GPU:  [SR card-1]──[SR card-2]──[SR card-3]──[SR card-4]──
        ///   CPU:              [CPU card-1]──[CPU card-2]──[CPU card-3]──[CPU card-4]
        /// </code>
        /// </summary>
        private static async Task<(int successes, int failures)> RunPipelineWithSrAsync(
            IReadOnlyList<string> imageFiles,
            string artOutputFolder,
            SKRectI cropRect,
            CudaNppImagePipeline? cuda)
        {
            var channel = Channel.CreateBounded<SrResult>(new BoundedChannelOptions(SrChannelCapacity)
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

            // ── GPU producer — runs AI SR, one image at a time ────────────────
            var producer = Task.Run(async () =>
            {
                foreach (string imageFile in imageFiles)
                {
                    string cardFileName = Path.GetFileName(imageFile);
                    string tempPath     = Path.Combine(Path.GetTempPath(),
                                             $"sr_{Guid.NewGuid():N}_{cardFileName}");

                    bool   ok     = await RunSuperResolutionAsync(imageFile, tempPath);
                    string label  = ok ? "[SR ✓]" : "[SR FAILED — Catmull-Rom fallback]";

                    await channel.Writer.WriteAsync(new SrResult(
                        SourcePath:         imageFile,
                        InputForProcessing: ok ? tempPath : imageFile,
                        CardFileName:       cardFileName,
                        WasUpscaled:        ok,
                        StatusPrefix:       $"  '{cardFileName}' {label}"));
                }
                channel.Writer.Complete();
            });

            // ── CPU consumer — processes each SR result as it arrives ─────────
            int successes = 0, failures = 0;

            await foreach (SrResult result in channel.Reader.ReadAllAsync())
            {
                bool ok = await TryCpuProcessAsync(result, artOutputFolder, cropRect, cuda);
                if (ok) successes++;
                else    failures++;

            if (result.WasUpscaled)
                    TryDeleteTempFile(result.InputForProcessing);
            }

            await producer; // re-throw any unhandled producer exception
            return (successes, failures);
        }

        /// <summary>
        /// CPU-only path (no AI SR): all images are processed in parallel across
        /// <see cref="CpuParallelism"/> cores using <see cref="Parallel.ForEachAsync"/>.
        /// </summary>
        private static async Task<(int successes, int failures)> RunParallelCpuAsync(
            IReadOnlyList<string> imageFiles,
            string artOutputFolder,
            SKRectI cropRect,
            CudaNppImagePipeline? cuda)
        {
            int successes = 0, failures = 0;

            await Parallel.ForEachAsync(
                imageFiles,
                new ParallelOptions { MaxDegreeOfParallelism = CpuParallelism },
                async (imageFile, _) =>
                {
                    string cardFileName = Path.GetFileName(imageFile);
                    var result = new SrResult(imageFile, imageFile, cardFileName,
                                              WasUpscaled: false,
                                              StatusPrefix: $"  '{cardFileName}'");

                    bool ok = await TryCpuProcessAsync(result, artOutputFolder, cropRect, cuda);
                    if (ok) Interlocked.Increment(ref successes);
                    else    Interlocked.Increment(ref failures);
                });

            return (successes, failures);
        }

        // ════════════════════════════════════════════════════════════════════
        // Per-image CPU pipeline
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Runs the synchronous image pipeline on the thread pool and writes the output file.
        /// </summary>
        private static async Task<bool> TryCpuProcessAsync(
            SrResult result,
            string artOutputFolder,
            SKRectI cropRect,
            CudaNppImagePipeline? cuda)
        {
            try
            {
                // Offload image-processing work to the thread pool.
                // When cuda != null the GPU stages execute inside Task.Run (holding the GPU lock);
                // when null, the SkiaSharp CPU stages execute there instead.
                byte[] jpegBytes = await Task.Run(() =>
                    ProcessCardImageToBytes(result.InputForProcessing, cropRect, cuda));

                SetJfifDpi(jpegBytes, TargetDpi);

                string outputPath = Path.Combine(artOutputFolder, result.CardFileName);
                await File.WriteAllBytesAsync(outputPath, jpegBytes);

                Console.WriteLine($"{result.StatusPrefix} ... OK");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{result.StatusPrefix} ... FAILED ({ex.Message})");
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Image processing (synchronous, runs on thread pool via Task.Run)
        // ════════════════════════════════════════════════════════════════════

        private static byte[] ProcessCardImageToBytes(
            string sourceImagePath, SKRectI cropRect, CudaNppImagePipeline? cuda)
        {
            // 1. Load from disk (common to both paths)
            using SKBitmap source = SKBitmap.Decode(sourceImagePath)
                ?? throw new InvalidOperationException(
                    $"SkiaSharp could not decode '{Path.GetFileName(sourceImagePath)}'.");

            if (cuda != null)
            {
                // ── GPU path (CUDA NPP): resize + crop + denoise + sharpen on CUDA cores ─────
                // ProcessOnGpu acquires an internal lock, so concurrent Task.Run calls are safe.
                using SKBitmap artBitmap = cuda.ProcessOnGpu(
                    source,
                    TargetCardWidth, TargetCardHeight,
                    cropRect.Left, cropRect.Top, cropRect.Width, cropRect.Height,
                    SharpenKernel);

                LogCropSize(artBitmap);
                return EncodeToJpeg(artBitmap);
            }
            else
            {
                // ── CPU path (SkiaSharp): original pipeline ────────────────────────────────
                // 2. Resize to 3000 × 4159 using Catmull-Rom cubic resampling
                var targetInfo = new SKImageInfo(TargetCardWidth, TargetCardHeight,
                                                 source.ColorType, source.AlphaType);
                using SKBitmap resized = source.Resize(targetInfo,
                                             new SKSamplingOptions(SKCubicResampler.CatmullRom))
                    ?? throw new InvalidOperationException("Resize returned null.");

                // 3. Crop to M15 art frame
                using SKBitmap artBitmap = ExtractCrop(resized, cropRect);
                LogCropSize(artBitmap);

                // 4. Denoise — Gaussian blur (σ = 0.5)
                using SKBitmap denoised = ApplyBlur(artBitmap, DenoiseSigma);

                // 5. Sharpen — 3×3 Laplacian unsharp-mask
                using SKBitmap sharpened = ApplySharpen(denoised);

                // 6. JPEG encode
                return EncodeToJpeg(sharpened);
            }
        }

        /// <summary>Logs the crop dimensions and emits a warning if they deviate from expected.</summary>
        private static void LogCropSize(SKBitmap artBitmap)
        {
            // Expected: 2527 × 1827 px @ 1200 DPI (≈ 2.106" × 1.523", verified in Photoshop).
            bool   cropOk   = artBitmap.Width == ExpectedArtWidth && artBitmap.Height == ExpectedArtHeight;
            string sizeLabel = cropOk
                ? $"{artBitmap.Width} × {artBitmap.Height} px ✓"
                : $"{artBitmap.Width} × {artBitmap.Height} px  ⚠ expected {ExpectedArtWidth} × {ExpectedArtHeight}";
            Console.WriteLine($"    crop size : {sizeLabel}");
        }

        /// <summary>Encodes a bitmap as JPEG at <see cref="JpegQuality"/> and returns the byte array.</summary>
        private static byte[] EncodeToJpeg(SKBitmap bitmap)
        {
            using SKImage artImage = SKImage.FromBitmap(bitmap);
            using SKData  encoded  = artImage.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                ?? throw new InvalidOperationException("JPEG encoding returned null.");
            return encoded.ToArray();
        }

        // ════════════════════════════════════════════════════════════════════
        // Image helpers
        // ════════════════════════════════════════════════════════════════════

        private static SKBitmap ExtractCrop(SKBitmap source, SKRectI cropRect)
        {
            var artBitmap = new SKBitmap(cropRect.Width, cropRect.Height);
            using var canvas = new SKCanvas(artBitmap);
            var src = new SKRect(cropRect.Left, cropRect.Top, cropRect.Right, cropRect.Bottom);
            var dst = new SKRect(0, 0, cropRect.Width, cropRect.Height);
            canvas.DrawBitmap(source, src, dst,
                new SKSamplingOptions(SKCubicResampler.CatmullRom), paint: null);
            return artBitmap;
        }

        private static SKBitmap ApplyBlur(SKBitmap source, float sigma)
        {
            var result = new SKBitmap(source.Width, source.Height);
            using var filter = SKImageFilter.CreateBlur(sigma, sigma);
            using var paint  = new SKPaint { ImageFilter = filter };
            using var canvas = new SKCanvas(result);
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(), paint);
            return result;
        }

        private static SKBitmap ApplySharpen(SKBitmap source)
        {
            var result = new SKBitmap(source.Width, source.Height);
            using var filter = SKImageFilter.CreateMatrixConvolution(
                kernelSize:    new SKSizeI(3, 3),
                kernel:        SharpenKernel,
                gain:          1f,
                bias:          0f,
                kernelOffset:  new SKPointI(1, 1),
                tileMode:      SKShaderTileMode.Clamp,
                convolveAlpha: false);
            using var paint  = new SKPaint { ImageFilter = filter };
            using var canvas = new SKCanvas(result);
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(), paint);
            return result;
        }

        // ════════════════════════════════════════════════════════════════════
        // AI Super-Resolution (realesrgan-ncnn-vulkan CLI)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves the tool path relative to <see cref="AppContext.BaseDirectory"/> (next to the
        /// compiled exe), with a CWD fallback.  Returns empty string if not found.
        /// </summary>
        private static string GetAiSrToolFullPath()
        {
            if (string.IsNullOrWhiteSpace(AiSrToolPath)) return string.Empty;

            if (Path.IsPathRooted(AiSrToolPath))
                return File.Exists(AiSrToolPath) ? AiSrToolPath : string.Empty;

            string appBasePath = Path.Combine(AppContext.BaseDirectory, AiSrToolPath);
            if (File.Exists(appBasePath)) return appBasePath;

            if (File.Exists(AiSrToolPath)) return Path.GetFullPath(AiSrToolPath);

            return string.Empty;
        }

        private static bool IsAiSrToolAvailable() =>
            !string.IsNullOrWhiteSpace(GetAiSrToolFullPath());

        /// <summary>
        /// Launches realesrgan-ncnn-vulkan with GPU-optimised flags and waits for it to finish.
        /// <list type="bullet">
        ///   <item><c>-g 0</c>  — use GPU 0 (RTX 3080) explicitly</item>
        ///   <item><c>-t 0</c>  — auto tile-size (maximises 10 GB VRAM; fewer GPU passes)</item>
        ///   <item><c>-j 1:4:1</c> — 4 GPU compute threads for RTX 3080 throughput</item>
        /// </list>
        /// </summary>
        private static async Task<bool> RunSuperResolutionAsync(string inputPath, string outputPath)
        {
            string toolPath = GetAiSrToolFullPath();
            if (string.IsNullOrWhiteSpace(toolPath)) return false;

            string toolDir       = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory;
            string modelsAbsPath = Path.Combine(toolDir, AiSrModelsFolder);

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName  = toolPath,
                        Arguments = $"-i \"{inputPath}\" -o \"{outputPath}\" "    +
                                    $"-n {AiSrModelName} -s {AiSrScale} -f jpg " +
                                    $"-m \"{modelsAbsPath}\" "                    +
                                    $"-g {AiSrGpuId} "                            +
                                    $"-t {AiSrTileSize} "                         +
                                    $"-j {AiSrThreadConfig}",
                        UseShellExecute        = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        CreateNoWindow         = true
                    }
                };

                process.Start();
                await process.WaitForExitAsync();
                return process.ExitCode == 0 && File.Exists(outputPath);
            }
            catch
            {
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // JFIF DPI header patching
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Patches the JFIF APP0 segment of a JPEG byte array to embed the specified DPI.
        /// </summary>
        private static void SetJfifDpi(byte[] jpegBytes, int dpi)
        {
            if (jpegBytes.Length < JfifMinLength) return;
            if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8) return;
            if (jpegBytes[2] != 0xFF || jpegBytes[3] != 0xE0) return;
            if (jpegBytes[6] != 0x4A || jpegBytes[7] != 0x46 ||
                jpegBytes[8] != 0x49 || jpegBytes[9] != 0x46) return;

            byte hi = (byte)(dpi >> 8);
            byte lo = (byte)(dpi & 0xFF);
            jpegBytes[JfifUnitsOffset]        = 0x01;
            jpegBytes[JfifXDensityOffset]     = hi;
            jpegBytes[JfifXDensityOffset + 1] = lo;
            jpegBytes[JfifYDensityOffset]     = hi;
            jpegBytes[JfifYDensityOffset + 1] = lo;
        }

        // ════════════════════════════════════════════════════════════════════
        // Geometry helpers
        // ════════════════════════════════════════════════════════════════════

        private static SKRectI ComputeArtCropRectangle()
        {
            int x      = (int)(TargetCardWidth  * ArtFrameLeft);    //  231 px
            int y      = (int)(TargetCardHeight * ArtFrameTop);     //  486 px  (75 px above previous 561)
            int width  = (int)(TargetCardWidth  * ArtFrameWidth);   // 2527 px
            int height = (int)(TargetCardHeight * ArtFrameHeight);  // 1827 px  (bottom @ 2313 px)
            return new SKRectI(x, y, x + width, y + height);
        }

        // ════════════════════════════════════════════════════════════════════
        // File system helpers
        // ════════════════════════════════════════════════════════════════════

        private static IReadOnlyList<string> DiscoverJpegs(string folder) =>
            Directory
                .EnumerateFiles(folder, $"*{JpegExtension}", SearchOption.TopDirectoryOnly)
                .ToList()
                .AsReadOnly();

        private static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        /// <summary>
        /// Best-effort deletion of a temp file with up to 3 retries spaced 200 ms apart.
        /// Windows Defender / Search Indexer can briefly lock a newly-written file in %TEMP%
        /// even after the writing process has exited.  Swallowing the exception is intentional:
        /// by the time this is called the output art file has already been written successfully,
        /// so a stale temp file is cosmetic, not fatal.
        /// </summary>
        private static void TryDeleteTempFile(string path)
        {
            const int maxAttempts  = 3;
            const int retryDelayMs = 200;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return; // success — exit immediately
                }
                catch (IOException) when (attempt < maxAttempts - 1)
                {
                    // Transient lock (antivirus / indexer) — wait briefly then retry
                    Thread.Sleep(retryDelayMs);
                }
                catch
                {
                    // Give up silently on any other error (permissions, path too long, etc.)
                    return;
                }
            }
        }
    }
}
