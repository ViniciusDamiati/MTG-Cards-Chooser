using SkiaSharp;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Extracts card art from Scryfall card images using SkiaSharp (Google Skia engine) —
    /// pure C# image processing, no Photoshop or licensed libraries required.
    ///
    /// Pipeline per image:
    ///   1. Load the Scryfall JPEG (typically 672 × 936 px).
    ///   2. Resize to 3000 × 4159 px using Catmull-Rom cubic resampling
    ///      (high-quality enlargement equivalent to Photoshop "Preserve Details").
    ///   3. Crop to the standard M15 art frame, discarding card border,
    ///      name bar, type line, text box, and legal text.
    ///   4. Encode as JPEG (quality 95) and patch the JFIF header to embed 1200 DPI.
    ///   5. Save the art-only file to the output folder.
    ///
    /// Art frame crop constants are fractions of the full resized card dimensions.
    /// Adjust the ArtFrame* constants if a specific card style requires a different crop.
    /// </summary>
    public class CardArtExtractorService : ICardArtExtractorService
    {
        // ── Target canvas (matches Proxyshop PSD template dimensions) ──────────
        private const int TargetCardWidth = 3000;
        private const int TargetCardHeight = 4159;
        private const int TargetDpi = 1200;

        // ── Art frame position as fractions of the resized card image ────────
        // Calibrated for the standard M15 card layout on a Scryfall 'large' scan.
        // Physical result at 1200 DPI: ≈ 53 mm × 40 mm — matches the actual print art frame.
        // Tune these constants if the crop is off for a particular card style.
        private const double ArtFrameLeft = 0.080;    // 8.0 % from the left edge
        private const double ArtFrameTop = 0.135;     // 13.5 % from the top (below name/mana bar)
        private const double ArtFrameWidth = 0.840;   // 84.0 % of card width  → 2520 px
        private const double ArtFrameHeight = 0.450;  // 45.0 % of card height → 1872 px

        // ── JPEG save quality ─────────────────────────────────────────────────
        private const int JpegQuality = 95;

        // ── File handling ─────────────────────────────────────────────────────
        private const string JpegExtension = ".jpg";

        // ── JFIF header offsets for DPI metadata ──────────────────────────────
        // JPEG structure: FF D8 | FF E0 | len(2) | "JFIF\0"(5) | ver(2) | units | Xdpi(2) | Ydpi(2)
        private const int JfifUnitsOffset = 13;    // byte index of the units field (1 = DPI)
        private const int JfifXDensityOffset = 14; // big-endian uint16 for horizontal density
        private const int JfifYDensityOffset = 16; // big-endian uint16 for vertical density
        private const int JfifMinLength = 18;      // minimum bytes needed to contain the DPI fields

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

            Console.WriteLine($"Extracting card art from {imageFiles.Count} image(s) into '{artOutputFolder}'...");
            Console.WriteLine();

            SKRectI cropRect = ComputeArtCropRectangle();

            int successCount = 0;
            int failureCount = 0;

            foreach (string imageFile in imageFiles)
            {
                bool succeeded = await TryExtractArtAsync(imageFile, artOutputFolder, cropRect);

                if (succeeded)
                    successCount++;
                else
                    failureCount++;
            }

            Console.WriteLine();
            Console.WriteLine($"Art extraction complete: {successCount} succeeded, {failureCount} failed.");
            Console.WriteLine();
        }

        /// <summary>
        /// Processes a single card image: resize → crop → encode → patch DPI → write.
        /// Offloads all CPU-bound image work to the thread pool via <see cref="Task.Run"/>.
        /// </summary>
        /// <param name="sourceImagePath">Full path to the Scryfall JPEG.</param>
        /// <param name="artOutputFolder">Destination folder for the cropped art.</param>
        /// <param name="cropRect">Pre-computed art frame crop rectangle (pixels in the resized image).</param>
        /// <returns><c>true</c> on success; <c>false</c> on any error.</returns>
        private static async Task<bool> TryExtractArtAsync(
            string sourceImagePath,
            string artOutputFolder,
            SKRectI cropRect)
        {
            string cardFileName = Path.GetFileName(sourceImagePath);

            try
            {
                // Offload CPU-bound work (decode, resize, crop, encode) to the thread pool
                byte[] jpegBytes = await Task.Run(() => ProcessCardImageToBytes(sourceImagePath, cropRect));

                // Patch the JFIF header in-memory to record 1200 DPI
                SetJfifDpi(jpegBytes, TargetDpi);

                // Write the finished art file
                string outputPath = Path.Combine(artOutputFolder, cardFileName);
                await File.WriteAllBytesAsync(outputPath, jpegBytes);

                Console.WriteLine($"  '{cardFileName}'... OK");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  '{cardFileName}'... FAILED ({ex.Message})");
                return false;
            }
        }

        /// <summary>
        /// Performs the full image processing pipeline synchronously (suitable for <see cref="Task.Run"/>):
        /// load → resize → crop → JPEG encode.
        /// </summary>
        /// <param name="sourceImagePath">Full path to the source Scryfall JPEG.</param>
        /// <param name="cropRect">Art frame crop rectangle in the resized image's coordinate space.</param>
        /// <returns>Raw JPEG bytes of the cropped art image.</returns>
        private static byte[] ProcessCardImageToBytes(string sourceImagePath, SKRectI cropRect)
        {
            // Load source image
            using SKBitmap source = SKBitmap.Decode(sourceImagePath)
                ?? throw new InvalidOperationException($"SkiaSharp could not decode '{Path.GetFileName(sourceImagePath)}'.");

            // Resize to 3000 × 4159 using Catmull-Rom cubic resampling (high-quality enlargement)
            var targetInfo = new SKImageInfo(TargetCardWidth, TargetCardHeight, source.ColorType, source.AlphaType);
            using SKBitmap resized = source.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.CatmullRom))
                ?? throw new InvalidOperationException("Resize returned null.");

            // Crop: draw the art frame area of the resized card into a new bitmap.
            // Use the SKSamplingOptions overload (DrawBitmap with two rects defaults to the
            // deprecated SKPaint overload in SkiaSharp 4.x).
            using SKBitmap artBitmap = new(cropRect.Width, cropRect.Height);
            using (var canvas = new SKCanvas(artBitmap))
            {
                var srcRect = new SKRect(cropRect.Left, cropRect.Top, cropRect.Right, cropRect.Bottom);
                var dstRect = new SKRect(0, 0, cropRect.Width, cropRect.Height);
                canvas.DrawBitmap(resized, srcRect, dstRect,
                    new SKSamplingOptions(SKCubicResampler.CatmullRom), paint: null);
            }

            // Encode as JPEG and return as byte array
            using SKImage artImage = SKImage.FromBitmap(artBitmap);
            using SKData encoded = artImage.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                ?? throw new InvalidOperationException("JPEG encoding returned null.");

            return encoded.ToArray();
        }

        /// <summary>
        /// Patches the JFIF APP0 header inside a JPEG byte array to set the horizontal and vertical
        /// density (DPI) to the specified value.  Silently does nothing if the header is absent or malformed.
        /// </summary>
        /// <remarks>
        /// JFIF APP0 layout (byte indices from start of file):
        /// <code>
        ///  0- 1   FF D8          SOI marker
        ///  2- 3   FF E0          APP0 marker
        ///  4- 5   00 10          Segment length (16, big-endian)
        ///  6-10   4A 46 49 46 00 "JFIF\0"
        /// 11-12   01 01          Version 1.1
        ///    13   01             Units  (0=none, 1=DPI, 2=DPCM)
        /// 14-15   XX XX          Xdensity (big-endian uint16)
        /// 16-17   XX XX          Ydensity (big-endian uint16)
        /// </code>
        /// </remarks>
        /// <param name="jpegBytes">JPEG byte array to patch in-place.</param>
        /// <param name="dpi">Target density in pixels-per-inch.</param>
        private static void SetJfifDpi(byte[] jpegBytes, int dpi)
        {
            if (jpegBytes.Length < JfifMinLength) return;

            // Verify SOI + APP0 markers
            if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8) return;
            if (jpegBytes[2] != 0xFF || jpegBytes[3] != 0xE0) return;

            // Verify "JFIF" signature at bytes 6–9
            if (jpegBytes[6] != 0x4A || jpegBytes[7] != 0x46 ||
                jpegBytes[8] != 0x49 || jpegBytes[9] != 0x46) return;

            byte dpiHigh = (byte)(dpi >> 8);   // e.g. 0x04 for 1200
            byte dpiLow  = (byte)(dpi & 0xFF); // e.g. 0xB0 for 1200  (0x04B0 = 1200)

            jpegBytes[JfifUnitsOffset]     = 0x01;    // units = pixels/inch
            jpegBytes[JfifXDensityOffset]  = dpiHigh;
            jpegBytes[JfifXDensityOffset + 1] = dpiLow;
            jpegBytes[JfifYDensityOffset]  = dpiHigh;
            jpegBytes[JfifYDensityOffset + 1] = dpiLow;
        }

        /// <summary>
        /// Computes the art frame crop rectangle in pixels for the resized 3000 × 4159 card image.
        /// </summary>
        private static SKRectI ComputeArtCropRectangle()
        {
            int x      = (int)(TargetCardWidth  * ArtFrameLeft);    // 240
            int y      = (int)(TargetCardHeight * ArtFrameTop);     // 561
            int width  = (int)(TargetCardWidth  * ArtFrameWidth);   // 2520
            int height = (int)(TargetCardHeight * ArtFrameHeight);  // 1872
            return new SKRectI(x, y, x + width, y + height);
        }

        /// <summary>
        /// Returns all JPEG files found directly inside <paramref name="folder"/> (non-recursive).
        /// </summary>
        private static IReadOnlyList<string> DiscoverJpegs(string folder) =>
            Directory
                .EnumerateFiles(folder, $"*{JpegExtension}", SearchOption.TopDirectoryOnly)
                .ToList()
                .AsReadOnly();

        /// <summary>Creates the directory (and all missing parents) if it does not already exist.</summary>
        private static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }
    }
}
