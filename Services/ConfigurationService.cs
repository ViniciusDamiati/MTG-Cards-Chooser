using CardChooser.Models;
using CardChooser.Services.Interfaces;

namespace CardChooser.Services
{
    /// <summary>
    /// Service for loading and validating configuration.
    /// </summary>
    public class ConfigurationService : IConfigurationService
    {
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

            if (configData.ContainsKey("InputCardsFile"))
            {
                config.InputCardsFile = configData["InputCardsFile"];
            }
            else
            {
                config.InputCardsFile = "cards.txt";
            }

            if (configData.ContainsKey("SourceFolder"))
            {
                config.SourceFolder = configData["SourceFolder"];
            }
            else
            {
                config.SourceFolder = string.Empty;
            }

            if (configData.ContainsKey("OutputFolder"))
            {
                config.OutputFolder = configData["OutputFolder"];
            }
            else
            {
                config.OutputFolder = string.Empty;
            }

            if (configData.ContainsKey("MissingCardsReport"))
            {
                config.MissingCardsReport = configData["MissingCardsReport"];
            }
            else
            {
                config.MissingCardsReport = "missing_cards.txt";
            }

            return config;
        }

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
