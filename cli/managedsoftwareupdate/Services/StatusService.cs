using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Models;
using Cimian.Core.Services;
using Microsoft.Win32;

// Resolve ambiguous references between CLI and Core models
using CatalogItem = Cimian.CLI.managedsoftwareupdate.Models.CatalogItem;
using StatusCheckResult = Cimian.CLI.managedsoftwareupdate.Models.StatusCheckResult;

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
    public static bool IsCimianPackage(CatalogItem item)
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
        // Go parity: Log CheckStatus starting with full context
        ConsoleLogger.Debug($"CheckStatus starting item: {item.Name} installType: {action} OnDemand: false");
        
        var result = new StatusCheckResult
        {
            Status = "unknown",
            NeedsAction = false,
            TargetVersion = item.Version
        };

        try
        {
            // For CimianTools (self-update), check running version first
            // This prevents attempting to "downgrade" to older catalog versions
            if (IsCimianPackage(item))
            {
                var runningVersion = GetRunningVersion();
                var catalogVersion = item.Version;
                ConsoleLogger.Detail($"Self-update check: {item.Name} running: {runningVersion} catalog: {catalogVersion}");
                
                // Compare versions: only update if catalog version > running version
                var comparison = CatalogService.CompareVersions(catalogVersion, runningVersion);
                
                if (comparison <= 0)
                {
                    // Running version is same or newer than catalog - no update needed
                    ConsoleLogger.Detail($"Self-update current: {item.Name} (running {runningVersion} >= catalog {catalogVersion})");
                    result.Status = "installed";
                    result.Reason = $"Running version {runningVersion} >= catalog version {catalogVersion}";
                    result.ReasonCode = StatusReasonCode.SelfUpdateCurrent;
                    result.DetectionMethod = DetectionMethod.SelfUpdate;
                    result.InstalledVersion = runningVersion;
                    return result;
                }
                ConsoleLogger.Info($"Self-update available: {item.Name} (running {runningVersion} < catalog {catalogVersion})");
                // Otherwise fall through to normal checks
            }

            // Priority 1: Check installcheck_script if defined (Go parity - runs before anything else)
            if (!string.IsNullOrEmpty(item.InstallcheckScript))
            {
                ConsoleLogger.Info($"Checking status via installcheck_script item: {item.Name}");
                return CheckInstallcheckScript(item);
            }

            // Priority 2: Check installs array if present (Go parity - file/hash verification)
            if (item.Installs != null && item.Installs.Count > 0)
            {
                ConsoleLogger.Info($"Checking installs array for file verification item: {item.Name} installsCount: {item.Installs.Count}");
                var installsResult = CheckInstallsArray(item);
                if (installsResult.NeedsAction)
                {
                    ConsoleLogger.Info($"File verification failed - reinstallation required item: {item.Name}");
                    ConsoleLogger.Debug($"CheckStatus explicitly indicates update required item: {item.Name}");
                    return installsResult;
                }
                ConsoleLogger.Debug($"Installation verification checks passed, no action needed item: {item.Name}");
                ConsoleLogger.Debug($"File verification passed - no update needed item: {item.Name}");
                // If installs array verification passed, item is installed
                result.Status = "installed";
                result.Reason = "Installs array verification passed";
                result.ReasonCode = StatusReasonCode.FileMatch;
                result.DetectionMethod = DetectionMethod.InstallsArray;
                ConsoleLogger.Debug($"CheckStatus explicitly indicates NO update required item: {item.Name}");
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

            // Go parity: For MSI items without explicit checks, compare version from ManagedInstalls registry
            // This handles cases like Zoom where no product_code is specified but we have registry version
            if (item.Installer?.Type?.ToLowerInvariant() == "msi")
            {
                var registryVersion = GetManagedInstallsVersion(item.Name);
                if (!string.IsNullOrEmpty(registryVersion))
                {
                    // Compare registry version to catalog version
                    var comparison = CatalogService.CompareVersions(item.Version, registryVersion);
                    if (comparison > 0)
                    {
                        // Catalog has newer version - update needed
                        result.Status = "pending";
                        result.NeedsAction = true;
                        result.IsUpdate = true;
                        result.Reason = $"Registry version {registryVersion} < catalog version {item.Version}";
                        result.ReasonCode = StatusReasonCode.UpdateAvailable;
                        result.DetectionMethod = DetectionMethod.ManagedInstalls;
                        result.InstalledVersion = registryVersion;
                        return result;
                    }
                    result.Status = "installed";
                    result.Reason = $"Registry version {registryVersion} >= catalog version {item.Version}";
                    result.ReasonCode = StatusReasonCode.VersionMatch;
                    result.DetectionMethod = DetectionMethod.ManagedInstalls;
                    result.InstalledVersion = registryVersion;
                    return result;
                }
            }

            // Go parity: If no checks are defined and no installs array, 
            // assume item doesn't need action (it may not have verification methods)
            // Don't fall back to ManagedInstalls registry as Go doesn't do this
            ConsoleLogger.Debug($"No file tracking needed - registry/product code verification sufficient item: {item.Name}");
            result.Status = "installed";
            result.Reason = "No explicit checks defined - assuming installed";
            result.ReasonCode = StatusReasonCode.NoChecks;
            result.DetectionMethod = DetectionMethod.None;
            ConsoleLogger.Debug($"CheckStatus explicitly indicates NO update required item: {item.Name}");
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Reason = ex.Message;
            result.ReasonCode = StatusReasonCode.CheckFailed;
            result.DetectionMethod = DetectionMethod.None;
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
        var result = new StatusCheckResult
        {
            DetectionMethod = DetectionMethod.Script,
            TargetVersion = item.Version
        };

        try
        {
            var scriptService = new ScriptService();
            var (success, output) = scriptService.ExecuteScriptAsync(item.InstallcheckScript!).Result;

            ConsoleLogger.Debug($"InstallCheckScript output stdout: {output?.Trim()} stderr:  error: <nil>");

            // Go behavior: exit code 0 = needs install, exit code 1 = does not need install
            if (success) // exit 0
            {
                // Check if this is an update or new install
                var hasRegistryEntry = HasManagedInstallsEntry(item.Name);
                result.Status = "pending";
                result.NeedsAction = true;
                result.IsUpdate = hasRegistryEntry;
                result.Reason = $"installcheck_script returned 0 (install needed): {output}";
                result.ReasonCode = StatusReasonCode.InstallcheckNeeded;
                ConsoleLogger.Debug($"CheckStatus explicitly indicates update required item: {item.Name}");
            }
            else // exit non-zero (typically 1)
            {
                result.Status = "installed";
                result.Reason = "installcheck_script returned non-zero (no install needed)";
                result.ReasonCode = StatusReasonCode.ScriptConfirmed;
                ConsoleLogger.Debug($"CheckStatus explicitly indicates NO update required item: {item.Name}");
            }
        }
        catch (Exception ex)
        {
            // On error, assume needs action
            result.Status = "error";
            result.NeedsAction = true;
            result.Reason = $"installcheck_script failed: {ex.Message}";
            result.ReasonCode = StatusReasonCode.ScriptError;
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
            Status = "installed",
            DetectionMethod = DetectionMethod.InstallsArray,
            TargetVersion = item.Version
        };

        // Go parity: Read the ManagedInstalls registry version first
        var registryVersion = GetManagedInstallsVersion(item.Name);

        foreach (var installItem in item.Installs)
        {
            switch (installItem.Type?.ToLowerInvariant())
            {
                case "file":
                    if (!string.IsNullOrEmpty(installItem.Path))
                    {
                        ConsoleLogger.Debug($"Checking file exists: {installItem.Path}");
                        if (!File.Exists(installItem.Path))
                        {
                            ConsoleLogger.Info($"File not found item: {item.Name} path: {installItem.Path}");
                            // File missing - check if this is a new install or update
                            var hasRegistryEntry = HasManagedInstallsEntry(item.Name);
                            result.Status = "pending";
                            result.NeedsAction = true;
                            result.IsUpdate = hasRegistryEntry; // If has registry entry, it was installed before
                            result.Reason = $"File not found: {installItem.Path}";
                            result.ReasonCode = StatusReasonCode.FileMissing;
                            result.DetectionMethod = DetectionMethod.File;
                            return result;
                        }

                        // File exists - check hash if specified (md5checksum field may contain MD5, SHA1, or SHA256)
                        // Go parity: hashVerificationPassed means hash is authoritative - version mismatches are informational only
                        var hashVerificationPassed = false;
                        if (!string.IsNullOrEmpty(installItem.Md5Checksum))
                        {
                            ConsoleLogger.Debug($"Verifying hash for: {installItem.Path}");
                            var actualHash = CalculateHash(installItem.Path, installItem.Md5Checksum);
                            if (!actualHash.Equals(installItem.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                            {
                                ConsoleLogger.Info($"Installs array verification failed - hash mismatch, reinstallation needed item: {item.Name} path: {installItem.Path} localHash: {actualHash} expectedHash: {installItem.Md5Checksum}");
                                result.Status = "pending";
                                result.NeedsAction = true;
                                result.IsUpdate = true; // File exists but hash mismatch = update
                                result.Reason = $"Hash mismatch for {installItem.Path}: expected {installItem.Md5Checksum}, got {actualHash}";
                                result.ReasonCode = StatusReasonCode.HashMismatch;
                                result.DetectionMethod = DetectionMethod.File;
                                return result;
                            }
                            hashVerificationPassed = true;
                            ConsoleLogger.Info($"Hash verification passed item: {item.Name} path: {installItem.Path} hash: {actualHash}");
                        }
                        else
                        {
                            // Go parity: File exists but no hash specified
                            ConsoleLogger.Debug($"File exists (no hash check) item: {item.Name} path: {installItem.Path}");
                        }

                        // Check version - use item.Version as fallback when install.Version is not specified (reduces pkgsinfo redundancy)
                        // Go parity: When hash verification passed, version mismatches are informational only (hash is authoritative)
                        var expectedVersion = !string.IsNullOrEmpty(installItem.Version) ? installItem.Version : item.Version;
                        if (!string.IsNullOrEmpty(expectedVersion))
                        {
                            var fileVersion = GetFileVersion(installItem.Path);
                            if (!string.IsNullOrEmpty(fileVersion))
                            {
                                var comparison = CatalogService.CompareVersions(expectedVersion, fileVersion);
                                if (comparison > 0)
                                {
                                    if (hashVerificationPassed)
                                    {
                                        // Go parity: Hash passed but file version appears outdated - hash is authoritative, accept
                                        ConsoleLogger.Info($"File version appears outdated, but hash verification passed - accepting installation item: {item.Name} path: {installItem.Path} fileVersion: {fileVersion} expectedVersion: {expectedVersion}");
                                    }
                                    else
                                    {
                                        // No hash verification - version mismatch means update needed
                                        ConsoleLogger.Info($"Installs array verification failed - file version outdated item: {item.Name} path: {installItem.Path} installedVersion: {fileVersion} catalogVersion: {expectedVersion}");
                                        result.Status = "pending";
                                        result.NeedsAction = true;
                                        result.IsUpdate = true;
                                        result.Reason = $"Version outdated: {fileVersion} -> {expectedVersion}";
                                        result.ReasonCode = StatusReasonCode.VersionOutdated;
                                        result.DetectionMethod = DetectionMethod.File;
                                        return result;
                                    }
                                }
                                else if (comparison < 0)
                                {
                                    // Installed version is newer than catalog - skip
                                    ConsoleLogger.Info($"Installed version is newer than catalog version - skipping installation item: {item.Name} path: {installItem.Path} installedVersion: {fileVersion} catalogVersion: {expectedVersion}");
                                }
                            }
                            else if (!hashVerificationPassed)
                            {
                                // Go parity: No file version metadata and no hash - action needed
                                ConsoleLogger.Info($"File version metadata unavailable and no hash verification - action needed item: {item.Name} path: {installItem.Path}");
                                result.Status = "pending";
                                result.NeedsAction = true;
                                result.IsUpdate = true;
                                result.Reason = $"No file version metadata available for {installItem.Path}";
                                result.ReasonCode = StatusReasonCode.VersionOutdated;
                                result.DetectionMethod = DetectionMethod.File;
                                return result;
                            }
                            else
                            {
                                // Go parity: No file version but hash passed - accept
                                ConsoleLogger.Info($"File version metadata unavailable but hash verification passed - accepting installation item: {item.Name} path: {installItem.Path}");
                            }
                        }
                    }
                    break;

                case "directory":
                    if (!string.IsNullOrEmpty(installItem.Path))
                    {
                        ConsoleLogger.Debug($"Checking directory exists: {installItem.Path}");
                        if (!Directory.Exists(installItem.Path))
                        {
                            ConsoleLogger.Info($"Directory not found item: {item.Name} path: {installItem.Path}");
                            var hasRegistryEntry = HasManagedInstallsEntry(item.Name);
                            result.Status = "pending";
                            result.NeedsAction = true;
                            result.IsUpdate = hasRegistryEntry;
                            result.Reason = $"Directory not found: {installItem.Path}";
                            result.ReasonCode = StatusReasonCode.DirectoryMissing;
                            result.DetectionMethod = DetectionMethod.Directory;
                            return result;
                        }
                        ConsoleLogger.Debug($"Directory exists item: {item.Name} path: {installItem.Path}");
                    }
                    break;

                case "msi":
                    // Use robust MSI detection with both ProductCode and UpgradeCode
                    // This handles auto-updating apps (Chrome, etc.) where ProductCode changes each version
                    var catalogVersion = !string.IsNullOrEmpty(installItem.Version) ? installItem.Version : item.Version;
                    var (msiInstalled, msiVersionMatch, msiInstalledVersion) = CheckMsiWithUpgradeCode(
                        installItem.ProductCode, installItem.UpgradeCode, catalogVersion);

                    // If MSI detection failed, try registry lookup using item's display_name or name as fallback
                    // This handles cases where app was installed via EXE instead of MSI (e.g., Chrome auto-update)
                    if (!msiInstalled)
                    {
                        var displayNameToSearch = !string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : item.Name;
                        var fallbackVersion = FindVersionByDisplayName(displayNameToSearch);
                        if (!string.IsNullOrEmpty(fallbackVersion))
                        {
                            msiInstalled = true;
                            msiInstalledVersion = fallbackVersion;
                            ConsoleLogger.Info($"Found app via display_name fallback item: {item.Name} displayName: {displayNameToSearch} installedVersion: {fallbackVersion}");
                            
                            // Check version match
                            if (!string.IsNullOrEmpty(catalogVersion))
                            {
                                var comparison = CatalogService.CompareVersions(catalogVersion, fallbackVersion);
                                msiVersionMatch = comparison <= 0;
                            }
                            else
                            {
                                msiVersionMatch = true;
                            }
                        }
                    }

                    if (!msiInstalled)
                    {
                        ConsoleLogger.Info($"MSI product not installed item: {item.Name} productCode: {installItem.ProductCode} upgradeCode: {installItem.UpgradeCode}");
                        var hasRegistryEntry = HasManagedInstallsEntry(item.Name);
                        result.Status = "pending";
                        result.NeedsAction = true;
                        result.IsUpdate = hasRegistryEntry;
                        result.Reason = $"MSI product not installed";
                        result.ReasonCode = StatusReasonCode.ProductCodeMissing;
                        result.DetectionMethod = DetectionMethod.Msi;
                        return result;
                    }
                    else if (!msiVersionMatch)
                    {
                        ConsoleLogger.Info($"MSI version outdated item: {item.Name} installedVersion: {msiInstalledVersion} catalogVersion: {catalogVersion}");
                        result.Status = "pending";
                        result.NeedsAction = true;
                        result.IsUpdate = true;
                        result.InstalledVersion = msiInstalledVersion;
                        result.Reason = $"MSI version outdated: {msiInstalledVersion} -> {catalogVersion}";
                        result.ReasonCode = StatusReasonCode.VersionOutdated;
                        result.DetectionMethod = DetectionMethod.Msi;
                        return result;
                    }
                    else
                    {
                        ConsoleLogger.Info($"MSI verification passed - version current or newer item: {item.Name} installedVersion: {msiInstalledVersion} catalogVersion: {catalogVersion}");
                        result.InstalledVersion = msiInstalledVersion;
                    }
                    break;
            }
        }

        result.Reason = $"All {item.Installs.Count} install checks passed";
        result.ReasonCode = StatusReasonCode.FileMatch;
        return result;
    }

    /// <summary>
    /// Check if an MSI product is installed and return its version
    /// </summary>
    private (bool installed, string? version) CheckMsiProductWithVersion(string productCode)
    {
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        
        foreach (var view in views)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            var registryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + productCode;
            using var uninstallKey = baseKey.OpenSubKey(registryPath);
            if (uninstallKey != null)
            {
                var displayVersion = uninstallKey.GetValue("DisplayVersion")?.ToString();
                ConsoleLogger.Debug($"Found MSI product in {(view == RegistryView.Registry64 ? "64-bit" : "32-bit")} registry productCode: {productCode} registryPath: {registryPath} version: {displayVersion}");
                return (true, displayVersion);
            }
        }
        return (false, null);
    }

    /// <summary>
    /// Converts a standard GUID format to Windows Installer packed GUID format.
    /// Example: {C1DFDF69-5945-32F2-A35E-EE94C99C7CF4} -> 96FDFD1C5495F232A3E5EE499CC9C74F
    /// </summary>
    private static string PackGuid(string guid)
    {
        // Remove braces and hyphens
        guid = guid.Replace("{", "").Replace("}", "").Replace("-", "").ToUpperInvariant();
        
        if (guid.Length != 32)
            return string.Empty;
        
        // Packed format reverses specific sections
        var result = new char[32];
        
        // Section 1: first 8 chars, reversed in pairs
        result[0] = guid[6]; result[1] = guid[7];
        result[2] = guid[4]; result[3] = guid[5];
        result[4] = guid[2]; result[5] = guid[3];
        result[6] = guid[0]; result[7] = guid[1];
        
        // Section 2: next 4 chars, reversed in pairs
        result[8] = guid[10]; result[9] = guid[11];
        result[10] = guid[8]; result[11] = guid[9];
        
        // Section 3: next 4 chars, reversed in pairs  
        result[12] = guid[14]; result[13] = guid[15];
        result[14] = guid[12]; result[15] = guid[13];
        
        // Section 4+5: remaining 16 chars, reversed in pairs
        for (int i = 16; i < 32; i += 2)
        {
            result[i] = guid[i + 1];
            result[i + 1] = guid[i];
        }
        
        return new string(result);
    }

    /// <summary>
    /// Finds any installed product with the given UpgradeCode and returns its version.
    /// Essential for auto-updating apps (Chrome, etc.) where ProductCode changes each version.
    /// </summary>
    private (bool installed, string? version) FindMsiByUpgradeCode(string upgradeCode)
    {
        if (string.IsNullOrEmpty(upgradeCode))
            return (false, null);

        var packedUpgradeCode = PackGuid(upgradeCode);
        if (string.IsNullOrEmpty(packedUpgradeCode))
        {
            ConsoleLogger.Debug($"Failed to pack UpgradeCode GUID upgradeCode: {upgradeCode}");
            return (false, null);
        }

        try
        {
            // Check installer UpgradeCodes registry
            var upgradeCodePath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UpgradeCodes\{packedUpgradeCode}";
            using var upgradeKey = Registry.LocalMachine.OpenSubKey(upgradeCodePath);
            
            if (upgradeKey == null)
            {
                ConsoleLogger.Debug($"UpgradeCode not found in registry upgradeCode: {upgradeCode} packedCode: {packedUpgradeCode}");
                return (false, null);
            }

            var valueNames = upgradeKey.GetValueNames();
            if (valueNames.Length == 0)
            {
                ConsoleLogger.Debug($"No products found under UpgradeCode upgradeCode: {upgradeCode}");
                return (false, null);
            }

            ConsoleLogger.Debug($"Found products under UpgradeCode upgradeCode: {upgradeCode} productCount: {valueNames.Length}");

            // Search uninstall registry for matching products
            var uninstallPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var basePath in uninstallPaths)
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(basePath);
                if (baseKey == null) continue;

                foreach (var subkeyName in baseKey.GetSubKeyNames())
                {
                    using var prodKey = baseKey.OpenSubKey(subkeyName);
                    if (prodKey == null) continue;

                    var version = prodKey.GetValue("DisplayVersion")?.ToString();
                    if (string.IsNullOrEmpty(version)) continue;

                    // Check if this product's packed code matches our UpgradeCode values
                    var packedProductCode = PackGuid(subkeyName);
                    foreach (var valueName in valueNames)
                    {
                        if (string.Equals(valueName, packedProductCode, StringComparison.OrdinalIgnoreCase))
                        {
                            ConsoleLogger.Info($"Found installed product via UpgradeCode lookup upgradeCode: {upgradeCode} productCode: {subkeyName} version: {version}");
                            return (true, version);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"Error during UpgradeCode lookup: {ex.Message}");
        }

        return (false, null);
    }

    /// <summary>
    /// Performs robust MSI detection using both ProductCode and UpgradeCode.
    /// Handles auto-updating apps where ProductCode changes each version but UpgradeCode stays stable.
    /// Returns: (installed, versionMatch, installedVersion)
    /// </summary>
    private (bool installed, bool versionMatch, string? installedVersion) CheckMsiWithUpgradeCode(
        string? productCode, string? upgradeCode, string catalogVersion)
    {
        // First try ProductCode lookup (faster, exact match)
        if (!string.IsNullOrEmpty(productCode))
        {
            var (installed, installedVersion) = CheckMsiProductWithVersion(productCode);
            if (installed && !string.IsNullOrEmpty(installedVersion))
            {
                ConsoleLogger.Debug($"Found MSI via ProductCode productCode: {productCode} installedVersion: {installedVersion}");

                if (string.IsNullOrEmpty(catalogVersion))
                    return (true, true, installedVersion);

                // Compare versions - skip if installed >= catalog
                var comparison = CatalogService.CompareVersions(catalogVersion, installedVersion);
                if (comparison <= 0)
                {
                    ConsoleLogger.Info($"MSI version current or newer - no action needed productCode: {productCode} installedVersion: {installedVersion} catalogVersion: {catalogVersion}");
                    return (true, true, installedVersion);
                }
                else
                {
                    ConsoleLogger.Info($"MSI version outdated - update available productCode: {productCode} installedVersion: {installedVersion} catalogVersion: {catalogVersion}");
                    return (true, false, installedVersion);
                }
            }
        }

        // ProductCode not found - try UpgradeCode (handles auto-updated apps)
        if (!string.IsNullOrEmpty(upgradeCode))
        {
            var (installed, installedVersion) = FindMsiByUpgradeCode(upgradeCode);
            if (installed && !string.IsNullOrEmpty(installedVersion))
            {
                ConsoleLogger.Debug($"Found MSI via UpgradeCode upgradeCode: {upgradeCode} installedVersion: {installedVersion}");

                if (string.IsNullOrEmpty(catalogVersion))
                    return (true, true, installedVersion);

                // Compare versions - skip if installed >= catalog
                var comparison = CatalogService.CompareVersions(catalogVersion, installedVersion);
                if (comparison <= 0)
                {
                    ConsoleLogger.Info($"MSI found via UpgradeCode - version current or newer, skipping installation upgradeCode: {upgradeCode} installedVersion: {installedVersion} catalogVersion: {catalogVersion}");
                    return (true, true, installedVersion);
                }
                else
                {
                    ConsoleLogger.Info($"MSI found via UpgradeCode - version outdated, update available upgradeCode: {upgradeCode} installedVersion: {installedVersion} catalogVersion: {catalogVersion}");
                    return (true, false, installedVersion);
                }
            }
        }

        ConsoleLogger.Debug($"MSI not found via ProductCode or UpgradeCode productCode: {productCode} upgradeCode: {upgradeCode}");
        return (false, false, null);
    }

    /// <summary>
    /// Search the Windows uninstall registry for an app by its display name.
    /// This is a fallback for apps that were installed via EXE installer instead of MSI.
    /// Returns the installed version or null if not found.
    /// </summary>
    private string? FindVersionByDisplayName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
            return null;

        var uninstallPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var basePath in uninstallPaths)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(basePath);
                if (baseKey == null) continue;

                foreach (var subkeyName in baseKey.GetSubKeyNames())
                {
                    using var subKey = baseKey.OpenSubKey(subkeyName);
                    if (subKey == null) continue;

                    var regDisplayName = subKey.GetValue("DisplayName")?.ToString();
                    var regVersion = subKey.GetValue("DisplayVersion")?.ToString();

                    if (string.IsNullOrEmpty(regDisplayName) || string.IsNullOrEmpty(regVersion))
                        continue;

                    // Exact match
                    if (string.Equals(regDisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleLogger.Debug($"Found exact display name match in registry displayName: {displayName} registryName: {regDisplayName} version: {regVersion}");
                        return regVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleLogger.Debug($"Error searching registry path {basePath}: {ex.Message}");
            }
        }

        // Try partial match (registry name contains our display name or vice versa)
        foreach (var basePath in uninstallPaths)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(basePath);
                if (baseKey == null) continue;

                foreach (var subkeyName in baseKey.GetSubKeyNames())
                {
                    using var subKey = baseKey.OpenSubKey(subkeyName);
                    if (subKey == null) continue;

                    var regDisplayName = subKey.GetValue("DisplayName")?.ToString();
                    var regVersion = subKey.GetValue("DisplayVersion")?.ToString();

                    if (string.IsNullOrEmpty(regDisplayName) || string.IsNullOrEmpty(regVersion))
                        continue;

                    // Partial match - either contains the other
                    if (regDisplayName.Contains(displayName, StringComparison.OrdinalIgnoreCase) ||
                        displayName.Contains(regDisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleLogger.Debug($"Found partial display name match in registry displayName: {displayName} registryName: {regDisplayName} version: {regVersion}");
                        return regVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleLogger.Debug($"Error searching registry path {basePath}: {ex.Message}");
            }
        }

        ConsoleLogger.Debug($"No registry entry found for display name: {displayName}");
        return null;
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
    /// Get the version of an item from the ManagedInstalls registry
    /// This is used for version comparison when no product_code is available
    /// </summary>
    private string? GetManagedInstallsVersion(string itemName)
    {
        ConsoleLogger.Debug($"Reading local installed version from registry (if any) item: {itemName}");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\ManagedInstalls\{itemName}");
            var version = key?.GetValue("version")?.ToString();
            if (!string.IsNullOrEmpty(version))
            {
                ConsoleLogger.Info($"Found Cimian-managed registry version item: {itemName} registryVersion: {version}");
            }
            else
            {
                ConsoleLogger.Debug($"No registry version found, returning empty item: {itemName}");
            }
            return version;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"No Cimian version found in registry or error reading it item: {itemName} error: {ex.Message}");
            return null;
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
    /// Calculate hash of a file, auto-detecting algorithm based on expected hash length.
    /// Matches Go parity: 32 chars = MD5, 40 chars = SHA1, 64 chars = SHA256
    /// </summary>
    private static string CalculateHash(string filePath, string? expectedHash = null)
    {
        int expectedLen = expectedHash?.Length ?? 32;
        
        using var stream = File.OpenRead(filePath);
        byte[] hash;
        
        switch (expectedLen)
        {
            case 40: // SHA1
                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                {
                    hash = sha1.ComputeHash(stream);
                }
                break;
            case 64: // SHA256
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    hash = sha256.ComputeHash(stream);
                }
                break;
            default: // MD5 (32 chars or unknown)
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    hash = md5.ComputeHash(stream);
                }
                break;
        }
        
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Calculate MD5 hash of a file (legacy method for backward compatibility)
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
        ConsoleLogger.Debug($"Checking registry for: {item.Name}");
        var result = new StatusCheckResult
        {
            DetectionMethod = DetectionMethod.Registry,
            TargetVersion = item.Version
        };
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
                        ConsoleLogger.Info($"Partial registry match found item: {item.Name} registryEntry: {displayName} registryVersion: {displayVersion}");
                        result.Status = "installed";
                        result.Reason = $"Found {displayName} version {displayVersion} in registry";
                        result.ReasonCode = StatusReasonCode.RegistryMatch;
                        result.InstalledVersion = displayVersion;

                        // Check if version matches
                        if (!string.IsNullOrEmpty(regCheck.Version) && !string.IsNullOrEmpty(displayVersion))
                        {
                            var comparison = CatalogService.CompareVersions(
                                item.Version, displayVersion);

                            if (comparison > 0)
                            {
                                ConsoleLogger.Info($"Update available for {item.Name}: {displayVersion} -> {item.Version}");
                                ConsoleLogger.Debug($"CheckStatus explicitly indicates update required item: {item.Name}");
                                result.Status = "pending";
                                result.NeedsAction = true;
                                result.IsUpdate = true; // Item is installed but needs update
                                result.Reason = $"Update available: {displayVersion} -> {item.Version}";
                                result.ReasonCode = StatusReasonCode.UpdateAvailable;
                            }
                            else
                            {
                                ConsoleLogger.Debug($"Version current - no update needed item: {item.Name} version: {displayVersion}");
                                ConsoleLogger.Debug($"CheckStatus explicitly indicates NO update required item: {item.Name}");
                            }
                        }
                        else
                        {
                            ConsoleLogger.Debug($"CheckStatus explicitly indicates NO update required item: {item.Name}");
                        }

                        return result;
                    }
                }
            }

            // Not found - new install
            ConsoleLogger.Debug($"Registry entry not found: {regCheck.Name}");
            ConsoleLogger.Debug($"CheckStatus explicitly indicates update required item: {item.Name}");
            result.Status = "pending";
            result.NeedsAction = true;
            result.IsUpdate = false; // Not installed - new install
            result.Reason = $"Not found in registry: {regCheck.Name}";
            result.ReasonCode = StatusReasonCode.RegistryMissing;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"Registry check failed item: {item.Name} error: {ex.Message}");
            result.Status = "error";
            result.Reason = $"Registry check failed: {ex.Message}";
            result.ReasonCode = StatusReasonCode.CheckFailed;
            result.Error = ex;
        }

        return result;
    }

    private StatusCheckResult CheckFileStatus(CatalogItem item)
    {
        var result = new StatusCheckResult
        {
            DetectionMethod = DetectionMethod.File,
            TargetVersion = item.Version
        };
        var fileCheck = item.Check.File!;
        ConsoleLogger.Debug($"Checking file status item: {item.Name} path: {fileCheck.Path}");

        try
        {
            if (!File.Exists(fileCheck.Path))
            {
                ConsoleLogger.Debug($"File not found item: {item.Name} path: {fileCheck.Path}");
                ConsoleLogger.Debug($"CheckStatus explicitly indicates update required item: {item.Name}");
                result.Status = "pending";
                result.NeedsAction = true;
                result.IsUpdate = false; // File doesn't exist - new install
                result.Reason = $"File not found: {fileCheck.Path}";
                result.ReasonCode = StatusReasonCode.FileMissing;
                return result;
            }

            ConsoleLogger.Debug($"File exists item: {item.Name} path: {fileCheck.Path}");
            result.Status = "installed";
            result.Reason = $"File exists: {fileCheck.Path}";
            result.ReasonCode = StatusReasonCode.FileMatch;

            // Check version if specified
            if (!string.IsNullOrEmpty(fileCheck.Version))
            {
                var fileVersion = GetFileVersion(fileCheck.Path);
                result.InstalledVersion = fileVersion;
                ConsoleLogger.Debug($"File version check item: {item.Name} installedVersion: {fileVersion} catalogVersion: {fileCheck.Version}");
                
                if (!string.IsNullOrEmpty(fileVersion))
                {
                    var comparison = CatalogService.CompareVersions(
                        fileCheck.Version, fileVersion);

                    if (comparison > 0)
                    {
                        ConsoleLogger.Info($"Update available for {item.Name}: {fileVersion} -> {fileCheck.Version}");
                        ConsoleLogger.Debug($"CheckStatus explicitly indicates update required item: {item.Name}");
                        result.Status = "pending";
                        result.NeedsAction = true;
                        result.IsUpdate = true; // File exists but needs version update
                        result.Reason = $"File at {fileCheck.Path} is version {fileVersion}, need {fileCheck.Version}";
                        result.ReasonCode = StatusReasonCode.UpdateAvailable;
                    }
                    else
                    {
                        ConsoleLogger.Debug($"File version OK item: {item.Name} version: {fileVersion}");
                        ConsoleLogger.Debug($"CheckStatus explicitly indicates NO update required item: {item.Name}");
                        result.Reason = $"File at {fileCheck.Path} verified at version {fileVersion}";
                    }
                }
                else
                {
                    ConsoleLogger.Debug($"No version info available in file item: {item.Name}");
                }
            }

            // Check hash if specified
            if (!string.IsNullOrEmpty(fileCheck.Hash))
            {
                ConsoleLogger.Debug($"Verifying hash item: {item.Name} path: {fileCheck.Path}");
                var actualHash = DownloadService.CalculateSHA256(fileCheck.Path);
                if (!actualHash.Equals(fileCheck.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleLogger.Debug($"Hash mismatch item: {item.Name} expected: {fileCheck.Hash.Substring(0, 12)}... got: {actualHash.Substring(0, 12)}...");
                    ConsoleLogger.Debug($"CheckStatus explicitly indicates update required item: {item.Name}");
                    result.Status = "pending";
                    result.NeedsAction = true;
                    result.IsUpdate = true; // File exists but hash mismatch - reinstall/update
                    result.Reason = $"Hash mismatch for {fileCheck.Path}";
                    result.ReasonCode = StatusReasonCode.HashMismatch;
                }
                else
                {
                    ConsoleLogger.Debug($"Hash verification passed item: {item.Name}");
                    ConsoleLogger.Debug($"CheckStatus explicitly indicates NO update required item: {item.Name}");
                    result.ReasonCode = StatusReasonCode.HashMatch;
                }
            }
            else if (result.Status == "installed")
            {
                ConsoleLogger.Debug($"CheckStatus explicitly indicates NO update required item: {item.Name}");
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"File check failed item: {item.Name} error: {ex.Message}");
            result.Status = "error";
            result.Reason = $"File check failed: {ex.Message}";
            result.ReasonCode = StatusReasonCode.CheckFailed;
            result.Error = ex;
        }

        return result;
    }

    private StatusCheckResult CheckScriptStatus(CatalogItem item)
    {
        var result = new StatusCheckResult
        {
            DetectionMethod = DetectionMethod.Script,
            TargetVersion = item.Version
        };
        ConsoleLogger.Debug($"Running check script for item: {item.Name}");

        try
        {
            var scriptService = new ScriptService();
            var (success, output) = scriptService.ExecuteScriptAsync(item.Check.Script!).Result;

            ConsoleLogger.Debug($"Check script output stdout: {output?.Trim()} stderr:  error: <nil>");

            if (success)
            {
                // Script returned success = installed
                ConsoleLogger.Debug($"Check script returned success item: {item.Name}");
                ConsoleLogger.Debug($"CheckStatus explicitly indicates NO update required item: {item.Name}");
                result.Status = "installed";
                result.Reason = "Check script returned success (exit 0)";
                result.ReasonCode = StatusReasonCode.ScriptConfirmed;
            }
            else
            {
                // Script returned failure = needs install
                ConsoleLogger.Debug($"Check script indicates install needed item: {item.Name} output: {output}");
                ConsoleLogger.Debug($"CheckStatus explicitly indicates update required item: {item.Name}");
                result.Status = "pending";
                result.NeedsAction = true;
                result.Reason = $"Check script indicates installation needed: {output}";
                result.ReasonCode = StatusReasonCode.NotInstalled;
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"Check script failed item: {item.Name} error: {ex.Message}");
            result.Status = "error";
            result.Reason = $"Check script failed: {ex.Message}";
            result.ReasonCode = StatusReasonCode.ScriptError;
            result.Error = ex;
        }

        return result;
    }

    private StatusCheckResult CheckManagedInstallsStatus(CatalogItem item)
    {
        var result = new StatusCheckResult
        {
            DetectionMethod = DetectionMethod.ManagedInstalls,
            TargetVersion = item.Version
        };
        ConsoleLogger.Detail($"Checking ManagedInstalls registry item: {item.Name}");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\ManagedInstalls\{item.Name}");

            if (key == null)
            {
                ConsoleLogger.Debug($"ManagedInstalls registry key not found item: {item.Name}");
                result.Status = "pending";
                result.NeedsAction = true;
                result.IsUpdate = false; // Not registered - new install
                result.Reason = $"Not registered in ManagedInstalls: {item.Name}";
                result.ReasonCode = StatusReasonCode.RegistryMissing;
                return result;
            }

            var installedVersion = key.GetValue("Version")?.ToString();
            result.InstalledVersion = installedVersion;
            ConsoleLogger.Detail($"Found ManagedInstalls registry item: {item.Name} installedVersion: {installedVersion}");
            
            if (string.IsNullOrEmpty(installedVersion))
            {
                ConsoleLogger.Debug($"ManagedInstalls version empty item: {item.Name}");
                result.Status = "pending";
                result.NeedsAction = true;
                result.IsUpdate = false; // No version = new install
                result.Reason = "No version recorded in ManagedInstalls registry";
                result.ReasonCode = StatusReasonCode.RegistryMissing;
                return result;
            }

            result.Status = "installed";
            result.Reason = $"Found in ManagedInstalls at version {installedVersion}";
            result.ReasonCode = StatusReasonCode.RegistryMatch;

            // Check if update needed
            var comparison = CatalogService.CompareVersions(item.Version, installedVersion);
            if (comparison > 0)
            {
                ConsoleLogger.Info($"Update available for {item.Name}: {installedVersion} -> {item.Version}");
                result.Status = "pending";
                result.NeedsAction = true;
                result.IsUpdate = true; // Has version but needs update
                result.Reason = $"Update available: {installedVersion} -> {item.Version}";
                result.ReasonCode = StatusReasonCode.UpdateAvailable;
            }
            else
            {
                ConsoleLogger.Debug($"Version current item: {item.Name} version: {installedVersion}");
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"ManagedInstalls check failed item: {item.Name} error: {ex.Message}");
            result.Status = "error";
            result.Reason = $"ManagedInstalls status check failed: {ex.Message}";
            result.ReasonCode = StatusReasonCode.CheckFailed;
            result.Error = ex;
        }

        return result;
    }

    private static string? GetFileVersion(string path)
    {
        try
        {
            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var version = versionInfo.FileVersion;
            
            // Validate that the returned string is actually a version number.
            // FileVersionInfo can return metadata fields like "InternalName" on some builds.
            if (!string.IsNullOrEmpty(version) && version.Any(char.IsDigit))
                return version;
            
            return null;
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

    #region Pending State Detection

    /// <summary>
    /// Checks if any blocking applications are running for the given item
    /// </summary>
    /// <param name="blockingApps">List of process names or executable paths to check</param>
    /// <param name="runningApps">Output: list of blocking apps that are currently running</param>
    /// <returns>True if any blocking apps are running</returns>
    public static bool CheckBlockingApps(IEnumerable<string>? blockingApps, out List<string> runningApps)
    {
        runningApps = new List<string>();
        
        if (blockingApps == null) return false;

        try
        {
            var processes = System.Diagnostics.Process.GetProcesses()
                .Select(p => p.ProcessName.ToLowerInvariant())
                .ToHashSet();

            foreach (var app in blockingApps)
            {
                if (string.IsNullOrEmpty(app)) continue;
                
                // Extract process name without extension
                var processName = Path.GetFileNameWithoutExtension(app).ToLowerInvariant();
                
                if (processes.Contains(processName))
                {
                    runningApps.Add(app);
                }
            }
        }
        catch
        {
            // On error, assume no blocking apps
        }

        return runningApps.Count > 0;
    }

    /// <summary>
    /// Checks if the system has a pending reboot
    /// </summary>
    /// <returns>True if a reboot is pending</returns>
    public static bool IsPendingReboot()
    {
        try
        {
            // Check Windows Update pending reboot
            using var wuKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wuKey != null) return true;

            // Check Component Based Servicing pending reboot
            using var cbsKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbsKey != null) return true;

            // Check Session Manager pending file rename operations
            using var smKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (smKey != null)
            {
                var pendingOps = smKey.GetValue("PendingFileRenameOperations");
                if (pendingOps is string[] ops && ops.Length > 0) return true;
            }

            // Check if SCCM/ConfigMgr says reboot is pending
            using var ccmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\CCM\ClientSDK\InProgress");
            if (ccmKey != null)
            {
                var rebootPending = ccmKey.GetValue("RebootRequired");
                if (rebootPending != null) return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if there's sufficient disk space for installation
    /// </summary>
    /// <param name="requiredBytes">Required space in bytes (from package size)</param>
    /// <param name="targetPath">Target installation path (defaults to C:\)</param>
    /// <param name="availableBytes">Output: available bytes on drive</param>
    /// <returns>True if sufficient space is available</returns>
    public static bool HasSufficientDiskSpace(long requiredBytes, string? targetPath, out long availableBytes)
    {
        availableBytes = 0;
        
        if (requiredBytes <= 0) return true; // No size specified, assume OK

        try
        {
            var drivePath = targetPath ?? @"C:\";
            
            // Get drive letter from path
            var driveLetter = Path.GetPathRoot(drivePath);
            if (string.IsNullOrEmpty(driveLetter)) driveLetter = @"C:\";

            var driveInfo = new DriveInfo(driveLetter);
            availableBytes = driveInfo.AvailableFreeSpace;

            // Require at least 2x the package size (for extraction, temp files, etc.)
            var requiredWithBuffer = requiredBytes * 2;
            
            return availableBytes >= requiredWithBuffer;
        }
        catch
        {
            // On error, assume sufficient space
            return true;
        }
    }

    /// <summary>
    /// Checks pending states for a package and returns appropriate status result if blocked
    /// </summary>
    /// <param name="item">The catalog item to check</param>
    /// <param name="missingDependencies">List of missing dependencies (from external dependency check)</param>
    /// <returns>StatusCheckResult if blocked by pending conditions, null otherwise</returns>
    public StatusCheckResult? CheckPendingConditions(CatalogItem item, IEnumerable<string>? missingDependencies = null)
    {
        // Check blocking apps
        if (CheckBlockingApps(item.BlockingApps, out var runningApps))
        {
            return new StatusCheckResult
            {
                Status = "pending",
                NeedsAction = true,
                Reason = $"Waiting for {string.Join(", ", runningApps)} to close",
                ReasonCode = StatusReasonCode.BlockingApps,
                DetectionMethod = DetectionMethod.None,
                TargetVersion = item.Version
            };
        }

        // Check pending reboot
        if (IsPendingReboot())
        {
            return new StatusCheckResult
            {
                Status = "pending",
                NeedsAction = true,
                Reason = "System requires reboot before installation can proceed",
                ReasonCode = StatusReasonCode.PendingReboot,
                DetectionMethod = DetectionMethod.None,
                TargetVersion = item.Version
            };
        }

        // Check missing dependencies
        var deps = missingDependencies?.ToList();
        if (deps != null && deps.Count > 0)
        {
            return new StatusCheckResult
            {
                Status = "pending",
                NeedsAction = true,
                Reason = $"Waiting for dependencies: {string.Join(", ", deps)}",
                ReasonCode = StatusReasonCode.DependencyMissing,
                DetectionMethod = DetectionMethod.None,
                TargetVersion = item.Version
            };
        }

        // Check disk space
        var installerSize = item.Installer.Size ?? 0;
        if (installerSize > 0 && !HasSufficientDiskSpace(installerSize, null, out var availableBytes))
        {
            var requiredMb = installerSize / (1024 * 1024);
            var availableMb = availableBytes / (1024 * 1024);
            return new StatusCheckResult
            {
                Status = "pending",
                NeedsAction = true,
                Reason = $"Insufficient disk space (need {requiredMb}MB, have {availableMb}MB)",
                ReasonCode = StatusReasonCode.DiskSpace,
                DetectionMethod = DetectionMethod.None,
                TargetVersion = item.Version
            };
        }

        // No pending conditions blocking installation
        return null;
    }

    #endregion

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
