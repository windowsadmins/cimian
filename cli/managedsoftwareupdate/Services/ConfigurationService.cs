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
    private readonly string? _policyRegistryPath;
    private readonly Microsoft.Win32.RegistryKey _policyHive;

    public ConfigurationService() : this(PolicyRegistryPath)
    {
    }

    /// <summary>
    /// Reads policy overrides from <paramref name="policyRegistryPath"/> under HKLM,
    /// or skips them when it is null, so tests do not pick up the machine's policy.
    /// </summary>
    internal ConfigurationService(string? policyRegistryPath)
        : this(policyRegistryPath, Microsoft.Win32.Registry.LocalMachine)
    {
    }

    /// <summary>
    /// Reads policy overrides from <paramref name="policyRegistryPath"/> under
    /// <paramref name="policyHive"/>, so tests can supply policy without writing HKLM.
    /// </summary>
    internal ConfigurationService(string? policyRegistryPath, Microsoft.Win32.RegistryKey policyHive)
    {
        _policyRegistryPath = policyRegistryPath;
        _policyHive = policyHive;
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
    /// Loads configuration from the default path
    /// </summary>
    public CimianConfig LoadConfig()
    {
        return LoadConfig(CimianConfig.ConfigPath);
    }

    /// <summary>
    /// MDM policy overrides delivered by the CimianPrefs Intune profile
    /// (ADMX-ingested Policy CSP writing to HKLM\SOFTWARE\Policies\Cimian).
    /// Policy wins over Config.yaml so fleet-wide settings can ship as an
    /// Intune configuration profile instead of per-device file edits.
    /// </summary>
    private const string PolicyRegistryPath = @"SOFTWARE\Policies\Cimian";

    private CimianConfig ApplyPolicyOverrides(CimianConfig config)
    {
        if (_policyRegistryPath is null)
        {
            return config;
        }

        try
        {
            using var key = _policyHive.OpenSubKey(_policyRegistryPath, false);
            if (key == null)
            {
                return config;
            }

            if (key.GetValue("SoftwareRepoURL") is string repoUrl && !string.IsNullOrWhiteSpace(repoUrl))
            {
                config.SoftwareRepoURL = repoUrl.Trim();
            }

            if (key.GetValue("ClientIdentifier") is string clientId && !string.IsNullOrWhiteSpace(clientId))
            {
                config.ClientIdentifier = clientId.Trim();
            }

            // ADMX decimal elements arrive as REG_DWORD; the Policy CSP has also
            // been observed delivering numerics as strings, so accept both.
            var timeoutRaw = key.GetValue("InstallerTimeout");
            var timeout = timeoutRaw switch
            {
                int i => i,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => 0
            };
            if (timeout >= 60)
            {
                config.InstallerTimeout = timeout;
            }

            // Cache retention is the only lever against superseded multi-gigabyte
            // payloads filling small system drives; let policy set it fleet-wide.
            var retentionRaw = key.GetValue("CacheRetentionDays");
            var retention = retentionRaw switch
            {
                int i => i,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => int.MinValue
            };
            if (retention != int.MinValue && retention >= 0)
            {
                config.CacheRetentionDays = retention;
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"Policy override read failed (using Config.yaml values): {ex.Message}");
        }

        return config;
    }

    /// <summary>
    /// Loads configuration from a specific path
    /// </summary>
    public CimianConfig LoadConfig(string path) =>
        LoadConfig(path, requireTrusted: ManagedInstallsSecurity.IsUnder(path, CimianPaths.ManagedInstallsRoot));

    /// <summary>
    /// Loads the ManagedInstalls configuration or stops the run. It never falls back to
    /// defaults: without MDM policy they point at an example repository, and a user who
    /// could make the load fail (by holding the file open, say) could otherwise steer
    /// every run there.
    /// </summary>
    private CimianConfig LoadTrustedConfig(string path)
    {
        // Read first, so "held open" and "untrusted" are reported as what they are.
        string yaml;
        try
        {
            yaml = File.ReadAllText(path);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ConfigurationRefusedException(ManagedInstallsSecurity.ConfigNeedsElevation);
        }
        catch (IOException ex)
        {
            throw new ConfigurationRefusedException($"Could not read configuration {path}: {ex.Message}");
        }

        if (!ManagedInstallsSecurity.IsSafeToExecute(path, out var reason))
        {
            throw new ConfigurationRefusedException($"Refusing configuration {path}: {reason}");
        }

        try
        {
            var config = _deserializer.Deserialize<CimianConfig>(yaml);
            return ConstrainCachePath(ApplyPolicyOverrides(NormalizePaths(config ?? GetDefaultConfig())));
        }
        catch (ConfigurationRefusedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConfigurationRefusedException($"Configuration {path} could not be parsed: {ex.Message}");
        }
    }

    /// <summary>
    /// The ManagedInstalls Config.yaml is missing: never written, or moved to quarantine    /// by the permissions repair because someone other than an administrator owned it. The
    /// run goes on only when MDM policy names the repository itself; the built-in defaults
    /// point at an example repository and are never used in its place.
    /// </summary>
    private CimianConfig LoadWithoutTrustedConfig(string path)
    {
        var config = ApplyPolicyOverrides(GetDefaultConfig());
        if (PolicyNamesRepository())
        {
            ConsoleLogger.Warn($"{path} is missing; running on the settings MDM policy supplies");
            return config;
        }

        throw new ConfigurationRefusedException(
            $"{path} is missing, and MDM policy does not set SoftwareRepoURL. Not running on the built-in defaults. " +
            $"If the permissions repair moved it, it is in {CimianPaths.QuarantineDir}; restore it with SYSTEM or Administrators as its owner.");
    }

    private bool PolicyNamesRepository()
    {
        if (_policyRegistryPath is null) return false;
        try
        {
            using var key = _policyHive.OpenSubKey(_policyRegistryPath, false);
            return key?.GetValue("SoftwareRepoURL") is string repoUrl && !string.IsNullOrWhiteSpace(repoUrl);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// SYSTEM installs what it downloads into CachePath, so it is checked here and nowhere
    /// changed. Inside ManagedInstalls only Cache\ or a folder in it is a cache. Outside, a
    /// folder is used only when it already exists, is not a system or shared folder, and is
    /// protected and closed to everyone but SYSTEM and Administrators up to the volume root.
    /// Anything else falls back to Cache\ with a warning.
    /// </summary>
    internal static CimianConfig ConstrainCachePath(CimianConfig config)
    {
        var cachePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.CachePath));
        var isDefault = string.Equals(cachePath, CimianPaths.CacheDir, StringComparison.OrdinalIgnoreCase);
        if (isDefault || ManagedInstallsSecurity.IsUnder(cachePath, CimianPaths.CacheDir))
        {
            return config;
        }

        string? reason;
        if (string.Equals(cachePath, CimianPaths.ManagedInstallsRoot, StringComparison.OrdinalIgnoreCase)
            || ManagedInstallsSecurity.IsUnder(cachePath, CimianPaths.ManagedInstallsRoot))
        {
            reason = "inside ManagedInstalls only Cache\\ is a cache";
        }
        else
        {
            ManagedInstallsSecurity.IsUsableExternalCache(cachePath, out reason);
        }

        if (reason != null)
        {
            ConsoleLogger.Warn($"Not using CachePath {config.CachePath}: {reason}. Using {CimianPaths.CacheDir} instead.");
            config.CachePath = CimianPaths.CacheDir;
        }
        return config;
    }

    internal CimianConfig LoadConfig(string path, bool requireTrusted)
    {
        if (!File.Exists(path))
        {
            return requireTrusted ? LoadWithoutTrustedConfig(path) : ApplyPolicyOverrides(GetDefaultConfig());
        }

        if (requireTrusted)
        {
            return LoadTrustedConfig(path);
        }

        try
        {
            var yaml = File.ReadAllText(path);
            var config = _deserializer.Deserialize<CimianConfig>(yaml);
            return ApplyPolicyOverrides(NormalizePaths(config ?? GetDefaultConfig()));
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Failed to load configuration from {path}: {ex.Message}");
            return ApplyPolicyOverrides(GetDefaultConfig());
        }
    }

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

        if (config.RepoFormatProblem is { } repoFormatProblem)
        {
            errors.Add(repoFormatProblem);
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

/// <summary>
/// The ManagedInstalls configuration could not be used. The run stops rather than carry on
/// with defaults.
/// </summary>
public sealed class ConfigurationRefusedException(string message) : Exception(message);
