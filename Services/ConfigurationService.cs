using CardChooser.Models;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service for loading and validating application configuration from a key=value text file.
    /// </summary>
    public class ConfigurationService : IConfigurationService
    {
        /// <summary>
        /// Loads the application configuration from the specified file.
        /// The file must use a simple "Key=Value" format, one entry per line.
        /// Lines starting with '#' are treated as comments and are ignored.
        /// </summary>
        /// <param name="configFilePath">Path to the configuration file.</param>
        /// <returns>A populated <see cref="AppConfiguration"/> instance.</returns>
        /// <exception cref="FileNotFoundException">Thrown when the configuration file does not exist.</exception>
        public AppConfiguration LoadConfiguration(string configFilePath)
        {
            if (!File.Exists(configFilePath))
            {
                throw new FileNotFoundException($"Configuration file '{configFilePath}' not found.");
            }

            var config = new AppConfiguration();
            var configData = new Dictionary<string, string>();

            foreach (var line in File.ReadAllLines(configFilePath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                    continue;

                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                {
                    configData[parts[0].Trim()] = parts[1].Trim();
                }
            }

            config.InputCardsFile = configData.ContainsKey("InputCardsFile")
                ? configData["InputCardsFile"]
                : "cards.txt";

            config.SourceFolder = configData.ContainsKey("SourceFolder")
                ? configData["SourceFolder"]
                : string.Empty;

            config.OutputFolder = configData.ContainsKey("OutputFolder")
                ? configData["OutputFolder"]
                : string.Empty;

            config.MissingCardsReport = configData.ContainsKey("MissingCardsReport")
                ? configData["MissingCardsReport"]
                : "missing_cards.txt";

            return config;
        }

        /// <summary>
        /// Validates that the configuration contains all required values and that referenced paths exist.
        /// Prints a descriptive error message to the console for each validation failure.
        /// </summary>
        /// <param name="config">The configuration to validate.</param>
        /// <returns><c>true</c> if the configuration is valid; otherwise <c>false</c>.</returns>
        public bool ValidateConfiguration(AppConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.SourceFolder))
            {
                Console.WriteLine("ERROR: Source folder not configured in config.txt");
                return false;
            }

            if (string.IsNullOrWhiteSpace(config.OutputFolder))
            {
                Console.WriteLine("ERROR: Output folder not configured in config.txt");
                return false;
            }

            if (!File.Exists(config.InputCardsFile))
            {
                Console.WriteLine($"ERROR: Input file '{config.InputCardsFile}' not found.");
                Console.WriteLine("Please create a file with the list of card names.");
                return false;
            }

            if (!Directory.Exists(config.SourceFolder))
            {
                Console.WriteLine($"ERROR: Source folder '{config.SourceFolder}' does not exist.");
                return false;
            }

            return true;
        }
    }
}
