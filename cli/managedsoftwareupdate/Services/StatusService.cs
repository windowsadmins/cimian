using System.Reflection;
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
    /// Checks if the item is the Cimian/CimianTools self-update package
    /// </summary>
    private static bool IsCimianPackage(CatalogItem item)
    {
        var itemName = item.Name.ToLowerInvariant();
        
        // Only check for the main Cimian installation packages
        var cimianMainPackages = new[] { "cimian", "cimiantools" };
        
        // Check for exact matches
        foreach (var packageName in cimianMainPackages)
        {
            if (itemName == packageName)
                return true;
                
            // Check with common suffixes
            var suffixes = new[] { "-msi", "-nupkg", "-tools", ".msi", ".nupkg" };
            foreach (var suffix in suffixes)
            {
                if (itemName == packageName + suffix)
                    return true;
            }
        }
        
        // Check installer location for main Cimian packages
        var installerLocation = item.Installer.Location?.ToLowerInvariant() ?? "";
        if (installerLocation.Contains("/cimian-") ||
            installerLocation.Contains("/cimiantools-") ||
            (installerLocation.Contains("/cimian.") && 
             (installerLocation.EndsWith(".msi") || installerLocation.EndsWith(".nupkg"))))
        {
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Gets the running version of the managedsoftwareupdate binary
    /// </summary>
    private static string GetRunningVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+');
            if (plusIndex >= 0)
            {
                return informationalVersion[..plusIndex];
            }
            return informationalVersion;
        }
        
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrEmpty(fileVersion))
        {
            return fileVersion;
        }
        
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "UNKNOWN";
    }

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
            // CRITICAL: For CimianTools (self-update), check running version first
            // This prevents attempting to "downgrade" to older catalog versions
            if (IsCimianPackage(item))
            {
                var runningVersion = GetRunningVersion();
                var catalogVersion = item.Version;
                
                // Compare versions: only update if catalog version > running version
                var comparison = CatalogService.CompareVersions(catalogVersion, runningVersion);
                
                if (comparison <= 0)
                {
                    // Running version is same or newer than catalog - no update needed
                    result.Status = "installed";
                    result.Reason = $"Running version {runningVersion} >= catalog version {catalogVersion}";
                    return result;
                }
                // Otherwise fall through to normal checks
            }

            // Priority 1: Check installcheck_script if defined (Go parity - runs before anything else)
            if (!string.IsNullOrEmpty(item.InstallcheckScript))
            {
                return CheckInstallcheckScript(item);
            }

            // Priority 2: Check installs array if present (Go parity - file/hash verification)
            if (item.Installs != null && item.Installs.Count > 0)
            {
                var installsResult = CheckInstallsArray(item);
                if (installsResult.NeedsAction)
                {
                    return installsResult;
                }
                // If installs array verification passed, item is installed
                result.Status = "installed";
                result.Reason = "Installs array verification passed";
                return result;
            }

            // Priority 3: Check registry if defined
            if (!string.IsNullOrEmpty(item.Check.Registry.Name))
            {
                return CheckRegistryStatus(item);
            }

            // Priority 4: Check file if defined
            if (item.Check.File != null && !string.IsNullOrEmpty(item.Check.File.Path))
            {
                return CheckFileStatus(item);
            }

            // Priority 5: Check script if defined
            if (!string.IsNullOrEmpty(item.Check.Script))
            {
                return CheckScriptStatus(item);
            }

            // Go parity: If no checks are defined and no installs array, 
            // assume item doesn't need action (it may not have verification methods)
            // Don't fall back to ManagedInstalls registry as Go doesn't do this
            result.Status = "installed";
            result.Reason = "No explicit checks defined - assuming installed";
            return result;
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

    /// <summary>
    /// Checks the installcheck_script - if exit code 0, install is needed; if exit code 1, install is not needed
    /// This is Go parity behavior
    /// </summary>
    private StatusCheckResult CheckInstallcheckScript(CatalogItem item)
    {
        var result = new StatusCheckResult();

        try
        {
            var scriptService = new ScriptService();
            var (success, output) = scriptService.ExecuteScriptAsync(item.InstallcheckScript!).Result;

            // Go behavior: exit code 0 = needs install, exit code 1 = does not need install
            if (success) // exit 0
            {
                // Check if this is an update or new install
                var hasRegistryEntry = HasManagedInstallsEntry(item.Name);
                result.Status = "pending";
                result.NeedsAction = true;
                result.IsUpdate = hasRegistryEntry;
                result.Reason = $"Installcheck script indicates installation needed: {output}";
            }
            else // exit non-zero (typically 1)
            {
                result.Status = "installed";
                result.Reason = "Installcheck script indicates item is installed";
            }
        }
        catch (Exception ex)
        {
            // On error, assume needs action
            result.Status = "error";
            result.NeedsAction = true;
            result.Reason = $"Installcheck script failed: {ex.Message}";
            result.Error = ex;
        }

        return result;
    }

    /// <summary>
    /// Verifies the installs array - checks files, MSI products, and directories
    /// This is the Go-parity verification that checks if files exist and hashes match
    /// </summary>
    private StatusCheckResult CheckInstallsArray(CatalogItem item)
    {
        var result = new StatusCheckResult
        {
            Status = "installed"
        };

        foreach (var installItem in item.Installs)
        {
            switch (installItem.Type?.ToLowerInvariant())
            {
                case "file":
                    if (!string.IsNullOrEmpty(installItem.Path))
                    {
                        if (!File.Exists(installItem.Path))
                        {
                            // File missing - check if this is a new install or update
                            var hasRegistryEntry = HasManagedInstallsEntry(item.Name);
                            result.Status = "pending";
                            result.NeedsAction = true;
                            result.IsUpdate = hasRegistryEntry; // If has registry entry, it was installed before
                            result.Reason = $"File not found: {installItem.Path}";
                            return result;
                        }

                        // File exists - check MD5 if specified
                        if (!string.IsNullOrEmpty(installItem.Md5Checksum))
                        {
                            var actualMd5 = CalculateMD5(installItem.Path);
                            if (!actualMd5.Equals(installItem.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Status = "pending";
                                result.NeedsAction = true;
                                result.IsUpdate = true; // File exists but hash mismatch = update
                                result.Reason = $"Hash mismatch for {installItem.Path}: expected {installItem.Md5Checksum}, got {actualMd5}";
                                return result;
                            }
                        }
                    }
                    break;

                case "directory":
                    if (!string.IsNullOrEmpty(installItem.Path))
                    {
                        if (!Directory.Exists(installItem.Path))
                        {
                            var hasRegistryEntry = HasManagedInstallsEntry(item.Name);
                            result.Status = "pending";
                            result.NeedsAction = true;
                            result.IsUpdate = hasRegistryEntry;
                            result.Reason = $"Directory not found: {installItem.Path}";
                            return result;
                        }
                    }
                    break;

                case "msi":
                    if (!string.IsNullOrEmpty(installItem.ProductCode))
                    {
                        if (!IsMsiInstalled(installItem.ProductCode))
                        {
                            var hasRegistryEntry = HasManagedInstallsEntry(item.Name);
                            result.Status = "pending";
                            result.NeedsAction = true;
                            result.IsUpdate = hasRegistryEntry;
                            result.Reason = $"MSI product not installed: {installItem.ProductCode}";
                            return result;
                        }
                    }
                    break;
            }
        }

        result.Reason = "Installs array verification passed";
        return result;
    }

    /// <summary>
    /// Check if item has a ManagedInstalls registry entry (indicates previous installation)
    /// </summary>
    private bool HasManagedInstallsEntry(string itemName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\ManagedInstalls\{itemName}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if an MSI product is installed by product code
    /// </summary>
    private bool IsMsiInstalled(string productCode)
    {
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        
        foreach (var view in views)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + productCode);
            if (uninstallKey != null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Calculate MD5 hash of a file
    /// </summary>
    private static string CalculateMD5(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
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
                        // Found the item - it's installed
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
                                result.IsUpdate = true; // Item is installed but needs update
                                result.Reason = $"Update available: {displayVersion} -> {item.Version}";
                            }
                        }

                        return result;
                    }
                }
            }

            // Not found - new install
            result.Status = "pending";
            result.NeedsAction = true;
            result.IsUpdate = false; // Not installed - new install
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
                result.IsUpdate = false; // File doesn't exist - new install
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
                        result.IsUpdate = true; // File exists but needs version update
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
                    result.IsUpdate = true; // File exists but hash mismatch - reinstall/update
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
                result.IsUpdate = false; // Not registered - new install
                result.Reason = "Not registered in ManagedInstalls";
                return result;
            }

            var installedVersion = key.GetValue("Version")?.ToString();
            
            if (string.IsNullOrEmpty(installedVersion))
            {
                result.Status = "pending";
                result.NeedsAction = true;
                result.IsUpdate = false; // No version = new install
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
                result.IsUpdate = true; // Has version but needs update
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
