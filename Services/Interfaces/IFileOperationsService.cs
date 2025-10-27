using CardChooser.Models;

namespace CardChooser.Services.Interfaces
{
    /// <summary>
    /// Interface for file operations service.
    /// Single Responsibility: File system operations (searching, copying).
    /// </summary>
    public interface IFileOperationsService
    {
        /// <summary>
        /// Searches for files matching the card name in the source folder.
        /// </summary>
        /// <param name="cardName">The card name to search for.</param>
        /// <param name="sourceFolder">The folder to search in.</param>
        /// <returns>List of matching file paths.</returns>
        List<string> SearchForCard(string cardName, string sourceFolder);

        /// <summary>
        /// Copies files to the output folder.
        /// </summary>
        /// <param name="sourceFiles">List of source file paths.</param>
        /// <param name="outputFolder">The destination folder.</param>
        /// <returns>Number of files copied.</returns>
        int CopyFiles(List<string> sourceFiles, string outputFolder);

        /// <summary>
        /// Gets all files from a directory recursively.
        /// </summary>
        /// <param name="directory">The directory to scan.</param>
        /// <returns>List of all file paths.</returns>
        List<string> GetAllFiles(string directory);

        /// <summary>
        /// Ensures the output folder exists, creating it if necessary.
        /// </summary>
        /// <param name="outputFolder">The folder path.</param>
        void EnsureOutputFolderExists(string outputFolder);
    }
}
