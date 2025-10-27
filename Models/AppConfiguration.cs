namespace CardChooser.Models
{
    /// <summary>
    /// Represents the application configuration.
    /// </summary>
    public class AppConfiguration
    {
        public string InputCardsFile { get; set; } = "cards.txt";
        public string SourceFolder { get; set; } = string.Empty;
        public string OutputFolder { get; set; } = string.Empty;
        public string MissingCardsReport { get; set; } = "missing_cards.txt";
    }
}
