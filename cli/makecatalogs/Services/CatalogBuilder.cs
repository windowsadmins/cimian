using System.Reflection;
using System.Text;
using Cimian.CLI.Makecatalogs.Models;
using Cimian.Core.Services;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Cimian.CLI.Makecatalogs.Services;

/// <summary>
/// Service for building package catalogs from pkginfo files
/// Migrated from Go: cmd/makecatalogs/main.go
/// </summary>
public class CatalogBuilder
{
    private readonly Action<string> _log;
    private readonly Action<string> _warn;
    private readonly Action<string> _success;

    // pkgsinfo files that failed to deserialize during the last ScanRepo. A parse
    // failure means the package is absent from every catalog written afterwards,
    // so this has to survive to the end of the run and affect the exit code --
    // publishing a silently incomplete catalog is the failure mode this guards.
    private readonly List<string> _parseErrors = new();

    /// <summary>Files skipped by the last <see cref="ScanRepo"/> because they could not be parsed.</summary>
    public IReadOnlyList<string> ParseErrors => _parseErrors;

    public CatalogBuilder(
        Action<string>? log = null,
        Action<string>? warn = null,
        Action<string>? success = null)
    {
        _log = log ?? Console.WriteLine;
        _warn = warn ?? (msg => Console.WriteLine($"WARNING: {msg}"));
        _success = success ?? (msg => Console.WriteLine($"SUCCESS: {msg}"));
    }

    /// <summary>
    /// Scans the repository for all pkginfo YAML files
    /// </summary>
    public List<PkgsInfo> ScanRepo(string repoPath)
    {
        var results = new List<PkgsInfo>();
        _parseErrors.Clear();
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
                var pkgInfo = YamlUtils.Deserializer.Deserialize<PkgsInfo>(yaml);
                if (pkgInfo != null)
                {
                    pkgInfo.FilePath = file;
                    pkgInfo.Source = ParseSource(yaml);
                    results.Add(pkgInfo);
                }
            }
            catch (Exception ex)
            {
                _warn($"Error parsing {file}: {ex.Message}");
                _parseErrors.Add($"{file}: {ex.Message}");
            }
        }

        return results;
    }

    private static YamlMappingNode? ParseSource(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
    }

    /// <summary>
    /// One catalog item: the item as the model serializes it, plus every key in the
    /// source pkgsinfo the model does not declare, at any depth (an uninstaller entry's
    /// <c>command</c>, an installs entry's <c>key_path</c>), with its original value and
    /// style. Munki's makecatalogs copies a pkgsinfo into the catalog whole; this does the
    /// same while keeping the model's validation and canonical form for the keys it knows.
    /// Like Munki, admin <c>notes</c> and top-level keys starting with an underscore
    /// (<c>_metadata</c>) stay out. Returns null when there is nothing to add, so an item
    /// with only known keys serializes exactly as before.
    /// </summary>
    private static string? SerializeWithSourceKeys(PkgsInfo pkg)
    {
        if (pkg.Source == null)
            return null;

        var stream = new YamlStream();
        stream.Load(new StringReader(YamlUtils.Serializer.Serialize(pkg)));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode item)
            return null;

        if (!AddUnknownKeys(typeof(PkgsInfo), item, pkg.Source, topLevel: true))
            return null;

        return YamlUtils.Serializer.Serialize(item);
    }

    private static bool AddUnknownKeys(Type model, YamlMappingNode target, YamlMappingNode source, bool topLevel)
    {
        var known = ModelKeys(model);
        var added = false;

        foreach (var (keyNode, value) in source.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key })
                continue;
            if (topLevel && (key == "notes" || key.StartsWith('_')))
                continue;

            if (!known.TryGetValue(key, out var property))
            {
                if (!target.Children.ContainsKey(keyNode))
                {
                    target.Add(new YamlScalarNode(key), value);
                    added = true;
                }
                continue;
            }

            // A key the model knows: keep the model's value, but look inside it for
            // unknown keys of its own.
            if (!target.Children.TryGetValue(keyNode, out var targetValue))
                continue;

            if (targetValue is YamlMappingNode targetMap && value is YamlMappingNode sourceMap &&
                IsModelType(property.PropertyType))
            {
                added |= AddUnknownKeys(property.PropertyType, targetMap, sourceMap, topLevel: false);
            }
            else if (targetValue is YamlSequenceNode targetList && value is YamlSequenceNode sourceList &&
                     ListElementType(property.PropertyType) is { } element && IsModelType(element) &&
                     targetList.Children.Count == sourceList.Children.Count)
            {
                for (var i = 0; i < targetList.Children.Count; i++)
                {
                    if (targetList.Children[i] is YamlMappingNode t && sourceList.Children[i] is YamlMappingNode src)
                    {
                        added |= AddUnknownKeys(element, t, src, topLevel: false);
                    }
                }
            }
        }

        return added;
    }

    private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> ModelKeyCache = new();

    // Every key a model type declares, as it appears in YAML.
    private static Dictionary<string, PropertyInfo> ModelKeys(Type model)
    {
        lock (ModelKeyCache)
        {
            if (!ModelKeyCache.TryGetValue(model, out var keys))
            {
                keys = model.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetCustomAttribute<YamlIgnoreAttribute>() == null)
                    .ToDictionary(p => p.GetCustomAttribute<YamlMemberAttribute>()?.Alias ?? p.Name, StringComparer.Ordinal);
                ModelKeyCache[model] = keys;
            }
            return keys;
        }
    }

    private static Type? ListElementType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;

    // A class from these models, as opposed to a string, collection or dictionary.
    private static bool IsModelType(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace == typeof(PkgsInfo).Namespace;

    /// <summary>
    /// A catalog in the same form as <see cref="YamlUtils.SerializeCatalog{T}"/>, with
    /// each item carrying the keys from its pkgsinfo that the model does not declare.
    /// </summary>
    private static string SerializeCatalog(List<PkgsInfo> items)
    {
        var withSourceKeys = items.Select(SerializeWithSourceKeys).ToList();
        if (withSourceKeys.All(yaml => yaml == null))
            return YamlUtils.SerializeCatalog(new CatalogFile { Items = items });

        // Same line breaks as the serializer, which writes Environment.NewLine.
        var newline = Environment.NewLine;
        var catalog = new StringBuilder("items:").Append(newline);
        for (var n = 0; n < items.Count; n++)
        {
            var mapping = withSourceKeys[n] ?? YamlUtils.Serializer.Serialize(items[n]);
            var lines = mapping.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                    catalog.Append(i == 0 ? "  - " : "    ").Append(lines[i]);
                catalog.Append(newline);
            }
        }

        return catalog.ToString();
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
                // Normalize path separators for comparison and trim leading slashes
                var location = pkg.Installer.Location.TrimStart('/', '\\').Replace('\\', '/');
                var relativePath = "pkgs/" + location;
                if (!existingFiles.Contains(relativePath))
                {
                    warnings.Add($"{pkg.FilePath} has missing installer => {relativePath}");
                }
                else
                {
                    // Size is checked on every run; hashing only under --hash_check.
                    // They used to share the flag, which meant neither ran in practice:
                    // the publishing pipeline does not pass it, because hashing every
                    // payload means re-reading multi-gigabyte packages on each publish.
                    // Reading a file's length costs a stat call, so there is no reason
                    // for it to sit behind the expensive check -- and a wrong size is
                    // exactly what went unnoticed for months, because the one detector
                    // for it never ran.
                    var fullPath = Path.Combine(repoPath, relativePath.Replace('/', '\\'));
                    if (File.Exists(fullPath))
                    {
                        var fileInfo = new FileInfo(fullPath);
                        if (pkg.Installer.Size.HasValue && fileInfo.Length != pkg.Installer.Size.Value)
                        {
                            warnings.Add($"{pkg.FilePath} installer size mismatch: expected {pkg.Installer.Size}, actual {fileInfo.Length}");
                        }
                        if (hashCheck && !string.IsNullOrEmpty(pkg.Installer.Hash))
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

            // Validate every uninstaller entry that references a file on disk.
            // MSIX/APPX uninstallers have only identity_name (no Location) so they're
            // skipped here and handled at runtime by managedsoftwareupdate.
            if (pkg.Uninstaller != null)
            {
                foreach (var uninst in pkg.Uninstaller)
                {
                    if (uninst.Location == null) continue;

                    var uninstallerLocation = uninst.Location.TrimStart('/', '\\').Replace('\\', '/');
                    var relativePath = "pkgs/" + uninstallerLocation;
                    if (!existingFiles.Contains(relativePath))
                    {
                        warnings.Add($"{pkg.FilePath} has missing uninstaller => {relativePath}");
                        continue;
                    }

                    var fullPath = Path.Combine(repoPath, relativePath.Replace('/', '\\'));
                    if (!File.Exists(fullPath)) continue;

                    var fileInfo = new FileInfo(fullPath);
                    if (uninst.Size.HasValue && fileInfo.Length != uninst.Size.Value)
                    {
                        warnings.Add($"{pkg.FilePath} uninstaller size mismatch: expected {uninst.Size}, actual {fileInfo.Length}");
                    }
                    if (hashCheck && !string.IsNullOrEmpty(uninst.Hash))
                    {
                        var actualHash = ComputeMd5Hash(fullPath);
                        if (!string.Equals(actualHash, uninst.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            warnings.Add($"{pkg.FilePath} uninstaller hash mismatch: expected {uninst.Hash}, actual {actualHash}");
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
    /// Stamps every item with <c>loop_fingerprint</c> — a hash of the item's own catalog
    /// content, and the fleet-wide lever for releasing LoopGuard suppression.
    /// <para>
    /// The client stores the fingerprint of the item it last installed and clears the
    /// package's loop history the moment it sees a different one, so publishing a fixed
    /// pkgsinfo is what gets a suppressed package installing again everywhere — nobody
    /// has to run <c>--clear-loop</c> on individual machines.
    /// </para>
    /// <para>
    /// Hashing the whole serialized item rather than a hand-picked field list is
    /// deliberate: the previous client-side fingerprint covered version, scripts,
    /// installer hash/location/type, installs[] and check, and therefore did NOT notice
    /// fixes to product_code/upgrade_code, installer switches/arguments/success_codes,
    /// blocking_applications, installer_timeout or requires — the exact fields our real
    /// loop fixes touch. Anything makecatalogs carries into the catalog is covered here,
    /// and stays covered as fields are added. The cost is that a description-only edit
    /// also clears suppression; that errs toward retrying an install, which is the side
    /// to err on.
    /// </para>
    /// <para>
    /// Line endings are normalized first so the hash does not depend on whether the
    /// pkgsinfo was last written on Windows or Linux.
    /// </para>
    /// </summary>
    public void StampLoopFingerprints(List<PkgsInfo> items)
    {
        foreach (var pkg in items)
        {
            NormalizeLineEndings(pkg);

            // Null it before hashing: the field is part of the serialized item, so
            // including a previous value would make the hash depend on itself.
            pkg.LoopFingerprint = null;
            // An item that carries keys the model does not declare is hashed as the
            // catalog will hold it, so a change to one of those keys clears suppression
            // too. Every other item hashes exactly as before.
            pkg.LoopFingerprint = LoopGuard.ComputeFingerprint(SerializeWithSourceKeys(pkg) ?? YamlUtils.SerializePkgInfo(pkg));
        }
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
    /// Normalizes line endings in multiline string fields to prevent extra blank lines
    /// Converts \r\n (Windows) to \n (Unix) to avoid YamlDotNet creating extra lines with folded scalar style
    /// Also collapses multiple consecutive newlines in the description. Scripts keep their
    /// blank lines: they can embed content that is verified byte-for-byte, such as a
    /// here-string checked against a SHA-256, and changing it breaks that check on clients.
    /// </summary>
    private static void NormalizeLineEndings(PkgsInfo pkg)
    {
        if (pkg.Description != null)
        {
            pkg.Description = pkg.Description.Replace("\r\n", "\n").Replace("\r", "\n");
            // Collapse triple+ newlines to double newlines (one blank line max)
            while (pkg.Description.Contains("\n\n\n"))
                pkg.Description = pkg.Description.Replace("\n\n\n", "\n\n");
        }
        
        if (pkg.PreinstallScript != null)
        {
            pkg.PreinstallScript = pkg.PreinstallScript.Replace("\r\n", "\n").Replace("\r", "\n");
        }
        
        if (pkg.PostinstallScript != null)
        {
            pkg.PostinstallScript = pkg.PostinstallScript.Replace("\r\n", "\n").Replace("\r", "\n");
        }
        
        if (pkg.PreuninstallScript != null)
        {
            pkg.PreuninstallScript = pkg.PreuninstallScript.Replace("\r\n", "\n").Replace("\r", "\n");
        }
        
        if (pkg.PostuninstallScript != null)
        {
            pkg.PostuninstallScript = pkg.PostuninstallScript.Replace("\r\n", "\n").Replace("\r", "\n");
        }
        
        if (pkg.InstallCheckScript != null)
        {
            pkg.InstallCheckScript = pkg.InstallCheckScript.Replace("\r\n", "\n").Replace("\r", "\n");
        }
        
        if (pkg.UninstallCheckScript != null)
        {
            pkg.UninstallCheckScript = pkg.UninstallCheckScript.Replace("\r\n", "\n").Replace("\r", "\n");
        }
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

            // Normalize line endings to prevent extra blank lines in YAML output
            foreach (var item in items)
            {
                NormalizeLineEndings(item);
            }

            var yaml = SerializeCatalog(items);

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
    public int Run(string repoPath, bool skipPayloadCheck = false, bool hashCheck = false, bool silent = false, bool tolerateParseErrors = false)
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

            // Stamp per-item loop fingerprints before the items are fanned out into
            // catalogs (the same instance appears in "All" and in each named catalog,
            // so this has to happen once, up front).
            StampLoopFingerprints(items);

            // Build catalogs
            var catalogs = BuildCatalogs(items, silent);

            // Write catalogs
            WriteCatalogs(repoPath, catalogs, silent);

            // Print warnings
            foreach (var warning in warnings)
            {
                _warn(warning);
            }

            // A package that failed to parse is missing from the catalogs just
            // written. Restate the failures here -- they scroll past mid-scan,
            // long before this point -- and fail the run, so a pipeline cannot
            // publish an incomplete catalog on a green exit code.
            if (_parseErrors.Count > 0)
            {
                _warn($"{_parseErrors.Count} pkgsinfo skipped (parse errors); those packages are NOT in the catalogs:");
                foreach (var err in _parseErrors)
                {
                    _warn($"  {err}");
                }

                if (!tolerateParseErrors)
                {
                    _warn("makecatalogs failed: fix the files above, or pass --tolerate_parse_errors to publish without them.");
                    return 1;
                }

                _success($"makecatalogs completed with {_parseErrors.Count} skipped pkgsinfo (--tolerate_parse_errors).");
                return 0;
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
