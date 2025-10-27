using CardChooser.Models;

namespace CardChooser.Services.Interfaces
{
    /// <summary>
    /// Interface for report generation service.
    /// Single Responsibility: Generating and displaying reports.
    /// </summary>
    public interface IReportService
    {
        /// <summary>
        /// Displays the application header.
        /// </summary>
        /// <param name="config">Application configuration.</param>
        void DisplayHeader(AppConfiguration config);

        /// <summary>
        /// Displays processing progress for a card.
        /// </summary>
        /// <param name="cardInfo">Information about the card being processed.</param>
        void DisplayCardProgress(CardInfo cardInfo);

        /// <summary>
        /// Displays the final summary.
        /// </summary>
        /// <param name="cards">List of all processed cards.</param>
        /// <param name="totalFilesCopied">Total number of files copied.</param>
        void DisplaySummary(List<CardInfo> cards, int totalFilesCopied);

        /// <summary>
        /// Generates a missing cards report file.
        /// </summary>
        /// <param name="missingCards">List of missing card names.</param>
        /// <param name="outputFolder">Folder to save the report.</param>
        /// <param name="reportFileName">Name of the report file.</param>
        void GenerateMissingCardsReport(List<string> missingCards, string outputFolder, string reportFileName);
    }
}
