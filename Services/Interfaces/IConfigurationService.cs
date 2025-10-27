using CardChooser.Models;

namespace CardChooser.Services.Interfaces
{
    /// <summary>
    /// Interface for configuration service.
    /// Single Responsibility: Loading and validating configuration.
    /// </summary>
    public interface IConfigurationService
    {
        /// <summary>
        /// Loads configuration from a file.
        /// </summary>
        /// <param name="configFilePath">Path to the configuration file.</param>
        /// <returns>Application configuration.</returns>
        AppConfiguration LoadConfiguration(string configFilePath);

        /// <summary>
        /// Validates the configuration.
        /// </summary>
        /// <param name="config">Configuration to validate.</param>
        /// <returns>True if valid, false otherwise.</returns>
        bool ValidateConfiguration(AppConfiguration config);
    }
}
