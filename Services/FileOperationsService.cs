using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service for all file-system operations: scanning, searching, and copying card image files.
    /// Caches the directory scan result so that repeated lookups do not re-read the disk.
    /// </summary>
    public class FileOperationsService : IFileOperationsService
    {
        private List<string>? _cachedFiles;
        private string? _cachedDirectory;

        /// <summary>
        /// Returns all file paths found under the given directory (recursive).
        /// Results are cached per directory to avoid redundant disk reads within a single run.
        /// </summary>
        /// <param name="directory">Root directory to scan.</param>
        /// <returns>A flat list of absolute file paths.</returns>
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

        /// <summary>
        /// Searches the source folder for files whose base name (after stripping metadata tokens)
        /// matches the given card name using a case-insensitive comparison.
        /// </summary>
        /// <param name="cardName">The card name to search for.</param>
        /// <param name="sourceFolder">The root folder to search within.</param>
        /// <returns>A list of full file paths that match the card name.</returns>
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
        /// Example: "Rhystic Study (Normal) [JMP] {169}" → "Rhystic Study"
        /// </summary>
        /// <param name="fileName">The filename (without extension) to extract from.</param>
        /// <returns>The extracted card name, trimmed of whitespace.</returns>
        private static string ExtractCardNameFromFileName(string fileName)
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

            return fileName.Trim();
        }

        /// <summary>
        /// Copies the given source files into the output folder, overwriting any existing files.
        /// Logs each copied file to the console.
        /// </summary>
        /// <param name="sourceFiles">List of absolute source file paths to copy.</param>
        /// <param name="outputFolder">Destination folder path.</param>
        /// <returns>The number of files successfully copied.</returns>
        public int CopyFiles(List<string> sourceFiles, string outputFolder)
        {
            int copiedCount = 0;

            foreach (var sourceFile in sourceFiles)
            {
                string fileName = Path.GetFileName(sourceFile);
                string destFile = Path.Combine(outputFolder, fileName);

                File.Copy(sourceFile, destFile, true);
                Console.WriteLine($"  Copied: {fileName}");
                copiedCount++;
            }

            return copiedCount;
        }

        /// <summary>
        /// Ensures the output folder exists, creating it (and any missing parent directories)
        /// if it does not. Logs folder creation to the console.
        /// </summary>
        /// <param name="outputFolder">Path of the folder to ensure exists.</param>
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
