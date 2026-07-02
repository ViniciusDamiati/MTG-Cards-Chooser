namespace CardChooser.Models
{
    /// <summary>
    /// Holds all runtime configuration values loaded from the config.txt file.
    /// </summary>
    public class AppConfiguration
    {
        /// <summary>
        /// Gets or sets the path to the text file containing the list of card names to process.
        /// Defaults to "cards.txt".
        /// </summary>
        public string InputCardsFile { get; set; } = "cards.txt";

        /// <summary>
        /// Gets or sets the root directory that will be scanned for card image files.
        /// </summary>
        public string SourceFolder { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the directory where found card image files will be copied
        /// and where the missing-cards report will be written.
        /// </summary>
        public string OutputFolder { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the filename (not path) of the missing-cards report.
        /// The file will be written inside <see cref="OutputFolder"/>.
        /// Defaults to "missing_cards.txt".
        /// </summary>
        public string MissingCardsReport { get; set; } = "missing_cards.txt";
    }
}
