namespace CardChooser.Services.Interfaces
{
    /// <summary>
    /// Interface for card processor service.
    /// Single Responsibility: Orchestrating the card processing workflow.
    /// </summary>
    public interface ICardProcessorService
    {
        /// <summary>
        /// Processes all cards from the input file.
        /// Downloads Scryfall images for any missing cards.
        /// </summary>
        /// <param name="configFilePath">Path to the configuration file.</param>
        Task ProcessCardsAsync(string configFilePath);
    }
}
