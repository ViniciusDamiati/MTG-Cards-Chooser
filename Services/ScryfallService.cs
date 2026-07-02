using System.Text.Json;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service responsible for downloading card images from the Scryfall API.
    /// Single Responsibility: All Scryfall API communication is encapsulated here.
    /// Strategy: fetch card JSON first to resolve the image URL, then download the image.
    /// This correctly handles both regular cards and double-faced cards (DFCs).
    ///
    /// All HTTP requests are throttled through a shared rate limiter so that no more than
    /// 2 requests per second are sent (Scryfall's published limit). Cards are downloaded
    /// concurrently via Task.WhenAll; the rate limiter acts as the shared bottleneck.
    /// </summary>
    public class ScryfallService : IScryfallService
    {
        private const string ScryfallNamedCardBaseUrl = "https://api.scryfall.com/cards/named";
        private const string ImageSize = "large";
        private const string ImageExtension = ".jpg";

        // Scryfall rate limit: 2 requests/second (500ms between requests).
        private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMilliseconds(500);

        private readonly HttpClient _httpClient;

        // Single-permit semaphore used as a rate limiter across ALL concurrent HTTP calls.
        // A permit is released 500ms after it is acquired, ensuring ≤ 2 requests/second.
        private readonly SemaphoreSlim _httpRateLimiter = new SemaphoreSlim(1, 1);

        public ScryfallService(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            // Both User-Agent and Accept are required by the Scryfall API
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MTG-Cards-Chooser/1.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        }

        /// <inheritdoc />
        public async Task DownloadCardImagesAsync(IReadOnlyList<string> cardNames, string targetFolder)
        {
            if (cardNames == null || cardNames.Count == 0)
                return;

            EnsureDirectoryExists(targetFolder);

            Console.WriteLine($"Downloading {cardNames.Count} missing card image(s) from Scryfall into '{targetFolder}'...");
            Console.WriteLine();

            // All cards are kicked off in parallel; the shared _httpRateLimiter ensures
            // individual HTTP requests are spaced at least 500ms apart (≤ 2 req/s).
            IEnumerable<Task<bool>> downloadTasks = cardNames
                .Select(cardName => TryDownloadCardImageAsync(cardName, targetFolder));

            bool[] results = await Task.WhenAll(downloadTasks);

            int successCount = results.Count(result => result);
            int failureCount = results.Count(result => !result);

            Console.WriteLine();
            Console.WriteLine($"Scryfall download complete: {successCount} succeeded, {failureCount} failed.");
            Console.WriteLine();
        }

        /// <summary>
        /// Attempts to download the image for a single card: fetches card JSON, extracts the
        /// image URL, downloads the image bytes, and writes them to disk.
        /// Returns <c>true</c> on success, <c>false</c> on any failure.
        /// </summary>
        /// <param name="cardName">The exact card name to look up on Scryfall.</param>
        /// <param name="targetFolder">Folder where the image file will be saved.</param>
        /// <returns><c>true</c> if the image was saved successfully; otherwise <c>false</c>.</returns>
        private async Task<bool> TryDownloadCardImageAsync(string cardName, string targetFolder)
        {
            Console.Write($"  Downloading image for '{cardName}'... ");

            try
            {
                string? imageUrl = await FetchImageUrlAsync(cardName);

                if (imageUrl == null)
                {
                    Console.WriteLine("FAILED (card not found or has no image)");
                    return false;
                }

                byte[] imageBytes = await ThrottledGetBytesAsync(imageUrl);

                string filePath = BuildImageFilePath(targetFolder, cardName);
                await File.WriteAllBytesAsync(filePath, imageBytes);

                Console.WriteLine("OK");
                return true;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"FAILED ({ex.StatusCode?.ToString() ?? ex.Message})");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED ({ex.Message})");
                return false;
            }
        }

        /// <summary>
        /// Fetches the card JSON from Scryfall and extracts the large image URL.
        /// Handles both regular cards (image_uris) and double-faced cards (card_faces[0].image_uris).
        /// </summary>
        private async Task<string?> FetchImageUrlAsync(string cardName)
        {
            string requestUrl = BuildCardJsonUrl(cardName);

            HttpResponseMessage response = await ThrottledGetAsync(requestUrl);

            if (!response.IsSuccessStatusCode)
                return null;

            string json = await response.Content.ReadAsStringAsync();

            using JsonDocument doc = JsonDocument.Parse(json);
            return ExtractImageUrl(doc.RootElement);
        }

        /// <summary>
        /// Performs a throttled HTTP GET, returning the full response.
        /// Acquires the rate-limiter permit and schedules its release after
        /// <see cref="RateLimitWindow"/> so that requests are spaced ≥ 500ms apart.
        /// </summary>
        private async Task<HttpResponseMessage> ThrottledGetAsync(string url)
        {
            await _httpRateLimiter.WaitAsync();
            ScheduleRateLimiterRelease();
            return await _httpClient.GetAsync(url);
        }

        /// <summary>
        /// Performs a throttled HTTP GET, returning the response body as bytes.
        /// </summary>
        private async Task<byte[]> ThrottledGetBytesAsync(string url)
        {
            await _httpRateLimiter.WaitAsync();
            ScheduleRateLimiterRelease();
            return await _httpClient.GetByteArrayAsync(url);
        }

        /// <summary>
        /// Releases the rate-limiter permit after <see cref="RateLimitWindow"/> has elapsed.
        /// The release is measured from the moment the permit was acquired (not from when
        /// the HTTP request finishes), which correctly enforces ≤ 2 requests/second.
        /// </summary>
        private void ScheduleRateLimiterRelease()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(RateLimitWindow);
                _httpRateLimiter.Release();
            });
        }

        /// <summary>
        /// Extracts the large image URL from a Scryfall card JSON element.
        /// Tries the top-level image_uris first, then falls back to card_faces[0] for DFCs.
        /// </summary>
        private static string? ExtractImageUrl(JsonElement cardRoot)
        {
            // Standard card: image_uris.large
            if (cardRoot.TryGetProperty("image_uris", out JsonElement imageUris) &&
                imageUris.TryGetProperty(ImageSize, out JsonElement largeUrl))
            {
                return largeUrl.GetString();
            }

            // Double-faced card: card_faces[0].image_uris.large
            if (cardRoot.TryGetProperty("card_faces", out JsonElement cardFaces) &&
                cardFaces.GetArrayLength() > 0)
            {
                JsonElement firstFace = cardFaces[0];
                if (firstFace.TryGetProperty("image_uris", out JsonElement faceImageUris) &&
                    faceImageUris.TryGetProperty(ImageSize, out JsonElement faceLargeUrl))
                {
                    return faceLargeUrl.GetString();
                }
            }

            return null;
        }

        /// <summary>
        /// Builds the Scryfall API URL for an exact card-name JSON lookup.
        /// </summary>
        /// <param name="cardName">The card name to search for (will be URL-encoded).</param>
        /// <returns>A fully formed request URL string.</returns>
        private static string BuildCardJsonUrl(string cardName)
        {
            string encodedName = Uri.EscapeDataString(cardName);
            return $"{ScryfallNamedCardBaseUrl}?exact={encodedName}";
        }

        /// <summary>
        /// Constructs the full file path where the card image will be saved.
        /// </summary>
        /// <param name="targetFolder">Destination directory.</param>
        /// <param name="cardName">Card name used to derive the file name.</param>
        /// <returns>Absolute file path for the image.</returns>
        private static string BuildImageFilePath(string targetFolder, string cardName)
        {
            string safeFileName = SanitizeFileName(cardName) + ImageExtension;
            return Path.Combine(targetFolder, safeFileName);
        }

        /// <summary>
        /// Replaces any characters that are illegal in file names with an underscore.
        /// </summary>
        /// <param name="name">The raw string to sanitise.</param>
        /// <returns>A file-system-safe version of the input string.</returns>
        private static string SanitizeFileName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        /// <summary>
        /// Creates the specified directory (and any missing parents) if it does not already exist.
        /// </summary>
        /// <param name="path">Directory path to ensure exists.</param>
        private static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }
    }
}
