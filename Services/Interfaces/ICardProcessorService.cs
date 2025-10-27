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
        /// </summary>
        /// <param name="configFilePath">Path to the configuration file.</param>
        void ProcessCards(string configFilePath);
    }
}
