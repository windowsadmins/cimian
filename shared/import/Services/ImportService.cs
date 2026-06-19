using System.Diagnostics;
using System.Globalization;
using Cimian.CLI.Cimiimport.Models;
using Cimian.Core;
using Cimian.Core.Services;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// Handles the import workflow for installers.
/// </summary>
public class ImportService
{
    private readonly MetadataExtractor _metadataExtractor;
    private readonly ConfigurationService _configService;

    public ImportService(MetadataExtractor? metadataExtractor = null, ConfigurationService? configService = null)
    {
        _metadataExtractor = metadataExtractor ?? new MetadataExtractor();
        _configService = configService ?? new ConfigurationService();
    }

    /// <summary>
    /// Performs the full import workflow. User interaction is routed through
    /// <paramref name="prompter"/>; pass <see cref="ConsolePrompter"/> for the
    /// classic CLI experience, <see cref="NoInteractivePrompter"/> for
    /// <c>--nointeractive</c>, or a custom implementation (e.g. CimianAdmin's
    /// WinUI prompter). When <paramref name="prompter"/> is null and
    /// <paramref name="noInteractive"/> is true we use <see cref="NoInteractivePrompter"/>,
    /// otherwise <see cref="ConsolePrompter"/>.
    /// </summary>
    public async Task<bool> ImportAsync(
        string packagePath,
        ImportConfiguration config,
        ScriptPaths scripts,
        string? uninstallerPath,
        List<string> installsPaths,
        string? minOSVersion,
        string? maxOSVersion,
        string? minCimianVersion = null,
        bool extractIcon = false,
        string? iconOutputPath = null,
        bool noInteractive = false,
        IImportPrompter? prompter = null,
        CancellationToken cancellationToken = default)
    {
        prompter ??= noInteractive ? new NoInteractivePrompter() : new ConsolePrompter();
        // Step 1: Check file exists
        if (!File.Exists(packagePath))
        {
            prompter.ReportError($"Package '{packagePath}' does not exist");
            return false;
        }

        // Step 2: Extract metadata
        prompter.ReportInfo("Extracting metadata...");
        var metadata = _metadataExtractor.ExtractMetadata(packagePath, config);
        if (string.IsNullOrEmpty(metadata.ID))
        {
            metadata.ID = Path.GetFileNameWithoutExtension(packagePath);
        }

        // Detect architecture from filename (this takes priority over MSI/MSIX metadata).
        var filenameArch = MetadataExtractor.DetectArchFromFilename(Path.GetFileName(packagePath));
        var hasFilenameArch = !string.IsNullOrEmpty(filenameArch);

        if (hasFilenameArch)
        {
            prompter.ReportInfo($"Detected architecture '{filenameArch}' from filename");
        }

        // Step 3: Check for existing item in All.yaml; if found, ask whether to template.
        var (existingPkg, found) = FindMatchingItemInAllCatalog(config.RepoPath, metadata.ID);
        if (found && existingPkg != null)
        {
            var useTemplate = await prompter.AskUseTemplateAsync(existingPkg, cancellationToken).ConfigureAwait(false);
            if (useTemplate)
            {
                ApplyTemplate(metadata, existingPkg, scripts);

                // Filename-detected arch wins over template arch.
                if (hasFilenameArch)
                {
                    metadata.Architecture = filenameArch;
                    metadata.SupportedArch = [filenameArch];
                }
            }
        }

        // Step 4: Let the user review/edit the seven metadata fields.
        metadata = await prompter.EditMetadataAsync(metadata, config, cancellationToken).ConfigureAwait(false);

        // Step 5: Gather script contents
        var preinstallScript = LoadScriptContent(scripts.Preinstall, existingPkg, "preinstall");
        var postinstallScript = LoadScriptContent(scripts.Postinstall, existingPkg, "postinstall");
        var preuninstallScript = LoadScriptContent(scripts.Preuninstall, existingPkg, "preuninstall");
        var postuninstallScript = LoadScriptContent(scripts.Postuninstall, existingPkg, "postuninstall");
        var installCheckScript = LoadScriptContent(scripts.InstallCheck, existingPkg, "installcheck");
        var uninstallCheckScript = LoadScriptContent(scripts.UninstallCheck, existingPkg, "uninstallcheck");

        // Step 6: Handle uninstaller
        Installer? uninstaller = null;
        if (!string.IsNullOrEmpty(uninstallerPath))
        {
            uninstaller = ProcessUninstaller(uninstallerPath, config.RepoPath, prompter);
        }

        // Step 7: Calculate file hash and size
        prompter.ReportInfo("Calculating file hash...");
        var fileHash = MetadataExtractor.CalculateSHA256(packagePath);
        var fileInfo = new FileInfo(packagePath);
        var fileSizeKB = fileInfo.Length / 1024;

        // Step 8: Build PkgsInfo
        var displayName = metadata.ID;
        var sanitizedName = MetadataExtractor.SanitizeName(metadata.ID);

        var pkgsInfo = new PkgsInfo
        {
            Name = sanitizedName,
            DisplayName = displayName,
            Version = metadata.Version,
            Description = metadata.Description,
            Category = metadata.Category,
            Developer = metadata.Developer,
            SupportedArch = metadata.SupportedArch,
            Catalogs = metadata.Catalogs,
            Installs = [],
            MinOSVersion = minOSVersion,
            MaxOSVersion = maxOSVersion,
            MinCimianVersion = minCimianVersion,
            Installer = new Installer
            {
                Hash = fileHash,
                Type = metadata.InstallerType,
                Size = fileSizeKB
            },
            Uninstaller = uninstaller != null ? [uninstaller] : null,
            UnattendedInstall = metadata.UnattendedInstall,
            UnattendedUninstall = metadata.UnattendedUninstall,
            Requires = metadata.Requires,
            UpdateFor = metadata.UpdateFor,
            BlockingApps = metadata.BlockingApps,
            PreinstallScript = preinstallScript,
            PostinstallScript = postinstallScript,
            PreuninstallScript = preuninstallScript,
            PostuninstallScript = postuninstallScript,
            InstallCheckScript = installCheckScript,
            UninstallCheckScript = uninstallCheckScript
        };

        // Decide architecture tag
        var archTag = pkgsInfo.SupportedArch.Count == 1 
            ? $"-{pkgsInfo.SupportedArch[0].ToLowerInvariant()}-" 
            : "-";

        // Step 9: Where in the repo should the installer + pkginfo live?
        // RepoPath is a non-nullable string defaulting to "" (the null-coalesce
        // never fires for empty strings) and templated values can arrive
        // forward-slashed ("/mgmt") because pkginfo is normalized that way on
        // disk. IsNullOrEmpty + an explicit \mgmt default keeps NoInteractive
        // runs from collapsing into the repo root.
        var defaultRepoSub = string.IsNullOrEmpty(metadata.RepoPath) ? @"\mgmt" : metadata.RepoPath;
        var repoSubPath = await prompter.AskRepoSubdirAsync(defaultRepoSub, cancellationToken).ConfigureAwait(false);

        // Step 10: Build installs array
        var finalInstalls = BuildFinalInstallsArray(metadata, pkgsInfo.Name, pkgsInfo.Version, installsPaths, prompter);
        pkgsInfo.Installs = finalInstalls.Count > 0 ? finalInstalls : null;

        // Auto-generate an MSIX uninstaller entry when the importer didn't receive
        // an explicit --uninstaller path. Remove-AppxProvisionedPackage at uninstall
        // time will use the stored identity name to resolve the PackageFullName.
        if (pkgsInfo.Uninstaller == null
            && metadata.InstallerType == "msix"
            && !string.IsNullOrEmpty(metadata.IdentityName))
        {
            pkgsInfo.Uninstaller =
            [
                new Installer
                {
                    Type = "msix",
                    IdentityName = metadata.IdentityName
                }
            ];
        }

        // Step 11: Final review + confirm. Prompter renders the summary; we just supply
        // the assembled pkginfo so any frontend can present it however it likes.
        var confirmed = await prompter.ConfirmImportAsync(pkgsInfo, cancellationToken).ConfigureAwait(false);
        if (!confirmed)
        {
            prompter.ReportInfo("Import canceled.");
            return false;
        }

        // Step 12a: Extract icon if requested. Failures here are non-fatal — we surface
        // them as warnings and let the import continue without an icon.
        string? iconName = null;
        if (extractIcon)
        {
            prompter.ReportInfo("Extracting icon (EXPERIMENTAL)...");
            try
            {
                var iconExtractor = new IconExtractor();
                var iconResult = iconExtractor.ExtractIconToPng(packagePath, config.RepoPath, sanitizedName, iconOutputPath);
                if (iconResult != null)
                {
                    iconName = iconResult;
                    pkgsInfo.IconName = iconName;
                    prompter.ReportInfo($"Icon extracted: {iconName}");
                }
                else
                {
                    prompter.ReportWarning("Could not extract icon from this installer type");
                }
            }
            catch (Exception ex)
            {
                prompter.ReportWarning($"Icon extraction failed: {ex.Message}");
            }
        }
        // Step 12: Copy installer to pkgs subdir
        prompter.ReportInfo("Copying installer to repo...");
        // Templated values can arrive forward-slashed ("/mgmt") because
        // Installer.Location is normalized that way. A rooted or drive-
        // qualified value silently makes Path.Combine ignore config.RepoPath
        // and write outside the repo — coerce to a relative subpath first.
        repoSubPath = NormalizeRepoSubPath(repoSubPath);
        var installerFolderPath = Path.Combine(config.RepoPath, "pkgs", repoSubPath);
        Directory.CreateDirectory(installerFolderPath);
        
        var installerFilename = $"{sanitizedName}{archTag}{pkgsInfo.Version}{Path.GetExtension(packagePath)}";
        var installerDest = Path.Combine(installerFolderPath, installerFilename);
        File.Copy(packagePath, installerDest, overwrite: true);

        var subpathAndFile = Path.Combine(repoSubPath, installerFilename);
        pkgsInfo.Installer!.Location = MetadataExtractor.NormalizeWindowsPath(subpathAndFile);

        // Step 13: Write pkginfo YAML
        prompter.ReportInfo("Writing pkginfo file...");
        var pkginfoFolderPath = Path.Combine(config.RepoPath, "pkgsinfo", repoSubPath);
        Directory.CreateDirectory(pkginfoFolderPath);

        var pkginfoFilename = $"{sanitizedName}{archTag}{pkgsInfo.Version}.yaml";
        var pkginfoPath = Path.Combine(pkginfoFolderPath, pkginfoFilename);

        // Preserve any existing _metadata block (cimian-promoter / autopkg
        // stamps like created_by / creation_date / cimian-promoter_edit_date).
        // Without this an in-place re-import would silently strip them, since
        // we're about to overwrite the file from a freshly-built PkgsInfo.
        if (File.Exists(pkginfoPath))
        {
            var existing = await File.ReadAllTextAsync(pkginfoPath, cancellationToken).ConfigureAwait(false);
            pkgsInfo.Metadata = YamlUtils.ExtractMetadataBlock(existing);
        }

        // Stamp authorship at the source for fresh imports: created_by is the
        // local user (USERPROFILE leaf), creation_date the local wall-clock time
        // with timezone offset -- deliberately NOT UTC -- so the record reflects
        // who imported and when on this machine. Only fills blanks, so existing
        // autopkg / cimian-promoter stamps and re-imports are never clobbered;
        // the prod-checks git backfill stays as the fallback for anything that
        // still arrives without _metadata (e.g. fully-CI builds).
        pkgsInfo.Metadata ??= new Dictionary<string, object?>();
        if (IsBlankMetadata(pkgsInfo.Metadata, "created_by"))
        {
            // Only stamp when we actually resolved a name. In an unusual host
            // (service / CI with no USERPROFILE and an empty Environment.UserName)
            // leave created_by absent rather than persist a blank string, so the
            // prod-checks "missing metadata" backfill can still fill it later.
            var localUser = LocalUserName();
            if (!string.IsNullOrWhiteSpace(localUser))
            {
                pkgsInfo.Metadata["created_by"] = localUser;
            }
        }
        if (IsBlankMetadata(pkgsInfo.Metadata, "creation_date"))
        {
            pkgsInfo.Metadata["creation_date"] =
                DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        var yaml = YamlUtils.SerializePkgInfo(pkgsInfo);
        await File.WriteAllTextAsync(pkginfoPath, yaml, cancellationToken).ConfigureAwait(false);

        prompter.ReportInfo($"Pkginfo created at: {pkginfoPath}");

        // Open in editor if configured. Suppressed under --nointeractive: the editor
        // (VS Code / Notepad) inherits the parent process's stdio, and when cimiimport
        // is itself invoked with redirected stdio (e.g. PowerShell `& cimiimport ... 2>&1`),
        // the editor's grandchild handles keep the pipe open after cimiimport exits and
        // deadlock the caller. CI must never spawn an editor.
        if (config.OpenImportedYaml && !noInteractive)
        {
            TryOpenFile(pkginfoPath);
        }

        prompter.ReportInfo("Installer imported successfully!");
        return true;
    }

    // Local Windows user driving the import, taken from %USERPROFILE% (its leaf
    // is the account/profile name), lowercased to match the created_by form used
    // by autopkg and the prod-checks backfill. Falls back to Environment.UserName
    // when USERPROFILE is unset (services / non-Windows hosts).
    private static string LocalUserName()
    {
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        var name = !string.IsNullOrWhiteSpace(profile)
            ? Path.GetFileName(profile.TrimEnd('\\', '/'))
            : Environment.UserName;
        return (name ?? string.Empty).Trim().ToLowerInvariant();
    }

    // A metadata key counts as blank when absent, null, or whitespace, so a file
    // carrying an empty `created_by:`/`creation_date:` still gets stamped.
    private static bool IsBlankMetadata(IDictionary<string, object?> metadata, string key)
        => !metadata.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v?.ToString());

    /// <summary>
    /// Finds the latest version of a matching item in All.yaml catalog.
    /// When multiple versions exist for the same name, returns the highest version.
    /// </summary>
    private (PkgsInfo?, bool) FindMatchingItemInAllCatalog(string repoPath, string newItemName)
    {
        try
        {
            // Run makecatalogs first
            RunMakeCatalogs(repoPath, silent: true);

            var allCatalogPath = Path.Combine(repoPath, "catalogs", "All.yaml");
            if (!File.Exists(allCatalogPath))
            {
                return (null, false);
            }

            var yaml = File.ReadAllText(allCatalogPath);
            var catalog = YamlUtils.Deserializer.Deserialize<AllCatalog>(yaml);

            var newNameLower = newItemName.Trim().ToLowerInvariant();
            var matches = catalog?.Items?
                .Where(i => i.Name.Trim().ToLowerInvariant() == newNameLower)
                .ToList();

            if (matches == null || matches.Count == 0)
                return (null, false);

            // Return the item with the highest version
            var best = matches
                .OrderByDescending(i => i.Version, new VersionComparer())
                .First();

            return (best, true);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Compares dot-separated version strings numerically.
    /// e.g. "2026.01.28" > "2025.11.27"
    /// </summary>
    private class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == y) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var parts1 = x.Split('.');
            var parts2 = y.Split('.');
            var maxLen = Math.Max(parts1.Length, parts2.Length);

            for (int i = 0; i < maxLen; i++)
            {
                int p1 = i < parts1.Length && int.TryParse(parts1[i], out var v1) ? v1 : 0;
                int p2 = i < parts2.Length && int.TryParse(parts2[i], out var v2) ? v2 : 0;

                if (p1 < p2) return -1;
                if (p1 > p2) return 1;
            }

            return 0;
        }
    }

    /// <summary>
    /// Applies template values to metadata.
    /// </summary>
    private void ApplyTemplate(InstallerMetadata metadata, PkgsInfo existing, ScriptPaths scripts)
    {
        var extractedVersion = metadata.Version;

        metadata.ID = existing.Name;
        metadata.Title = existing.DisplayName ?? existing.Name;
        metadata.Version = extractedVersion;
        metadata.Developer = existing.Developer;

        // Update description with new version
        var desc = existing.Description;
        desc = desc.Replace(existing.Version, extractedVersion);
        metadata.Description = desc;

        metadata.Category = existing.Category;
        metadata.SupportedArch = existing.SupportedArch;
        metadata.Catalogs = existing.Catalogs;

        // Mark scripts as coming from template
        if (string.IsNullOrEmpty(scripts.Preinstall) && !string.IsNullOrEmpty(existing.PreinstallScript))
            scripts.Preinstall = "template";
        if (string.IsNullOrEmpty(scripts.Postinstall) && !string.IsNullOrEmpty(existing.PostinstallScript))
            scripts.Postinstall = "template";
        if (string.IsNullOrEmpty(scripts.Preuninstall) && !string.IsNullOrEmpty(existing.PreuninstallScript))
            scripts.Preuninstall = "template";
        if (string.IsNullOrEmpty(scripts.Postuninstall) && !string.IsNullOrEmpty(existing.PostuninstallScript))
            scripts.Postuninstall = "template";
        if (string.IsNullOrEmpty(scripts.InstallCheck) && !string.IsNullOrEmpty(existing.InstallCheckScript))
            scripts.InstallCheck = "template";
        if (string.IsNullOrEmpty(scripts.UninstallCheck) && !string.IsNullOrEmpty(existing.UninstallCheckScript))
            scripts.UninstallCheck = "template";

        metadata.UnattendedInstall = existing.UnattendedInstall;
        metadata.UnattendedUninstall = existing.UnattendedUninstall;
        metadata.Requires = existing.Requires;
        metadata.UpdateFor = existing.UpdateFor;
        metadata.BlockingApps = existing.BlockingApps;

        if (existing.Installer?.Location != null)
        {
            metadata.RepoPath = Path.GetDirectoryName(existing.Installer.Location) ?? "";
        }
    }

    /// <summary>
    /// Loads script content from file or template.
    /// </summary>
    private string? LoadScriptContent(string? path, PkgsInfo? template, string scriptType)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (path == "template" && template != null)
        {
            return scriptType switch
            {
                "preinstall" => template.PreinstallScript,
                "postinstall" => template.PostinstallScript,
                "preuninstall" => template.PreuninstallScript,
                "postuninstall" => template.PostuninstallScript,
                "installcheck" => template.InstallCheckScript,
                "uninstallcheck" => template.UninstallCheckScript,
                _ => null
            };
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

    /// <summary>
    /// Processes uninstaller file.
    /// </summary>
    private Installer? ProcessUninstaller(string uninstallerPath, string repoPath, IImportPrompter prompter)
    {
        if (!File.Exists(uninstallerPath))
        {
            prompter.ReportWarning($"Uninstaller '{uninstallerPath}' does not exist");
            return null;
        }

        try
        {
            var hash = MetadataExtractor.CalculateSHA256(uninstallerPath);
            var filename = Path.GetFileName(uninstallerPath);
            var destPath = Path.Combine(repoPath, "pkgs", filename);
            File.Copy(uninstallerPath, destPath, overwrite: true);

            return new Installer
            {
                Location = MetadataExtractor.NormalizeWindowsPath(Path.Combine("/", filename)),
                Hash = hash,
                Type = Path.GetExtension(uninstallerPath).TrimStart('.')
            };
        }
        catch (Exception ex)
        {
            prompter.ReportWarning($"Failed to process uninstaller: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Coerces a user-supplied repo subdirectory string to a strictly relative
    /// subpath. Forward slashes (from templated Installer.Location values like
    /// "/mgmt") are converted to backslashes, leading separators are stripped,
    /// and rooted / drive-qualified / UNC inputs are rejected outright. Without
    /// this, <c>Path.Combine(config.RepoPath, "pkgs", repoSubPath)</c> would
    /// silently treat a rooted input as absolute and write outside the repo.
    /// </summary>
    public static string NormalizeRepoSubPath(string repoSubPath)
    {
        if (string.IsNullOrWhiteSpace(repoSubPath))
        {
            return string.Empty;
        }

        var normalized = repoSubPath.Replace('/', '\\').Trim();

        // Reject UNC and drive-qualified paths before trimming separators —
        // after trimming "\\server\share" would silently become "server\share".
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Repo subdirectory must be relative, got UNC path: '{repoSubPath}'",
                nameof(repoSubPath));
        }
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            throw new ArgumentException(
                $"Repo subdirectory must be relative, got drive-qualified path: '{repoSubPath}'",
                nameof(repoSubPath));
        }

        normalized = normalized.TrimStart('\\');

        if (Path.IsPathRooted(normalized))
        {
            throw new ArgumentException(
                $"Repo subdirectory must be relative: '{repoSubPath}'",
                nameof(repoSubPath));
        }

        return normalized;
    }

    /// <summary>
    /// Assembles the final auto-generated installs array for an installer:
    /// explicit -i paths win; otherwise an installer-type-specific identity
    /// entry (exe fallback path, MSIX identity, MSI ProductCode/UpgradeCode),
    /// with any BOM-derived companion file checks from
    /// <see cref="InstallerMetadata.Installs"/> (see
    /// <see cref="MetadataExtractor"/> PopulateMsiBom) appended on top.
    /// </summary>
    public List<InstallItem> BuildFinalInstallsArray(
        InstallerMetadata metadata,
        string packageName,
        string packageVersion,
        List<string> installsPaths,
        IImportPrompter prompter)
    {
        List<InstallItem> finalInstalls;
        if (installsPaths.Count > 0)
        {
            finalInstalls = BuildInstallsArray(installsPaths, prompter);
        }
        else if (metadata.InstallerType == "exe")
        {
            var fallbackExe = $@"C:\Program Files\{packageName}\{packageName}.exe";
            prompter.ReportInfo($"Using fallback .exe => {fallbackExe}");
            finalInstalls =
            [
                new InstallItem
                {
                    Type = "file",
                    Path = fallbackExe,
                    Version = packageVersion
                }
            ];
        }
        else if (metadata.InstallerType == "msix" && !string.IsNullOrEmpty(metadata.IdentityName))
        {
            // MSIX packages are detected via Get-AppxProvisionedPackage filtered by Identity.Name
            prompter.ReportInfo($"Using MSIX identity => {metadata.IdentityName}");
            finalInstalls =
            [
                new InstallItem
                {
                    Type = "msix",
                    IdentityName = metadata.IdentityName,
                    Version = packageVersion
                }
            ];
        }
        else if (metadata.InstallerType == "msi"
            && (!string.IsNullOrEmpty(metadata.ProductCode) || !string.IsNullOrEmpty(metadata.UpgradeCode)))
        {
            // MSI packages are verified by querying Windows Installer for the ProductCode
            // (per-version) or UpgradeCode (stable). Without an installs[] entry of type=msi
            // the runtime verifier emits "No installs array for X — cannot verify".
            var productCode = string.IsNullOrEmpty(metadata.ProductCode) ? null : metadata.ProductCode.Trim();
            var upgradeCode = string.IsNullOrEmpty(metadata.UpgradeCode) ? null : metadata.UpgradeCode.Trim();
            var codeSummary = !string.IsNullOrEmpty(productCode)
                ? (!string.IsNullOrEmpty(upgradeCode) ? $"ProductCode={productCode}, UpgradeCode={upgradeCode}" : $"ProductCode={productCode}")
                : $"UpgradeCode={upgradeCode}";
            prompter.ReportInfo($"Using MSI {codeSummary}");
            // Version is intentionally omitted — StatusService falls back to the
            // top-level pkginfo version when the installs entry has none, and the
            // MSI's per-version identity is already the ProductCode.
            //
            // KeyPath is populated by ExtractMsiMetadata for cimipkg-built MSIs
            // (either an explicit build-info override or the BOM heuristic pick).
            // Third-party MSIs leave KeyPath empty here — those packages get up
            // to 3 companion type=file entries appended further down via
            // metadata.Installs (see PopulateMsiBom).
            // DisplayName is the wrapper-MSI escape hatch: PopulateMsiBom sets
            // ArpDisplayName only when the File table carries no payload, and
            // the runtime falls back to an ARP DisplayName lookup when the
            // declared codes miss (Firefox-style products that drop their
            // Windows Installer registration after self-update).
            finalInstalls =
            [
                new InstallItem
                {
                    Type = "msi",
                    ProductCode = productCode,
                    UpgradeCode = upgradeCode,
                    DisplayName = string.IsNullOrWhiteSpace(metadata.ArpDisplayName) ? null : metadata.ArpDisplayName,
                    KeyPath = string.IsNullOrWhiteSpace(metadata.KeyPath) ? null : metadata.KeyPath
                }
            ];
        }
        else
        {
            finalInstalls = [];
        }

        if (metadata.Installs.Count > 0)
        {
            finalInstalls.AddRange(metadata.Installs);
        }
        return finalInstalls;
    }

    /// <summary>
    /// Implements <c>--emit-installs</c>: extracts installer metadata (including
    /// the MSI bill-of-materials walk) and writes the auto-generated installs
    /// array as YAML to stdout without importing anything. Status messages go to
    /// stderr so stdout stays machine-parseable — autopkg's CimianInfoCreator
    /// consumes this instead of reimplementing the generation logic.
    /// </summary>
    public bool EmitInstalls(string packagePath, ImportConfiguration config, List<string> installsPaths)
    {
        var prompter = new NoInteractivePrompter(Console.Error);
        if (!File.Exists(packagePath))
        {
            prompter.ReportError($"Package '{packagePath}' does not exist");
            return false;
        }

        var metadata = _metadataExtractor.ExtractMetadata(packagePath, config);
        if (string.IsNullOrEmpty(metadata.ID))
        {
            metadata.ID = Path.GetFileNameWithoutExtension(packagePath);
        }

        var sanitizedName = MetadataExtractor.SanitizeName(metadata.ID);
        var installs = BuildFinalInstallsArray(metadata, sanitizedName, metadata.Version, installsPaths, prompter);

        var doc = new Dictionary<string, List<InstallItem>> { ["installs"] = installs };
        Console.Write(YamlUtils.Serializer.Serialize(doc));
        return true;
    }

    /// <summary>
    /// Builds installs array from file paths.
    /// </summary>
    private List<InstallItem> BuildInstallsArray(List<string> paths, IImportPrompter prompter)
    {
        var items = new List<InstallItem>();
        foreach (var p in paths)
        {
            var absPath = Path.GetFullPath(p);
            if (!File.Exists(absPath) || Directory.Exists(absPath))
            {
                // Use ReportInfo (no prefix) to preserve the prior
                // `Console.WriteLine("Skipping -i path: ...")` output verbatim.
                // ReportWarning would prepend "[WARN] " and break the
                // byte-identical claim from the PR description.
                prompter.ReportInfo($"Skipping -i path: '{p}'");
                continue;
            }

            string? md5 = null;
            string? version = null;

            try
            {
                md5 = MetadataExtractor.CalculateMD5(absPath);

                if (Path.GetExtension(absPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(absPath);
                    version = versionInfo.FileVersion;
                }
            }
            catch
            {
                // Ignore errors
            }

            // Replace user profile path with variable
            var finalPath = ReplacePathUserProfile(absPath);

            items.Add(new InstallItem
            {
                Type = "file",
                Path = finalPath,
                MD5Checksum = md5,
                Version = version
            });
        }
        return items;
    }

    /// <summary>
    /// Replaces the resolved user-profile prefix in a path with %USERPROFILE%
    /// so the resulting metadata is portable across machines / users.
    /// </summary>
    private static string ReplacePathUserProfile(string path)
    {
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(userProfile))
            return path;

        if (path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "%USERPROFILE%" + path[userProfile.Length..];
        }
        return path;
    }

    /// <summary>
    /// Runs makecatalogs against the given repo path. Without --repo_path,
    /// makecatalogs falls back to whatever Config.yaml says, which may not
    /// be the workspace cimiimport just imported into.
    /// </summary>
    private void RunMakeCatalogs(string repoPath, bool silent)
    {
        var makeCatalogsBinary = CimianPaths.MakeCatalogsExe;
        if (!File.Exists(makeCatalogsBinary))
        {
            return; // Silently skip if not found
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = makeCatalogsBinary,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(repoPath))
            {
                psi.ArgumentList.Add("--repo_path");
                psi.ArgumentList.Add(repoPath);
            }
            if (silent)
            {
                psi.ArgumentList.Add("--silent");
            }
            using var process = Process.Start(psi);
            process?.WaitForExit();
        }
        catch
        {
            // Ignore errors
        }
    }

    /// <summary>
    /// Tries to open file in editor.
    /// </summary>
    private void TryOpenFile(string filePath)
    {
        try
        {
            // Try VS Code first
            var codeCmd = FindExecutable("code.cmd");
            if (codeCmd != null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = codeCmd,
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return;
            }

            // Fallback to notepad
            Process.Start("notepad.exe", filePath);
        }
        catch
        {
            // Ignore
        }
    }

    /// <summary>
    /// Finds an executable in PATH.
    /// </summary>
    private string? FindExecutable(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir, name);
            if (File.Exists(fullPath))
                return fullPath;
        }
        return null;
    }


    /// <summary>
    /// Checks if repo is a git repository.
    /// </summary>
    public static bool IsGitRepository(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            var gitPath = Path.Combine(current, ".git");
            if (Directory.Exists(gitPath))
                return true;

            var parent = Path.GetDirectoryName(current);
            if (parent == current)
                break;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// Runs git pull in the repository.
    /// </summary>
    public void RunGitPull(string repoPath)
    {
        try
        {
            Console.WriteLine($"Running git pull in: {repoPath}");
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "pull",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // Never block on a credential prompt — fail the pull instead.
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";

            using var process = Process.Start(psi);
            if (process != null)
            {
                // Drain both pipes concurrently — a redirected stream nobody
                // reads deadlocks the child once its output fills the pipe
                // buffer (git fetch progress alone is enough).
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(120_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    Console.WriteLine("[WARN] Git pull timed out after 120s — continuing without pull");
                    return;
                }

                if (process.ExitCode == 0)
                {
                    Console.WriteLine("Git pull completed successfully");
                }
                else
                {
                    var detail = stderr.GetAwaiter().GetResult().Trim();
                    if (detail.Length > 200) detail = detail[..200];
                    Console.WriteLine($"[WARN] Git pull may have had issues{(detail.Length > 0 ? $": {detail}" : "")}");
                }
                _ = stdout.GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Git pull failed: {ex.Message}");
        }
    }
}
