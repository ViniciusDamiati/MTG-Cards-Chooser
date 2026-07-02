using CardChooser.Models;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Orchestrates the full card-processing workflow: loading configuration, scanning files,
    /// copying found cards to the output folder, reporting missing cards, and downloading their
    /// images from Scryfall.
    /// Follows the Single Responsibility Principle by delegating specific tasks to specialised services.
    /// </summary>
    public class CardProcessorService : ICardProcessorService
    {
        private const string ScryfallImagesFolder = "scryfall_images";

        private readonly IConfigurationService _configurationService;
        private readonly ICardParserService _cardParserService;
        private readonly IFileOperationsService _fileOperationsService;
        private readonly IReportService _reportService;
        private readonly IScryfallService _scryfallService;

        /// <summary>
        /// Initialises a new instance of <see cref="CardProcessorService"/> with all required dependencies.
        /// </summary>
        /// <param name="configurationService">Service for loading and validating configuration.</param>
        /// <param name="cardParserService">Service for parsing card names from the input file.</param>
        /// <param name="fileOperationsService">Service for file-system search and copy operations.</param>
        /// <param name="reportService">Service for console output and report generation.</param>
        /// <param name="scryfallService">Service for downloading card images from the Scryfall API.</param>
        public CardProcessorService(
            IConfigurationService configurationService,
            ICardParserService cardParserService,
            IFileOperationsService fileOperationsService,
            IReportService reportService,
            IScryfallService scryfallService)
        {
            _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
            _cardParserService = cardParserService ?? throw new ArgumentNullException(nameof(cardParserService));
            _fileOperationsService = fileOperationsService ?? throw new ArgumentNullException(nameof(fileOperationsService));
            _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
            _scryfallService = scryfallService ?? throw new ArgumentNullException(nameof(scryfallService));
        }

        /// <inheritdoc />
        public async Task ProcessCardsAsync(string configFilePath)
        {
            try
            {
                // Load and validate configuration
                var config = _configurationService.LoadConfiguration(configFilePath);

                if (!_configurationService.ValidateConfiguration(config))
                    return;

                _reportService.DisplayHeader(config);

                // Ensure output folder exists
                _fileOperationsService.EnsureOutputFolderExists(config.OutputFolder);

                // Read card names
                Console.WriteLine("Reading card list...");
                var cardNames = _cardParserService.ReadCardNames(config.InputCardsFile);
                Console.WriteLine($"Found {cardNames.Count} card(s) in the input file.");
                Console.WriteLine();

                if (cardNames.Count == 0)
                {
                    Console.WriteLine("No cards to process.");
                    return;
                }

                // Scan source folder
                Console.WriteLine("Scanning source folder...");
                var allFiles = _fileOperationsService.GetAllFiles(config.SourceFolder);
                Console.WriteLine($"Found {allFiles.Count} file(s) in source folder.");
                Console.WriteLine();

                // Process each card
                var processedCards = new List<CardInfo>();
                int totalFilesCopied = 0;

                Console.WriteLine("Processing cards...");
                foreach (var cardName in cardNames)
                {
                    var cardInfo = ProcessCard(cardName, config.SourceFolder, config.OutputFolder);
                    processedCards.Add(cardInfo);
                    totalFilesCopied += cardInfo.MatchingFiles.Count;

                    _reportService.DisplayCardProgress(cardInfo);
                }

                // Generate missing cards report
                var missingCards = processedCards
                    .Where(card => !card.Found)
                    .Select(card => card.Name)
                    .ToList();

                _reportService.GenerateMissingCardsReport(missingCards, config.OutputFolder, config.MissingCardsReport);

                // Download Scryfall images for missing cards
                if (missingCards.Count > 0)
                {
                    string scryfallImagesPath = Path.Combine(config.OutputFolder, ScryfallImagesFolder);
                    await _scryfallService.DownloadCardImagesAsync(missingCards, scryfallImagesPath);
                }

                _reportService.DisplaySummary(processedCards, totalFilesCopied);
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                DisplayConfigurationHelp();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                Console.WriteLine($"Details: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Searches for a single card in the source folder and copies any matching files
        /// to the output folder. Returns a <see cref="CardInfo"/> with the result.
        /// </summary>
        /// <param name="cardName">The card name to search for.</param>
        /// <param name="sourceFolder">Folder to search within.</param>
        /// <param name="outputFolder">Folder to copy found files into.</param>
        /// <returns>A <see cref="CardInfo"/> indicating whether the card was found and which files matched.</returns>
        private CardInfo ProcessCard(string cardName, string sourceFolder, string outputFolder)
        {
            var cardInfo = new CardInfo { Name = cardName };

            var matchingFiles = _fileOperationsService.SearchForCard(cardName, sourceFolder);

            if (matchingFiles.Any())
            {
                cardInfo.Found = true;
                cardInfo.MatchingFiles = matchingFiles;
                _fileOperationsService.CopyFiles(matchingFiles, outputFolder);
            }

            return cardInfo;
        }

        /// <summary>
        /// Prints a usage hint to the console explaining the required config.txt format.
        /// Called when configuration loading fails with a <see cref="FileNotFoundException"/>.
        /// </summary>
        private static void DisplayConfigurationHelp()
        {
            Console.WriteLine("Please create a config.txt file with the following format:");
            Console.WriteLine("InputCardsFile=cards.txt");
            Console.WriteLine("SourceFolder=C:\\MTG\\Cards");
            Console.WriteLine("OutputFolder=C:\\MTG\\Output");
            Console.WriteLine("MissingCardsReport=missing_cards.txt");
        }
    }
}
