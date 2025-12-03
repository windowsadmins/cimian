using System.Runtime.InteropServices;
using System.Security.Principal;
using Cimian.CLI.managedsoftwareupdate.Models;
using Microsoft.Win32;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for checking installation status
/// Migrated from Go pkg/status
/// </summary>
public class StatusService
{
    private const string BootstrapFlagFile = @"C:\ProgramData\ManagedInstalls\.cimian.bootstrap";

    /// <summary>
    /// Checks if the item needs to be installed/updated
    /// </summary>
    public StatusCheckResult CheckStatus(CatalogItem item, string action, string cachePath)
    {
        var result = new StatusCheckResult
        {
            Status = "unknown",
            NeedsAction = false
        };

        try
        {
            // Check registry if defined
            if (!string.IsNullOrEmpty(item.Check.Registry.Name))
            {
                return CheckRegistryStatus(item);
            }

            // Check file if defined
            if (item.Check.File != null && !string.IsNullOrEmpty(item.Check.File.Path))
            {
                return CheckFileStatus(item);
            }

            // Check script if defined
            if (!string.IsNullOrEmpty(item.Check.Script))
            {
                return CheckScriptStatus(item);
            }

            // Default: check ManagedInstalls registry
            return CheckManagedInstallsStatus(item);
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Reason = ex.Message;
            result.Error = ex;
            result.NeedsAction = true;
        }

        return result;
    }

    private StatusCheckResult CheckRegistryStatus(CatalogItem item)
    {
        var result = new StatusCheckResult();
        var regCheck = item.Check.Registry;

        try
        {
            // Try both 32-bit and 64-bit registry views
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            var registryPath = regCheck.Path ?? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

            foreach (var view in views)
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstallKey = baseKey.OpenSubKey(registryPath);

                if (uninstallKey == null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var displayName = subKey.GetValue("DisplayName")?.ToString();
                    var displayVersion = subKey.GetValue("DisplayVersion")?.ToString();

                    if (!string.IsNullOrEmpty(displayName) &&
                        displayName.Contains(regCheck.Name!, StringComparison.OrdinalIgnoreCase))
                    {
                        // Found the item
                        result.Status = "installed";
                        result.Reason = $"Found {displayName} version {displayVersion}";

                        // Check if version matches
                        if (!string.IsNullOrEmpty(regCheck.Version) && !string.IsNullOrEmpty(displayVersion))
                        {
                            var comparison = CatalogService.CompareVersions(
                                item.Version, displayVersion);

                            if (comparison > 0)
                            {
                                result.Status = "pending";
                                result.NeedsAction = true;
                                result.Reason = $"Update available: {displayVersion} -> {item.Version}";
                            }
                        }

                        return result;
                    }
                }
            }

            // Not found
            result.Status = "pending";
            result.NeedsAction = true;
            result.Reason = $"Not installed: {regCheck.Name}";
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Reason = $"Registry check failed: {ex.Message}";
            result.Error = ex;
        }

        return result;
    }

    private StatusCheckResult CheckFileStatus(CatalogItem item)
    {
        var result = new StatusCheckResult();
        var fileCheck = item.Check.File!;

        try
        {
            if (!File.Exists(fileCheck.Path))
            {
                result.Status = "pending";
                result.NeedsAction = true;
                result.Reason = $"File not found: {fileCheck.Path}";
                return result;
            }

            result.Status = "installed";
            result.Reason = $"File exists: {fileCheck.Path}";

            // Check version if specified
            if (!string.IsNullOrEmpty(fileCheck.Version))
            {
                var fileVersion = GetFileVersion(fileCheck.Path);
                if (!string.IsNullOrEmpty(fileVersion))
                {
                    var comparison = CatalogService.CompareVersions(
                        fileCheck.Version, fileVersion);

                    if (comparison > 0)
                    {
                        result.Status = "pending";
                        result.NeedsAction = true;
                        result.Reason = $"Version mismatch: {fileVersion} < {fileCheck.Version}";
                    }
                }
            }

            // Check hash if specified
            if (!string.IsNullOrEmpty(fileCheck.Hash))
            {
                var actualHash = DownloadService.CalculateSHA256(fileCheck.Path);
                if (!actualHash.Equals(fileCheck.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    result.Status = "pending";
                    result.NeedsAction = true;
                    result.Reason = "Hash mismatch";
                }
            }
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Reason = $"File check failed: {ex.Message}";
            result.Error = ex;
        }

        return result;
    }

    private StatusCheckResult CheckScriptStatus(CatalogItem item)
    {
        var result = new StatusCheckResult();

        try
        {
            var scriptService = new ScriptService();
            var (success, output) = scriptService.ExecuteScriptAsync(item.Check.Script!).Result;

            if (success)
            {
                // Script returned success = installed
                result.Status = "installed";
                result.Reason = "Check script returned success";
            }
            else
            {
                // Script returned failure = needs install
                result.Status = "pending";
                result.NeedsAction = true;
                result.Reason = $"Check script indicates installation needed: {output}";
            }
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Reason = $"Check script failed: {ex.Message}";
            result.Error = ex;
        }

        return result;
    }

    private StatusCheckResult CheckManagedInstallsStatus(CatalogItem item)
    {
        var result = new StatusCheckResult();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\ManagedInstalls\{item.Name}");

            if (key == null)
            {
                result.Status = "pending";
                result.NeedsAction = true;
                result.Reason = "Not registered in ManagedInstalls";
                return result;
            }

            var installedVersion = key.GetValue("Version")?.ToString();
            
            if (string.IsNullOrEmpty(installedVersion))
            {
                result.Status = "pending";
                result.NeedsAction = true;
                result.Reason = "No version recorded";
                return result;
            }

            result.Status = "installed";
            result.Reason = $"Installed version: {installedVersion}";

            // Check if update needed
            var comparison = CatalogService.CompareVersions(item.Version, installedVersion);
            if (comparison > 0)
            {
                result.Status = "pending";
                result.NeedsAction = true;
                result.Reason = $"Update available: {installedVersion} -> {item.Version}";
            }
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Reason = $"Status check failed: {ex.Message}";
            result.Error = ex;
        }

        return result;
    }

    private static string? GetFileVersion(string path)
    {
        try
        {
            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return versionInfo.FileVersion;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the system architecture
    /// </summary>
    public static string GetSystemArchitecture()
    {
        return CatalogService.GetSystemArchitecture();
    }

    /// <summary>
    /// Checks if current process has admin privileges
    /// </summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if bootstrap mode is enabled
    /// </summary>
    public static bool IsBootstrapMode()
    {
        return File.Exists(BootstrapFlagFile);
    }

    /// <summary>
    /// Enables bootstrap mode
    /// </summary>
    public static void EnableBootstrapMode()
    {
        var dir = Path.GetDirectoryName(BootstrapFlagFile);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(BootstrapFlagFile,
            $"Bootstrap mode enabled at: {DateTime.Now:O}\n");
    }

    /// <summary>
    /// Disables bootstrap mode
    /// </summary>
    public static void DisableBootstrapMode()
    {
        if (File.Exists(BootstrapFlagFile))
        {
            File.Delete(BootstrapFlagFile);
        }
    }

    /// <summary>
    /// Gets user idle time in seconds
    /// </summary>
    public static int GetIdleSeconds()
    {
        var lastInput = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        
        if (!GetLastInputInfo(ref lastInput))
        {
            return 0;
        }

        var tickCount = Environment.TickCount;
        var idleTime = (tickCount - (int)lastInput.dwTime) / 1000;
        
        return Math.Max(0, idleTime);
    }

    /// <summary>
    /// Checks if user is currently active (idle less than 5 minutes)
    /// </summary>
    public static bool IsUserActive()
    {
        return GetIdleSeconds() < 300;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
