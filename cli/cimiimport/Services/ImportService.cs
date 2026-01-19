using System.Diagnostics;
using Cimian.CLI.Cimiimport.Models;
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
        Console.WriteLine("[INFO] Extracting metadata...");
        var metadata = _metadataExtractor.ExtractMetadata(packagePath, config);
        if (string.IsNullOrEmpty(metadata.ID))
        {
            metadata.ID = Path.GetFileNameWithoutExtension(packagePath);
        }

        // Detect architecture from filename (this takes priority)
        var filenameArch = MetadataExtractor.DetectArchFromFilename(Path.GetFileName(packagePath));
        var hasFilenameArch = !string.IsNullOrEmpty(filenameArch);

        // Step 3: Check for existing item in All.yaml
        var (existingPkg, found) = FindMatchingItemInAllCatalog(config.RepoPath, metadata.ID);
        if (found && existingPkg != null)
        {
            Console.WriteLine("[INFO] This item has the same Name as an existing item in the repo:");
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
        Console.WriteLine("[INFO] Calculating file hash...");
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
            Installer = new Installer
            {
                Hash = fileHash,
                Type = metadata.InstallerType,
                Size = fileSizeKB,
                ProductCode = string.IsNullOrEmpty(metadata.ProductCode) ? null : metadata.ProductCode.Trim(),
                UpgradeCode = string.IsNullOrEmpty(metadata.UpgradeCode) ? null : metadata.UpgradeCode.Trim()
            },
            Uninstaller = uninstaller,
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
        else
        {
            finalInstalls = [];
        }

        if (metadata.Installs.Count > 0)
        {
            finalInstalls.AddRange(metadata.Installs);
        }
        pkgsInfo.Installs = finalInstalls.Count > 0 ? finalInstalls : null;

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
            Console.WriteLine("[INFO] Extracting icon (EXPERIMENTAL)...");
            try
            {
                var iconExtractor = new IconExtractor();
                var iconResult = iconExtractor.ExtractIconToPng(packagePath, config.RepoPath, sanitizedName, iconOutputPath);
                if (iconResult != null)
                {
                    iconName = iconResult;
                    pkgsInfo.IconName = iconName;
                    Console.WriteLine($"[OK] Icon extracted: {iconName}");
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
        Console.WriteLine("[INFO] Copying installer to repo...");
        repoSubPath = repoSubPath.TrimStart('\\');
        var installerFolderPath = Path.Combine(config.RepoPath, "pkgs", repoSubPath);
        Directory.CreateDirectory(installerFolderPath);
        
        var installerFilename = $"{sanitizedName}{archTag}{pkgsInfo.Version}{Path.GetExtension(packagePath)}";
        var installerDest = Path.Combine(installerFolderPath, installerFilename);
        File.Copy(packagePath, installerDest, overwrite: true);

        var subpathAndFile = Path.Combine(repoSubPath, installerFilename);
        pkgsInfo.Installer!.Location = MetadataExtractor.NormalizeWindowsPath(subpathAndFile);

        // Step 13: Write pkginfo YAML
        Console.WriteLine("[INFO] Writing pkginfo file...");
        var pkginfoFolderPath = Path.Combine(config.RepoPath, "pkgsinfo", repoSubPath);
        Directory.CreateDirectory(pkginfoFolderPath);

        var pkginfoFilename = $"{sanitizedName}{archTag}{pkgsInfo.Version}.yaml";
        var pkginfoPath = Path.Combine(pkginfoFolderPath, pkginfoFilename);

        var yaml = _serializer.Serialize(pkgsInfo);
        await File.WriteAllTextAsync(pkginfoPath, yaml);

        Console.WriteLine($"[OK] Pkginfo created at: {pkginfoPath}");

        // Open in editor if configured
        if (config.OpenImportedYaml)
        {
            TryOpenFile(pkginfoPath);
        }

        Console.WriteLine("[OK] Installer imported successfully!");
        return true;
    }

    /// <summary>
    /// Finds a matching item in All.yaml catalog.
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
            var match = catalog?.Items?.FirstOrDefault(i => 
                i.Name.Trim().ToLowerInvariant() == newNameLower);

            return (match, match != null);
        }
        catch
        {
            return (null, false);
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
        // Preserve blocking_applications from template
        if (existing.BlockingApps != null && existing.BlockingApps.Count > 0)
        {
            metadata.BlockingApps = existing.BlockingApps;
        }

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
    /// Replaces user profile path with variable.
    /// </summary>
    private static string ReplacePathUserProfile(string path)
    {
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(userProfile))
            return path;

        if (path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return @"C:\Users\%USERPROFILE%" + path[userProfile.Length..];
        }
        return path;
    }

    /// <summary>
    /// Runs makecatalogs.
    /// </summary>
    private void RunMakeCatalogs(string repoPath, bool silent)
    {
        var makeCatalogsBinary = @"C:\Program Files\Cimian\makecatalogs.exe";
        if (!File.Exists(makeCatalogsBinary))
        {
            return; // Silently skip if not found
        }

        try
        {
            var args = silent ? "--silent" : "";
            var psi = new ProcessStartInfo
            {
                FileName = makeCatalogsBinary,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };
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
            Console.WriteLine($"[INFO] Running git pull in: {repoPath}");
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
                    Console.WriteLine("[OK] Git pull completed successfully");
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
