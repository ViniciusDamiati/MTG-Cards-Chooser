namespace CardChooser.Services.Interfaces
{
    /// <summary>
    /// Interface for card parsing service.
    /// Single Responsibility: Parsing card names from different formats.
    /// </summary>
    public interface ICardParserService
    {
        /// <summary>
        /// Extracts the card name from a line in the input file.
        /// Supports both simple format and extended format.
        /// </summary>
        /// <param name="line">The line to parse.</param>
        /// <returns>The extracted card name.</returns>
        string ExtractCardName(string line);

        /// <summary>
        /// Reads all card names from a file.
        /// </summary>
        /// <param name="filePath">Path to the file containing card names.</param>
        /// <returns>List of card names.</returns>
        List<string> ReadCardNames(string filePath);
    }
}
