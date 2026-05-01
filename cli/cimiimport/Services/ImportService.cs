using System.Diagnostics;
using Cimian.CLI.Cimiimport.Models;
using Cimian.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// Handles the import workflow for installers.
/// </summary>
public class ImportService
{
    private readonly MetadataExtractor _metadataExtractor;
    private readonly ConfigurationService _configService;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;

    public ImportService(MetadataExtractor? metadataExtractor = null, ConfigurationService? configService = null)
    {
        _metadataExtractor = metadataExtractor ?? new MetadataExtractor();
        _configService = configService ?? new ConfigurationService();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    /// <summary>
    /// Performs the full import workflow.
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
        bool noInteractive = false)
    {
        // Step 1: Check file exists
        if (!File.Exists(packagePath))
        {
            Console.WriteLine($"[ERROR] Package '{packagePath}' does not exist");
            return false;
        }

        // Step 2: Extract metadata
        Console.WriteLine("Extracting metadata...");
        var metadata = _metadataExtractor.ExtractMetadata(packagePath, config);
        if (string.IsNullOrEmpty(metadata.ID))
        {
            metadata.ID = Path.GetFileNameWithoutExtension(packagePath);
        }

        // Detect architecture from filename (this takes priority)
        var filenameArch = MetadataExtractor.DetectArchFromFilename(Path.GetFileName(packagePath));
        var hasFilenameArch = !string.IsNullOrEmpty(filenameArch);
        
        if (hasFilenameArch)
        {
            Console.WriteLine($"Detected architecture '{filenameArch}' from filename");
        }

        // Step 3: Check for existing item in All.yaml
        var (existingPkg, found) = FindMatchingItemInAllCatalog(config.RepoPath, metadata.ID);
        if (found && existingPkg != null)
        {
            Console.WriteLine("This item has the same Name as an existing item in the repo:");
            Console.WriteLine($"    Name: {existingPkg.Name}");
            Console.WriteLine($"    Version: {existingPkg.Version}");
            Console.WriteLine($"    Description: {existingPkg.Description}");

            if (noInteractive)
            {
                // Auto-apply template in non-interactive mode
                ApplyTemplate(metadata, existingPkg, scripts);

                // Restore the detected architecture from filename if it was detected
                if (hasFilenameArch)
                {
                    metadata.Architecture = filenameArch;
                    metadata.SupportedArch = [filenameArch];
                }
            }
            else
            {
                Console.Write("Use existing item as a template? [Y/n]: ");
                var ans = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(ans) || ans.Equals("y", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyTemplate(metadata, existingPkg, scripts);

                    // Restore the detected architecture from filename if it was detected
                    if (hasFilenameArch)
                    {
                        metadata.Architecture = filenameArch;
                        metadata.SupportedArch = [filenameArch];
                    }
                }
            }
        }

        // Step 4: Let user override fields (skip in non-interactive mode)
        if (!noInteractive)
        {
            metadata = PromptForMetadata(packagePath, metadata, config);
        }

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
            uninstaller = ProcessUninstaller(uninstallerPath, config.RepoPath);
        }

        // Step 7: Calculate file hash and size
        Console.WriteLine("Calculating file hash...");
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
                Size = fileSizeKB,
                ProductCode = string.IsNullOrEmpty(metadata.ProductCode) ? null : metadata.ProductCode.Trim(),
                UpgradeCode = string.IsNullOrEmpty(metadata.UpgradeCode) ? null : metadata.UpgradeCode.Trim()
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

        // Step 9: Prompt for repo subdirectory (use default in non-interactive mode)
        var repoSubPath = PromptInstallerPath(metadata.RepoPath, noInteractive);

        // Step 10: Build installs array
        List<InstallItem> finalInstalls;
        if (installsPaths.Count > 0)
        {
            finalInstalls = BuildInstallsArray(installsPaths);
        }
        else if (metadata.InstallerType == "exe")
        {
            var fallbackExe = $@"C:\Program Files\{pkgsInfo.Name}\{pkgsInfo.Name}.exe";
            Console.WriteLine($"Using fallback .exe => {fallbackExe}");
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
            Console.WriteLine($"Using MSIX identity => {metadata.IdentityName}");
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
            Console.WriteLine($"Using MSI {codeSummary}");
            finalInstalls =
            [
                new InstallItem
                {
                    Type = "msi",
                    ProductCode = productCode,
                    UpgradeCode = upgradeCode,
                    Version = pkgsInfo.Version
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

        // Step 11: Show final details
        Console.WriteLine();
        Console.WriteLine("Pkginfo details:");
        Console.WriteLine($"     Name: {pkgsInfo.Name}");
        Console.WriteLine($"     Display Name: {pkgsInfo.DisplayName}");
        Console.WriteLine($"     Version: {pkgsInfo.Version}");
        Console.WriteLine($"     Description: {pkgsInfo.Description}");
        Console.WriteLine($"     Category: {pkgsInfo.Category}");
        Console.WriteLine($"     Developer: {pkgsInfo.Developer}");
        Console.WriteLine($"     Architectures: {string.Join(", ", pkgsInfo.SupportedArch)}");
        Console.WriteLine($"     Catalogs: {string.Join(", ", pkgsInfo.Catalogs)}");
        Console.WriteLine($"     Installer Type: {pkgsInfo.Installer?.Type}");
        Console.WriteLine();

        // Confirm import (auto-yes in non-interactive mode)
        if (!noInteractive)
        {
            Console.Write("Import this item? (y/n) [n]: ");
            var confirm = Console.ReadLine()?.Trim();
            if (!confirm?.Equals("y", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                Console.WriteLine("Import canceled.");
                return false;
            }
        }

        // Step 12a: Extract icon if requested
        string? iconName = null;
        if (extractIcon)
        {
            Console.WriteLine("Extracting icon (EXPERIMENTAL)...");
            try
            {
                var iconExtractor = new IconExtractor();
                var iconResult = iconExtractor.ExtractIconToPng(packagePath, config.RepoPath, sanitizedName, iconOutputPath);
                if (iconResult != null)
                {
                    iconName = iconResult;
                    pkgsInfo.IconName = iconName;
                    Console.WriteLine($"Icon extracted: {iconName}");
                }
                else
                {
                    Console.WriteLine("[WARN] Could not extract icon from this installer type");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Icon extraction failed: {ex.Message}");
            }
        }
        // Step 12: Copy installer to pkgs subdir
        Console.WriteLine("Copying installer to repo...");
        repoSubPath = repoSubPath.TrimStart('\\');
        var installerFolderPath = Path.Combine(config.RepoPath, "pkgs", repoSubPath);
        Directory.CreateDirectory(installerFolderPath);
        
        var installerFilename = $"{sanitizedName}{archTag}{pkgsInfo.Version}{Path.GetExtension(packagePath)}";
        var installerDest = Path.Combine(installerFolderPath, installerFilename);
        File.Copy(packagePath, installerDest, overwrite: true);

        var subpathAndFile = Path.Combine(repoSubPath, installerFilename);
        pkgsInfo.Installer!.Location = MetadataExtractor.NormalizeWindowsPath(subpathAndFile);

        // Step 13: Write pkginfo YAML
        Console.WriteLine("Writing pkginfo file...");
        var pkginfoFolderPath = Path.Combine(config.RepoPath, "pkgsinfo", repoSubPath);
        Directory.CreateDirectory(pkginfoFolderPath);

        var pkginfoFilename = $"{sanitizedName}{archTag}{pkgsInfo.Version}.yaml";
        var pkginfoPath = Path.Combine(pkginfoFolderPath, pkginfoFilename);

        var yaml = SerializePkgsInfoWithKeyOrder(pkgsInfo);
        await File.WriteAllTextAsync(pkginfoPath, yaml);

        Console.WriteLine($"Pkginfo created at: {pkginfoPath}");

        // Open in editor if configured. Suppressed under --nointeractive: the editor
        // (VS Code / Notepad) inherits the parent process's stdio, and when cimiimport
        // is itself invoked with redirected stdio (e.g. PowerShell `& cimiimport ... 2>&1`),
        // the editor's grandchild handles keep the pipe open after cimiimport exits and
        // deadlock the caller. CI must never spawn an editor.
        if (config.OpenImportedYaml && !noInteractive)
        {
            TryOpenFile(pkginfoPath);
        }

        Console.WriteLine("Installer imported successfully!");
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
            var catalog = _deserializer.Deserialize<AllCatalog>(yaml);

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
    /// Prompts user for metadata fields.
    /// </summary>
    private InstallerMetadata PromptForMetadata(string packagePath, InstallerMetadata m, ImportConfiguration config)
    {
        var defaultID = !string.IsNullOrEmpty(m.ID) ? m.ID : Path.GetFileNameWithoutExtension(packagePath);
        var defaultVersion = !string.IsNullOrEmpty(m.Version) ? m.Version : "1.0.0";

        m.ID = ReadLineWithDefault("Name", defaultID);
        m.Version = MetadataExtractor.ParseVersion(ReadLineWithDefault("Version", defaultVersion));
        m.Developer = ReadLineWithDefault("Developer", m.Developer);
        m.Description = ReadLineWithDefault("Description", m.Description);
        m.Category = ReadLineWithDefault("Category", m.Category);

        // Architectures
        var archDefault = string.Join(",", m.SupportedArch);
        var archLine = ReadLineWithDefault("Architecture(s)", archDefault).Trim();
        if (!string.IsNullOrEmpty(archLine))
        {
            var parts = archLine.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            m.SupportedArch = parts.Select(p => p.Trim().ToLowerInvariant()).ToList();
            if (m.SupportedArch.Count > 0)
            {
                m.Architecture = m.SupportedArch[0];
            }
        }

        // Catalogs
        var fallbackCatalogs = m.Catalogs.Count > 0 ? m.Catalogs : [config.DefaultCatalog];
        var catalogsStr = string.Join(", ", fallbackCatalogs);
        var typedCatalogs = ReadLineWithDefault("Catalogs", catalogsStr);
        if (typedCatalogs == catalogsStr)
        {
            m.Catalogs = fallbackCatalogs;
        }
        else
        {
            m.Catalogs = typedCatalogs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim())
                .ToList();
        }

        return m;
    }

    /// <summary>
    /// Prompts for installer location in repo.
    /// </summary>
    private string PromptInstallerPath(string? defaultPath, bool noInteractive = false)
    {
        defaultPath ??= @"\mgmt";
        
        if (noInteractive)
        {
            // Use default path without prompting
            var result = defaultPath;
            if (!result.StartsWith('\\'))
            {
                result = "\\" + result;
            }
            return result.TrimEnd('\\');
        }
        
        Console.Write($"Location in repo [{defaultPath}]: ");
        var path = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            path = defaultPath;
        }
        if (!path.StartsWith('\\'))
        {
            path = "\\" + path;
        }
        return path.TrimEnd('\\');
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
    private Installer? ProcessUninstaller(string uninstallerPath, string repoPath)
    {
        if (!File.Exists(uninstallerPath))
        {
            Console.WriteLine($"[WARN] Uninstaller '{uninstallerPath}' does not exist");
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
            Console.WriteLine($"[WARN] Failed to process uninstaller: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds installs array from file paths.
    /// </summary>
    private List<InstallItem> BuildInstallsArray(List<string> paths)
    {
        var items = new List<InstallItem>();
        foreach (var p in paths)
        {
            var absPath = Path.GetFullPath(p);
            if (!File.Exists(absPath) || Directory.Exists(absPath))
            {
                Console.WriteLine($"Skipping -i path: '{p}'");
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
    /// Reads a line with default value.
    /// </summary>
    private static string ReadLineWithDefault(string prompt, string defaultVal)
    {
        Console.Write($"{prompt} [{defaultVal}]: ");
        var line = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(line) ? defaultVal : line;
    }

    /// <summary>
    /// Serializes PkgsInfo with custom key ordering:
    /// 1. name
    /// 2. display_name
    /// 3. version
    /// 4. all other keys alphabetically
    /// 5. _metadata (if present)
    /// </summary>
    private string SerializePkgsInfoWithKeyOrder(PkgsInfo pkgsInfo)
    {
        var sb = new System.Text.StringBuilder();

        // Priority keys in order
        sb.AppendLine($"name: {pkgsInfo.Name}");
        
        if (!string.IsNullOrEmpty(pkgsInfo.DisplayName))
            sb.AppendLine($"display_name: {pkgsInfo.DisplayName}");
        
        sb.AppendLine($"version: {pkgsInfo.Version}");

        // Collect all other non-null properties alphabetically
        var otherProps = new SortedDictionary<string, object?>(StringComparer.Ordinal);

        // Add blocking_applications if present
        if (pkgsInfo.BlockingApps != null && pkgsInfo.BlockingApps.Count > 0)
            otherProps["blocking_applications"] = pkgsInfo.BlockingApps;

        if (pkgsInfo.Catalogs.Count > 0)
            otherProps["catalogs"] = pkgsInfo.Catalogs;

        if (!string.IsNullOrEmpty(pkgsInfo.Category))
            otherProps["category"] = pkgsInfo.Category;

        if (!string.IsNullOrEmpty(pkgsInfo.Description))
            otherProps["description"] = pkgsInfo.Description;

        if (!string.IsNullOrEmpty(pkgsInfo.Developer))
            otherProps["developer"] = pkgsInfo.Developer;

        if (!string.IsNullOrEmpty(pkgsInfo.IconName))
            otherProps["icon_name"] = pkgsInfo.IconName;

        if (!string.IsNullOrEmpty(pkgsInfo.Identifier))
            otherProps["identifier"] = pkgsInfo.Identifier;

        if (!string.IsNullOrEmpty(pkgsInfo.InstallCheckScript))
            otherProps["installcheck_script"] = pkgsInfo.InstallCheckScript;

        if (pkgsInfo.Installer != null)
            otherProps["installer"] = pkgsInfo.Installer;

        if (pkgsInfo.Installs != null && pkgsInfo.Installs.Count > 0)
            otherProps["installs"] = pkgsInfo.Installs;

        if (!string.IsNullOrEmpty(pkgsInfo.MaxOSVersion))
            otherProps["maximum_os_version"] = pkgsInfo.MaxOSVersion;

        if (!string.IsNullOrEmpty(pkgsInfo.MinOSVersion))
            otherProps["minimum_os_version"] = pkgsInfo.MinOSVersion;

        if (!string.IsNullOrEmpty(pkgsInfo.MinCimianVersion))
            otherProps["minimum_cimian_version"] = pkgsInfo.MinCimianVersion;

        if (!string.IsNullOrEmpty(pkgsInfo.PostinstallScript))
            otherProps["postinstall_script"] = pkgsInfo.PostinstallScript;

        if (!string.IsNullOrEmpty(pkgsInfo.PostuninstallScript))
            otherProps["postuninstall_script"] = pkgsInfo.PostuninstallScript;

        if (!string.IsNullOrEmpty(pkgsInfo.PreinstallScript))
            otherProps["preinstall_script"] = pkgsInfo.PreinstallScript;

        if (!string.IsNullOrEmpty(pkgsInfo.PreuninstallScript))
            otherProps["preuninstall_script"] = pkgsInfo.PreuninstallScript;

        if (pkgsInfo.Requires != null && pkgsInfo.Requires.Count > 0)
            otherProps["requires"] = pkgsInfo.Requires;

        if (pkgsInfo.SupportedArch.Count > 0)
            otherProps["supported_architectures"] = pkgsInfo.SupportedArch;

        // Always include unattended_install and unattended_uninstall
        otherProps["unattended_install"] = pkgsInfo.UnattendedInstall;
        otherProps["unattended_uninstall"] = pkgsInfo.UnattendedUninstall;

        if (!string.IsNullOrEmpty(pkgsInfo.UninstallCheckScript))
            otherProps["uninstallcheck_script"] = pkgsInfo.UninstallCheckScript;

        if (pkgsInfo.Uninstaller != null)
            otherProps["uninstaller"] = pkgsInfo.Uninstaller;

        if (pkgsInfo.UpdateFor != null && pkgsInfo.UpdateFor.Count > 0)
            otherProps["update_for"] = pkgsInfo.UpdateFor;

        // Build serializer for nested objects
        var nestedSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .Build();

        // Write other properties in alphabetical order
        foreach (var kvp in otherProps)
        {
            if (kvp.Value is bool boolVal)
            {
                sb.AppendLine($"{kvp.Key}: {boolVal.ToString().ToLowerInvariant()}");
            }
            else if (kvp.Value is string strVal)
            {
                // Check if string contains newlines (multi-line script)
                if (strVal.Contains('\n'))
                {
                    sb.AppendLine($"{kvp.Key}: |");
                    foreach (var line in strVal.Split('\n'))
                    {
                        sb.AppendLine($"  {line.TrimEnd('\r')}");
                    }
                }
                else
                {
                    sb.AppendLine($"{kvp.Key}: {EscapeYamlString(strVal)}");
                }
            }
            else if (kvp.Value is List<string> listVal)
            {
                sb.AppendLine($"{kvp.Key}:");
                foreach (var item in listVal)
                {
                    sb.AppendLine($"- {item}");
                }
            }
            else if (kvp.Value is Installer installer)
            {
                sb.AppendLine($"{kvp.Key}:");
                // Output installer properties in a specific order
                if (!string.IsNullOrEmpty(installer.Type))
                    sb.AppendLine($"  type: {installer.Type}");
                if (installer.Size > 0)
                    sb.AppendLine($"  size: {installer.Size}");
                if (!string.IsNullOrEmpty(installer.Location))
                    sb.AppendLine($"  location: {installer.Location}");
                if (!string.IsNullOrEmpty(installer.Hash))
                    sb.AppendLine($"  hash: {installer.Hash}");
                if (!string.IsNullOrEmpty(installer.ProductCode))
                    sb.AppendLine($"  product_code: {EscapeYamlString(installer.ProductCode)}");
                if (!string.IsNullOrEmpty(installer.UpgradeCode))
                    sb.AppendLine($"  upgrade_code: {EscapeYamlString(installer.UpgradeCode)}");
                if (installer.Arguments != null && installer.Arguments.Count > 0)
                {
                    sb.AppendLine("  arguments:");
                    foreach (var arg in installer.Arguments)
                    {
                        sb.AppendLine($"  - {arg}");
                    }
                }
            }
            else if (kvp.Value is List<InstallItem> installItems)
            {
                sb.AppendLine($"{kvp.Key}:");
                foreach (var item in installItems)
                {
                    sb.AppendLine($"- type: {item.Type}");
                    if (!string.IsNullOrEmpty(item.Path))
                        sb.AppendLine($"  path: {item.Path}");
                    if (!string.IsNullOrEmpty(item.MD5Checksum))
                        sb.AppendLine($"  md5checksum: {item.MD5Checksum}");
                    if (!string.IsNullOrEmpty(item.Version))
                        sb.AppendLine($"  version: {item.Version}");
                    if (!string.IsNullOrEmpty(item.ProductCode))
                        sb.AppendLine($"  product_code: {EscapeYamlString(item.ProductCode)}");
                    if (!string.IsNullOrEmpty(item.UpgradeCode))
                        sb.AppendLine($"  upgrade_code: {EscapeYamlString(item.UpgradeCode)}");
                    if (!string.IsNullOrEmpty(item.IdentityName))
                        sb.AppendLine($"  identity_name: {item.IdentityName}");
                }
            }
            else if (kvp.Value is List<Installer> installerList)
            {
                // Used for the uninstaller block (emitted as a list for managedsoftwareupdate compatibility).
                sb.AppendLine($"{kvp.Key}:");
                foreach (var inst in installerList)
                {
                    sb.AppendLine($"- type: {inst.Type}");
                    if (inst.Size > 0)
                        sb.AppendLine($"  size: {inst.Size}");
                    if (!string.IsNullOrEmpty(inst.Location))
                        sb.AppendLine($"  location: {inst.Location}");
                    if (!string.IsNullOrEmpty(inst.Hash))
                        sb.AppendLine($"  hash: {inst.Hash}");
                    if (!string.IsNullOrEmpty(inst.ProductCode))
                        sb.AppendLine($"  product_code: {EscapeYamlString(inst.ProductCode)}");
                    if (!string.IsNullOrEmpty(inst.UpgradeCode))
                        sb.AppendLine($"  upgrade_code: {EscapeYamlString(inst.UpgradeCode)}");
                    if (!string.IsNullOrEmpty(inst.IdentityName))
                        sb.AppendLine($"  identity_name: {inst.IdentityName}");
                    if (inst.Arguments != null && inst.Arguments.Count > 0)
                    {
                        sb.AppendLine("  arguments:");
                        foreach (var arg in inst.Arguments)
                        {
                            sb.AppendLine($"  - {arg}");
                        }
                    }
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes a string for YAML if needed.
    /// </summary>
    private static string EscapeYamlString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        // Check if the string needs quoting
        if (value.Contains(':') || value.Contains('#') || value.Contains('\'') || 
            value.Contains('"') || value.StartsWith(' ') || value.EndsWith(' ') ||
            value.StartsWith('-') || value.StartsWith('[') || value.StartsWith('{'))
        {
            // Use single quotes and escape any existing single quotes
            return $"'{value.Replace("'", "''")}'";
        }

        return value;
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
