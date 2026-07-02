using System.Text.Json;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service responsible for downloading card images from the Scryfall API.
    /// Single Responsibility: All Scryfall API communication is encapsulated here.
    /// Strategy: fetch card JSON first to resolve the image URL, then download the image.
    /// This correctly handles both regular cards and double-faced cards (DFCs).
    /// </summary>
    public class ScryfallService : IScryfallService, IDisposable
    {
        private const string ScryfallNamedCardBaseUrl = "https://api.scryfall.com/cards/named";
        private const string ImageSize = "large";
        private const string ImageExtension = ".jpg";

        // Scryfall rate limit: 2 req/s. Each card makes 2 HTTP calls (JSON + image).
        // Limiting to 2 concurrent card downloads keeps us at ~4 requests in-flight max.
        private const int MaxConcurrentDownloads = 2;

        private readonly HttpClient _httpClient;

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

            using var semaphore = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads);

            IEnumerable<Task<bool>> downloadTasks = cardNames
                .Select(cardName => DownloadWithThrottleAsync(cardName, targetFolder, semaphore));

            bool[] results = await Task.WhenAll(downloadTasks);

            int successCount = results.Count(r => r);
            int failureCount = results.Count(r => !r);

            Console.WriteLine();
            Console.WriteLine($"Scryfall download complete: {successCount} succeeded, {failureCount} failed.");
            Console.WriteLine();
        }

        /// <summary>
        /// Acquires the semaphore slot before downloading so that at most
        /// <see cref="MaxConcurrentDownloads"/> card images are fetched in parallel.
        /// </summary>
        private async Task<bool> DownloadWithThrottleAsync(string cardName, string targetFolder, SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();
            try
            {
                return await TryDownloadCardImageAsync(cardName, targetFolder);
            }
            finally
            {
                semaphore.Release();
            }
        }

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

                byte[] imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);

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

            HttpResponseMessage response = await _httpClient.GetAsync(requestUrl);

            if (!response.IsSuccessStatusCode)
                return null;

            string json = await response.Content.ReadAsStringAsync();

            using JsonDocument doc = JsonDocument.Parse(json);
            return ExtractImageUrl(doc.RootElement);
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

        private static string BuildCardJsonUrl(string cardName)
        {
            string encodedName = Uri.EscapeDataString(cardName);
            return $"{ScryfallNamedCardBaseUrl}?exact={encodedName}";
        }

        private static string BuildImageFilePath(string targetFolder, string cardName)
        {
            string safeFileName = SanitizeFileName(cardName) + ImageExtension;
            return Path.Combine(targetFolder, safeFileName);
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
