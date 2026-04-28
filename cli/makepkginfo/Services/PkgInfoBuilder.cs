using Cimian.CLI.Makepkginfo.Models;
using Cimian.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Makepkginfo.Services;

/// <summary>
/// Service for creating and managing pkgsinfo files
/// Migrated from Go: cmd/makepkginfo/main.go
/// </summary>
public class PkgInfoBuilder
{
    private readonly MetadataExtractor _extractor;

    public PkgInfoBuilder()
    {
        _extractor = new MetadataExtractor();
    }

    public PkgInfoBuilder(MetadataExtractor extractor)
    {
        _extractor = extractor;
    }

    /// <summary>
    /// Configuration for loading repository settings
    /// </summary>
    public static readonly string DefaultConfigPath = CimianPaths.ConfigYaml;

    /// <summary>
    /// Loads Cimian configuration from the config file
    /// </summary>
    public CimianConfig LoadConfig(string configPath)
    {
        var yaml = File.ReadAllText(configPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<CimianConfig>(yaml);
    }

    /// <summary>
    /// Creates a new minimal pkgsinfo stub file
    /// </summary>
    public void CreateNewPkgsInfo(string pkgsinfoPath, string name)
    {
        var pkgsinfo = new PkgsInfo
        {
            Name = name,
            Version = DateTime.Now.ToString("yyyy.MM.dd"),
            Catalogs = new List<string> { "Testing" },
            UnattendedInstall = true
        };

        var yaml = SerializePkgsInfo(pkgsinfo);
        
        // Ensure directory exists
        var dir = Path.GetDirectoryName(pkgsinfoPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(pkgsinfoPath, yaml);
    }

    /// <summary>
    /// Gathers installer information and builds a PkgsInfo object
    /// </summary>
    public PkgsInfo BuildFromInstaller(string installerPath, PkgsInfoOptions options)
    {
        var extension = Path.GetExtension(installerPath).ToLowerInvariant();
        
        string metaName = ParsePackageName(Path.GetFileName(installerPath));
        string metaVersion = "";
        string metaDeveloper = "";
        string metaDesc = "";
        string installerType = "unknown";
        string productCode = "";
        string upgradeCode = "";
        string? metaIdent = null;
        var installs = new List<InstallItem>();

        switch (extension)
        {
            case ".msi":
                installerType = "msi";
                var msiMeta = _extractor.ExtractMsiMetadata(installerPath);
                metaName = msiMeta.ProductName;
                metaVersion = msiMeta.ProductVersion;
                metaDeveloper = msiMeta.Developer;
                metaDesc = msiMeta.Description;
                productCode = msiMeta.ProductCode;
                upgradeCode = msiMeta.UpgradeCode;
                
                installs.Add(new InstallItem
                {
                    Type = "file",
                    Path = installerPath,
                    Md5Checksum = _extractor.CalculateMd5(installerPath),
                    Version = metaVersion
                });
                break;

            case ".exe":
                installerType = "exe";
                metaVersion = _extractor.ExtractExeVersion(installerPath) ?? "";
                
                installs.Add(new InstallItem
                {
                    Type = "file",
                    Path = installerPath,
                    Md5Checksum = _extractor.CalculateMd5(installerPath),
                    Version = metaVersion
                });
                break;

            case ".nupkg":
                installerType = "nupkg";
                var nupkgMeta = _extractor.ExtractNupkgMetadata(installerPath);
                metaIdent = nupkgMeta.Identifier;
                metaName = nupkgMeta.Name;
                metaVersion = nupkgMeta.Version;
                metaDeveloper = nupkgMeta.Developer;
                metaDesc = nupkgMeta.Description;
                
                installs.Add(new InstallItem
                {
                    Type = "file",
                    Path = installerPath,
                    Md5Checksum = _extractor.CalculateMd5(installerPath),
                    Version = metaVersion
                });
                break;

            default:
                installs.Add(new InstallItem
                {
                    Type = "file",
                    Path = installerPath,
                    Md5Checksum = _extractor.CalculateMd5(installerPath)
                });
                break;
        }

        // Apply option overrides
        var finalName = !string.IsNullOrEmpty(options.Name) ? options.Name : metaName;
        var finalVersion = !string.IsNullOrEmpty(options.Version) ? options.Version : metaVersion;
        if (string.IsNullOrEmpty(finalVersion))
        {
            finalVersion = DateTime.Now.ToString("yyyy.MM.dd");
        }
        var finalDeveloper = !string.IsNullOrEmpty(options.Developer) ? options.Developer : metaDeveloper;
        var finalDesc = !string.IsNullOrEmpty(options.Description) ? options.Description : metaDesc;

        var pkgsinfo = new PkgsInfo
        {
            Name = finalName,
            DisplayName = options.DisplayName,
            Identifier = metaIdent,
            Version = finalVersion,
            Catalogs = options.Catalogs ?? new List<string> { "Development" },
            Category = options.Category,
            Developer = finalDeveloper,
            Description = finalDesc,
            InstallerType = installerType,
            UnattendedInstall = options.UnattendedInstall,
            OnDemand = options.OnDemand,
            MinOSVersion = options.MinOSVersion,
            MaxOSVersion = options.MaxOSVersion,
            Installs = installs
        };

        // Build installer info
        try
        {
            var sizeKB = _extractor.GetFileSizeKB(installerPath);
            var hash = _extractor.CalculateSha256(installerPath);

            pkgsinfo.Installer = new Installer
            {
                Location = NormalizeWindowsPath(Path.GetFileName(installerPath)),
                Hash = hash,
                Type = installerType,
                Size = sizeKB
            };

            if (installerType == "msi")
            {
                pkgsinfo.Installer.ProductCode = productCode;
                pkgsinfo.Installer.UpgradeCode = upgradeCode;
            }
        }
        catch
        {
            // Ignore file info errors
        }

        // Load scripts if specified
        if (!string.IsNullOrEmpty(options.InstallCheckScriptPath))
        {
            pkgsinfo.InstallCheckScript = ReadFileOrEmpty(options.InstallCheckScriptPath);
        }
        if (!string.IsNullOrEmpty(options.UninstallCheckScriptPath))
        {
            pkgsinfo.UninstallCheckScript = ReadFileOrEmpty(options.UninstallCheckScriptPath);
        }
        if (!string.IsNullOrEmpty(options.PreinstallScriptPath))
        {
            pkgsinfo.PreinstallScript = ReadFileOrEmpty(options.PreinstallScriptPath);
        }
        if (!string.IsNullOrEmpty(options.PostinstallScriptPath))
        {
            pkgsinfo.PostinstallScript = ReadFileOrEmpty(options.PostinstallScriptPath);
        }
        if (!string.IsNullOrEmpty(options.PreuninstallScriptPath))
        {
            pkgsinfo.PreuninstallScript = ReadFileOrEmpty(options.PreuninstallScriptPath);
        }
        if (!string.IsNullOrEmpty(options.PostuninstallScriptPath))
        {
            pkgsinfo.PostuninstallScript = ReadFileOrEmpty(options.PostuninstallScriptPath);
        }
        if (!string.IsNullOrEmpty(options.UninstallerPath))
        {
            // Store the uninstaller path - typically used for EXE installers
            pkgsinfo.UninstallerPath = options.UninstallerPath;
        }

        // Process additional -f file paths
        if (options.AdditionalFiles?.Count > 0)
        {
            var userInstalls = BuildInstallsArray(options.AdditionalFiles);
            
            bool hasValidVersion = !string.IsNullOrEmpty(pkgsinfo.Version) && pkgsinfo.Version != "1.0.0";
            
            foreach (var install in userInstalls)
            {
                var fileVersion = install.Version;
                install.Version = null; // Remove per-file version from final YAML
                
                if (!hasValidVersion && !string.IsNullOrEmpty(fileVersion) && fileVersion != "1.0.0")
                {
                    pkgsinfo.Version = fileVersion;
                    hasValidVersion = true;
                }
            }
            
            pkgsinfo.Installs ??= new List<InstallItem>();
            pkgsinfo.Installs.AddRange(userInstalls);
        }

        return pkgsinfo;
    }

    /// <summary>
    /// Builds an installs array from file paths (for -f option)
    /// </summary>
    public List<InstallItem> BuildInstallsArray(List<string> filePaths)
    {
        var items = new List<InstallItem>();

        foreach (var path in filePaths)
        {
            var absPath = Path.GetFullPath(path);
            
            if (!File.Exists(absPath) || (File.GetAttributes(absPath) & FileAttributes.Directory) != 0)
            {
                Console.Error.WriteLine($"Skipping -f path: '{path}'");
                continue;
            }

            try
            {
                var md5 = _extractor.CalculateMd5(absPath);
                string? fileVersion = null;

                if (OperatingSystem.IsWindows() && 
                    Path.GetExtension(absPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    fileVersion = _extractor.ExtractExeVersion(absPath);
                }

                var finalPath = ReplacePathUserProfile(absPath);

                items.Add(new InstallItem
                {
                    Type = "file",
                    Path = finalPath,
                    Md5Checksum = md5,
                    Version = fileVersion
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error processing {path}: {ex.Message}");
            }
        }

        return items;
    }

    /// <summary>
    /// Serializes a PkgsInfo object to YAML
    /// </summary>
    public string SerializePkgsInfo(PkgsInfo pkgsinfo)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .Build();
        return serializer.Serialize(pkgsinfo);
    }

    /// <summary>
    /// Parses the package name from a filename (removes extension)
    /// </summary>
    private static string ParsePackageName(string filename)
    {
        return Path.GetFileNameWithoutExtension(filename);
    }

    /// <summary>
    /// Normalizes Windows paths (forward slashes)
    /// </summary>
    private static string NormalizeWindowsPath(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Replaces user profile path with placeholder
    /// </summary>
    private static string ReplacePathUserProfile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(userProfile))
        {
            return path;
        }

        if (path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "%USERPROFILE%" + path[userProfile.Length..];
        }

        return path;
    }

    /// <summary>
    /// Reads a file or returns empty string
    /// </summary>
    private static string? ReadFileOrEmpty(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Options for building a PkgsInfo
/// </summary>
public class PkgsInfoOptions
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Version { get; set; }
    public List<string>? Catalogs { get; set; }
    public string? Category { get; set; }
    public string? Developer { get; set; }
    public string? Description { get; set; }
    public bool UnattendedInstall { get; set; }
    public bool UnattendedUninstall { get; set; }
    public bool OnDemand { get; set; }
    public string? MinOSVersion { get; set; }
    public string? MaxOSVersion { get; set; }
    public string? InstallCheckScriptPath { get; set; }
    public string? UninstallCheckScriptPath { get; set; }
    public string? PreinstallScriptPath { get; set; }
    public string? PostinstallScriptPath { get; set; }
    public string? PreuninstallScriptPath { get; set; }
    public string? PostuninstallScriptPath { get; set; }
    public string? UninstallerPath { get; set; }
    public List<string>? AdditionalFiles { get; set; }
}
