using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for loading and managing Cimian configuration
/// Migrated from Go pkg/config
/// </summary>
public class ConfigurationService
{
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;

    public ConfigurationService()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    /// <summary>
    /// Loads configuration from the default path
    /// </summary>
    public CimianConfig LoadConfig()
    {
        return LoadConfig(CimianConfig.ConfigPath);
    }

    /// <summary>
    /// Loads configuration from a specific path
    /// </summary>
    public CimianConfig LoadConfig(string path)
    {
        if (!File.Exists(path))
        {
            return GetDefaultConfig();
        }

        try
        {
            var yaml = File.ReadAllText(path);
            var config = _deserializer.Deserialize<CimianConfig>(yaml);
            return config ?? GetDefaultConfig();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Failed to load configuration from {path}: {ex.Message}");
            return GetDefaultConfig();
        }
    }

    /// <summary>
    /// Saves configuration to the default path
    /// </summary>
    public void SaveConfig(CimianConfig config)
    {
        SaveConfig(config, CimianConfig.ConfigPath);
    }

    /// <summary>
    /// Saves configuration to a specific path
    /// </summary>
    public void SaveConfig(CimianConfig config, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var yaml = _serializer.Serialize(config);
        File.WriteAllText(path, yaml);
    }

    /// <summary>
    /// Returns default configuration
    /// </summary>
    public CimianConfig GetDefaultConfig()
    {
        return new CimianConfig
        {
            SoftwareRepoURL = "https://your-repo.example.com",
            ClientIdentifier = Environment.MachineName,
            CachePath = CimianPaths.CacheDir,
            CatalogsPath = CimianPaths.CatalogsDir,
            ManifestsPath = CimianPaths.ManifestsDir,
            LogLevel = "INFO",
            InstallerTimeout = 900,
            Catalogs = new List<string> { "Production" }
        };
    }

    /// <summary>
    /// Validates the configuration
    /// </summary>
    public List<string> ValidateConfig(CimianConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.SoftwareRepoURL))
        {
            errors.Add("SoftwareRepoURL is required");
        }
        else if (!Uri.TryCreate(config.SoftwareRepoURL, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            errors.Add("SoftwareRepoURL must be a valid HTTP/HTTPS URL");
        }

        if (string.IsNullOrWhiteSpace(config.CachePath))
        {
            errors.Add("CachePath is required");
        }

        if (config.InstallerTimeout < 60)
        {
            errors.Add("InstallerTimeout must be at least 60 seconds");
        }

        return errors;
    }

    /// <summary>
    /// Ensures all required directories exist
    /// </summary>
    public void EnsureDirectoriesExist(CimianConfig config)
    {
        var directories = new[]
        {
            config.CachePath,
            config.CatalogsPath,
            config.ManifestsPath,
            CimianPaths.LogsDir,
            CimianPaths.ReportsDir
        };

        foreach (var dir in directories)
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }
}
