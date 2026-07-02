using CardChooser.Benchmark;
using CardChooser.Services;
using CardChooser.Services.Interfaces;

namespace CardChooser
{
    /// <summary>
    /// Entry point for the MTG Card Chooser application.
    /// Uses manual dependency injection to wire up all services, following SOLID principles.
    ///
    /// Special modes:
    ///   --benchmark [folder]   Run GPU vs CPU performance benchmark.
    ///                          'folder' defaults to "scryfall_images" next to the exe.
    /// </summary>
    internal class Program
    {
        /// <summary>
        /// Application entry point. Constructs the service graph and runs the card-processing workflow.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        static async Task Main(string[] args)
        {
            // ── Benchmark mode ────────────────────────────────────────────────
            // Run with:  dotnet run -- --benchmark [path-to-jpeg-folder]
            // Compares GPU parallel (CUDA NPP) vs CPU parallel vs CPU sequential.
            // Results are NOT saved to git — this is a diagnostic / tuning tool.
            if (args.Length > 0 && args[0] == "--benchmark")
            {
                string folder = args.Length > 1
                    ? args[1]
                    : Path.Combine(AppContext.BaseDirectory, "scryfall_images");
                await GpuBenchmark.RunAsync(folder);
                return;
            }

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
