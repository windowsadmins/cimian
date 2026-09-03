using Cimian.CLI.Makecatalogs.Models;
using Cimian.Core.Services;

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
            pkg.LoopFingerprint = LoopGuard.ComputeFingerprint(YamlUtils.SerializePkgInfo(pkg));
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
    /// Also collapses multiple consecutive newlines to prevent excessive blank lines in output
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
            while (pkg.PreinstallScript.Contains("\n\n\n"))
                pkg.PreinstallScript = pkg.PreinstallScript.Replace("\n\n\n", "\n\n");
        }
        
        if (pkg.PostinstallScript != null)
        {
            pkg.PostinstallScript = pkg.PostinstallScript.Replace("\r\n", "\n").Replace("\r", "\n");
            while (pkg.PostinstallScript.Contains("\n\n\n"))
                pkg.PostinstallScript = pkg.PostinstallScript.Replace("\n\n\n", "\n\n");
        }
        
        if (pkg.PreuninstallScript != null)
        {
            pkg.PreuninstallScript = pkg.PreuninstallScript.Replace("\r\n", "\n").Replace("\r", "\n");
            while (pkg.PreuninstallScript.Contains("\n\n\n"))
                pkg.PreuninstallScript = pkg.PreuninstallScript.Replace("\n\n\n", "\n\n");
        }
        
        if (pkg.PostuninstallScript != null)
        {
            pkg.PostuninstallScript = pkg.PostuninstallScript.Replace("\r\n", "\n").Replace("\r", "\n");
            while (pkg.PostuninstallScript.Contains("\n\n\n"))
                pkg.PostuninstallScript = pkg.PostuninstallScript.Replace("\n\n\n", "\n\n");
        }
        
        if (pkg.InstallCheckScript != null)
        {
            pkg.InstallCheckScript = pkg.InstallCheckScript.Replace("\r\n", "\n").Replace("\r", "\n");
            while (pkg.InstallCheckScript.Contains("\n\n\n"))
                pkg.InstallCheckScript = pkg.InstallCheckScript.Replace("\n\n\n", "\n\n");
        }
        
        if (pkg.UninstallCheckScript != null)
        {
            pkg.UninstallCheckScript = pkg.UninstallCheckScript.Replace("\r\n", "\n").Replace("\r", "\n");
            while (pkg.UninstallCheckScript.Contains("\n\n\n"))
                pkg.UninstallCheckScript = pkg.UninstallCheckScript.Replace("\n\n\n", "\n\n");
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

            var catalogWrapper = new CatalogFile { Items = items };
            var yaml = YamlUtils.SerializeCatalog(catalogWrapper);

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
