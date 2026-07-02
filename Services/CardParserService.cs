using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service for parsing card names from different formats.
    /// </summary>
    public class CardParserService : ICardParserService
    {
        public string ExtractCardName(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;

            // Remove leading quantity (e.g., "1 ", "2 ", etc.) only if the first token is a number
            int firstSpaceIndex = line.IndexOf(' ');
            if (firstSpaceIndex == -1)
                return line; // No space found, return the whole line

            string firstToken = line.Substring(0, firstSpaceIndex);
            string withoutQuantity = int.TryParse(firstToken, out _)
                ? line.Substring(firstSpaceIndex + 1).Trim()
                : line.Trim();

            // Find the first opening parenthesis to remove set code and card number
            int parenthesisIndex = withoutQuantity.IndexOf('(');
            if (parenthesisIndex == -1)
                return withoutQuantity; // No parenthesis found, return what we have

            // Extract everything before the parenthesis (the card name)
            string cardName = withoutQuantity.Substring(0, parenthesisIndex).Trim();
            return cardName;
        }

        public List<string> ReadCardNames(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Card list file '{filePath}' not found.");
            }

            var cardNames = File.ReadAllLines(filePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => ExtractCardName(line.Trim()))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            return cardNames;
        }
    }
}
