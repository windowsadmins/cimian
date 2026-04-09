using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cimian.CLI.Cimiimport.Models;
using WixToolset.Dtf.WindowsInstaller;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// Extracts metadata from various installer types.
/// </summary>
public partial class MetadataExtractor
{
    /// <summary>
    /// Extracts metadata from the specified installer file.
    /// </summary>
    public InstallerMetadata ExtractMetadata(string packagePath, ImportConfiguration config)
    {
        var ext = Path.GetExtension(packagePath).ToLowerInvariant();
        var metadata = new InstallerMetadata();

        // First, check if the filename contains architecture hints (x64, arm64, etc.)
        // This takes priority over the default configuration
        var detectedArch = DetectArchFromFilename(Path.GetFileName(packagePath));
        if (!string.IsNullOrEmpty(detectedArch))
        {
            metadata.Architecture = detectedArch;
            metadata.SupportedArch = [detectedArch];
        }

        switch (ext)
        {
            case ".msi":
                ExtractMsiMetadata(packagePath, metadata);
                break;
            case ".exe":
                ExtractExeMetadata(packagePath, metadata);
                break;
            case ".nupkg":
                ExtractNupkgMetadata(packagePath, metadata);
                break;
            case ".msix":
                ExtractMsixMetadata(packagePath, metadata);
                break;
            case ".pkg":
                ExtractPkgMetadata(packagePath, metadata);
                break;
            default:
                metadata.InstallerType = "unknown";
                metadata.Title = ParsePackageName(Path.GetFileName(packagePath));
                metadata.ID = metadata.Title;
                metadata.Version = "1.0.0";
                break;
        }

        // Ensure architecture is set from config default if not detected from filename
        if (string.IsNullOrEmpty(metadata.Architecture))
        {
            metadata.Architecture = config.DefaultArch;
        }

        // Parse architectures
        if (metadata.Architecture.Contains(','))
        {
            var parts = metadata.Architecture.Split(',', StringSplitOptions.RemoveEmptyEntries);
            metadata.SupportedArch = parts.Select(p => p.Trim().ToLowerInvariant()).ToList();
            if (metadata.SupportedArch.Count > 0)
            {
                metadata.Architecture = metadata.SupportedArch[0];
            }
        }
        else
        {
            metadata.SupportedArch = [metadata.Architecture.ToLowerInvariant()];
        }

        return metadata;
    }

    /// <summary>
    /// Extracts MSI metadata using DTF (direct msi.dll interop).
    /// For cimipkg-built MSI, also extracts embedded build-info.yaml for full metadata transfer.
    /// </summary>
    private void ExtractMsiMetadata(string packagePath, InstallerMetadata metadata)
    {
        metadata.InstallerType = "msi";

        if (!File.Exists(packagePath))
        {
            metadata.Title = ParsePackageName(Path.GetFileName(packagePath));
            metadata.ID = metadata.Title;
            return;
        }

        try
        {
            using var db = new Database(packagePath, DatabaseOpenMode.ReadOnly);

            string? ReadProp(string name) {
                try { return db.ExecuteScalar($"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'")?.ToString(); }
                catch { return null; }
            }

            metadata.Title = ReadProp("ProductName") ?? ParsePackageName(Path.GetFileName(packagePath));
            metadata.ID = metadata.Title;
            metadata.Version = ParseVersion(ReadProp("ProductVersion") ?? "");
            metadata.Developer = ReadProp("Manufacturer") ?? "";
            metadata.ProductCode = ReadProp("ProductCode") ?? "";
            metadata.UpgradeCode = ReadProp("UpgradeCode") ?? "";

            // For cimipkg-built MSI, extract rich metadata from embedded build-info.yaml
            var buildInfoYaml = ReadProp("CIMIAN_PKG_BUILD_INFO");
            if (!string.IsNullOrEmpty(buildInfoYaml))
            {
                try
                {
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();
                    var buildInfo = deserializer.Deserialize<PkgBuildInfo>(buildInfoYaml);

                    if (buildInfo?.Product != null)
                    {
                        if (!string.IsNullOrEmpty(buildInfo.Product.Name))
                            metadata.Title = buildInfo.Product.Name;
                        if (!string.IsNullOrEmpty(buildInfo.Product.Version))
                            metadata.Version = ParseVersion(buildInfo.Product.Version);
                        if (!string.IsNullOrEmpty(buildInfo.Product.Developer))
                            metadata.Developer = buildInfo.Product.Developer;
                        if (!string.IsNullOrEmpty(buildInfo.Product.Description))
                            metadata.Description = buildInfo.Product.Description;
                        if (!string.IsNullOrEmpty(buildInfo.Product.Architecture) &&
                            string.IsNullOrEmpty(metadata.Architecture))
                            metadata.Architecture = buildInfo.Product.Architecture;
                    }
                }
                catch
                {
                    // build-info.yaml parse failed; standard MSI properties already captured
                }
            }
        }
        catch
        {
            // Fallback to filename parsing
            metadata.Title = ParsePackageName(Path.GetFileName(packagePath));
            metadata.ID = metadata.Title;
        }
    }

    /// <summary>
    /// Extracts EXE metadata using FileVersionInfo.
    /// </summary>
    private void ExtractExeMetadata(string packagePath, InstallerMetadata metadata)
    {
        metadata.InstallerType = "exe";
        metadata.Title = ParsePackageName(Path.GetFileName(packagePath));
        metadata.ID = metadata.Title;

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(packagePath);
            
            if (!string.IsNullOrEmpty(versionInfo.FileVersion))
            {
                metadata.Version = ParseVersion(versionInfo.FileVersion);
            }
            else if (!string.IsNullOrEmpty(versionInfo.ProductVersion))
            {
                metadata.Version = ParseVersion(versionInfo.ProductVersion);
            }

            if (!string.IsNullOrEmpty(versionInfo.CompanyName))
            {
                metadata.Developer = versionInfo.CompanyName;
            }

            if (!string.IsNullOrEmpty(versionInfo.FileDescription))
            {
                metadata.Description = versionInfo.FileDescription;
            }
        }
        catch
        {
            // Keep defaults
        }
    }

    /// <summary>
    /// Extracts NUPKG metadata from the nuspec file.
    /// </summary>
    private void ExtractNupkgMetadata(string packagePath, InstallerMetadata metadata)
    {
        metadata.InstallerType = "nupkg";

        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(packagePath);
            var nuspecEntry = archive.Entries.FirstOrDefault(e => 
                e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

            if (nuspecEntry != null)
            {
                using var stream = nuspecEntry.Open();
                var doc = System.Xml.Linq.XDocument.Load(stream);
                var ns = doc.Root?.GetDefaultNamespace() ?? System.Xml.Linq.XNamespace.None;
                var metadataEl = doc.Root?.Element(ns + "metadata");

                if (metadataEl != null)
                {
                    var id = metadataEl.Element(ns + "id")?.Value ?? "";
                    
                    // For reverse domain identifiers, only keep the last part
                    if (id.Contains('.'))
                    {
                        var parts = id.Split('.');
                        metadata.ID = parts[^1];
                    }
                    else
                    {
                        metadata.ID = id;
                    }

                    metadata.Title = metadataEl.Element(ns + "title")?.Value ?? metadata.ID;
                    metadata.Version = ParseVersion(metadataEl.Element(ns + "version")?.Value ?? "");
                    metadata.Developer = metadataEl.Element(ns + "authors")?.Value ?? "";
                    metadata.Description = metadataEl.Element(ns + "description")?.Value ?? "";
                }
            }
        }
        catch
        {
            metadata.Title = ParsePackageName(Path.GetFileName(packagePath));
            metadata.ID = metadata.Title;
        }
    }

    /// <summary>
    /// Extracts MSIX metadata (basic fallback).
    /// </summary>
    private void ExtractMsixMetadata(string packagePath, InstallerMetadata metadata)
    {
        metadata.InstallerType = "msix";
        metadata.Title = ParsePackageName(Path.GetFileName(packagePath));
        metadata.ID = metadata.Title;
        metadata.Version = "1.0.0";
    }

    /// <summary>
    /// Extracts .pkg (Cimian package) metadata from build-info.yaml.
    /// </summary>
    private void ExtractPkgMetadata(string packagePath, InstallerMetadata metadata)
    {
        metadata.InstallerType = "pkg";
        
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var buildInfoEntry = archive.Entries.FirstOrDefault(e => 
                e.Name.Equals("build-info.yaml", StringComparison.OrdinalIgnoreCase));

            if (buildInfoEntry != null)
            {
                using var stream = buildInfoEntry.Open();
                using var reader = new StreamReader(stream);
                var yamlContent = reader.ReadToEnd();

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var buildInfo = deserializer.Deserialize<PkgBuildInfo>(yamlContent);

                if (buildInfo?.Product != null)
                {
                    var product = buildInfo.Product;
                    
                    // Use name as primary identifier, fallback to identifier
                    metadata.Title = !string.IsNullOrEmpty(product.Name) 
                        ? product.Name 
                        : product.Identifier ?? ParsePackageName(Path.GetFileName(packagePath));
                    
                    metadata.ID = metadata.Title;
                    metadata.Version = ParseVersion(product.Version ?? "1.0.0");
                    metadata.Developer = product.Developer ?? "";
                    metadata.Description = buildInfo.Description ?? product.Description ?? "";
                    
                    // Handle architecture from build-info.yaml
                    // Only use it if architecture wasn't already detected from filename
                    if (string.IsNullOrEmpty(metadata.Architecture) && !string.IsNullOrEmpty(product.Architecture))
                    {
                        metadata.Architecture = product.Architecture.ToLowerInvariant();
                    }
                    
                    return;
                }
            }
        }
        catch
        {
            // Fallback to filename parsing
        }

        // Fallback
        metadata.Title = ParsePackageName(Path.GetFileName(packagePath));
        metadata.ID = metadata.Title;
        metadata.Version = "1.0.0";
    }

    /// <summary>
    /// Calculates SHA256 hash of a file.
    /// </summary>
    public static string CalculateSHA256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Calculates MD5 hash of a file.
    /// </summary>
    public static string CalculateMD5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Parses package name from filename.
    /// </summary>
    private static string ParsePackageName(string filename)
    {
        return Path.GetFileNameWithoutExtension(filename);
    }

    /// <summary>
    /// Normalizes version string, handling date-based versions.
    /// </summary>
    public static string ParseVersion(string? versionStr)
    {
        if (string.IsNullOrEmpty(versionStr))
            return versionStr ?? "";

        var parts = versionStr.Split('.');
        if (parts.Length < 3 || parts.Length > 4)
            return versionStr;

        // Try to parse as date format
        if (int.TryParse(parts[0], out var yearNum))
        {
            bool isDateFormat = false;

            // Full 4-digit years
            if (yearNum is >= 2000 and <= 2100)
            {
                isDateFormat = true;
            }
            // Chocolatey-truncated 2-digit years
            else if (yearNum is >= 0 and <= 99)
            {
                if (int.TryParse(parts[1], out var monthNum) && monthNum is >= 1 and <= 12 &&
                    int.TryParse(parts[2], out var dayNum) && dayNum is >= 1 and <= 31)
                {
                    isDateFormat = true;
                }
            }

            if (isDateFormat)
            {
                // Validate all parts are numeric
                var numericParts = new List<int>();
                bool allNumeric = true;
                foreach (var part in parts)
                {
                    if (int.TryParse(part, out var num))
                    {
                        numericParts.Add(num);
                    }
                    else
                    {
                        allNumeric = false;
                        break;
                    }
                }

                if (allNumeric && numericParts.Count >= 3)
                {
                    var year = numericParts[0];
                    var month = numericParts[1];
                    var day = numericParts[2];

                    // Convert 2-digit year to 4-digit year
                    if (year < 100)
                    {
                        year = year <= 50 ? 2000 + year : 1900 + year;
                    }

                    if (numericParts.Count == 4)
                    {
                        // Preserve the original 4th segment string to keep leading zeros
                        // (e.g. "0838" HHMM timestamps must not become "838"). Month and day
                        // are still zero-padded from parsed ints so "2026.4.9.0838" normalizes
                        // to "2026.04.09.0838". Trim incidental whitespace (CR/LF, spaces)
                        // that int.TryParse accepts but shouldn't bleed into the output.
                        return $"{year}.{month:D2}.{day:D2}.{parts[3].Trim()}";
                    }
                    else
                    {
                        return $"{year}.{month:D2}.{day:D2}";
                    }
                }
            }
        }

        return versionStr;
    }

    /// <summary>
    /// Sanitizes a name for use in file paths.
    /// </summary>
    public static string SanitizeName(string name)
    {
        // Replace spaces with dashes
        name = name.Replace(" ", "-");
        
        // Remove any other problematic characters
        return SanitizeNameRegex().Replace(name, "-");
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\-_.]")]
    private static partial Regex SanitizeNameRegex();

    /// <summary>
    /// Normalizes Windows path format.
    /// </summary>
    public static string NormalizeWindowsPath(string path)
    {
        // Ensure forward slashes and leading slash
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }
        return path;
    }

    /// <summary>
    /// Detects architecture from filename patterns like "x64", "arm64", "amd64", etc.
    /// Returns empty string if no architecture hint is found.
    /// </summary>
    public static string DetectArchFromFilename(string filename)
    {
        var lower = filename.ToLowerInvariant();
        
        // Check for arm64 first (more specific)
        if (lower.Contains("arm64") || lower.Contains("aarch64"))
        {
            return "arm64";
        }
        
        // Check for x64/amd64/x86_64
        if (lower.Contains("x64") || lower.Contains("amd64") || 
            lower.Contains("x86_64") || lower.Contains("x86-64"))
        {
            return "x64";
        }
        
        // Check for x86/win32 (32-bit)
        if (lower.Contains("x86") || lower.Contains("win32") || 
            lower.Contains("i386") || lower.Contains("i686"))
        {
            return "x86";
        }
        
        return "";
    }
}
/// <summary>
/// Represents the build-info.yaml structure from .pkg packages.
/// </summary>
internal class PkgBuildInfo
{
    public PkgProductInfo? Product { get; set; }
    public string? Description { get; set; }
    public string? InstallLocation { get; set; }
    public string? PostinstallAction { get; set; }
    public PkgInstallerInfo? Installer { get; set; }
}

/// <summary>
/// Contains product metadata from .pkg packages.
/// </summary>
internal class PkgProductInfo
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Identifier { get; set; }
    public string? Developer { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Architecture { get; set; }
}

/// <summary>
/// Contains installer configuration for .pkg packages.
/// </summary>
internal class PkgInstallerInfo
{
    public string? Type { get; set; }
    public string? SilentArgs { get; set; }
    public List<int>? ExitCodes { get; set; }
}