using CardChooser.Models;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service for generating console output reports and saving the missing-cards file.
    /// Single Responsibility: all user-facing reporting is centralised here.
    /// </summary>
    public class ReportService : IReportService
    {
        /// <summary>
        /// Writes the application header and current configuration settings to the console.
        /// </summary>
        /// <param name="config">The loaded application configuration.</param>
        public void DisplayHeader(AppConfiguration config)
        {
            Console.WriteLine("=== MTG Card Chooser ===");
            Console.WriteLine($"Input Cards File: {config.InputCardsFile}");
            Console.WriteLine($"Source Folder: {config.SourceFolder}");
            Console.WriteLine($"Output Folder: {config.OutputFolder}");
            Console.WriteLine();
        }

        /// <summary>
        /// Writes a single line to the console reporting whether a card was found or is missing.
        /// </summary>
        /// <param name="cardInfo">The result of processing a single card.</param>
        public void DisplayCardProgress(CardInfo cardInfo)
        {
            Console.Write($"Searching for '{cardInfo.Name}'... ");

            if (cardInfo.Found)
            {
                Console.WriteLine($"FOUND ({cardInfo.MatchingFiles.Count} file(s))");
            }
            else
            {
                Console.WriteLine("MISSING");
            }
        }

        /// <summary>
        /// Writes a final summary to the console showing totals for found cards, missing cards,
        /// and files copied. Lists all missing card names if any are present.
        /// </summary>
        /// <param name="cards">The complete list of processed card results.</param>
        /// <param name="totalFilesCopied">Total number of files copied to the output folder.</param>
        public void DisplaySummary(List<CardInfo> cards, int totalFilesCopied)
        {
            var foundCards = cards.Where(card => card.Found).ToList();
            var missingCards = cards.Where(card => !card.Found).Select(card => card.Name).ToList();

            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"Total cards in list: {cards.Count}");
            Console.WriteLine($"Cards found: {foundCards.Count}");
            Console.WriteLine($"Cards missing: {missingCards.Count}");
            Console.WriteLine($"Files copied: {totalFilesCopied}");
            Console.WriteLine();

            if (missingCards.Any())
            {
                Console.WriteLine("Missing cards:");
                foreach (var card in missingCards)
                {
                    Console.WriteLine($"  - {card}");
                }
            }
            else
            {
                Console.WriteLine("All cards were found!");
            }

            Console.WriteLine();
            Console.WriteLine("Process completed successfully!");
        }

        /// <summary>
        /// Saves the list of missing card names to a text file inside the output folder.
        /// Does nothing if the list is empty.
        /// </summary>
        /// <param name="missingCards">Names of cards that could not be found in the source folder.</param>
        /// <param name="outputFolder">Destination folder where the report file will be written.</param>
        /// <param name="reportFileName">Name of the report file (e.g. "missing_cards.txt").</param>
        public void GenerateMissingCardsReport(List<string> missingCards, string outputFolder, string reportFileName)
        {
            if (!missingCards.Any())
                return;

            string reportPath = Path.Combine(outputFolder, reportFileName);
            Console.WriteLine($"Creating missing cards report: {reportPath}");

            var reportLines = new List<string> { "Missing Cards:" };
            reportLines.AddRange(missingCards);

            File.WriteAllLines(reportPath, reportLines);
            Console.WriteLine($"Missing cards list saved to: {reportPath}");
            Console.WriteLine();
        }
    }
}
