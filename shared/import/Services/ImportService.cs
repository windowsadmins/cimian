using System.Diagnostics;
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
        var repoSubPath = await prompter.AskRepoSubdirAsync(metadata.RepoPath ?? @"\mgmt", cancellationToken).ConfigureAwait(false);

        // Step 10: Build installs array
        List<InstallItem> finalInstalls;
        if (installsPaths.Count > 0)
        {
            finalInstalls = BuildInstallsArray(installsPaths, prompter);
        }
        else if (metadata.InstallerType == "exe")
        {
            var fallbackExe = $@"C:\Program Files\{pkgsInfo.Name}\{pkgsInfo.Name}.exe";
            prompter.ReportInfo($"Using fallback .exe => {fallbackExe}");
            finalInstalls =
            [
                new InstallItem
                {
                    Type = "file",
                    Path = fallbackExe,
                    Version = pkgsInfo.Version
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
                    Version = pkgsInfo.Version
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
            finalInstalls =
            [
                new InstallItem
                {
                    Type = "msi",
                    ProductCode = productCode,
                    UpgradeCode = upgradeCode
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
        repoSubPath = repoSubPath.TrimStart('\\');
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
            var existing = await File.ReadAllTextAsync(pkginfoPath).ConfigureAwait(false);
            pkgsInfo.Metadata = YamlUtils.ExtractMetadataBlock(existing);
        }

        var yaml = YamlUtils.SerializePkgInfo(pkgsInfo);
        await File.WriteAllTextAsync(pkginfoPath, yaml);

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
                prompter.ReportWarning($"Skipping -i path: '{p}'");
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

            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    Console.WriteLine("Git pull completed successfully");
                }
                else
                {
                    Console.WriteLine("[WARN] Git pull may have had issues");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Git pull failed: {ex.Message}");
        }
    }
}
