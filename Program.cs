using CardChooser.Services;
using CardChooser.Services.Interfaces;

namespace CardChooser
{
    /// <summary>
    /// Entry point for the Card Chooser application.
    /// Uses dependency injection to wire up services following SOLID principles.
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            // Manual Dependency Injection - create service instances
            IConfigurationService configurationService = new ConfigurationService();
            ICardParserService cardParserService = new CardParserService();
            IFileOperationsService fileOperationsService = new FileOperationsService();
            IReportService reportService = new ReportService();
            IScryfallService scryfallService = new ScryfallService(new HttpClient());

            // Create the main processor with all dependencies
            ICardProcessorService cardProcessorService = new CardProcessorService(
                configurationService,
                cardParserService,
                fileOperationsService,
                reportService,
                scryfallService
            );

            // Execute the card processing workflow
            const string configFilePath = "config.txt";
            await cardProcessorService.ProcessCardsAsync(configFilePath);
        }
    }
}
