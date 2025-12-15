using Cimian.CLI.Makecatalogs.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Makecatalogs.Services;

/// <summary>
/// Service for building package catalogs from pkginfo files
/// Migrated from Go: cmd/makecatalogs/main.go
/// </summary>
public class CatalogBuilder
{
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly Action<string> _log;
    private readonly Action<string> _warn;
    private readonly Action<string> _success;

    public CatalogBuilder(
        Action<string>? log = null,
        Action<string>? warn = null,
        Action<string>? success = null)
    {
        _log = log ?? Console.WriteLine;
        _warn = warn ?? (msg => Console.WriteLine($"WARNING: {msg}"));
        _success = success ?? (msg => Console.WriteLine($"SUCCESS: {msg}"));

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections | DefaultValuesHandling.OmitDefaults)
            .WithIndentedSequences()
            .Build();
    }

    /// <summary>
    /// Scans the repository for all pkginfo YAML files
    /// </summary>
    public List<PkgsInfo> ScanRepo(string repoPath)
    {
        var results = new List<PkgsInfo>();
        var pkgsInfoDir = Path.Combine(repoPath, "pkgsinfo");

        if (!Directory.Exists(pkgsInfoDir))
        {
            throw new DirectoryNotFoundException($"pkgsinfo directory not found: {pkgsInfoDir}");
        }

        foreach (var file in Directory.EnumerateFiles(pkgsInfoDir, "*.yaml", SearchOption.AllDirectories))
        {
            try
            {
                var yaml = File.ReadAllText(file);
                var pkgInfo = _deserializer.Deserialize<PkgsInfo>(yaml);
                if (pkgInfo != null)
                {
                    pkgInfo.FilePath = file;
                    results.Add(pkgInfo);
                }
            }
            catch (Exception ex)
            {
                _warn($"Error parsing {file}: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Verifies that installer/uninstaller payloads exist
    /// Returns warnings for missing files
    /// </summary>
    public List<string> VerifyPayloads(string repoPath, List<PkgsInfo> items, bool hashCheck = false)
    {
        var warnings = new List<string>();
        var pkgsDir = Path.Combine(repoPath, "pkgs");

        // Gather all existing files in /pkgs - normalize to forward slashes for comparison
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(pkgsDir))
        {
            foreach (var file in Directory.EnumerateFiles(pkgsDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repoPath, file).Replace('\\', '/');
                existingFiles.Add(relativePath);
            }
        }

        foreach (var pkg in items)
        {
            if (pkg.Installer?.Location != null)
            {
                // Normalize path separators for comparison
                var relativePath = "pkgs/" + pkg.Installer.Location.Replace('\\', '/');
                if (!existingFiles.Contains(relativePath))
                {
                    warnings.Add($"{pkg.FilePath} has missing installer => {relativePath}");
                }
                else if (hashCheck)
                {
                    // Hash and size validation when --hash_check is enabled
                    var fullPath = Path.Combine(repoPath, relativePath.Replace('/', '\\'));
                    if (File.Exists(fullPath))
                    {
                        var fileInfo = new FileInfo(fullPath);
                        if (pkg.Installer.Size.HasValue && fileInfo.Length != pkg.Installer.Size.Value)
                        {
                            warnings.Add($"{pkg.FilePath} installer size mismatch: expected {pkg.Installer.Size}, actual {fileInfo.Length}");
                        }
                        if (!string.IsNullOrEmpty(pkg.Installer.Hash))
                        {
                            var actualHash = ComputeMd5Hash(fullPath);
                            if (!string.Equals(actualHash, pkg.Installer.Hash, StringComparison.OrdinalIgnoreCase))
                            {
                                warnings.Add($"{pkg.FilePath} installer hash mismatch: expected {pkg.Installer.Hash}, actual {actualHash}");
                            }
                        }
                    }
                }
            }

            if (pkg.Uninstaller?.Location != null)
            {
                var relativePath = "pkgs/" + pkg.Uninstaller.Location.Replace('\\', '/');
                if (!existingFiles.Contains(relativePath))
                {
                    warnings.Add($"{pkg.FilePath} has missing uninstaller => {relativePath}");
                }
                else if (hashCheck)
                {
                    // Hash and size validation when --hash_check is enabled
                    var fullPath = Path.Combine(repoPath, relativePath.Replace('/', '\\'));
                    if (File.Exists(fullPath))
                    {
                        var fileInfo = new FileInfo(fullPath);
                        if (pkg.Uninstaller.Size.HasValue && fileInfo.Length != pkg.Uninstaller.Size.Value)
                        {
                            warnings.Add($"{pkg.FilePath} uninstaller size mismatch: expected {pkg.Uninstaller.Size}, actual {fileInfo.Length}");
                        }
                        if (!string.IsNullOrEmpty(pkg.Uninstaller.Hash))
                        {
                            var actualHash = ComputeMd5Hash(fullPath);
                            if (!string.Equals(actualHash, pkg.Uninstaller.Hash, StringComparison.OrdinalIgnoreCase))
                            {
                                warnings.Add($"{pkg.FilePath} uninstaller hash mismatch: expected {pkg.Uninstaller.Hash}, actual {actualHash}");
                            }
                        }
                    }
                }
            }
        }

        return warnings;
    }

    private static string ComputeMd5Hash(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Builds catalog dictionaries from package info items
    /// Always includes "All" catalog containing all items
    /// </summary>
    public Dictionary<string, List<PkgsInfo>> BuildCatalogs(List<PkgsInfo> items, bool silent = false)
    {
        var catalogs = new Dictionary<string, List<PkgsInfo>>(StringComparer.OrdinalIgnoreCase)
        {
            ["All"] = new List<PkgsInfo>()
        };

        foreach (var pkg in items)
        {
            // Always add to "All"
            catalogs["All"].Add(pkg);

            // Add to each item's catalogs (skip if null/empty)
            if (pkg.Catalogs == null || pkg.Catalogs.Count == 0)
                continue;

            foreach (var catName in pkg.Catalogs)
            {
                if (string.IsNullOrWhiteSpace(catName))
                    continue;

                if (!catalogs.ContainsKey(catName))
                {
                    catalogs[catName] = new List<PkgsInfo>();
                }

                if (!silent)
                {
                    _log($"Adding {Path.GetFileName(pkg.FilePath)} to {catName}...");
                }

                catalogs[catName].Add(pkg);
            }
        }

        return catalogs;
    }

    /// <summary>
    /// Writes catalog files to the repository
    /// </summary>
    public void WriteCatalogs(string repoPath, Dictionary<string, List<PkgsInfo>> catalogs, bool silent = false)
    {
        var catalogDir = Path.Combine(repoPath, "catalogs");
        Directory.CreateDirectory(catalogDir);

        // Remove stale catalog files
        var existingCatalogs = Directory.GetFiles(catalogDir, "*.yaml");
        foreach (var existingFile in existingCatalogs)
        {
            var baseName = Path.GetFileNameWithoutExtension(existingFile);
            if (!catalogs.ContainsKey(baseName))
            {
                File.Delete(existingFile);
                if (!silent)
                {
                    _warn($"Removed stale catalog {existingFile}");
                }
            }
        }

        // Write current catalogs
        foreach (var (catName, items) in catalogs)
        {
            var outPath = Path.Combine(catalogDir, catName + ".yaml");

            var catalogWrapper = new CatalogFile { Items = items };
            var yaml = _serializer.Serialize(catalogWrapper);

            File.WriteAllText(outPath, yaml);

            if (!silent)
            {
                _success($"Wrote catalog {catName} ({items.Count} items)");
            }
        }
    }

    /// <summary>
    /// Runs the complete catalog building process
    /// </summary>
    public int Run(string repoPath, bool skipPayloadCheck = false, bool hashCheck = false, bool silent = false)
    {
        if (!silent)
        {
            _log($"Scanning {repoPath} for .yaml pkginfo...");
            if (hashCheck)
            {
                _log("Hash validation enabled (this may be slow for large repos)");
            }
        }

        try
        {
            // Scan repo
            var items = ScanRepo(repoPath);

            // Verify payloads
            List<string> warnings = new();
            if (!skipPayloadCheck)
            {
                warnings = VerifyPayloads(repoPath, items, hashCheck);
            }

            // Build catalogs
            var catalogs = BuildCatalogs(items, silent);

            // Write catalogs
            WriteCatalogs(repoPath, catalogs, silent);

            // Print warnings
            foreach (var warning in warnings)
            {
                _warn(warning);
            }

            _success("makecatalogs completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            _warn($"Error: {ex.Message}");
            return 1;
        }
    }
}
