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
    private readonly Func<IReadOnlyDictionary<string, object>?> _readPolicy;
    private readonly Func<IReadOnlyDictionary<string, object>?> _readMachineSettings;
    private readonly bool _requireTrustedFile;

    private static readonly Func<IReadOnlyDictionary<string, object>?> NoValues = () => null;

    public ConfigurationService() : this(SettingsLayers.PolicyRegistryPath)
    {
    }

    /// <summary>
    /// Reads policy from <paramref name="policyRegistryPath"/> under HKLM, plus the machine
    /// settings key and the Config.yaml permission check. Null skips all three, so tests
    /// read only the file they write and do not pick up the machine's policy.
    /// </summary>
    internal ConfigurationService(string? policyRegistryPath)
        : this(
            policyRegistryPath is null ? NoValues : () => SettingsLayers.ReadMachineKey(policyRegistryPath),
            policyRegistryPath is null ? NoValues : () => SettingsLayers.ReadMachineKey(SettingsLayers.MachineSettingsRegistryPath),
            requireTrustedFile: policyRegistryPath is not null)
    {
    }

    /// <summary>
    /// Takes the policy and machine settings layers as readers, so tests can supply them
    /// without writing HKLM.
    /// </summary>
    internal ConfigurationService(
        Func<IReadOnlyDictionary<string, object>?> readPolicy,
        Func<IReadOnlyDictionary<string, object>?> readMachineSettings,
        bool requireTrustedFile)
    {
        _readPolicy = readPolicy;
        _readMachineSettings = readMachineSettings;
        _requireTrustedFile = requireTrustedFile;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .WithTypeConverter(new HeaderListConverter())
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .WithTypeConverter(new HeaderListConverter())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    /// <summary>
    /// Where each setting set by a registry layer in the last load came from:
    /// <see cref="SettingsLayers.PolicySource"/> or <see cref="SettingsLayers.MachineSettingsSource"/>.
    /// A setting that is absent took its value from Config.yaml or the default.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastLoadSources { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads configuration from the default path
    /// </summary>
    public CimianConfig LoadConfig()
    {
        return LoadConfig(CimianConfig.ConfigPath);
    }

    /// <summary>
    /// Loads configuration from a specific path. Precedence, highest first: policy
    /// (HKLM\SOFTWARE\Policies\Cimian, delivered by MDM or Group Policy), machine settings
    /// (HKLM\SOFTWARE\Cimian\Settings), the file at <paramref name="path"/>, then the
    /// defaults. Command-line flags are applied over the result by the caller.
    /// </summary>
    public CimianConfig LoadConfig(string path)
    {
        var config = NormalizePaths(ReadConfigFile(path) ?? GetDefaultConfig());
        return NormalizePaths(ApplyRegistryLayers(config));
    }

    /// <summary>
    /// The file's settings, or null to use the defaults: when it is missing, cannot be
    /// parsed, or could have been written by someone other than SYSTEM or an administrator.
    /// </summary>
    private CimianConfig? ReadConfigFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        if (_requireTrustedFile && !ConfigFileGuard.IsTrusted(path, out var reason))
        {
            ConsoleLogger.Warn($"Ignoring {path}: {reason}. Only SYSTEM and Administrators may be able to change it; " +
                               "fix its permissions or set these values by policy.");
            return null;
        }

        try
        {
            var yaml = File.ReadAllText(path);
            return _deserializer.Deserialize<CimianConfig>(yaml);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Failed to load configuration from {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Applies the machine settings key and then policy over <paramref name="config"/>, so
    /// policy wins. Fleet-wide settings can ship as an MDM configuration profile instead
    /// of per-device file edits.
    /// </summary>
    private CimianConfig ApplyRegistryLayers(CimianConfig config)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, read) in new[]
                 {
                     (SettingsLayers.MachineSettingsSource, _readMachineSettings),
                     (SettingsLayers.PolicySource, _readPolicy)
                 })
        {
            var applied = SettingsLayers.Apply(config, read(), source, ValidateSetting, ConsoleLogger.Warn);
            foreach (var name in applied)
            {
                sources[name] = source;
            }
        }
        LastLoadSources = sources;
        return config;
    }

    /// <summary>
    /// Applies -v/-vvv for this run. A command-line flag outranks every other source, so
    /// this runs after <see cref="LoadConfig(string)"/> and again after any reload.
    /// </summary>
    public static void ApplyCommandLineVerbosity(CimianConfig config, int verbosity)
    {
        if (verbosity >= 1)
        {
            config.Verbose = true;
            config.LogLevel = "INFO";
        }

        if (verbosity >= 3)
        {
            config.Debug = true;
            config.LogLevel = "DEBUG";
        }
    }

    /// <summary>
    /// Range checks for values from the registry layers; a rejected value leaves the
    /// lower layer's value in place.
    /// </summary>
    internal static string? ValidateSetting(string name, object value) => (name, value) switch
    {
        (nameof(CimianConfig.InstallerTimeout), int seconds) when seconds < 60 => "must be at least 60 seconds",
        (_, int number) when number < 0 => "must not be negative",
        _ => null
    };

    /// <summary>
    /// An explicit empty string in Config.yaml (older bootstraps wrote
    /// CachePath: "") deserialises over the CimianPaths default, and every
    /// Path.Combine on it then yields a relative path resolved against the
    /// process working directory - Program Files\Cimian when launched by the
    /// watcher service. Downloads landed there, outside the retention sweep,
    /// and filled system drives. Blank means "default", never "here".
    /// </summary>
    private static CimianConfig NormalizePaths(CimianConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.CachePath))
        {
            config.CachePath = CimianPaths.CacheDir;
        }
        if (string.IsNullOrWhiteSpace(config.CatalogsPath))
        {
            config.CatalogsPath = CimianPaths.CatalogsDir;
        }
        if (string.IsNullOrWhiteSpace(config.ManifestsPath))
        {
            config.ManifestsPath = CimianPaths.ManifestsDir;
        }
        return config;
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

        // A file written here inherits ProgramData's ACL; give it the one the next load requires.
        if (_requireTrustedFile && ConfigFileGuard.Lock(path) is { } failure)
        {
            ConsoleLogger.Warn(failure);
        }
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
        // Before creating anything: a device provisioned before the lowercase convention
        // still has ManagedInstalls\Logs, and every path built from CimianPaths resolves
        // onto it because NTFS is case-insensitive. Renaming it here means the reported
        // path matches the convention from the next session on. Best effort by design.
        foreach (var conventionDir in CimianPaths.ConventionDirs)
        {
            CimianPaths.NormalizeDirectoryCasing(conventionDir);
        }

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
