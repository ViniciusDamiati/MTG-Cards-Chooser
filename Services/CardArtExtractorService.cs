using System.Diagnostics;
using SkiaSharp;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Extracts and enhances card art from Scryfall card images using SkiaSharp (Google Skia engine).
    ///
    /// Pipeline per image:
    ///   1. [Optional] AI Super-Resolution — if <c>realesrgan-ncnn-vulkan.exe</c> is present at
    ///      <see cref="AiSrToolPath"/>, upscale the original Scryfall JPEG 4× before any other
    ///      processing (672×936 → 2688×3744). Produces far sharper detail than bicubic upscaling.
    ///   2. Resize to 3000 × 4159 px using Catmull-Rom cubic resampling, matching the Proxyshop
    ///      PSD template canvas (equivalent to Photoshop "Preserve Details" enlargement).
    ///   3. Crop to the standard M15 art frame, discarding border, name bar, text box, etc.
    ///   4. Denoise — gentle Gaussian blur (σ = 0.5) to suppress JPEG compression artefacts
    ///      without softening edges.
    ///   5. Sharpen — 3×3 Laplacian unsharp-mask kernel to recover fine edge detail.
    ///   6. Encode as JPEG (quality 95) and patch the JFIF header to embed 1200 DPI.
    ///   7. Save to <c>scryfall_images/cards_arts/</c>.
    ///
    /// All constants (crop percentages, blur sigma, kernel, quality) are defined at the top of
    /// this class and can be tuned without touching any other file.
    /// </summary>
    public class CardArtExtractorService : ICardArtExtractorService
    {
        // ── Target canvas (matches Proxyshop PSD template dimensions) ──────────
        private const int TargetCardWidth = 3000;
        private const int TargetCardHeight = 4159;
        private const int TargetDpi = 1200;

        // ── Art frame position (fractions of the full resized card image) ─────
        // Calibrated for standard M15 cards from Scryfall 'large' JPEGs (672 × 936 px source).
        //
        // M15 art-frame boundaries (measured on 672×936 Scryfall source, then scaled):
        //   name bar bottom ≈ 126 px → 13.5 % of 936  →  ArtFrameTop must be ≥ 0.135
        //   art top         ≈ 126 px → 13.5 % of 936  →  ArtFrameTop   = 0.135
        //   art bottom      ≈ 496 px → 53.0 % of 936  →  bottom edge @ 0.135 + 0.395 = 0.530
        //   art left        ≈  52 px →  7.7 % of 672  →  ArtFrameLeft  = 0.077
        //   art right       ≈ 620 px → 92.3 % of 672  →  width         = 0.846
        //
        // These values crop exactly to the printed art frame — no name bar, no type bar.
        // Adjust only if a different Scryfall image style produces a misaligned crop.
        private const double ArtFrameLeft   = 0.077;   //  7.7 % →  231 px left edge
        private const double ArtFrameTop    = 0.135;   // 13.5 % →  562 px top edge (below name bar)
        private const double ArtFrameWidth  = 0.846;   // 84.6 % → 2538 px wide
        private const double ArtFrameHeight = 0.395;   // 39.5 % → 1643 px tall  (bottom @ 53.0 %)

        // ── Enhancement — denoise ────────────────────────────────────────────
        // Gaussian sigma used for JPEG-artefact reduction before sharpening.
        // Lower = less blurring (preserve more detail); higher = more noise removal.
        private const float DenoiseSigma = 0.5f;

        // ── Enhancement — sharpening kernel ─────────────────────────────────
        // 3×3 eight-connected Laplacian with sum = 1 (brightness-preserving).
        // Increase magnitude of negative weights for stronger sharpening.
        private static readonly float[] SharpenKernel =
        {
            -0.25f, -0.5f, -0.25f,
            -0.5f,   4.0f, -0.5f,
            -0.25f, -0.5f, -0.25f
        };
        // sum = 4 + 4*(-0.5) + 4*(-0.25) = 4 - 2 - 1 = 1  ✓

        // ── JPEG output quality ──────────────────────────────────────────────
        private const int JpegQuality = 95;

        // ── AI Super-Resolution (optional) ───────────────────────────────────
        // Path to the realesrgan-ncnn-vulkan executable.
        // The exe, vcomp140.dll, vcomp140d.dll, and the models folder are expected
        // in the project root (next to CardChooser.exe when running via dotnet run).
        // Set to an empty string to disable AI super-resolution entirely.
        private const string AiSrToolPath = "realesrgan-ncnn-vulkan.exe";

        // Folder containing the .bin/.param model weight files (relative to CWD).
        // Maps to d:\Projects\MTG\MTG-Cards-Chooser\realesrgan-models\
        private const string AiSrModelsFolder = "realesrgan-models";

        // Model name (without scale suffix or extension).
        // Available models in realesrgan-models/:
        //   realesr-animevideov3       — fast, painted/anime art  (1.2 MB, default)
        //   realesrgan-x4plus-anime    — higher quality illustration (8.9 MB)
        //   realesrgan-x4plus          — photo-realistic images  (33 MB)
        private const string AiSrModelName = "realesr-animevideov3";
        private const int AiSrScale = 4;

        // ── JFIF DPI header offsets ──────────────────────────────────────────
        private const int JfifUnitsOffset = 13;
        private const int JfifXDensityOffset = 14;
        private const int JfifYDensityOffset = 16;
        private const int JfifMinLength = 18;

        // ── File handling ────────────────────────────────────────────────────
        private const string JpegExtension = ".jpg";

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
            Console.WriteLine($"Extracting card art from {imageFiles.Count} image(s)...");
            Console.WriteLine($"  AI super-resolution : {(aiSrAvailable ? $"ON  ({AiSrToolPath})" : "OFF (realesrgan-ncnn-vulkan.exe not found)")}");
            Console.WriteLine($"  Denoise + sharpen   : ON (always)");
            Console.WriteLine($"  Output folder       : {artOutputFolder}");
            Console.WriteLine();

            SKRectI cropRect = ComputeArtCropRectangle();

            int successCount = 0;
            int failureCount = 0;

            foreach (string imageFile in imageFiles)
            {
                bool succeeded = await TryExtractArtAsync(imageFile, artOutputFolder, cropRect, aiSrAvailable);
                if (succeeded) successCount++;
                else           failureCount++;
            }

            Console.WriteLine();
            Console.WriteLine($"Art extraction complete: {successCount} succeeded, {failureCount} failed.");
            Console.WriteLine();
        }

        // ────────────────────────────────────────────────────────────────────
        // Per-image pipeline
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Processes one card image through the full pipeline and writes the result.
        /// If the AI SR tool is available, it is applied to the original small JPEG
        /// before the main resize, giving far better upscaling quality.
        /// </summary>
        private static async Task<bool> TryExtractArtAsync(
            string sourceImagePath,
            string artOutputFolder,
            SKRectI cropRect,
            bool aiSrAvailable)
        {
            string cardFileName = Path.GetFileName(sourceImagePath);
            string? tempSrPath = null;

            try
            {
                string inputPath = sourceImagePath;

                // ── Step 1 (optional): AI super-resolution ───────────────────
                if (aiSrAvailable)
                {
                    tempSrPath = Path.Combine(Path.GetTempPath(), $"sr_{Guid.NewGuid():N}_{cardFileName}");
                    bool srOk = await RunSuperResolutionAsync(sourceImagePath, tempSrPath);
                    if (srOk)
                    {
                        inputPath = tempSrPath;
                        Console.Write($"  '{cardFileName}' [SR OK]");
                    }
                    else
                    {
                        Console.Write($"  '{cardFileName}' [SR FAILED — using Catmull-Rom]");
                    }
                }

                // ── Steps 2-5: Resize → Crop → Denoise → Sharpen ────────────
                byte[] jpegBytes = await Task.Run(() => ProcessCardImageToBytes(inputPath, cropRect));

                // ── Step 6: Patch JFIF header to embed 1200 DPI ──────────────
                SetJfifDpi(jpegBytes, TargetDpi);

                // ── Step 7: Write art file ───────────────────────────────────
                string outputPath = Path.Combine(artOutputFolder, cardFileName);
                await File.WriteAllBytesAsync(outputPath, jpegBytes);

                if (!aiSrAvailable) Console.Write($"  '{cardFileName}'");
                Console.WriteLine(" ... OK");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  '{cardFileName}' ... FAILED ({ex.Message})");
                return false;
            }
            finally
            {
                if (tempSrPath is not null && File.Exists(tempSrPath))
                    File.Delete(tempSrPath);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Image processing (synchronous, runs on thread pool via Task.Run)
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Core synchronous image pipeline: load → resize → crop → denoise → sharpen → encode.
        /// </summary>
        private static byte[] ProcessCardImageToBytes(string sourceImagePath, SKRectI cropRect)
        {
            // 1. Load
            using SKBitmap source = SKBitmap.Decode(sourceImagePath)
                ?? throw new InvalidOperationException(
                    $"SkiaSharp could not decode '{Path.GetFileName(sourceImagePath)}'.");

            // 2. Resize to 3000 × 4159 using Catmull-Rom (high-quality for both up- and down-scale)
            var targetInfo = new SKImageInfo(TargetCardWidth, TargetCardHeight, source.ColorType, source.AlphaType);
            using SKBitmap resized = source.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.CatmullRom))
                ?? throw new InvalidOperationException("Resize returned null.");

            // 3. Crop to M15 art frame
            using SKBitmap artBitmap = ExtractCrop(resized, cropRect);

            // 4. Denoise — gentle Gaussian blur to suppress JPEG compression artefacts
            using SKBitmap denoised = ApplyBlur(artBitmap, DenoiseSigma);

            // 5. Sharpen — 3×3 Laplacian unsharp-mask kernel
            using SKBitmap sharpened = ApplySharpen(denoised);

            // 6. JPEG encode
            using SKImage artImage = SKImage.FromBitmap(sharpened);
            using SKData encoded = artImage.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                ?? throw new InvalidOperationException("JPEG encoding returned null.");

            return encoded.ToArray();
        }

        // ────────────────────────────────────────────────────────────────────
        // Image helpers
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Crops a region from <paramref name="source"/> and returns it as a new independent bitmap.
        /// </summary>
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

        /// <summary>
        /// Applies a Gaussian blur of the specified sigma to <paramref name="source"/>.
        /// Used for noise/JPEG artefact reduction before sharpening.
        /// </summary>
        private static SKBitmap ApplyBlur(SKBitmap source, float sigma)
        {
            var result = new SKBitmap(source.Width, source.Height);
            using var filter = SKImageFilter.CreateBlur(sigma, sigma);
            using var paint  = new SKPaint { ImageFilter = filter };
            using var canvas = new SKCanvas(result);
            // Use nearest-neighbour sampling (no rescaling — 1:1 draw)
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(), paint);
            return result;
        }

        /// <summary>
        /// Applies the <see cref="SharpenKernel"/> (3×3 Laplacian, sum=1) to <paramref name="source"/>
        /// to enhance edge clarity after the denoise step.
        /// </summary>
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
            // Use nearest-neighbour sampling (no rescaling — 1:1 draw)
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(), paint);
            return result;
        }

        // ────────────────────────────────────────────────────────────────────
        // AI Super-Resolution (realesrgan-ncnn-vulkan CLI)
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves <see cref="AiSrToolPath"/> to a full absolute path by looking next to the
        /// running application (<see cref="AppContext.BaseDirectory"/>). This ensures the tool is
        /// found regardless of the process working directory.
        /// Returns an empty string when the file cannot be located.
        /// </summary>
        private static string GetAiSrToolFullPath()
        {
            if (string.IsNullOrWhiteSpace(AiSrToolPath)) return string.Empty;

            // If the constant is already absolute, use it directly.
            if (Path.IsPathRooted(AiSrToolPath))
                return File.Exists(AiSrToolPath) ? AiSrToolPath : string.Empty;

            // Resolve relative path from the application's base directory
            // (works whether the app is run via `dotnet run` or as a published exe).
            string appBasePath = Path.Combine(AppContext.BaseDirectory, AiSrToolPath);
            if (File.Exists(appBasePath)) return appBasePath;

            // Fallback: try the current working directory
            if (File.Exists(AiSrToolPath)) return Path.GetFullPath(AiSrToolPath);

            return string.Empty;
        }

        /// <summary>
        /// Returns <c>true</c> when <see cref="AiSrToolPath"/> can be resolved to an existing executable.
        /// </summary>
        private static bool IsAiSrToolAvailable() =>
            !string.IsNullOrWhiteSpace(GetAiSrToolFullPath());

        /// <summary>
        /// Calls <c>realesrgan-ncnn-vulkan.exe</c> to upscale <paramref name="inputPath"/> by
        /// <see cref="AiSrScale"/>× and save the result to <paramref name="outputPath"/>.
        /// Returns <c>true</c> on success.
        /// </summary>
        /// <remarks>
        /// The tool is located via <see cref="GetAiSrToolFullPath"/> — it is copied to the build
        /// output directory by the project file, so it always lives next to <c>CardChooser.exe</c>.
        /// </remarks>
        private static async Task<bool> RunSuperResolutionAsync(string inputPath, string outputPath)
        {
            string toolPath = GetAiSrToolFullPath();
            if (string.IsNullOrWhiteSpace(toolPath)) return false;

            // Resolve models folder relative to the same directory as the tool
            string toolDir      = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory;
            string modelsAbsPath = Path.Combine(toolDir, AiSrModelsFolder);

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName  = toolPath,
                        Arguments = $"-i \"{inputPath}\" -o \"{outputPath}\" " +
                                    $"-n {AiSrModelName} -s {AiSrScale} -f jpg " +
                                    $"-m \"{modelsAbsPath}\"",
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

        // ────────────────────────────────────────────────────────────────────
        // JFIF DPI header patching
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Patches the JFIF APP0 segment in a JPEG byte array to embed the specified DPI.
        /// Silently does nothing if the header is absent or malformed.
        /// </summary>
        /// <remarks>
        /// JFIF APP0 layout (byte offsets from start of file):
        /// <code>
        ///  0- 1   FF D8          SOI
        ///  2- 3   FF E0          APP0 marker
        ///  4- 5   00 10          Segment length = 16
        ///  6-10   4A 46 49 46 00 "JFIF\0"
        /// 11-12   01 01          Version 1.1
        ///    13   01             Units: 0=none, 1=DPI, 2=DPCM
        /// 14-15   XX XX          Xdensity (big-endian uint16)
        /// 16-17   XX XX          Ydensity (big-endian uint16)
        /// </code>
        /// </remarks>
        private static void SetJfifDpi(byte[] jpegBytes, int dpi)
        {
            if (jpegBytes.Length < JfifMinLength) return;
            if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8) return; // not JPEG
            if (jpegBytes[2] != 0xFF || jpegBytes[3] != 0xE0) return; // no APP0
            if (jpegBytes[6] != 0x4A || jpegBytes[7] != 0x46 ||
                jpegBytes[8] != 0x49 || jpegBytes[9] != 0x46) return; // no "JFIF"

            byte hi = (byte)(dpi >> 8);
            byte lo = (byte)(dpi & 0xFF);

            jpegBytes[JfifUnitsOffset]         = 0x01; // pixels/inch
            jpegBytes[JfifXDensityOffset]      = hi;
            jpegBytes[JfifXDensityOffset + 1]  = lo;
            jpegBytes[JfifYDensityOffset]      = hi;
            jpegBytes[JfifYDensityOffset + 1]  = lo;
        }

        // ────────────────────────────────────────────────────────────────────
        // Geometry helpers
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the art frame crop rectangle in pixels for the 3000 × 4159 resized card image.
        /// </summary>
        private static SKRectI ComputeArtCropRectangle()
        {
            int x      = (int)(TargetCardWidth  * ArtFrameLeft);    //  231 px
            int y      = (int)(TargetCardHeight * ArtFrameTop);     //  562 px
            int width  = (int)(TargetCardWidth  * ArtFrameWidth);   // 2538 px
            int height = (int)(TargetCardHeight * ArtFrameHeight);  // 1643 px  (bottom @ 2205 px = 53.0 %)
            return new SKRectI(x, y, x + width, y + height);
        }

        // ────────────────────────────────────────────────────────────────────
        // File system helpers
        // ────────────────────────────────────────────────────────────────────

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
    }
}
