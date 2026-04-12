using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for installing/uninstalling packages
/// Migrated from Go pkg/installer (3,308 lines)
/// 
/// Supports:
/// - sbin-installer for .pkg and .nupkg (PRIMARY - matches Go)
/// - MSI via msiexec.exe
/// - EXE with silent switches
/// - Chocolatey fallback for .nupkg
/// - MSIX/AppX via PowerShell
/// - PowerShell scripts
/// </summary>
public class InstallerService
{
    // sbin-installer paths (matches Go: detectSbinInstaller)
    private const string SbinInstallerPath = @"C:\Program Files\sbin\installer.exe";
    private const string SbinInstallerPathAlt = @"C:\Program Files (x86)\sbin\installer.exe";
    
    private readonly CimianConfig _config;
    private readonly ScriptService _scriptService;
    private SessionLogger? _sessionLogger;
    
    // Cached sbin-installer path (null = not checked, empty = not available)
    private static string? _sbinInstallerBin;

    public InstallerService(CimianConfig config)
    {
        _config = config;
        _scriptService = new ScriptService();
    }

    /// <summary>
    /// Sets the session logger for structured event logging
    /// </summary>
    public void SetSessionLogger(SessionLogger? logger)
    {
        _sessionLogger = logger;
    }

    #region sbin-installer Support (Ported from Go pkg/installer)

    /// <summary>
    /// Detects and returns the sbin-installer path if available.
    /// Matches Go: detectSbinInstaller()
    /// </summary>
    private string? DetectSbinInstaller()
    {
        // Return cached result if already checked
        if (_sbinInstallerBin != null)
        {
            return string.IsNullOrEmpty(_sbinInstallerBin) ? null : _sbinInstallerBin;
        }

        // Check configured path first
        if (!string.IsNullOrEmpty(_config.SbinInstallerPath) && File.Exists(_config.SbinInstallerPath))
        {
            ConsoleLogger.Debug($"Using configured sbin-installer path: {_config.SbinInstallerPath}");
            _sbinInstallerBin = _config.SbinInstallerPath;
            return _sbinInstallerBin;
        }

        // Try common installation paths
        string[] commonPaths = 
        {
            SbinInstallerPath,
            SbinInstallerPathAlt,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "sbin", "installer.exe"),
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                ConsoleLogger.Debug($"Found sbin-installer at: {path}");
                _sbinInstallerBin = path;
                return _sbinInstallerBin;
            }
        }

        ConsoleLogger.Debug("sbin-installer not found in any common locations");
        _sbinInstallerBin = ""; // Mark as checked but not found
        return null;
    }

    /// <summary>
    /// Checks if sbin-installer is available and functional.
    /// Matches Go: isSbinInstallerAvailable()
    /// </summary>
    private bool IsSbinInstallerAvailable()
    {
        // If Chocolatey is forced, don't use sbin-installer
        if (_config.ForceChocolatey)
        {
            ConsoleLogger.Debug("Chocolatey forced via configuration, skipping sbin-installer");
            return false;
        }

        var sbinPath = DetectSbinInstaller();
        if (string.IsNullOrEmpty(sbinPath))
        {
            return false;
        }

        // Test that it's functional by checking version
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var startInfo = new ProcessStartInfo
            {
                FileName = sbinPath,
                Arguments = "--vers",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return false;
            
            process.WaitForExit(10000);
            if (process.ExitCode == 0)
            {
                ConsoleLogger.Debug($"sbin-installer is available and functional: {sbinPath}");
                return true;
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"sbin-installer version check failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Executes sbin-installer for a package file.
    /// Matches Go: runSbinInstaller()
    /// </summary>
    private async Task<(bool Success, string Output)> RunSbinInstallerAsync(
        string packagePath,
        CatalogItem item,
        CancellationToken cancellationToken)
    {
        var sbinPath = DetectSbinInstaller();
        if (string.IsNullOrEmpty(sbinPath))
        {
            return (false, "sbin-installer not available");
        }

        var target = _config.SbinInstallerTargetRoot ?? "/";

        ConsoleLogger.Info($"Installing package with sbin-installer: {item.Name}");
        _sessionLogger?.Log("INFO", $"Installing package with sbin-installer: {item.Name} -> {packagePath}");
        _sessionLogger?.LogInstall(item.Name, item.Version, "install", "started", 
            $"sbin-installer installation started for {item.Name}");

        // Build command arguments (matches Go)
        // TODO(pkg-sunset): Remove --pkg flag usage
        var argsBuilder = new StringBuilder($"--pkg \"{packagePath}\" --target {target} --verbose");

        // Add temp-dir if specified in pkginfo (helps avoid MAX_PATH 260 char limit issues)
        // Priority: per-package temp_dir > flags containing temp-dir
        if (!string.IsNullOrWhiteSpace(item.Installer?.TempDir))
        {
            argsBuilder.Append($" --temp-dir \"{item.Installer.TempDir}\"");
            ConsoleLogger.Debug($"Using per-package temp directory: {item.Installer.TempDir}");
        }

        // Process flags from pkginfo (similar to MSI/EXE flag handling)
        // Supports: temp-dir, --temp-dir, temp-dir=C:\path, --temp-dir=C:\path
        if (item.Installer?.Flags != null)
        {
            foreach (var flag in item.Installer.Flags)
            {
                var trimmedFlag = flag?.Trim();
                if (string.IsNullOrEmpty(trimmedFlag))
                    continue;

                // If user already provided dashes, preserve their exact format
                if (trimmedFlag.StartsWith("--") || trimmedFlag.StartsWith("-"))
                {
                    if (trimmedFlag.Contains('='))
                    {
                        // User used = format (e.g., "--temp-dir=C:\path"), split for sbin-installer
                        var parts = trimmedFlag.Split('=', 2);
                        argsBuilder.Append($" {parts[0]} \"{parts[1]}\"");
                    }
                    else if (trimmedFlag.Contains(' '))
                    {
                        // User used space format (e.g., "--temp-dir C:\path")
                        var parts = trimmedFlag.Split(' ', 2);
                        argsBuilder.Append($" {parts[0]} \"{parts[1].Trim()}\"");
                    }
                    else
                    {
                        // Single flag without value (e.g., "--verbose")
                        argsBuilder.Append($" {trimmedFlag}");
                    }
                }
                else
                {
                    // User provided without dashes - add -- prefix (sbin-installer convention)
                    string key, val = "";
                    if (trimmedFlag.Contains('='))
                    {
                        var parts = trimmedFlag.Split('=', 2);
                        key = parts[0];
                        val = parts[1];
                    }
                    else if (trimmedFlag.Contains(' '))
                    {
                        var parts = trimmedFlag.Split(' ', 2);
                        key = parts[0];
                        val = parts[1].Trim();
                    }
                    else
                    {
                        key = trimmedFlag;
                    }

                    if (!string.IsNullOrEmpty(val))
                    {
                        argsBuilder.Append($" --{key} \"{val}\"");
                    }
                    else
                    {
                        argsBuilder.Append($" --{key}");
                    }
                }
            }
        }

        var args = argsBuilder.ToString();

        // Use configured timeout or default to 30 minutes
        var timeoutMinutes = _config.InstallerTimeout > 0 
            ? _config.InstallerTimeout / 60 
            : 30;

        ConsoleLogger.Debug($"sbin-installer command: {sbinPath} {args}");

        var startInfo = new ProcessStartInfo
        {
            FileName = sbinPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    output.AppendLine(e.Data);
                    ConsoleLogger.Debug($"  {e.Data}");
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    output.AppendLine($"ERROR: {e.Data}");
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                var errorMsg = $"sbin-installer timed out after {timeoutMinutes} minutes";
                ConsoleLogger.Error(errorMsg);
                _sessionLogger?.LogInstall(item.Name, item.Version, "install", "failed", errorMsg);
                return (false, errorMsg);
            }

            var outputStr = output.ToString();

            if (process.ExitCode == 0)
            {
                ConsoleLogger.Success($"sbin-installer completed successfully for {item.Name}");
                _sessionLogger?.Log("INFO", $"sbin-installer completed successfully for {item.Name}");
                _sessionLogger?.LogInstall(item.Name, item.Version, "install", "completed",
                    $"sbin-installer installation succeeded for {item.Name}");
                return (true, outputStr);
            }
            else
            {
                var errorMsg = $"sbin-installer failed with exit code: {process.ExitCode}";
                ConsoleLogger.Error(errorMsg);
                _sessionLogger?.Log("ERROR", $"{errorMsg}\n{outputStr}");
                _sessionLogger?.LogInstall(item.Name, item.Version, "install", "failed", errorMsg);
                return (false, $"{errorMsg}\n{outputStr}");
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"sbin-installer execution failed: {ex.Message}";
            ConsoleLogger.Error(errorMsg);
            _sessionLogger?.LogInstall(item.Name, item.Version, "install", "failed", errorMsg, ex.Message);
            return (false, errorMsg);
        }
    }

    // TODO(pkg-sunset): Remove InstallPkgWithSbinAsync method
    /// <summary>
    /// Installs a .pkg package using sbin-installer.
    /// Matches Go: installOrUpgradePkgWithSbin()
    /// </summary>
    private async Task<(bool Success, string Output)> InstallPkgWithSbinAsync(
        CatalogItem item,
        string packagePath,
        CancellationToken cancellationToken)
    {
        ConsoleLogger.Info($"Installing .pkg package: {item.Name}");
        _sessionLogger?.Log("INFO", $"Installing .pkg package with sbin-installer: {item.Name}");

        // Try to extract and log package metadata (build-info.yaml)
        var buildInfo = ExtractPkgBuildInfo(packagePath);
        if (buildInfo != null)
        {
            ConsoleLogger.Detail($"Package ID: {buildInfo.ProductIdentifier}");
            ConsoleLogger.Detail($"Package Version: {buildInfo.ProductVersion}");
            ConsoleLogger.Detail($"Developer: {buildInfo.Developer}");
            ConsoleLogger.Detail($"Architecture: {buildInfo.Architecture}");
            
            _sessionLogger?.Log("INFO", $"Package metadata: {buildInfo.ProductIdentifier} v{buildInfo.ProductVersion} by {buildInfo.Developer}");

            // Verify architecture compatibility
            if (!IsArchitectureCompatible(buildInfo.Architecture))
            {
                var archError = $"Package architecture '{buildInfo.Architecture}' is not compatible with system";
                ConsoleLogger.Warn(archError);
                _sessionLogger?.Log("WARN", archError);
                // Don't fail - let sbin-installer handle it (it may have its own logic)
            }

            // Verify package signature if signature info is present
            if (buildInfo.Signature != null)
            {
                var (signatureValid, signatureDetails) = VerifyPkgSignature(packagePath, buildInfo);
                if (signatureValid)
                {
                    ConsoleLogger.Success($"✓ Signature verified: {signatureDetails}");
                    _sessionLogger?.Log("INFO", $"Signature verified for {item.Name}: {signatureDetails}");
                }
                else
                {
                    ConsoleLogger.Warn($"⚠ Signature verification failed: {signatureDetails}");
                    _sessionLogger?.Log("WARN", $"Signature verification failed for {item.Name}: {signatureDetails}");
                    
                    // Check if signature is required by config
                    if (_config?.PkgRequireSignature == true)
                    {
                        var error = $"Package signature verification required but failed: {signatureDetails}";
                        ConsoleLogger.Error(error);
                        _sessionLogger?.Log("ERROR", error);
                        return (false, error);
                    }
                }
            }
            else
            {
                ConsoleLogger.Detail("Package is unsigned");
                _sessionLogger?.Log("INFO", $"Package {item.Name} is unsigned");
            }
        }

        // Execute sbin-installer
        var result = await RunSbinInstallerAsync(packagePath, item, cancellationToken);

        if (result.Success)
        {
            // Store enhanced version information in registry
            RegisterInstallationWithPkgInfo(item, buildInfo);
        }

        return result;
    }

    /// <summary>
    /// Installs a .nupkg package using sbin-installer with Chocolatey fallback.
    /// Matches Go: installOrUpgradeNupkgWithSbin() and installOrUpgradePackage()
    /// </summary>
    private async Task<(bool Success, string Output)> InstallNupkgWithSbinAsync(
        CatalogItem item,
        string packagePath,
        CancellationToken cancellationToken)
    {
        // Try sbin-installer first if available
        if (IsSbinInstallerAvailable())
        {
            ConsoleLogger.Info($"[INSTALLER METHOD: sbin-installer] Attempting .nupkg installation: {item.Name}");
            _sessionLogger?.Log("INFO", $"Attempting .nupkg installation with sbin-installer: {item.Name}");

            var result = await RunSbinInstallerAsync(packagePath, item, cancellationToken);
            if (result.Success)
            {
                ConsoleLogger.Success($"[INSTALLER METHOD: sbin-installer] Installation successful: {item.Name}");
                return result;
            }

            // Log fallback
            ConsoleLogger.Warn($"[INSTALLER METHOD: sbin-installer → choco] sbin-installer failed, falling back to Chocolatey");
            _sessionLogger?.Log("WARN", $"sbin-installer failed for {item.Name}, attempting Chocolatey fallback");
        }

        // Fallback to Chocolatey
        ConsoleLogger.Info($"[INSTALLER METHOD: choco] Using Chocolatey for .nupkg installation: {item.Name}");
        return await InstallChocolateyAsync(item, packagePath, cancellationToken);
    }

    /// <summary>
    /// Extracts build-info.yaml from a .pkg file.
    /// Matches Go: extract.ExtractPkgBuildInfo()
    /// </summary>
    private PkgBuildInfo? ExtractPkgBuildInfo(string packagePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var buildInfoEntry = archive.GetEntry("build-info.yaml");
            if (buildInfoEntry == null)
            {
                ConsoleLogger.Debug("No build-info.yaml found in package");
                return null;
            }

            using var stream = buildInfoEntry.Open();
            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            return deserializer.Deserialize<PkgBuildInfo>(yaml);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"Failed to extract build-info.yaml: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks if package architecture is compatible with the current system.
    /// </summary>
    private bool IsArchitectureCompatible(string? packageArch)
    {
        if (string.IsNullOrEmpty(packageArch) || packageArch.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var systemArch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "unknown"
        };

        // x64 packages can run on arm64 via emulation
        if (packageArch.Equals("x64", StringComparison.OrdinalIgnoreCase) && systemArch == "arm64")
        {
            return true; // Windows ARM64 can run x64 via emulation
        }

        return packageArch.Equals(systemArch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers installation with enhanced .pkg metadata.
    /// Matches Go: storeInstalledVersionInRegistryWithPkgInfo()
    /// </summary>
    private void RegisterInstallationWithPkgInfo(CatalogItem item, PkgBuildInfo? buildInfo)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\ManagedInstalls\{item.Name}");
            if (key == null) return;

            var version = !string.IsNullOrEmpty(item.Version) ? item.Version 
                : buildInfo?.ProductVersion ?? "0.0.0";
            
            key.SetValue("Version", version);
            key.SetValue("DisplayName", item.DisplayName ?? item.Name);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyy-MM-dd"));
            key.SetValue("InstallerType", "pkg");

            if (buildInfo != null)
            {
                if (!string.IsNullOrEmpty(buildInfo.ProductIdentifier))
                    key.SetValue("PackageIdentifier", buildInfo.ProductIdentifier);
                if (!string.IsNullOrEmpty(buildInfo.Developer))
                    key.SetValue("Developer", buildInfo.Developer);
                if (!string.IsNullOrEmpty(buildInfo.Architecture))
                    key.SetValue("Architecture", buildInfo.Architecture);
            }

            // If this is Cimian, also update the main Cimian registry key
            var itemName = item.Name.ToLowerInvariant();
            if (itemName == "cimian" || itemName == "cimiantools" || 
                itemName.StartsWith("cimian-") || itemName.StartsWith("cimiantools-"))
            {
                using var cimianKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Cimian");
                cimianKey?.SetValue("Version", version);
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to register installation with pkg info: {ex.Message}");
        }
    }

    #endregion

    /// <summary>
    /// Installs a catalog item
    /// </summary>
    public async Task<(bool Success, string Output)> InstallAsync(
        CatalogItem item,
        string localFile,
        CancellationToken cancellationToken = default)
    {
        ConsoleLogger.Info($"Installing {item.Name} v{item.Version}...");
        _sessionLogger?.Log("INFO", $"Starting installation: {item.Name} v{item.Version}");
        _sessionLogger?.LogInstall(item.Name, item.Version, "install", "started", $"Installing {item.Name}");

        // Run preinstall script if present
        if (!string.IsNullOrEmpty(item.PreinstallScript))
        {
            ConsoleLogger.Info($"Running preinstall script for {item.Name}...");
            _sessionLogger?.Log("INFO", $"Executing preinstall script for {item.Name}");
            var preResult = await _scriptService.ExecuteScriptAsync(item.PreinstallScript, cancellationToken);
            if (!preResult.Success)
            {
                var errorMsg = $"Preinstall script failed: {preResult.Output}";
                _sessionLogger?.LogInstall(item.Name, item.Version, "install", "failed", errorMsg);
                return (false, errorMsg);
            }
        }

        // Determine installer type
        var installerType = GetInstallerType(item, localFile);
        ConsoleLogger.Detail($"Installer type: {installerType}");
        _sessionLogger?.Log("DEBUG", $"Using installer type: {installerType} for {item.Name}");
        
        var result = installerType.ToLowerInvariant() switch
        {
            // TODO(pkg-sunset): Remove .pkg format switch case
            // PRIMARY: .pkg files use sbin-installer (matches Go behavior)
            "pkg" => await InstallPkgWithSbinAsync(item, localFile, cancellationToken),
            
            // .nupkg files: try sbin-installer first, fallback to Chocolatey
            "nupkg" => await InstallNupkgWithSbinAsync(item, localFile, cancellationToken),
            
            // Legacy Chocolatey (explicit request)
            "chocolatey" => await InstallChocolateyAsync(item, localFile, cancellationToken),
            
            // nopkg / script-only: no installer binary, run install_script directly
            "nopkg" or "script" => await InstallScriptOnlyAsync(item, cancellationToken),
            
            // Standard installers
            "msi" => await InstallMsiAsync(item, localFile, cancellationToken),
            "exe" => await InstallExeAsync(item, localFile, cancellationToken),
            "msix" or "appx" => await InstallMsixAsync(item, localFile, cancellationToken),
            "powershell" or "ps1" => await InstallPowerShellAsync(item, localFile, cancellationToken),
            _ => await InstallExeAsync(item, localFile, cancellationToken) // Default to EXE
        };

        if (!result.Success)
        {
            _sessionLogger?.LogInstall(item.Name, item.Version, "install", "failed", result.Output);
            return result;
        }

        // Run postinstall script if present
        if (!string.IsNullOrEmpty(item.PostinstallScript))
        {
            ConsoleLogger.Info($"Running postinstall script for {item.Name}...");
            _sessionLogger?.Log("INFO", $"Executing postinstall script for {item.Name}");
            var postResult = await _scriptService.ExecuteScriptAsync(item.PostinstallScript, cancellationToken);
            if (!postResult.Success)
            {
                ConsoleLogger.Warn($"Postinstall script failed: {postResult.Output}");
                _sessionLogger?.Log("WARN", $"Postinstall script failed for {item.Name}: {postResult.Output}");
                // Don't fail the installation for postinstall script failures
            }
        }

        // Verify installation before registering (prevents phantom installs)
        if (installerType != "pkg")
        {
            var (verifyOk, verifyReason) = VerifyInstallationBeforeRegistry(item);
            if (verifyOk)
            {
                RegisterInstallation(item);
            }
            else
            {
                var verifyError = $"Installation verification failed for {item.Name}: {verifyReason}";
                ConsoleLogger.Warn(verifyError);
                _sessionLogger?.LogInstall(item.Name, item.Version, "install", "failed", verifyError);
                return (false, verifyError);
            }
        }

        ConsoleLogger.Success($"Successfully installed {item.Name} v{item.Version}");
        _sessionLogger?.LogInstall(item.Name, item.Version, "install", "completed", $"Successfully installed {item.Name}");

        return result;
    }

    /// <summary>
    /// Uninstalls a catalog item
    /// </summary>
    public async Task<(bool Success, string Output)> UninstallAsync(
        CatalogItem item,
        CancellationToken cancellationToken = default)
    {
        ConsoleLogger.Info($"Uninstalling {item.Name}...");

        // Run preuninstall script if present
        if (!string.IsNullOrEmpty(item.PreuninstallScript))
        {
            ConsoleLogger.Info($"Running preuninstall script for {item.Name}...");
            var preResult = await _scriptService.ExecuteScriptAsync(item.PreuninstallScript, cancellationToken);
            if (!preResult.Success)
            {
                return (false, $"Preuninstall script failed: {preResult.Output}");
            }
        }

        var result = (Success: false, Output: "No uninstaller defined");

        if (item.Uninstaller.Count > 0)
        {
            var uninstaller = item.Uninstaller[0];
            result = uninstaller.Type.ToLowerInvariant() switch
            {
                "msi" => await UninstallMsiAsync(uninstaller, cancellationToken),
                "exe" => await UninstallExeAsync(uninstaller, cancellationToken),
                "powershell" or "ps1" => await UninstallPowerShellAsync(uninstaller, cancellationToken),
                "msix" or "appx" => await UninstallMsixAsync(item, uninstaller, cancellationToken),
                _ => await UninstallMsiAsync(uninstaller, cancellationToken)
            };
        }
        else if (!string.IsNullOrWhiteSpace(item.UninstallScript))
        {
            ConsoleLogger.Info($"Running uninstall_script for {item.Name}...");
            result = await _scriptService.ExecuteScriptAsync(item.UninstallScript, cancellationToken);
        }
        else
        {
            // Self-uninstallable MSIX: the pkginfo has an installs-array entry of type
            // msix but no explicit uninstaller block. Synthesize one from the installs
            // entry — the identity_name there carries everything we need.
            var msixInstall = item.Installs.FirstOrDefault(i =>
                string.Equals(i.Type, "msix", StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Type, "appx", StringComparison.OrdinalIgnoreCase));

            if (msixInstall != null && !string.IsNullOrEmpty(msixInstall.IdentityName))
            {
                var synthetic = new UninstallerInfo
                {
                    Type = "msix",
                    IdentityName = msixInstall.IdentityName
                };
                result = await UninstallMsixAsync(item, synthetic, cancellationToken);
            }
        }

        if (!result.Success)
        {
            return result;
        }

        // Run postuninstall script if present
        if (!string.IsNullOrEmpty(item.PostuninstallScript))
        {
            ConsoleLogger.Info($"Running postuninstall script for {item.Name}...");
            var postResult = await _scriptService.ExecuteScriptAsync(item.PostuninstallScript, cancellationToken);
            if (!postResult.Success)
            {
                ConsoleLogger.Warn($"Postuninstall script failed: {postResult.Output}");
            }
        }

        // Remove from ManagedInstalls registry
        UnregisterInstallation(item);

        return result;
    }

    private string GetInstallerType(CatalogItem item, string localFile)
    {
        if (!string.IsNullOrEmpty(item.Installer.Type))
        {
            return item.Installer.Type;
        }

        if (string.IsNullOrEmpty(localFile))
        {
            return "script";
        }

        var ext = Path.GetExtension(localFile).ToLowerInvariant();
        return ext switch
        {
            ".pkg" => "pkg",      // PRIMARY: sbin-installer .pkg format
            ".msi" => "msi",
            ".exe" => "exe",
            ".nupkg" => "nupkg",  // sbin-installer with choco fallback
            ".msix" or ".appx" or ".msixbundle" or ".appxbundle" => "msix",
            ".ps1" => "powershell",
            _ => "exe"
        };
    }

    private async Task<(bool Success, string Output)> InstallMsiAsync(
        CatalogItem item,
        string localFile,
        CancellationToken cancellationToken)
    {
        // cimipkg-built MSI have CIMIAN_PKG_BUILD_INFO in the Property table.
        // Route these through sbin-installer (deterministic structure, tested path).
        // Commercial/vendor MSI go straight to msiexec (battle-tested, 25 years of edge cases).
        if (IsCimianBuiltMsi(localFile) && IsSbinInstallerAvailable())
        {
            ConsoleLogger.Info($"[INSTALLER METHOD: sbin-installer] cimipkg-built MSI detected: {item.Name}");
            return await RunSbinInstallerAsync(localFile, item, cancellationToken);
        }

        ConsoleLogger.Info($"[INSTALLER METHOD: msiexec] Installing MSI: {item.Name}");

        var args = new List<string>
        {
            "/i",
            $"\"{localFile}\"",
            "/qn",  // Quiet, no UI
            "/norestart",
            $"/l*v \"{Path.Combine(_config.CachePath, $"{item.Name}_install.log")}\""
        };

        // Add custom args (switches, flags, and args combined)
        args.AddRange(item.Installer.GetAllArgs());

        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        return await RunProcessWithTimeoutAsync(startInfo, item.Name, cancellationToken);
    }

    /// <summary>
    /// Checks if an MSI was built by cimipkg by looking for the CIMIAN_PKG_BUILD_INFO property.
    /// </summary>
    private static bool IsCimianBuiltMsi(string msiPath)
    {
        try
        {
            using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);
            var value = db.ExecuteScalar(
                "SELECT `Value` FROM `Property` WHERE `Property` = 'CIMIAN_PKG_BUILD_INFO'");
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(bool Success, string Output)> InstallExeAsync(
        CatalogItem item,
        string localFile,
        CancellationToken cancellationToken)
    {
        ConsoleLogger.Detail($"EXE installer path: {localFile}");
        
        // Log parsed installer fields
        if (!string.IsNullOrEmpty(item.Installer.Subcommand))
            ConsoleLogger.Detail($"Subcommand: {item.Installer.Subcommand}");
        if (item.Installer.Switches.Count > 0)
            ConsoleLogger.Detail($"Switches: {string.Join(", ", item.Installer.Switches)}");
        if (item.Installer.Flags.Count > 0)
            ConsoleLogger.Detail($"Flags: {string.Join(", ", item.Installer.Flags)}");
        if (item.Installer.Args.Count > 0)
            ConsoleLogger.Detail($"Args: {string.Join(", ", item.Installer.Args)}");

        // Get all args (subcommand + switches + flags + args combined)
        var allArgs = item.Installer.GetAllArgs();
        var usingDefaults = false;
        if (allArgs.Count == 0)
        {
            allArgs = new List<string> { "/S", "/silent", "/quiet", "/SILENT", "/VERYSILENT", "/qn" };
            usingDefaults = true;
        }
        
        var argString = string.Join(" ", allArgs);
        ConsoleLogger.Detail($"Command: \"{localFile}\" {argString}{(usingDefaults ? " (default silent flags)" : "")}");

        var startInfo = new ProcessStartInfo
        {
            FileName = localFile,
            Arguments = argString,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        return await RunProcessWithTimeoutAsync(startInfo, item.Name, cancellationToken);
    }

    private async Task<(bool Success, string Output)> InstallChocolateyAsync(
        CatalogItem item,
        string localFile,
        CancellationToken cancellationToken)
    {
        var chocoExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "chocolatey", "bin", "choco.exe");

        if (!File.Exists(chocoExe))
        {
            return (false, "Chocolatey is not installed");
        }

        var args = new List<string>
        {
            "install",
            item.Name,
            "--yes",
            "--no-progress",
            "--force"
        };

        if (!string.IsNullOrEmpty(item.Version))
        {
            args.Add($"--version={item.Version}");
        }

        if (!string.IsNullOrEmpty(localFile))
        {
            args.Add($"--source=\"{Path.GetDirectoryName(localFile)}\"");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = chocoExe,
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        return await RunProcessWithTimeoutAsync(startInfo, item.Name, cancellationToken);
    }

    /// <summary>
    /// Most recently resolved MSIX PackageFullName from a successful install.
    /// Consumed by VerifyInstallationBeforeRegistry + RegisterInstallation so the
    /// exact PackageFullName can be persisted to HKLM\SOFTWARE\ManagedInstalls\&lt;Name&gt;.
    /// Reset on each install attempt.
    /// </summary>
    private string? _lastResolvedMsixPackageFullName;

    /// <summary>
    /// Installs an MSIX/APPX package (or .msixbundle/.appxbundle) using
    /// Add-AppxProvisionedPackage -Online, which provisions the package at the OS
    /// image level. Works from both elevated admin and SYSTEM contexts (DISM-based
    /// operation) and matches the enterprise deployment model used by Intune.
    ///
    /// The install script performs a preflight check against both Appx stores
    /// (per-user via Get-AppxPackage -AllUsers and provisioned via
    /// Get-AppxProvisionedPackage -Online) before calling the install cmdlet,
    /// to handle three real-world scenarios:
    ///
    ///   1. Higher version already installed → emit SKIP, return success
    ///      ("never downgrade" policy; StatusService detection should normally
    ///      catch this first, but this preflight is a safety net).
    ///   2. Older per-user install blocks provisioning of the catalog version
    ///      (classic 0x80070490 "Element not found" failure mode when a vendor
    ///      like Slack auto-updates itself) → run Remove-AppxPackage -AllUsers
    ///      for the identity first, then provision.
    ///   3. Nothing installed → provision directly.
    ///
    /// Known limitation: currently-signed-in users won't see the app until next
    /// login; new user profiles get it automatically. This is acceptable for
    /// Cimian's daemon-style deployment model. Per-user-install removal preserves
    /// the app's data directory under %LOCALAPPDATA%\Packages\&lt;PackageFamilyName&gt;
    /// so user settings survive the remediation.
    /// </summary>
    private async Task<(bool Success, string Output)> InstallMsixAsync(
        CatalogItem item,
        string localFile,
        CancellationToken cancellationToken)
    {
        _lastResolvedMsixPackageFullName = null;
        _sessionLogger?.LogInstall(item.Name, item.Version, "install", "started",
            $"Installing MSIX {item.Name} via Add-AppxProvisionedPackage");

        // Look up the package Identity.Name from the installs-array entry that
        // cimiimport emits. This is used for the preflight query; without it we
        // can't identify the package to clean up older per-user installs.
        var msixInstallEntry = item.Installs.FirstOrDefault(i =>
            string.Equals(i.Type, "msix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(i.Type, "appx", StringComparison.OrdinalIgnoreCase));
        var identityName = msixInstallEntry?.IdentityName ?? "";

        var logPath = Path.Combine(_config.CachePath, $"{item.Name}_msix_install.log");
        var escapedPath = localFile.Replace("'", "''");
        var escapedLog = logPath.Replace("'", "''");
        var escapedIdentity = identityName.Replace("'", "''");
        var escapedCatalogVer = (item.Version ?? "").Replace("'", "''");

        // Install script: preflight → decide action → install.
        // Compatible with Windows PowerShell 5.1 (what ScriptService prefers for
        // Appx/Dism cmdlets). Avoids Tee-Object -Encoding (pwsh 7+ only).
        // Emits one of three outcomes on the last line:
        //   OK|<PackageFullName>    — new install or upgrade succeeded
        //   SKIP|<PackageFullName>  — newer version already present, no action
        //   ERROR|<message>         — install failed
        var installScript = $@"
$ErrorActionPreference = 'Stop'
$logFile = '{escapedLog}'
$identity = '{escapedIdentity}'
$catalogVerStr = '{escapedCatalogVer}'
function Write-Log($msg) {{ try {{ Add-Content -Path $logFile -Value $msg -Encoding utf8 }} catch {{}} }}

# --- Preflight: discover any existing installation across both stores ---
$existing = @()
if ($identity) {{
    try {{
        $userPkgs = Get-AppxPackage -AllUsers -Name $identity -ErrorAction SilentlyContinue
        foreach ($p in $userPkgs) {{
            $existing += [pscustomobject]@{{ Version = $p.Version; FullName = $p.PackageFullName; PerUser = $true }}
        }}
    }} catch {{}}
    try {{
        $provPkgs = Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object DisplayName -eq $identity
        foreach ($p in $provPkgs) {{
            $existing += [pscustomobject]@{{ Version = $p.Version; FullName = $p.PackageName; PerUser = $false }}
        }}
    }} catch {{}}
}}

$highest = $null
if ($existing.Count -gt 0) {{
    $highest = $existing | Sort-Object {{ [System.Version]$_.Version }} -Descending | Select-Object -First 1
}}

# --- Never-downgrade: if installed version >= catalog version, no action ---
if ($highest -and $catalogVerStr) {{
    try {{
        $catalogVer = [System.Version]$catalogVerStr
        $installedVer = [System.Version]$highest.Version
        if ($installedVer -ge $catalogVer) {{
            $skipMsg = ""Newer version installed: $($highest.Version) >= $catalogVerStr (PackageFullName: $($highest.FullName))""
            Write-Log $skipMsg
            Write-Output ""SKIP|$($highest.FullName)""
            exit 0
        }}
    }} catch {{}}
}}

# --- Per-user conflict: remove older per-user install before provisioning ---
if ($highest -and $highest.PerUser -and $identity) {{
    $cleanupMsg = ""Removing older per-user install: $($highest.FullName) (identity: $identity)""
    Write-Log $cleanupMsg
    try {{
        Get-AppxPackage -AllUsers -Name $identity | Remove-AppxPackage -AllUsers -ErrorAction Stop
        Write-Log ""Per-user cleanup complete""
    }} catch {{
        Write-Log ""Per-user cleanup failed (continuing anyway): $($_.Exception.Message)""
    }}
}}

# --- Install: provision the new package ---
try {{
    $result = Add-AppxProvisionedPackage -Online -PackagePath '{escapedPath}' -SkipLicense
    if ($result) {{
        $result | Out-File -FilePath $logFile -Append -Encoding utf8
        Write-Output ""OK|$($result.PackageName)""
        exit 0
    }} else {{
        $msg = 'Add-AppxProvisionedPackage returned no result'
        Write-Log $msg
        Write-Output ""ERROR|$msg""
        exit 1
    }}
}} catch {{
    $msg = $_.Exception.Message
    Write-Log $msg
    Write-Output ""ERROR|$msg""
    exit 1
}}
";

        var (success, output) = await _scriptService.ExecuteScriptAsync(installScript, cancellationToken);

        // Parse the outcome marker from stdout. ScriptService returns combined
        // stdout+stderr; the script emits exactly one of OK/SKIP/ERROR on its
        // terminal line.
        string outcome = "";
        string payload = "";
        foreach (var line in output.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("OK|", StringComparison.Ordinal))
            {
                outcome = "OK";
                payload = trimmed[3..].Trim();
                break;
            }
            if (trimmed.StartsWith("SKIP|", StringComparison.Ordinal))
            {
                outcome = "SKIP";
                payload = trimmed[5..].Trim();
                break;
            }
            if (trimmed.StartsWith("ERROR|", StringComparison.Ordinal))
            {
                outcome = "ERROR";
                payload = trimmed[6..].Trim();
                break;
            }
        }

        var version = item.Version ?? string.Empty;

        if (!success && outcome != "SKIP")
        {
            var errorMsg = $"MSIX install failed: {(string.IsNullOrEmpty(payload) ? output.Trim() : payload)}";
            _sessionLogger?.LogInstall(item.Name, version, "install", "failed", errorMsg);
            return (false, errorMsg);
        }

        if (outcome == "SKIP")
        {
            // Newer version already installed. Record the existing PackageFullName
            // so VerifyInstallationBeforeRegistry passes and RegisterInstallation
            // starts tracking the pre-existing install under ManagedInstalls\<Name>.
            _lastResolvedMsixPackageFullName = payload;
            ConsoleLogger.Info($"MSIX {item.Name}: newer version already installed, no action");
            _sessionLogger?.LogInstall(item.Name, version, "install", "completed",
                $"Newer version already installed: {payload}");
            return (true, $"Newer version installed: {payload}");
        }

        // OK path — new install or upgrade succeeded.
        _lastResolvedMsixPackageFullName = payload;

        if (string.IsNullOrEmpty(_lastResolvedMsixPackageFullName))
        {
            ConsoleLogger.Warn($"MSIX install for {item.Name} succeeded but PackageFullName was not captured from output");
        }

        _sessionLogger?.LogInstall(item.Name, version, "install", "completed",
            $"MSIX installed: {_lastResolvedMsixPackageFullName ?? item.Name}");

        return (true, output);
    }

    private async Task<(bool Success, string Output)> InstallPowerShellAsync(
        CatalogItem item,
        string localFile,
        CancellationToken cancellationToken)
    {
        return await _scriptService.ExecuteScriptFileAsync(localFile, cancellationToken);
    }

    private async Task<(bool Success, string Output)> InstallScriptOnlyAsync(
        CatalogItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.InstallScript))
        {
            ConsoleLogger.Warn($"nopkg item '{item.Name}' has no install_script defined");
            return (true, "No install_script defined; nothing to run");
        }

        ConsoleLogger.Info($"Running install_script for {item.Name}...");
        _sessionLogger?.Log("INFO", $"Executing install_script for {item.Name}");
        return await _scriptService.ExecuteScriptAsync(item.InstallScript, cancellationToken);
    }

    private async Task<(bool Success, string Output)> UninstallMsiAsync(
        UninstallerInfo uninstaller,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(uninstaller.ProductCode))
        {
            return (false, "No product code specified for MSI uninstall");
        }

        var args = new List<string>
        {
            "/x",
            uninstaller.ProductCode,
            "/qn",
            "/norestart"
        };

        args.AddRange(uninstaller.GetAllArgs());

        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        return await RunProcessWithTimeoutAsync(startInfo, "uninstall", cancellationToken);
    }

    private async Task<(bool Success, string Output)> UninstallExeAsync(
        UninstallerInfo uninstaller,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(uninstaller.Command))
        {
            return (false, "No uninstall command specified");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = uninstaller.Command,
            Arguments = string.Join(" ", uninstaller.GetAllArgs()),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        return await RunProcessWithTimeoutAsync(startInfo, "uninstall", cancellationToken);
    }

    private async Task<(bool Success, string Output)> UninstallPowerShellAsync(
        UninstallerInfo uninstaller,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(uninstaller.Command))
        {
            return (false, "No uninstall script specified");
        }

        return await _scriptService.ExecuteScriptAsync(uninstaller.Command, cancellationToken);
    }

    /// <summary>
    /// Uninstalls an MSIX/APPX package belt-and-braces:
    ///   1. Remove-AppxProvisionedPackage -Online by PackageFullName (removes the
    ///      system-wide provisioning entry that would otherwise re-provision the
    ///      app on new user profiles).
    ///   2. Get-AppxPackage -AllUsers by identity | Remove-AppxPackage -AllUsers
    ///      (removes any per-user registrations left behind, including from
    ///      vendor auto-updates or previous Store installs).
    ///
    /// Both steps are required in practice — removing only the provisioned entry
    /// leaves the app fully functional for currently-registered users, which is
    /// surprising and inconsistent with what an admin expects from "uninstall".
    /// This is the same pattern Gorilla's MSIX PR uses.
    ///
    /// PackageFullName is read from HKLM\SOFTWARE\ManagedInstalls\&lt;Name&gt; (written
    /// at install time by RegisterInstallation). Falls back to runtime discovery
    /// via Get-AppxProvisionedPackage filtered by IdentityName when the registry
    /// value is absent.
    ///
    /// User data under %LOCALAPPDATA%\Packages\&lt;PackageFamilyName&gt; is preserved
    /// by Windows' Appx subsystem across Remove-AppxPackage — settings, chat
    /// history, etc. survive an uninstall/reinstall cycle unless the admin
    /// explicitly wipes that directory.
    /// </summary>
    private async Task<(bool Success, string Output)> UninstallMsixAsync(
        CatalogItem item,
        UninstallerInfo uninstaller,
        CancellationToken cancellationToken)
    {
        _sessionLogger?.LogInstall(item.Name, item.Version, "uninstall", "started",
            $"Uninstalling MSIX {item.Name}");

        // Resolve PackageFullName: prefer the value stored at install time in registry.
        var packageFullName = ReadManagedInstallsValue(item.Name, "PackageFullName");

        // Fallback: runtime discovery via IdentityName.
        if (string.IsNullOrEmpty(packageFullName) && !string.IsNullOrEmpty(uninstaller.IdentityName))
        {
            var escapedIdentity = uninstaller.IdentityName.Replace("'", "''");
            var discoverScript = $@"
$ErrorActionPreference = 'Stop'
$pkg = Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq '{escapedIdentity}' | Select-Object -First 1
if ($pkg) {{ Write-Output $pkg.PackageName }}
";
            var (discoverOk, discoverOut) = await _scriptService.ExecuteScriptAsync(discoverScript, cancellationToken);
            if (discoverOk)
            {
                foreach (var line in discoverOut.Split('\n', '\r'))
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed)
                        && !trimmed.StartsWith("ERROR", StringComparison.Ordinal))
                    {
                        packageFullName = trimmed;
                        break;
                    }
                }
            }
        }

        // IdentityName is required for step 2 (per-user cleanup). If absent, we
        // can still remove the provisioned entry by PackageFullName, but can't
        // reach per-user registrations. Log it but don't fail — partial success
        // is better than no uninstall at all.
        var identityName = uninstaller.IdentityName
            ?? ReadManagedInstallsValue(item.Name, "IdentityName")
            ?? "";

        if (string.IsNullOrEmpty(packageFullName) && string.IsNullOrEmpty(identityName))
        {
            var errorMsg = $"MSIX uninstall failed: unable to resolve PackageFullName or IdentityName for {item.Name}";
            _sessionLogger?.LogInstall(item.Name, item.Version, "uninstall", "failed", errorMsg);
            return (false, errorMsg);
        }

        var escapedName = packageFullName?.Replace("'", "''") ?? "";
        var escapedIdentity2 = identityName.Replace("'", "''");

        // Both cmdlets tolerate the target being absent (-ErrorAction SilentlyContinue
        // on the per-user side; the provisioned removal is wrapped in try/catch).
        // This keeps the script idempotent — running twice doesn't fail.
        var removeScript = $@"
$ErrorActionPreference = 'Stop'
$errors = @()

# Step 1: remove provisioned entry (by full name, if we have one)
if ('{escapedName}' -ne '') {{
    try {{
        Remove-AppxProvisionedPackage -Online -PackageName '{escapedName}' -ErrorAction Stop | Out-Null
        Write-Output ""Removed provisioned: {escapedName}""
    }} catch {{
        # 'not found' is OK — provisioned entry may have already been cleaned.
        if ($_.Exception.Message -notmatch 'not found|not installed') {{
            $errors += ""provisioned-remove: $($_.Exception.Message)""
        }}
    }}
}}

# Step 2: remove per-user registrations (by identity, across all users)
if ('{escapedIdentity2}' -ne '') {{
    try {{
        $userPkgs = Get-AppxPackage -AllUsers -Name '{escapedIdentity2}' -ErrorAction SilentlyContinue
        if ($userPkgs) {{
            foreach ($p in $userPkgs) {{
                try {{
                    Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction Stop
                    Write-Output ""Removed per-user: $($p.PackageFullName)""
                }} catch {{
                    $errors += ""per-user-remove $($p.PackageFullName): $($_.Exception.Message)""
                }}
            }}
        }}
    }} catch {{
        $errors += ""per-user-enumerate: $($_.Exception.Message)""
    }}
}}

if ($errors.Count -gt 0) {{
    Write-Output ""ERROR|$($errors -join '; ')""
    exit 1
}}
Write-Output 'OK'
exit 0
";

        var (success, output) = await _scriptService.ExecuteScriptAsync(removeScript, cancellationToken);

        if (!success)
        {
            var errorMsg = $"MSIX uninstall failed: {output.Trim()}";
            _sessionLogger?.LogInstall(item.Name, item.Version, "uninstall", "failed", errorMsg);
            return (false, errorMsg);
        }

        _sessionLogger?.LogInstall(item.Name, item.Version, "uninstall", "completed",
            $"MSIX removed: {packageFullName ?? identityName}");
        return (true, output);
    }

    /// <summary>
    /// Reads a single string value from HKLM\SOFTWARE\ManagedInstalls\&lt;itemName&gt;.
    /// Returns null if the key or value is absent.
    /// </summary>
    private static string? ReadManagedInstallsValue(string itemName, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\ManagedInstalls\{itemName}", writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Success, string Output)> RunProcessWithTimeoutAsync(
        ProcessStartInfo startInfo,
        string itemName,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var timeout = TimeSpan.FromSeconds(_config.InstallerTimeout);

        ConsoleLogger.Detail($"Launching process: {startInfo.FileName}");
        if (!string.IsNullOrEmpty(startInfo.Arguments))
            ConsoleLogger.Detail($"Arguments: {startInfo.Arguments}");
        ConsoleLogger.Detail($"Timeout: {timeout.TotalMinutes} minutes");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            
            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    output.AppendLine(e.Data);
                    ConsoleLogger.Detail($"[{itemName}:stdout] {e.Data}");
                }
            };
            
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    output.AppendLine($"ERROR: {e.Data}");
                    ConsoleLogger.Detail($"[{itemName}:stderr] {e.Data}");
                }
            };

            process.Start();
            ConsoleLogger.Detail($"Process started with PID {process.Id}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                ConsoleLogger.Warn($"Process timed out after {timeout.TotalMinutes} minutes, killing PID {process.Id}");
                try
                {
                    process.Kill(true);
                }
                catch { }
                
                return (false, $"Installation timed out after {timeout.TotalMinutes} minutes");
            }

            var exitCode = process.ExitCode;
            ConsoleLogger.Detail($"Process exited with code {exitCode}");
            
            // Common success exit codes
            if (exitCode == 0 || exitCode == 3010) // 3010 = reboot required
            {
                if (exitCode == 3010)
                {
                    output.AppendLine("Note: A reboot is required to complete the installation");
                }
                return (true, output.ToString());
            }

            return (false, $"Exit code: {exitCode}\n{output}");
        }
        catch (Exception ex)
        {
            return (false, $"Process execution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifies that an installation actually succeeded by checking the installs array.
    /// For MSI items, verifies the product is registered in Windows Installer via ProductCode/UpgradeCode.
    /// For MSIX/APPX items, checks that InstallMsixAsync captured a PackageFullName from the provisioning call.
    /// For file/directory items, checks existence on disk.
    /// Returns (true, "") if verification passes or no installs array is defined; otherwise
    /// (false, &lt;installer-type-specific reason&gt;) so callers can surface accurate error messages.
    /// </summary>
    private (bool Ok, string Reason) VerifyInstallationBeforeRegistry(CatalogItem item)
    {
        if (item.Installs.Count == 0)
        {
            // No installs array - skip verification for backward compatibility
            var installerType = item.Installer.Type?.ToLowerInvariant() ?? "";
            if (installerType is "nopkg" or "script" or "")
            {
                ConsoleLogger.Debug($"No installs array for script-only/nopkg item {item.Name} - expected");
                return (true, "");
            }
            ConsoleLogger.Warn($"No installs array for {item.Name} - cannot verify, assuming success");
            return (true, "");
        }

        foreach (var install in item.Installs)
        {
            switch (install.Type?.ToLowerInvariant())
            {
                case "file":
                    if (!string.IsNullOrEmpty(install.Path) && !File.Exists(install.Path))
                    {
                        var reason = $"expected file not found: {install.Path}";
                        ConsoleLogger.Warn($"Verification failed for {item.Name}: {reason}");
                        return (false, reason);
                    }
                    break;

                case "directory":
                    if (!string.IsNullOrEmpty(install.Path) && !Directory.Exists(install.Path))
                    {
                        var reason = $"expected directory not found: {install.Path}";
                        ConsoleLogger.Warn($"Verification failed for {item.Name}: {reason}");
                        return (false, reason);
                    }
                    break;

                case "msi":
                    // Verify MSI was registered by Windows Installer via ProductCode or UpgradeCode
                    ConsoleLogger.Debug($"Verifying MSI registration for {item.Name}: ProductCode={install.ProductCode} UpgradeCode={install.UpgradeCode}");
                    var msiFound = false;

                    // Try ProductCode first (faster)
                    if (!string.IsNullOrEmpty(install.ProductCode))
                    {
                        var version = FindMsiVersionByProductCode(install.ProductCode);
                        if (!string.IsNullOrEmpty(version))
                        {
                            ConsoleLogger.Debug($"MSI verification via ProductCode successful for {item.Name}: {version}");
                            msiFound = true;
                        }
                    }

                    // Fall back to UpgradeCode
                    if (!msiFound && !string.IsNullOrEmpty(install.UpgradeCode))
                    {
                        var (installed, version) = FindMsiByUpgradeCodeStatic(install.UpgradeCode);
                        if (installed && !string.IsNullOrEmpty(version))
                        {
                            ConsoleLogger.Debug($"MSI verification via UpgradeCode successful for {item.Name}: {version}");
                            msiFound = true;
                        }
                    }

                    if (!msiFound)
                    {
                        var reason = $"MSI not registered in Windows Installer (ProductCode={install.ProductCode}, UpgradeCode={install.UpgradeCode})";
                        ConsoleLogger.Warn($"Verification failed for {item.Name}: {reason}");
                        return (false, reason);
                    }
                    break;

                case "msix":
                case "appx":
                    // InstallMsixAsync already captured the PackageFullName from
                    // Add-AppxProvisionedPackage output into _lastResolvedMsixPackageFullName.
                    // A non-empty value means the provisioning command returned a registration.
                    // This is cheaper than a second Get-AppxProvisionedPackage round-trip and
                    // avoids making VerifyInstallationBeforeRegistry async.
                    if (string.IsNullOrEmpty(_lastResolvedMsixPackageFullName))
                    {
                        const string reason = "Add-AppxProvisionedPackage did not return a PackageFullName";
                        ConsoleLogger.Warn($"Verification failed for {item.Name}: {reason}");
                        return (false, reason);
                    }
                    ConsoleLogger.Debug($"MSIX verification successful for {item.Name}: {_lastResolvedMsixPackageFullName}");
                    break;

                default:
                    ConsoleLogger.Debug($"Unknown install type '{install.Type}' for {item.Name}, skipping verification");
                    break;
            }
        }

        ConsoleLogger.Debug($"Installation verification successful for {item.Name}");
        return (true, "");
    }

    /// <summary>
    /// Looks up MSI DisplayVersion by ProductCode in the Uninstall registry (64-bit and 32-bit).
    /// </summary>
    private static string? FindMsiVersionByProductCode(string productCode)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstallKey = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{productCode}");
                var version = uninstallKey?.GetValue("DisplayVersion")?.ToString();
                if (!string.IsNullOrEmpty(version))
                    return version;
            }
            catch { /* continue to next view */ }
        }
        return null;
    }

    /// <summary>
    /// Finds installed product via UpgradeCode using DTF's native Windows Installer API.
    /// Replaces the previous packed GUID + registry walking approach.
    /// </summary>
    private static (bool installed, string? version) FindMsiByUpgradeCodeStatic(string upgradeCode)
    {
        if (string.IsNullOrEmpty(upgradeCode))
            return (false, null);

        try
        {
            bool anyProductFound = false;
            foreach (var installation in ProductInstallation.GetRelatedProducts(upgradeCode))
            {
                anyProductFound = true;
                try
                {
                    var version = installation.ProductVersion?.ToString();
                    if (!string.IsNullOrEmpty(version))
                        return (true, version);

                    // Fallback to registry DisplayVersion
                    var regVersion = FindMsiVersionByProductCode(installation.ProductCode);
                    if (!string.IsNullOrEmpty(regVersion))
                        return (true, regVersion);
                }
                catch { /* continue to next product */ }
            }

            // Products exist but none had a readable version
            if (anyProductFound)
                return (true, null);
        }
        catch { /* failed to enumerate related products */ }

        return (false, null);
    }

    private void RegisterInstallation(CatalogItem item)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                $@"SOFTWARE\ManagedInstalls\{item.Name}");

            key?.SetValue("Version", item.Version);
            key?.SetValue("DisplayName", item.DisplayName ?? item.Name);
            key?.SetValue("InstallDate", DateTime.Now.ToString("yyyy-MM-dd"));

            // MSIX enrichment: persist InstallerType + PackageFullName + IdentityName so
            // UninstallMsixAsync can find the exact PackageName to remove without a runtime
            // Get-AppxProvisionedPackage round-trip.
            var installerType = item.Installer.Type?.ToLowerInvariant() ?? "";
            if (installerType is "msix" or "appx")
            {
                key?.SetValue("InstallerType", installerType);

                if (!string.IsNullOrEmpty(_lastResolvedMsixPackageFullName))
                    key?.SetValue("PackageFullName", _lastResolvedMsixPackageFullName);

                var msixInstall = item.Installs.FirstOrDefault(i =>
                    string.Equals(i.Type, "msix", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(i.Type, "appx", StringComparison.OrdinalIgnoreCase));

                if (msixInstall != null && !string.IsNullOrEmpty(msixInstall.IdentityName))
                    key?.SetValue("IdentityName", msixInstall.IdentityName);
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to register installation: {ex.Message}");
        }
    }

    private void UnregisterInstallation(CatalogItem item)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKey(
                $@"SOFTWARE\ManagedInstalls\{item.Name}", false);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to unregister installation: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if blocking applications are running
    /// </summary>
    public bool CheckBlockingApps(CatalogItem item, out List<string> runningApps)
    {
        runningApps = new List<string>();

        if (item.BlockingApps.Count == 0)
        {
            return false;
        }

        var processes = Process.GetProcesses();
        
        foreach (var blockingApp in item.BlockingApps)
        {
            var appName = Path.GetFileNameWithoutExtension(blockingApp);
            
            if (processes.Any(p => 
                p.ProcessName.Equals(appName, StringComparison.OrdinalIgnoreCase)))
            {
                runningApps.Add(blockingApp);
            }
        }

        return runningApps.Count > 0;
    }

    /// <summary>
    /// Checks if the local installation needs updating
    /// </summary>
    public bool NeedsUpdate(ManifestItem manifestItem, Dictionary<string, CatalogItem> catalogMap)
    {
        var key = manifestItem.Name.ToLowerInvariant();
        
        if (!catalogMap.TryGetValue(key, out var catalogItem))
        {
            return false;
        }

        // Check registry for installed version
        try
        {
            using var regKey = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\ManagedInstalls\{manifestItem.Name}");
            
            if (regKey == null)
            {
                return true; // Not installed
            }

            var installedVersion = regKey.GetValue("Version")?.ToString();
            
            if (string.IsNullOrEmpty(installedVersion))
            {
                return true;
            }

            return CatalogService.CompareVersions(catalogItem.Version, installedVersion) > 0;
        }
        catch
        {
            return true; // Assume needs update on error
        }
    }

    /// <summary>
    /// Verifies the signature of a .pkg package.
    /// Matches Go: extract.VerifyPkgSignature()
    /// </summary>
    private (bool Valid, string Details) VerifyPkgSignature(string packagePath, PkgBuildInfo buildInfo)
    {
        if (buildInfo.Signature == null)
        {
            return (false, "Package is not signed");
        }

        var signature = buildInfo.Signature;
        var issues = new List<string>();

        // 1. Verify certificate validity (date range)
        var certificateValid = VerifyCertificateValidity(signature.Certificate);
        if (!certificateValid)
        {
            issues.Add("certificate expired or not yet valid");
        }

        // 2. Calculate package hash (excluding build-info.yaml)
        var (calculatedHash, hashError) = CalculatePkgPackageHash(packagePath);
        if (hashError != null)
        {
            return (false, $"Failed to calculate package hash: {hashError}");
        }

        // 3. Verify hash matches
        var expectedHash = signature.PackageHash?.Replace("sha256:", "") ?? "";
        var hashValid = string.Equals(calculatedHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        if (!hashValid)
        {
            issues.Add("hash mismatch - package may have been tampered with");
        }

        // 4. Verify signature (simplified - checks hash, not full crypto verification)
        var signatureValid = !string.IsNullOrEmpty(signature.SignedHash);
        if (!signatureValid)
        {
            issues.Add("signature missing");
        }

        var valid = certificateValid && hashValid && signatureValid;

        if (valid)
        {
            return (true, $"Signature valid, signed by {signature.Certificate?.Subject ?? "unknown"}");
        }
        else
        {
            return (false, $"Signature verification failed: {string.Join(", ", issues)}");
        }
    }

    /// <summary>
    /// Verifies certificate validity based on date range.
    /// </summary>
    private bool VerifyCertificateValidity(PkgCertificateInfo? certificate)
    {
        if (certificate == null) return false;
        
        var now = DateTime.UtcNow;
        
        // Parse NotBefore
        if (!string.IsNullOrEmpty(certificate.NotBefore) && 
            DateTime.TryParse(certificate.NotBefore, out var notBefore))
        {
            if (now < notBefore) return false;
        }
        
        // Parse NotAfter
        if (!string.IsNullOrEmpty(certificate.NotAfter) && 
            DateTime.TryParse(certificate.NotAfter, out var notAfter))
        {
            if (now > notAfter) return false;
        }
        
        return true;
    }

    /// <summary>
    /// Calculates SHA256 hash of package contents, excluding build-info.yaml.
    /// Matches Go: calculatePkgPackageHash()
    /// </summary>
    private (string Hash, string? Error) CalculatePkgPackageHash(string packagePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var fileHashes = new List<string>();

            foreach (var entry in archive.Entries.OrderBy(e => e.FullName))
            {
                if (entry.FullName.EndsWith("/")) continue; // Skip directories
                if (entry.Name == "build-info.yaml") continue; // Skip signature file

                using var stream = entry.Open();
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(stream);
                fileHashes.Add($"{entry.FullName}:{Convert.ToHexString(hash).ToLowerInvariant()}");
            }

            // Combine all file hashes
            using var finalSha = SHA256.Create();
            var combined = string.Join("\n", fileHashes);
            var combinedBytes = Encoding.UTF8.GetBytes(combined);
            var finalHash = finalSha.ComputeHash(combinedBytes);

            return (Convert.ToHexString(finalHash).ToLowerInvariant(), null);
        }
        catch (Exception ex)
        {
            return ("", ex.Message);
        }
    }
}
