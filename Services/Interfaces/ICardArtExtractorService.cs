namespace CardChooser.Services.Interfaces
{
    /// <summary>
    /// Service responsible for extracting card art from Scryfall card images.
    /// Resizes each image to the standard proxy canvas (3000 × 4159 px @ 1200 DPI) and crops
    /// to retain only the art frame, removing the card border, name bar, type line, and text box.
    /// Single Responsibility: all image processing is encapsulated here.
    /// </summary>
    public interface ICardArtExtractorService
    {
        /// <summary>
        /// Processes every JPEG image found in <paramref name="sourceImagesFolder"/>,
        /// resizes each to 3000 × 4159 px at 1200 DPI, crops to the standard M15 art frame,
        /// and saves the results into <paramref name="artOutputFolder"/>.
        /// </summary>
        /// <param name="sourceImagesFolder">
        /// Folder that contains the Scryfall card images (e.g. <c>scryfall_images/</c>).
        /// Non-image files in this folder are silently skipped.
        /// </param>
        /// <param name="artOutputFolder">
        /// Destination folder for the cropped art images (e.g. <c>scryfall_images/cards_arts/</c>).
        /// Will be created if it does not exist.
        /// </param>
        Task ExtractArtsAsync(string sourceImagesFolder, string artOutputFolder);
    }
}
