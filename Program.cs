using CardChooser.Services;
using CardChooser.Services.Interfaces;

namespace CardChooser
{
    /// <summary>
    /// Entry point for the MTG Card Chooser application.
    /// Uses manual dependency injection to wire up all services, following SOLID principles.
    /// </summary>
    internal class Program
    {
        /// <summary>
        /// Application entry point. Constructs the service graph and runs the card-processing workflow.
        /// </summary>
        /// <param name="args">Command-line arguments (currently unused).</param>
        static async Task Main(string[] args)
        {
            // Manual Dependency Injection - create service instances
            IConfigurationService configurationService = new ConfigurationService();
            ICardParserService cardParserService = new CardParserService();
            IFileOperationsService fileOperationsService = new FileOperationsService();
            IReportService reportService = new ReportService();
            IScryfallService scryfallService = new ScryfallService(new HttpClient());
            ICardArtExtractorService cardArtExtractorService = new CardArtExtractorService();

            // Create the main processor with all dependencies
            ICardProcessorService cardProcessorService = new CardProcessorService(
                configurationService,
                cardParserService,
                fileOperationsService,
                reportService,
                scryfallService,
                cardArtExtractorService
            );

            // Execute the card processing workflow
            const string configFilePath = "config.txt";
            await cardProcessorService.ProcessCardsAsync(configFilePath);
        }
    }
}
