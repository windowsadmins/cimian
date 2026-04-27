using Cimian.CLI.Cimiimport.Models;
using Cimian.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// Manages Cimian configuration.
/// </summary>
public class ConfigurationService
{
    public static readonly string ConfigPath = CimianPaths.ConfigYaml;

    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;

    public ConfigurationService()
    {
        // Use NullNamingConvention to match Go's PascalCase YAML keys (yaml:"RepoPath" etc.)
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    /// <summary>
    /// Loads or creates the configuration.
    /// </summary>
    public ImportConfiguration LoadOrCreateConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var yaml = File.ReadAllText(ConfigPath);
                var config = _deserializer.Deserialize<ImportConfiguration>(yaml);
                return config ?? GetDefaultConfig();
            }
        }
        catch
        {
            // Return defaults on error
        }

        return GetDefaultConfig();
    }

    /// <summary>
    /// Saves the configuration while preserving existing settings not managed by cimiimport.
    /// </summary>
    public void SaveConfig(ImportConfiguration config)
    {
        var configDir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
        {
            Directory.CreateDirectory(configDir);
        }

        // Load existing config to preserve other settings
        Dictionary<string, object>? existingConfig = null;
        if (File.Exists(ConfigPath))
        {
            try
            {
                var existingYaml = File.ReadAllText(ConfigPath);
                var rawDeserializer = new DeserializerBuilder()
                    .WithNamingConvention(NullNamingConvention.Instance)
                    .Build();
                existingConfig = rawDeserializer.Deserialize<Dictionary<string, object>>(existingYaml);
            }
            catch
            {
                // If we can't parse existing config, we'll create a new one
            }
        }

        existingConfig ??= new Dictionary<string, object>();

        // Update only the fields managed by cimiimport (PascalCase keys matching Go config)
        existingConfig["RepoPath"] = config.RepoPath;
        existingConfig["CloudProvider"] = config.CloudProvider;
        existingConfig["CloudBucket"] = config.CloudBucket;
        existingConfig["DefaultCatalog"] = config.DefaultCatalog;
        existingConfig["DefaultArch"] = config.DefaultArch;
        existingConfig["OpenImportedYaml"] = config.OpenImportedYaml;

        var yaml = _serializer.Serialize(existingConfig);
        File.WriteAllText(ConfigPath, yaml);
    }

    /// <summary>
    /// Gets default configuration. RepoPath is resolved at runtime from the
    /// surrounding git checkout — never hardcoded to a user-profile path.
    /// </summary>
    public ImportConfiguration GetDefaultConfig()
    {
        return new ImportConfiguration
        {
            RepoPath = RepoResolver.ResolveDefaultRepoPath() ?? string.Empty,
            CloudProvider = "none",
            CloudBucket = "",
            DefaultCatalog = "Development",
            DefaultArch = "x64,arm64",
            OpenImportedYaml = true
        };
    }

    /// <summary>
    /// Runs interactive configuration.
    /// </summary>
    public void ConfigureInteractive(ImportConfiguration config)
    {
        var defaults = GetDefaultConfig();

        var repoPrompt = !string.IsNullOrEmpty(config.RepoPath) ? config.RepoPath
                       : !string.IsNullOrEmpty(defaults.RepoPath) ? defaults.RepoPath
                       : "(no Cimian deployment detected — please enter)";
        Console.Write($"Enter Repo Path [{repoPrompt}]: ");
        var input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input))
        {
            config.RepoPath = input;
        }
        else if (string.IsNullOrEmpty(config.RepoPath))
        {
            config.RepoPath = defaults.RepoPath;
        }

        Console.Write($"Enter Cloud Provider (aws/azure/none) [{config.CloudProvider ?? defaults.CloudProvider}]: ");
        input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input))
        {
            config.CloudProvider = input;
        }
        else if (string.IsNullOrEmpty(config.CloudProvider))
        {
            config.CloudProvider = defaults.CloudProvider;
        }

        if (config.CloudProvider != "none")
        {
            Console.Write("Enter Cloud Bucket: ");
            config.CloudBucket = Console.ReadLine()?.Trim() ?? "";
        }

        Console.Write($"Enter Default Catalog [{config.DefaultCatalog ?? defaults.DefaultCatalog}]: ");
        input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input))
        {
            config.DefaultCatalog = input;
        }
        else if (string.IsNullOrEmpty(config.DefaultCatalog))
        {
            config.DefaultCatalog = defaults.DefaultCatalog;
        }

        Console.Write($"Enter Default Architecture [{config.DefaultArch ?? defaults.DefaultArch}]: ");
        input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input))
        {
            config.DefaultArch = input;
        }
        else if (string.IsNullOrEmpty(config.DefaultArch))
        {
            config.DefaultArch = defaults.DefaultArch;
        }

        Console.Write($"Open imported YAML after creation? [true/false] ({config.OpenImportedYaml}): ");
        input = Console.ReadLine()?.Trim()?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(input))
        {
            config.OpenImportedYaml = input == "true";
        }

        SaveConfig(config);
        Console.WriteLine("✅ Configuration saved successfully.");
    }

    /// <summary>
    /// Runs non-interactive configuration with defaults.
    /// </summary>
    public void ConfigureNonInteractive(ImportConfiguration config)
    {
        var defaults = GetDefaultConfig();

        if (string.IsNullOrEmpty(config.RepoPath))
            config.RepoPath = defaults.RepoPath;
        if (string.IsNullOrEmpty(config.CloudProvider))
            config.CloudProvider = defaults.CloudProvider;
        if (string.IsNullOrEmpty(config.DefaultCatalog))
            config.DefaultCatalog = defaults.DefaultCatalog;
        if (string.IsNullOrEmpty(config.DefaultArch))
            config.DefaultArch = defaults.DefaultArch;

        SaveConfig(config);
        Console.WriteLine("✅ Configuration saved (non-interactive).");
    }
}
