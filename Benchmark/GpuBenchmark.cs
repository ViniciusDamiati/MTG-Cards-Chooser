using System.Collections.Concurrent;
using System.Diagnostics;
using SkiaSharp;
using CardChooser.Services;

namespace CardChooser.Benchmark
{
    /// <summary>
    /// Micro-benchmark comparing CUDA NPP vs SkiaSharp (CPU) image processing.
    ///
    /// Invoke via:
    ///   dotnet run -- --benchmark [folder-of-jpegs]
    ///
    /// If no folder is supplied the benchmark looks for a "scryfall_images" sub-directory
    /// next to the executable.
    ///
    /// Three modes are timed:
    ///   1. GPU parallel   — CUDA NPP, 4 concurrent streams (requires CUDA Toolkit 12.2+)
    ///   2. CPU parallel   — SkiaSharp, ProcessorCount/2 threads
    ///   3. CPU sequential — SkiaSharp, 1 thread (matches old pre-parallelism behaviour)
    ///
    /// All source images are decoded from disk BEFORE the timers start so that disk I/O
    /// does not skew the results.  Results are written to the system temp directory.
    /// </summary>
    internal static class GpuBenchmark
    {
        // ── Art frame constants (must match CardArtExtractorService) ─────────
        private const int TargetCardWidth  = 3000;
        private const int TargetCardHeight = 4159;

        private const double ArtFrameLeft   = 0.077;
        private const double ArtFrameTop    = 0.1169;
        private const double ArtFrameWidth  = 0.8425;
        private const double ArtFrameHeight = 0.4394;

        private static readonly float[] SharpenKernel =
        {
            -0.25f, -0.5f, -0.25f,
            -0.5f,   4.0f, -0.5f,
            -0.25f, -0.5f, -0.25f
        };

        private const int JpegQuality   = 95;
        private const int AiSrGpuIndex  = 0;
        private static readonly int CpuParallelism = Math.Max(1, Environment.ProcessorCount / 2);

        // ════════════════════════════════════════════════════════════════════
        // Entry point
        // ════════════════════════════════════════════════════════════════════

        public static async Task RunAsync(string imageFolder)
        {
            Console.WriteLine();
            Console.WriteLine("══════════════════════════════════════════════════════════════");
            Console.WriteLine("  MTG Card Art Extractor — GPU vs CPU Benchmark");
            Console.WriteLine("══════════════════════════════════════════════════════════════");
            Console.WriteLine();

            // ── 1. Discover images ────────────────────────────────────────────
            if (!Directory.Exists(imageFolder))
            {
                Console.WriteLine($"  ⚠ Image folder not found: '{imageFolder}'");
                Console.WriteLine("  Usage: dotnet run -- --benchmark <path-to-jpeg-folder>");
                return;
            }

            string[] jpegPaths = Directory
                .GetFiles(imageFolder, "*.jpg", SearchOption.TopDirectoryOnly);

            if (jpegPaths.Length == 0)
            {
                Console.WriteLine($"  ⚠ No JPEG files found in '{imageFolder}'.");
                return;
            }

            Console.WriteLine($"  Image folder  : {imageFolder}");
            Console.WriteLine($"  Images found  : {jpegPaths.Length}");
            Console.WriteLine($"  CPU parallelism: {CpuParallelism} threads (ProcessorCount/2)");
            Console.WriteLine();

            // ── 2. Pre-load all source bitmaps (exclude disk I/O from timing) ─
            Console.Write("  Pre-loading images into memory ... ");
            var images = new List<(string Name, SKBitmap Bmp)>(jpegPaths.Length);
            foreach (string path in jpegPaths)
            {
                SKBitmap? bmp = SKBitmap.Decode(path);
                if (bmp != null)
                    images.Add((Path.GetFileName(path), bmp));
            }
            Console.WriteLine($"OK ({images.Count} decoded)");
            Console.WriteLine();

            // ── 3. Compute crop rectangle ─────────────────────────────────────
            var cropRect = ComputeCropRect();

            // ── 4. Prepare output directory (throw-away results) ──────────────
            string outDir = Path.Combine(Path.GetTempPath(), $"mtg_benchmark_{Guid.NewGuid():N}");
            Directory.CreateDirectory(outDir);

            // ── 5. Initialise CUDA pipeline ───────────────────────────────────
            Console.Write("  Initialising CUDA NPP pipeline ... ");
            using CudaNppImagePipeline? cuda = CudaNppImagePipeline.TryCreate(AiSrGpuIndex);
            if (cuda != null)
                Console.WriteLine($"OK ({CudaNppImagePipeline.GetDeviceName(AiSrGpuIndex)}, " +
                                  $"{cuda.Parallelism} streams)");
            else
                Console.WriteLine("UNAVAILABLE — GPU runs will be skipped");
            Console.WriteLine();

            // ── 6. GPU warmup pass (no timing — ensures JIT + CUDA kernel compile) ──
            if (cuda != null && images.Count > 0)
            {
                Console.Write("  GPU warmup (1 image, untimed) ... ");
                RunCpuOrGpuImage(images[0].Bmp, cropRect, cuda);
                Console.WriteLine("done");
                Console.WriteLine();
            }

            // ══════════════════════════════════════════════════════════════════
            // Benchmark runs
            // ══════════════════════════════════════════════════════════════════

            var results = new List<BenchResult>();

            // ── Run A: GPU parallel (4 CUDA streams) ──────────────────────────
            if (cuda != null)
            {
                var r = await RunParallelAsync(
                    "GPU parallel (4 CUDA streams)",
                    images, cropRect, cuda,
                    maxDop: cuda.Parallelism,
                    outDir);
                results.Add(r);
                Console.WriteLine();
            }

            // ── Run B: CPU parallel (ProcessorCount/2 threads) ────────────────
            {
                var r = await RunParallelAsync(
                    $"CPU parallel ({CpuParallelism} SkiaSharp threads)",
                    images, cropRect, cuda: null,
                    maxDop: CpuParallelism,
                    outDir);
                results.Add(r);
                Console.WriteLine();
            }

            // ── Run C: CPU sequential (1 thread) ─────────────────────────────
            {
                var r = await RunSequentialAsync(
                    "CPU sequential (1 SkiaSharp thread)",
                    images, cropRect,
                    outDir);
                results.Add(r);
                Console.WriteLine();
            }

            // ── 7. Cleanup temp output ────────────────────────────────────────
            try { Directory.Delete(outDir, recursive: true); } catch { }

            // ── 8. Dispose bitmaps ────────────────────────────────────────────
            foreach (var (_, bmp) in images)
                bmp.Dispose();

            // ── 9. Print comparison table ─────────────────────────────────────
            PrintTable(results);
        }

        // ════════════════════════════════════════════════════════════════════
        // Parallel runner
        // ════════════════════════════════════════════════════════════════════

        private static async Task<BenchResult> RunParallelAsync(
            string label,
            List<(string Name, SKBitmap Bmp)> images,
            SKRectI cropRect,
            CudaNppImagePipeline? cuda,
            int maxDop,
            string outDir)
        {
            var perImage = new ConcurrentBag<long>();
            int errors   = 0;

            Console.WriteLine($"  ▶ {label}");

            var sw = Stopwatch.StartNew();

            await Parallel.ForEachAsync(
                images,
                new ParallelOptions { MaxDegreeOfParallelism = maxDop },
                async (entry, _) =>
                {
                    var imgSw = Stopwatch.StartNew();
                    try
                    {
                        byte[] jpeg = await Task.Run(() =>
                            ProcessToJpeg(entry.Bmp, cropRect, cuda));

                        string dest = Path.Combine(outDir, $"{label[..3]}_{entry.Name}");
                        await File.WriteAllBytesAsync(dest, jpeg);
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                    finally
                    {
                        imgSw.Stop();
                        perImage.Add(imgSw.ElapsedMilliseconds);
                    }
                });

            sw.Stop();

            long[] times = perImage.OrderBy(t => t).ToArray();
            var result = new BenchResult(
                Label:       label,
                TotalMs:     sw.ElapsedMilliseconds,
                ImageCount:  images.Count,
                MinMs:       times.Length > 0 ? times[0]                    : 0,
                MaxMs:       times.Length > 0 ? times[^1]                   : 0,
                MedianMs:    times.Length > 0 ? times[times.Length / 2]     : 0,
                AvgMs:       times.Length > 0 ? (long)times.Average()       : 0,
                Errors:      errors);

            Console.WriteLine($"    Total : {result.TotalMs,7} ms  |  " +
                              $"Avg/img : {result.AvgMs,5} ms  |  " +
                              $"Min : {result.MinMs,4} ms  |  " +
                              $"Max : {result.MaxMs,5} ms  |  " +
                              $"Errors : {result.Errors}");
            return result;
        }

        // ════════════════════════════════════════════════════════════════════
        // Sequential runner
        // ════════════════════════════════════════════════════════════════════

        private static async Task<BenchResult> RunSequentialAsync(
            string label,
            List<(string Name, SKBitmap Bmp)> images,
            SKRectI cropRect,
            string outDir)
        {
            var perImage = new List<long>(images.Count);
            int errors   = 0;

            Console.WriteLine($"  ▶ {label}");

            var sw = Stopwatch.StartNew();

            foreach (var (name, bmp) in images)
            {
                var imgSw = Stopwatch.StartNew();
                try
                {
                    byte[] jpeg = ProcessToJpeg(bmp, cropRect, cuda: null);
                    string dest = Path.Combine(outDir, $"seq_{name}");
                    await File.WriteAllBytesAsync(dest, jpeg);
                }
                catch
                {
                    errors++;
                }
                finally
                {
                    imgSw.Stop();
                    perImage.Add(imgSw.ElapsedMilliseconds);
                }
            }

            sw.Stop();

            long[] times = perImage.OrderBy(t => t).ToArray();
            var result = new BenchResult(
                Label:       label,
                TotalMs:     sw.ElapsedMilliseconds,
                ImageCount:  images.Count,
                MinMs:       times.Length > 0 ? times[0]                    : 0,
                MaxMs:       times.Length > 0 ? times[^1]                   : 0,
                MedianMs:    times.Length > 0 ? times[times.Length / 2]     : 0,
                AvgMs:       times.Length > 0 ? (long)times.Average()       : 0,
                Errors:      errors);

            Console.WriteLine($"    Total : {result.TotalMs,7} ms  |  " +
                              $"Avg/img : {result.AvgMs,5} ms  |  " +
                              $"Min : {result.MinMs,4} ms  |  " +
                              $"Max : {result.MaxMs,5} ms  |  " +
                              $"Errors : {result.Errors}");
            return result;
        }

        // ════════════════════════════════════════════════════════════════════
        // Image processing helpers
        // ════════════════════════════════════════════════════════════════════

        private static byte[] ProcessToJpeg(
            SKBitmap source, SKRectI cropRect, CudaNppImagePipeline? cuda)
        {
            if (cuda != null)
            {
                // ── GPU path ──────────────────────────────────────────────────
                using SKBitmap art = cuda.ProcessOnGpu(
                    source,
                    TargetCardWidth, TargetCardHeight,
                    cropRect.Left, cropRect.Top, cropRect.Width, cropRect.Height,
                    SharpenKernel);
                return EncodeJpeg(art);
            }
            else
            {
                // ── CPU path (SkiaSharp) ───────────────────────────────────────
                var resizeInfo = new SKImageInfo(TargetCardWidth, TargetCardHeight,
                                                 source.ColorType, source.AlphaType);
                using SKBitmap resized = source.Resize(resizeInfo,
                    new SKSamplingOptions(SKCubicResampler.CatmullRom))
                    ?? throw new InvalidOperationException("Resize returned null.");

                using SKBitmap cropped = ExtractCrop(resized, cropRect);
                using SKBitmap blurred = ApplyBlur(cropped, sigma: 0.5f);
                using SKBitmap sharp   = ApplySharpen(blurred);
                return EncodeJpeg(sharp);
            }
        }

        private static byte[] EncodeJpeg(SKBitmap bmp)
        {
            using SKImage img  = SKImage.FromBitmap(bmp);
            using SKData  data = img.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                ?? throw new InvalidOperationException("JPEG encode returned null.");
            return data.ToArray();
        }

        private static SKBitmap ExtractCrop(SKBitmap source, SKRectI rect)
        {
            var dst = new SKBitmap(rect.Width, rect.Height);
            using var canvas = new SKCanvas(dst);
            canvas.DrawBitmap(source,
                new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                new SKRect(0, 0, rect.Width, rect.Height),
                new SKSamplingOptions(SKCubicResampler.CatmullRom),
                paint: null);
            return dst;
        }

        private static SKBitmap ApplyBlur(SKBitmap source, float sigma)
        {
            var dst = new SKBitmap(source.Width, source.Height);
            using var filter = SKImageFilter.CreateBlur(sigma, sigma);
            using var paint  = new SKPaint { ImageFilter = filter };
            using var canvas = new SKCanvas(dst);
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(), paint);
            return dst;
        }

        private static SKBitmap ApplySharpen(SKBitmap source)
        {
            var dst = new SKBitmap(source.Width, source.Height);
            using var filter = SKImageFilter.CreateMatrixConvolution(
                new SKSizeI(3, 3), SharpenKernel,
                gain: 1f, bias: 0f,
                new SKPointI(1, 1), SKShaderTileMode.Clamp,
                convolveAlpha: false);
            using var paint  = new SKPaint { ImageFilter = filter };
            using var canvas = new SKCanvas(dst);
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(), paint);
            return dst;
        }

        private static SKBitmap RunCpuOrGpuImage(
            SKBitmap bmp, SKRectI cropRect, CudaNppImagePipeline? cuda)
        {
            if (cuda != null)
                return cuda.ProcessOnGpu(bmp,
                    TargetCardWidth, TargetCardHeight,
                    cropRect.Left, cropRect.Top, cropRect.Width, cropRect.Height,
                    SharpenKernel);

            // CPU warmup (identical pipeline)
            var info = new SKImageInfo(TargetCardWidth, TargetCardHeight,
                                       bmp.ColorType, bmp.AlphaType);
            using var r = bmp.Resize(info, new SKSamplingOptions(SKCubicResampler.CatmullRom))
                ?? throw new InvalidOperationException();
            using var c = ExtractCrop(r, cropRect);
            using var b = ApplyBlur(c, 0.5f);
            return ApplySharpen(b);
        }

        // ════════════════════════════════════════════════════════════════════
        // Geometry
        // ════════════════════════════════════════════════════════════════════

        private static SKRectI ComputeCropRect()
        {
            int x = (int)(TargetCardWidth  * ArtFrameLeft);
            int y = (int)(TargetCardHeight * ArtFrameTop);
            int w = (int)(TargetCardWidth  * ArtFrameWidth);
            int h = (int)(TargetCardHeight * ArtFrameHeight);
            return new SKRectI(x, y, x + w, y + h);
        }

        // ════════════════════════════════════════════════════════════════════
        // Results table
        // ════════════════════════════════════════════════════════════════════

        private static void PrintTable(List<BenchResult> results)
        {
            if (results.Count == 0) return;

            long baseline = results[^1].TotalMs; // CPU sequential is last = baseline

            Console.WriteLine();
            Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
            Console.WriteLine(" RESULTS");
            Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
            Console.WriteLine($"  {"Mode",-45} {"Total":>9} {"Avg/img":>9} {"Median":>8} {"Min":>7} {"Max":>7} {"Speedup":>8}");
            Console.WriteLine($"  {"----",-45} {"-----":>9} {"-------":>9} {"------":>8} {"---":>7} {"---":>7} {"-------":>8}");

            foreach (BenchResult r in results)
            {
                double speedup = baseline > 0 ? (double)baseline / r.TotalMs : 1.0;
                string speedupStr = speedup >= 1.0
                    ? $"{speedup:F2}×"
                    : $"0.{(int)(speedup * 100):D2}×";

                Console.WriteLine(
                    $"  {r.Label,-45} " +
                    $"{r.TotalMs,8} ms " +
                    $"{r.AvgMs,7} ms " +
                    $"{r.MedianMs,6} ms " +
                    $"{r.MinMs,5} ms " +
                    $"{r.MaxMs,5} ms " +
                    $"{speedupStr,8}");
            }

            Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");

            // ── Per-image throughput ──────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("  Throughput (images / second):");
            foreach (BenchResult r in results)
            {
                double imgPerSec = r.TotalMs > 0
                    ? r.ImageCount / (r.TotalMs / 1000.0)
                    : 0;
                Console.WriteLine($"    {r.Label,-45} {imgPerSec,6:F2} img/s");
            }

            Console.WriteLine();
            if (results.Count >= 2)
            {
                BenchResult best = results.MinBy(r => r.TotalMs)!;
                Console.WriteLine($"  Fastest mode: {best.Label}");
                if (best.TotalMs < baseline)
                {
                    double gain = (baseline - best.TotalMs) / (double)baseline * 100;
                    Console.WriteLine($"  Time saved vs CPU sequential: {baseline - best.TotalMs} ms ({gain:F1}% faster)");
                }
            }
            Console.WriteLine();
        }

        // ════════════════════════════════════════════════════════════════════
        // Result record
        // ════════════════════════════════════════════════════════════════════

        private sealed record BenchResult(
            string Label,
            long   TotalMs,
            int    ImageCount,
            long   MinMs,
            long   MaxMs,
            long   MedianMs,
            long   AvgMs,
            int    Errors);
    }
}
