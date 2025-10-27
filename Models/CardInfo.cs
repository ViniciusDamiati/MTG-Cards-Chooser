namespace CardChooser.Models
{
    /// <summary>
    /// Represents information about a card.
    /// </summary>
    public class CardInfo
    {
        public string Name { get; set; } = string.Empty;
        public bool Found { get; set; }
        public List<string> MatchingFiles { get; set; } = new List<string>();
    }
}
