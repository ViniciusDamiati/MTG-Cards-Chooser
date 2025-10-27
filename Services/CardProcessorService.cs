using CardChooser.Models;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service that orchestrates the card processing workflow.
    /// Follows the Single Responsibility Principle by delegating specific tasks to specialized services.
    /// </summary>
    public class CardProcessorService : ICardProcessorService
    {
        private readonly IConfigurationService _configurationService;
        private readonly ICardParserService _cardParserService;
        private readonly IFileOperationsService _fileOperationsService;
        private readonly IReportService _reportService;

        public CardProcessorService(
            IConfigurationService configurationService,
            ICardParserService cardParserService,
            IFileOperationsService fileOperationsService,
            IReportService reportService)
        {
            _configurationService = configurationService;
            _cardParserService = cardParserService;
            _fileOperationsService = fileOperationsService;
            _reportService = reportService;
        }

        public void ProcessCards(string configFilePath)
        {
            try
            {
                // Load and validate configuration
                var config = _configurationService.LoadConfiguration(configFilePath);
                
                if (!_configurationService.ValidateConfiguration(config))
                {
                    return;
                }

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

                // Generate reports
                var missingCards = processedCards
                    .Where(c => !c.Found)
                    .Select(c => c.Name)
                    .ToList();

                _reportService.GenerateMissingCardsReport(missingCards, config.OutputFolder, config.MissingCardsReport);
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

        private void DisplayConfigurationHelp()
        {
            Console.WriteLine("Please create a config.txt file with the following format:");
            Console.WriteLine("InputCardsFile=cards.txt");
            Console.WriteLine("SourceFolder=C:\\MTG\\Cards");
            Console.WriteLine("OutputFolder=C:\\MTG\\Output");
            Console.WriteLine("MissingCardsReport=missing_cards.txt");
        }
    }
}
