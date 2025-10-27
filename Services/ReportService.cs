using CardChooser.Models;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service for generating and displaying reports.
    /// </summary>
    public class ReportService : IReportService
    {
        public void DisplayHeader(AppConfiguration config)
        {
            Console.WriteLine("=== MTG Card Chooser ===");
            Console.WriteLine($"Input Cards File: {config.InputCardsFile}");
            Console.WriteLine($"Source Folder: {config.SourceFolder}");
            Console.WriteLine($"Output Folder: {config.OutputFolder}");
            Console.WriteLine();
        }

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

        public void DisplaySummary(List<CardInfo> cards, int totalFilesCopied)
        {
            var foundCards = cards.Where(c => c.Found).ToList();
            var missingCards = cards.Where(c => !c.Found).Select(c => c.Name).ToList();

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
