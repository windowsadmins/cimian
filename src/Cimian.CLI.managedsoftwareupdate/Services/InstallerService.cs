using System.Diagnostics;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Text;
using Cimian.CLI.managedsoftwareupdate.Models;
using Microsoft.Win32;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for installing/uninstalling packages
/// Migrated from Go pkg/installer
/// </summary>
public class InstallerService
{
    private readonly CimianConfig _config;
    private readonly ScriptService _scriptService;

    public InstallerService(CimianConfig config)
    {
        _config = config;
        _scriptService = new ScriptService();
    }

    /// <summary>
    /// Installs a catalog item
    /// </summary>
    public async Task<(bool Success, string Output)> InstallAsync(
        CatalogItem item,
        string localFile,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[INFO] Installing {item.Name} v{item.Version}...");

        // Run preinstall script if present
        if (!string.IsNullOrEmpty(item.PreinstallScript))
        {
            Console.WriteLine($"[INFO] Running preinstall script for {item.Name}...");
            var preResult = await _scriptService.ExecuteScriptAsync(item.PreinstallScript, cancellationToken);
            if (!preResult.Success)
            {
                return (false, $"Preinstall script failed: {preResult.Output}");
            }
        }

        // Determine installer type
        var installerType = GetInstallerType(item, localFile);
        
        var result = installerType.ToLowerInvariant() switch
        {
            "msi" => await InstallMsiAsync(item, localFile, cancellationToken),
            "exe" => await InstallExeAsync(item, localFile, cancellationToken),
            "nupkg" or "chocolatey" => await InstallChocolateyAsync(item, localFile, cancellationToken),
            "msix" or "appx" => await InstallMsixAsync(item, localFile, cancellationToken),
            "powershell" or "ps1" => await InstallPowerShellAsync(item, localFile, cancellationToken),
            "script" => await InstallScriptOnlyAsync(item, cancellationToken),
            _ => await InstallExeAsync(item, localFile, cancellationToken) // Default to EXE
        };

        if (!result.Success)
        {
            return result;
        }

        // Run postinstall script if present
        if (!string.IsNullOrEmpty(item.PostinstallScript))
        {
            Console.WriteLine($"[INFO] Running postinstall script for {item.Name}...");
            var postResult = await _scriptService.ExecuteScriptAsync(item.PostinstallScript, cancellationToken);
            if (!postResult.Success)
            {
                Console.Error.WriteLine($"[WARNING] Postinstall script failed: {postResult.Output}");
                // Don't fail the installation for postinstall script failures
            }
        }

        // Register in ManagedInstalls registry
        RegisterInstallation(item);

        return result;
    }

    /// <summary>
    /// Uninstalls a catalog item
    /// </summary>
    public async Task<(bool Success, string Output)> UninstallAsync(
        CatalogItem item,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[INFO] Uninstalling {item.Name}...");

        // Run preuninstall script if present
        if (!string.IsNullOrEmpty(item.PreuninstallScript))
        {
            Console.WriteLine($"[INFO] Running preuninstall script for {item.Name}...");
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
                _ => await UninstallMsiAsync(uninstaller, cancellationToken)
            };
        }

        if (!result.Success)
        {
            return result;
        }

        // Run postuninstall script if present
        if (!string.IsNullOrEmpty(item.PostuninstallScript))
        {
            Console.WriteLine($"[INFO] Running postuninstall script for {item.Name}...");
            var postResult = await _scriptService.ExecuteScriptAsync(item.PostuninstallScript, cancellationToken);
            if (!postResult.Success)
            {
                Console.Error.WriteLine($"[WARNING] Postuninstall script failed: {postResult.Output}");
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
            ".msi" => "msi",
            ".exe" => "exe",
            ".nupkg" => "nupkg",
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
        var args = new List<string>
        {
            "/i",
            $"\"{localFile}\"",
            "/qn",  // Quiet, no UI
            "/norestart",
            $"/l*v \"{Path.Combine(_config.CachePath, $"{item.Name}_install.log")}\""
        };

        // Add custom args
        args.AddRange(item.Installer.Args);

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

    private async Task<(bool Success, string Output)> InstallExeAsync(
        CatalogItem item,
        string localFile,
        CancellationToken cancellationToken)
    {
        var args = item.Installer.Args.Count > 0
            ? item.Installer.Args
            : new List<string> { "/S", "/silent", "/quiet", "/SILENT", "/VERYSILENT", "/qn" };

        var startInfo = new ProcessStartInfo
        {
            FileName = localFile,
            Arguments = string.Join(" ", args),
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

    private async Task<(bool Success, string Output)> InstallMsixAsync(
        CatalogItem item,
        string localFile,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.AddCommand("Add-AppxPackage")
              .AddParameter("Path", localFile)
              .AddParameter("ForceApplicationShutdown");

            var results = await ps.InvokeAsync();
            var output = new StringBuilder();

            foreach (var result in results)
            {
                output.AppendLine(result?.ToString() ?? "");
            }

            if (ps.HadErrors)
            {
                foreach (var error in ps.Streams.Error)
                {
                    output.AppendLine($"ERROR: {error}");
                }
                return (false, output.ToString());
            }

            return (true, output.ToString());
        }
        catch (Exception ex)
        {
            return (false, $"MSIX installation failed: {ex.Message}");
        }
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
        // Script-only item - preinstall/postinstall already handled
        await Task.CompletedTask;
        return (true, "Script-only installation completed");
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

        args.AddRange(uninstaller.Args);

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
            Arguments = string.Join(" ", uninstaller.Args),
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

    private async Task<(bool Success, string Output)> RunProcessWithTimeoutAsync(
        ProcessStartInfo startInfo,
        string itemName,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var timeout = TimeSpan.FromSeconds(_config.InstallerTimeout);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            
            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    output.AppendLine(e.Data);
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
            cts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch { }
                
                return (false, $"Installation timed out after {timeout.TotalMinutes} minutes");
            }

            var exitCode = process.ExitCode;
            
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

    private void RegisterInstallation(CatalogItem item)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                $@"SOFTWARE\ManagedInstalls\{item.Name}");
            
            key?.SetValue("Version", item.Version);
            key?.SetValue("DisplayName", item.DisplayName ?? item.Name);
            key?.SetValue("InstallDate", DateTime.Now.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARNING] Failed to register installation: {ex.Message}");
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
            Console.Error.WriteLine($"[WARNING] Failed to unregister installation: {ex.Message}");
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
}
