namespace CardChooser.Services.Interfaces
{
    /// <summary>
    /// Interface for fetching card images from the Scryfall API.
    /// Single Responsibility: Abstracts all communication with the Scryfall API.
    /// </summary>
    public interface IScryfallService
    {
        /// <summary>
        /// Downloads card images for all provided card names and saves them to the target folder.
        /// </summary>
        /// <param name="cardNames">List of card names to download images for.</param>
        /// <param name="targetFolder">The folder where images will be saved.</param>
        Task DownloadCardImagesAsync(IReadOnlyList<string> cardNames, string targetFolder);
    }
}
