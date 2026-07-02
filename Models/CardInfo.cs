namespace CardChooser.Models
{
    /// <summary>
    /// Represents the result of searching for a single Magic: The Gathering card
    /// in the source folder.
    /// </summary>
    public class CardInfo
    {
        /// <summary>
        /// Gets or sets the card name as it was read from the input list.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether at least one matching file was found
        /// in the source folder.
        /// </summary>
        public bool Found { get; set; }

        /// <summary>
        /// Gets or sets the list of full file paths that matched this card name.
        /// Empty when <see cref="Found"/> is <c>false</c>.
        /// </summary>
        public List<string> MatchingFiles { get; set; } = new List<string>();
    }
}
