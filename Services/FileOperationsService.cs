using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service for file system operations.
    /// </summary>
    public class FileOperationsService : IFileOperationsService
    {
        private List<string>? _cachedFiles;
        private string? _cachedDirectory;

        public List<string> GetAllFiles(string directory)
        {
            if (_cachedDirectory == directory && _cachedFiles != null)
            {
                return _cachedFiles;
            }

            _cachedFiles = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories).ToList();
            _cachedDirectory = directory;
            return _cachedFiles;
        }

        public List<string> SearchForCard(string cardName, string sourceFolder)
        {
            var allFiles = GetAllFiles(sourceFolder);
            
            var matchingFiles = allFiles
                .Where(file => 
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    string extractedCardName = ExtractCardNameFromFileName(fileName);
                    return extractedCardName.Equals(cardName, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            return matchingFiles;
        }

        /// <summary>
        /// Extracts the card name from a filename by removing metadata in parentheses, brackets, and braces.
        /// Example: "Rhystic Study (Normal) [JMP] {169}" -> "Rhystic Study"
        /// </summary>
        /// <param name="fileName">The filename to extract from.</param>
        /// <returns>The extracted card name.</returns>
        private string ExtractCardNameFromFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            // Remove content in parentheses: (...)
            int openParen = fileName.IndexOf('(');
            if (openParen >= 0)
            {
                fileName = fileName.Substring(0, openParen);
            }

            // Remove content in square brackets: [...]
            int openBracket = fileName.IndexOf('[');
            if (openBracket >= 0)
            {
                fileName = fileName.Substring(0, openBracket);
            }

            // Remove content in curly braces: {...}
            int openBrace = fileName.IndexOf('{');
            if (openBrace >= 0)
            {
                fileName = fileName.Substring(0, openBrace);
            }

            // Trim any trailing whitespace
            return fileName.Trim();
        }

        public int CopyFiles(List<string> sourceFiles, string outputFolder)
        {
            int copiedCount = 0;

            foreach (var sourceFile in sourceFiles)
            {
                string fileName = Path.GetFileName(sourceFile);
                string destFile = Path.Combine(outputFolder, fileName);

                // Overwrite existing files
                File.Copy(sourceFile, destFile, true);
                Console.WriteLine($"  Copied: {fileName}");
                copiedCount++;
            }

            return copiedCount;
        }

        public void EnsureOutputFolderExists(string outputFolder)
        {
            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
                Console.WriteLine($"Created output folder: {outputFolder}");
            }
        }
    }
}
