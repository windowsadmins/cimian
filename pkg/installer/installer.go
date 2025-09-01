// pkg/installer/installer.go contains the main logic for installing, updating, and uninstalling software items.

package installer

import (
	"archive/zip"
	"bytes"
	"context"
	"encoding/xml"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"

	"golang.org/x/sys/windows/registry"

	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/manifest"
	"github.com/windowsadmins/cimian/pkg/retry"
	"github.com/windowsadmins/cimian/pkg/selfservice"
	"github.com/windowsadmins/cimian/pkg/status"
	"github.com/windowsadmins/cimian/pkg/utils"
)

// By default, we expect these paths for msiexec/powershell/chocolatey.
var (
	commandMsi = filepath.Join(os.Getenv("WINDIR"), "system32", "msiexec.exe")
	commandPs1 = filepath.Join(os.Getenv("WINDIR"), "system32", "WindowsPowershell", "v1.0", "powershell.exe")

	// Typically "C:\\ProgramData\\chocolatey\\bin\\choco.exe"
	chocolateyBin = filepath.Join(os.Getenv("ProgramData"), "chocolatey", "bin", "choco.exe")
)

// buildPowerShellArgs creates a consistent set of PowerShell arguments with execution policy bypass
func buildPowerShellArgs(cfg *config.Configuration, args ...string) []string {
	baseArgs := []string{"-NoProfile"}

	// Add execution policy bypass if configured (default: true)
	if cfg == nil || cfg.ForceExecutionPolicyBypass {
		baseArgs = append(baseArgs, "-ExecutionPolicy", "Bypass")
	}

	// Add any additional arguments
	baseArgs = append(baseArgs, args...)
	return baseArgs
}

// buildStandardPowerShellArgs creates the standard PowerShell arguments with execution policy bypass
// This function provides a consistent set of arguments for PowerShell execution across all Cimian components
func buildStandardPowerShellArgs(args ...string) []string {
	baseArgs := []string{"-NoProfile", "-ExecutionPolicy", "Bypass"}
	baseArgs = append(baseArgs, args...)
	return baseArgs
}

// storeInstalledVersionInRegistry writes an installed version to HKLM\Software\ManagedInstalls\<Name>.
func storeInstalledVersionInRegistry(item catalog.Item) {
	regPath := `Software\ManagedInstalls\` + item.Name
	k, _, err := registry.CreateKey(registry.LOCAL_MACHINE, regPath, registry.SET_VALUE)
	if err != nil {
		logging.Warn("Failed to create registry key for installed version",
			"key", regPath, "error", err)
		return
	}
	defer k.Close()

	versionStr := strings.TrimSpace(item.Version)
	if versionStr == "" {
		versionStr = "0.0.0"
	}
	err = k.SetStringValue("Version", versionStr)
	if err != nil {
		logging.Warn("Failed to set 'Version' in registry",
			"key", regPath, "error", err)
		return
	}
	logging.Debug("Wrote local installed version to registry",
		"item", item.Name, "version", versionStr)

	// If this is a Cimian package, also write to the main Cimian registry key
	itemName := strings.ToLower(strings.TrimSpace(item.Name))
	if itemName == "cimian" || itemName == "cimiantools" || strings.HasPrefix(itemName, "cimian-") || strings.HasPrefix(itemName, "cimiantools-") {
		if err := config.WriteCimianVersionToRegistry(versionStr); err != nil {
			logging.Warn("Failed to write Cimian version to main registry key", "error", err)
		} else {
			logging.Info("Updated Cimian version in registry", "version", versionStr)
		}
	}
}

// removeInstalledVersionFromRegistry deletes HKLM\Software\ManagedInstalls\<Name>.
func removeInstalledVersionFromRegistry(item catalog.Item) {
	regPath := `Software\ManagedInstalls\` + item.Name
	err := registry.DeleteKey(registry.LOCAL_MACHINE, regPath)
	if err != nil {
		if err == registry.ErrNotExist {
			logging.Debug("No registry entry to remove", "item", item.Name)
			return
		}
		logging.Warn("Failed to delete registry key for item",
			"item", item.Name, "key", regPath, "error", err)
		return
	}
	logging.Debug("Removed registry key after uninstall",
		"item", item.Name, "key", regPath)
}

// Install is the main entry point for installing/updating/uninstalling a catalog item.
func Install(item catalog.Item, action, localFile, cachePath string, checkOnly bool, cfg *config.Configuration) (string, error) {
	// DEBUG: Add explicit logging at start of Install function
	logging.Info("installer.Install called", "item", item.Name, "action", action, "localFile", localFile, "checkOnly", checkOnly)
	
	// If we are only checking, do not proceed with actual installation.
	if checkOnly {
		logging.Info("CheckOnly mode: would perform action",
			"action", action, "item", item.Name)
		return "CheckOnly: No action performed.", nil
	}

	switch strings.ToLower(action) {
	case "install", "update":
		// Architecture check
		sysArch := status.GetSystemArchitecture()
		if !status.SupportsArchitecture(item, sysArch) {
			// Log the architecture failure as both warning and failed for proper ReportMate tracking
			detailedError := fmt.Sprintf("Architecture mismatch: system arch %s not supported (package supports: %v)", sysArch, item.SupportedArch)
			
			// Log as warning for configuration awareness
			logging.LogEventEntry("install", "architecture_check", logging.StatusWarning,
				detailedError,
				logging.WithContext("item", item.Name),
				logging.WithContext("system_arch", sysArch),
				logging.WithContext("supported_arch", fmt.Sprintf("%v", item.SupportedArch)),
				logging.WithContext("error_type", "architecture_incompatibility"))
			
			// Also log as failed installation for proper failure tracking
			logging.LogEventEntry("install", "install_package", logging.StatusError,
				fmt.Sprintf("Installation failed: %s", detailedError),
				logging.WithContext("item", item.Name),
				logging.WithContext("detailed_error", detailedError),
				logging.WithContext("failure_reason", "architecture_incompatibility"),
				logging.WithContext("system_arch", sysArch),
				logging.WithContext("supported_arch", fmt.Sprintf("%v", item.SupportedArch)))
				
			return "", fmt.Errorf("system arch %s not in supported_arch=%v for item %s",
				sysArch, item.SupportedArch, item.Name)
		}

		// Check if this is a script-only item (no installer section)
		if item.Installer.Type == "" && (string(item.InstallCheckScript) != "" || string(item.PreScript) != "" || string(item.PostScript) != "") {
			// Handle script-only packages with no installer file
			logging.Debug("Processing script-only item (nopkg)", "item", item.Name)

			// Run preinstall script if present
			if item.PreScript != "" {
				out, err := runNopkgScript(item.PreScript, cachePath, "preinstall")
				if err != nil {
					return "", fmt.Errorf("preinstall script failed: %w", err)
				}
				logging.Debug("Preinstall script completed", "item", item.Name, "output", out)
			}

			// Run postinstall script if present
			if item.PostScript != "" {
				out, err := runNopkgScript(item.PostScript, cachePath, "postinstall")
				if err != nil {
					return "", fmt.Errorf("postinstall script failed: %w", err)
				}
				logging.Debug("Postinstall script completed", "item", item.Name, "output", out)
			}

			// For OnDemand items, do not store installed version in registry
			if !item.OnDemand {
				storeInstalledVersionInRegistry(item)
			} else {
				logging.Info("OnDemand item completed successfully (not marking as installed)", "item", item.Name)
				// Remove OnDemand items from the self-service manifest after successful execution
				if err := selfservice.RemoveFromSelfServiceInstalls(item.Name); err != nil {
					logging.Warn("Failed to remove OnDemand item from self-service manifest",
						"item", item.Name, "error", err)
				}
			}

			logging.Info("Script-only item processed successfully", "item", item.Name)
			return "Script-only installation success", nil
		}

		// If it's a nupkg, handle it via Chocolatey logic
		if strings.ToLower(item.Installer.Type) == "nupkg" {
			result, err := installOrUpgradeNupkg(item, localFile, cachePath, cfg)
			if err != nil {
				logging.Error("NUPKG installation failed", "item", item.Name, "error", err)
				// Log event for ItemRecord tracking
				status := logging.StatusFromError("nupkg_install", err)
				logging.LogEventEntry("install", "install_package", status,
					fmt.Sprintf("NUPKG installation failed: %v", err),
					logging.WithContext("item", item.Name),
					logging.WithContext("error", err.Error()),
					logging.WithContext("installer_type", "nupkg"))
				return result, err
			}
			// Log successful nupkg installation
			logging.LogEventEntry("install", "install_package", logging.StatusInstalled,
				"NUPKG installation completed successfully",
				logging.WithContext("item", item.Name),
				logging.WithContext("installer_type", "nupkg"))
			return result, err
		}

		// Otherwise, handle MSI/EXE/Powershell, etc.
		err := installNonNupkg(item, localFile, cachePath, cfg)
		if err != nil {
			logging.Error("Installation failed", "item", item.Name, "error", err)
			// Log event for ItemRecord tracking
			status := logging.StatusFromError("package_install", err)
			logging.LogEventEntry("install", "install_package", status,
				fmt.Sprintf("Package installation failed: %v", err),
				logging.WithContext("item", item.Name),
				logging.WithContext("error", err.Error()),
				logging.WithContext("installer_type", item.Installer.Type))
			return "", err
		}

		// For OnDemand items, do not store installed version in registry
		// This allows them to be run repeatedly without being considered "installed"
		if !item.OnDemand {
			storeInstalledVersionInRegistry(item)
		} else {
			logging.Info("OnDemand item completed successfully (not marking as installed)", "item", item.Name)
			// Remove OnDemand items from the self-service manifest after successful execution
			if err := selfservice.RemoveFromSelfServiceInstalls(item.Name); err != nil {
				logging.Warn("Failed to remove OnDemand item from self-service manifest",
					"item", item.Name, "error", err)
			}
		}

		// Attempt immediate cleanup if item has installs array for verification
		// This provides faster cache cleanup than waiting for the next run
		immediateCleanupAfterInstall(item, localFile, cfg)

		logging.Info("Installed item successfully", "item", item.Name)
		// Log successful installation event for ItemRecord tracking
		logging.LogEventEntry("install", "install_package", logging.StatusInstalled,
			"Package installation completed successfully",
			logging.WithContext("item", item.Name),
			logging.WithContext("installer_type", item.Installer.Type))
		
		// Additional cache cleanup attempt for items that don't have installs arrays
		if len(item.Installs) == 0 && localFile != "" && localFile != "." {
			if cleanupErr := cleanupInstallerFile(localFile, item.Name); cleanupErr == nil {
				logging.Info("Successfully performed fallback cache cleanup for item without installs array", "item", item.Name)
			}
		}
		
		return "Installation success", nil

	case "uninstall":
		sysArch := status.GetSystemArchitecture()
		if !status.SupportsArchitecture(item, sysArch) {
			logging.Warn("Skipping uninstall due to system arch mismatch",
				"item", item.Name, "arch", sysArch)
		}
		out, err := uninstallItem(item, cachePath)
		if err != nil {
			logging.Error("Uninstall failed", "item", item.Name, "error", err)
			return out, err
		}
		removeInstalledVersionFromRegistry(item)
		logging.Info("Uninstalled item successfully", "item", item.Name)
		return out, nil

	case "profile":
		// Configuration profiles are handled by Device Management Service solution, not directly by Cimian
		logging.Info("Configuration profile scheduled for deployment via Device Management Service", "profile", item.Name)
		return "Profile scheduled for Device Management Service deployment", nil

	case "app":
		// Apps are handled by Device Management Service solution, not directly by Cimian
		logging.Info("App scheduled for deployment via Device Management Service", "app", item.Name)
		return "App scheduled for Device Management Service deployment", nil

	default:
		msg := fmt.Sprintf("Unsupported action: %s", action)
		logging.Warn(msg)
		return "", fmt.Errorf("%v", msg)
	}
}

// LocalNeedsUpdate explicitly prioritizes clear version comparison over status.CheckStatus.
func LocalNeedsUpdate(m manifest.Item, catMap map[string]catalog.Item, cfg *config.Configuration) bool {
	key := strings.ToLower(m.Name)
	catItem, found := catMap[key]
	if !found {
		logging.Debug("Item not found in local catalog; falling back to old update check", "item", m.Name)
		return needsUpdateOld(m, cfg)
	}

	// Call CheckStatus explicitly instead of just comparing versions
	needed, err := status.CheckStatus(catItem, "update", cfg.CachePath)
	if err != nil {
		logging.Warn("Error in CheckStatus, assuming update needed",
			"item", m.Name, "error", err)
		return true
	}
	if needed {
		logging.Debug("CheckStatus explicitly indicates update required", "item", m.Name)
		return true
	}

	logging.Debug("CheckStatus explicitly indicates NO update required", "item", m.Name)
	return false
}

// PrepareDownloadItemsWithCatalog returns the catalog items that need to be installed/updated,
// based on the deduplicated manifest items.
func PrepareDownloadItemsWithCatalog(manifestItems []manifest.Item, catMap map[string]catalog.Item, cfg *config.Configuration) []catalog.Item {
	var results []catalog.Item
	dedupedItems := status.DeduplicateManifestItems(manifestItems)
	for _, m := range dedupedItems {
		if LocalNeedsUpdate(m, catMap, cfg) {
			key := strings.ToLower(m.Name)
			if catItem, found := catMap[key]; found {
				results = append(results, catItem)
			} else {
				logging.Warn("Manifest item %s not found in local catalog", m.Name)
			}
		}
	}
	return results
}

// needsUpdateOld is the original fallback logic for deciding an update is needed.
func needsUpdateOld(item manifest.Item, _ *config.Configuration) bool {
	if item.InstallCheckScript != "" {
		exitCode, err := runPowerShellInline(item.InstallCheckScript)
		if err != nil {
			logging.Warn("InstallCheckScript failed for %s with error: %v; defaulting to update", item.Name, err)
			return true
		}
		logging.Debug("InstallCheckScript for %s returned exit code %d", item.Name, exitCode)
		if exitCode == 0 {
			logging.Debug("InstallCheckScript for %s indicates not installed; update needed", item.Name)
			return true
		}
		logging.Debug("InstallCheckScript for %s indicates installed (exit code %d); no update needed", item.Name, exitCode)
		return false
	}
	logging.Debug("No InstallCheckScript defined for %s; assuming no update needed", item.Name)
	return false
}

// installNonNupkg handles MSI/EXE/Powershell items.
func installNonNupkg(item catalog.Item, localFile, cachePath string, cfg *config.Configuration) error {
	// DEBUG: Add explicit logging at start of installNonNupkg
	logging.Info("installNonNupkg called", "item", item.Name, "type", item.Installer.Type, "localFile", localFile)
	
	switch strings.ToLower(item.Installer.Type) {
	case "msi":
		logging.Info("Executing MSI installer", "item", item.Name, "localFile", localFile)
		out, err := runMSIInstaller(item, localFile, cfg)
		if err != nil {
			logging.Error("MSI installer failed", "item", item.Name, "error", err)
			
			// Log the MSI failure as an error (actual software installation failure) for ReportMate
			status := logging.StatusFromError("msi_execution", err)
			logging.LogEventEntry("install", "msi_execution", status,
				fmt.Sprintf("MSI installer failed: %s", err.Error()),
				logging.WithContext("item", item.Name),
				logging.WithContext("installer_type", "msi"),
				logging.WithContext("installer_path", localFile),
				logging.WithContext("error_details", err.Error()))
			return err
		}
		logging.Debug("MSI install output", "output", out)
		return nil

	case "exe":
		logging.Info("Executing EXE installer", "item", item.Name, "localFile", localFile)
		// Run preinstall script if present
		if item.PreScript != "" {
			out, err := runPreinstallScript(item, localFile, cachePath)
			if err != nil {
				logging.Error("Preinstall script failed", "item", item.Name, "error", err)
				
				// Log the PreScript failure as an event for ReportMate
				status := logging.StatusFromError("prescript", err)
				logging.LogEventEntry("install", "prescript_execution", status,
					fmt.Sprintf("Preinstall script failed: %s", err.Error()),
					logging.WithContext("item", item.Name),
					logging.WithContext("script_type", "preinstall"),
					logging.WithContext("installer_path", localFile),
					logging.WithContext("error_details", err.Error()))
				return err
			}
			logging.Debug("Preinstall script for EXE completed", "output", out)
		}
		// Always run the EXE afterwards
		out, err := runEXEInstaller(item, localFile, cfg)
		if err != nil {
			// Log the EXE failure as an error (actual software installation failure) for ReportMate
			status := logging.StatusFromError("exe_execution", err)
			logging.LogEventEntry("install", "exe_execution", status,
				fmt.Sprintf("EXE installer failed: %s", err.Error()),
				logging.WithContext("item", item.Name),
				logging.WithContext("installer_type", "exe"),
				logging.WithContext("installer_path", localFile),
				logging.WithContext("error_details", err.Error()))
			return err
		}
		logging.Debug("EXE install output", "output", out)
		return nil

	case "powershell":
		out, err := runPS1Installer(item, localFile, cfg)
		if err != nil {
			// Log the PowerShell failure as an error (actual software installation failure) for ReportMate
			status := logging.StatusFromError("powershell_execution", err)
			logging.LogEventEntry("install", "powershell_execution", status,
				fmt.Sprintf("PowerShell installer failed: %s", err.Error()),
				logging.WithContext("item", item.Name),
				logging.WithContext("installer_type", "powershell"),
				logging.WithContext("installer_path", localFile),
				logging.WithContext("error_details", err.Error()))
			return err
		}
		logging.Debug("PS1 install output", "output", out)
		return nil

	case "msix":
		out, err := runMSIXInstaller(item, localFile, cfg)
		if err != nil {
			return err
		}
		logging.Debug("MSIX install output", "output", out)
		return nil

	case "nopkg":
		// Handle script-only packages with no installer file
		logging.Debug("Processing nopkg item (script-only)", "item", item.Name)

		// Run preinstall script if present
		if item.PreScript != "" {
			out, err := runNopkgScript(item.PreScript, cachePath, "preinstall")
			if err != nil {
				return fmt.Errorf("preinstall script failed: %w", err)
			}
			logging.Debug("Preinstall script completed", "item", item.Name, "output", out)
		}

		// Run postinstall script if present
		if item.PostScript != "" {
			out, err := runNopkgScript(item.PostScript, cachePath, "postinstall")
			if err != nil {
				return fmt.Errorf("postinstall script failed: %w", err)
			}
			logging.Debug("Postinstall script completed", "item", item.Name, "output", out)
		}

		return nil

	default:
		return fmt.Errorf("unknown installer type: %s", item.Installer.Type)
	}
}

// installOrUpgradeNupkg handles local .nupkg installs/updates using Chocolatey without unnecessary renaming.
func installOrUpgradeNupkg(item catalog.Item, downloadedFile, cachePath string, cfg *config.Configuration) (string, error) {
	nupkgID, nupkgVer, metaErr := extractNupkgMetadata(downloadedFile)
	if metaErr != nil {
		logging.Warn("Failed to parse .nuspec; falling back to item.Name",
			"file", downloadedFile, "err", metaErr)
		nupkgID = strings.TrimSpace(item.Identifier)
		if nupkgID == "" {
			nupkgID = strings.TrimSpace(item.Name)
		}
		if nupkgID == "" {
			nupkgID = "unknown-nupkgID"
		}
		nupkgVer = "0.0.0"
	}
	logging.Debug("Parsed .nuspec metadata", "nupkgID", nupkgID, "nupkgVer", nupkgVer)

	installed, checkErr := isNupkgInstalled(nupkgID)
	if checkErr != nil {
		logging.Warn("Could not detect if nupkg is installed; forcing install",
			"pkgID", nupkgID, "err", checkErr)
		return doChocoInstall(downloadedFile, nupkgID, nupkgVer, cachePath, item, cfg)
	}

	if !installed {
		logging.Info("Nupkg not installed; installing", "pkgID", nupkgID)
		return doChocoInstall(downloadedFile, nupkgID, nupkgVer, cachePath, item, cfg)
	}

	logging.Info("Nupkg is installed; forcing upgrade/downgrade", "pkgID", nupkgID)
	return doChocoUpgrade(downloadedFile, nupkgID, nupkgVer, cachePath, item, cfg)
}

// doChocoInstall runs choco install with the given nupkg file.
func doChocoInstall(filePath, pkgID, pkgVer, cachePath string, item catalog.Item, cfg *config.Configuration) (string, error) {
	// Run chocolateyBeforeInstall.ps1 if it exists in the .nupkg
	extractAndRunChocolateyBeforeInstall(filePath, item)

	chocoLog := filepath.Join(logging.GetCurrentLogDir(), fmt.Sprintf("choco_install_%s.log", pkgID))
	sourceDir := filepath.Dir(filePath)
	cmdArgs := []string{
		"install", pkgID,
		"--version", pkgVer,
		"--source", sourceDir,
		"-y",
		"--force",
		"--allowdowngrade",
		"--debug",
		fmt.Sprintf("--log-file=%s", chocoLog),
	}

	logging.Debug("doChocoInstall => final command",
		"exe", chocolateyBin, "args", strings.Join(cmdArgs, " "))

	out, err := runCMD(chocolateyBin, cmdArgs)
	if err != nil {
		logging.Error("Choco install failed", "pkgID", pkgID, "error", err)
		
		// Log the Chocolatey failure as an event for ReportMate
		status := logging.StatusFromError("chocolatey_install", err)
		logging.LogEventEntry("install", "chocolatey_install", status,
			fmt.Sprintf("Chocolatey install failed: %s", err.Error()),
			logging.WithContext("item", item.Name),
			logging.WithContext("package_id", pkgID),
			logging.WithContext("package_version", pkgVer),
			logging.WithContext("chocolatey_command", fmt.Sprintf("choco install %s --version %s", pkgID, pkgVer)),
			logging.WithContext("error_details", err.Error()))
		return out, err
	}

	storeInstalledVersionInRegistry(catalog.Item{
		Name:    item.Name,
		Version: pkgVer,
	})

	// Attempt immediate cleanup if item has installs array for verification
	immediateCleanupAfterInstall(item, filePath, cfg)

	logging.Info("Choco install succeeded", "pkgID", pkgID)
	return out, nil
}

// doChocoUpgrade runs choco upgrade with the given nupkg file.
func doChocoUpgrade(filePath, pkgID, pkgVer, cachePath string, item catalog.Item, cfg *config.Configuration) (string, error) {
	// Run chocolateyBeforeInstall.ps1 if it exists in the .nupkg
	extractAndRunChocolateyBeforeInstall(filePath, item)

	chocoLog := filepath.Join(logging.GetCurrentLogDir(), fmt.Sprintf("choco_upgrade_%s.log", pkgID))
	sourceDir := filepath.Dir(filePath)
	cmdArgs := []string{
		"upgrade", pkgID,
		"--version", pkgVer,
		"--source", sourceDir,
		"-y",
		"--force",
		"--allowdowngrade",
		"--debug",
		fmt.Sprintf("--log-file=%s", chocoLog),
	}

	logging.Debug("doChocoUpgrade => final command",
		"exe", chocolateyBin, "args", strings.Join(cmdArgs, " "))

	out, err := runCMD(chocolateyBin, cmdArgs)
	if err != nil {
		logging.Error("Choco upgrade failed", "pkgID", pkgID, "error", err)
		
		// Log the Chocolatey upgrade failure as an event for ReportMate
		status := logging.StatusFromError("chocolatey_upgrade", err)
		logging.LogEventEntry("install", "chocolatey_upgrade", status,
			fmt.Sprintf("Chocolatey upgrade failed: %s", err.Error()),
			logging.WithContext("item", item.Name),
			logging.WithContext("package_id", pkgID),
			logging.WithContext("package_version", pkgVer),
			logging.WithContext("chocolatey_command", fmt.Sprintf("choco upgrade %s --version %s", pkgID, pkgVer)),
			logging.WithContext("error_details", err.Error()))
		return out, err
	}

	storeInstalledVersionInRegistry(catalog.Item{
		Name:    item.Name,
		Version: pkgVer,
	})

	// Attempt immediate cleanup if item has installs array for verification
	immediateCleanupAfterInstall(item, filePath, cfg)

	logging.Info("Choco upgrade succeeded", "pkgID", pkgID)
	return out, nil
}

func renameNupkgFile(downloadedFile, cacheDir, pkgID, pkgVer string) error {
	desiredName := fmt.Sprintf("%s.%s.nupkg", pkgID, pkgVer)
	newPath := filepath.Join(cacheDir, desiredName)
	if strings.EqualFold(downloadedFile, newPath) {
		return nil
	}
	if _, err := os.Stat(newPath); err == nil {
		if err := os.Remove(newPath); err != nil {
			return fmt.Errorf("failed to remove existing file: %w", err)
		}
	}
	if err := os.Rename(downloadedFile, newPath); err != nil {
		return fmt.Errorf("rename nupkg -> %s: %w", newPath, err)
	}
	return nil
}

func isNupkgInstalled(pkgID string) (bool, error) {
	cmdArgs := []string{
		"list",
		"--local-only",
		"--limit-output",
		"--exact",
		pkgID,
	}
	out, err := runCMD(chocolateyBin, cmdArgs)
	if err != nil {
		return false, err
	}
	lines := strings.Split(strings.TrimSpace(out), "\n")
	for _, line := range lines {
		if strings.HasPrefix(strings.ToLower(strings.TrimSpace(line)), strings.ToLower(pkgID)+"|") {
			return true, nil
		}
	}
	return false, nil
}

// uninstallItem decides how to uninstall MSI/EXE/PS1/nupkg/msix.
func uninstallItem(item catalog.Item, cachePath string) (string, error) {
	// Check if the item is uninstallable
	if !item.IsUninstallable() {
		msg := fmt.Sprintf("Item %s is marked as not uninstallable", item.Name)
		logging.Info(msg)
		return msg, fmt.Errorf("%v", msg)
	}

	// If uninstaller array is defined, use it for advanced uninstall operations
	if len(item.Uninstaller) > 0 {
		return processUninstallerArray(item, cachePath)
	}

	// Fall back to traditional uninstaller logic
	return processTraditionalUninstall(item, cachePath)
}

// processUninstallerArray handles uninstall operations using the uninstaller array
func processUninstallerArray(item catalog.Item, cachePath string) (string, error) {
	logging.Info("Processing uninstall using uninstaller array", "item", item.Name, "items", len(item.Uninstaller))

	var results []string
	var errors []string

	for _, uninstallItem := range item.Uninstaller {
		result, err := processUninstallItem(uninstallItem, item, cachePath)
		if err != nil {
			errorMsg := fmt.Sprintf("Failed to process uninstall item %s: %v", uninstallItem.Path, err)
			errors = append(errors, errorMsg)
			logging.Warn("Uninstall item failed", "item", item.Name, "path", uninstallItem.Path, "error", err)
		} else {
			results = append(results, result)
			logging.Info("Successfully processed uninstall item", "item", item.Name, "path", uninstallItem.Path)
		}
	}

	// Combine results
	finalResult := strings.Join(results, "; ")

	if len(errors) > 0 {
		if len(results) == 0 {
			// All items failed
			return finalResult, fmt.Errorf("all uninstall items failed: %s", strings.Join(errors, "; "))
		} else {
			// Some items succeeded, some failed - log warnings but don't fail entirely
			logging.Warn("Some uninstall items failed", "item", item.Name, "errors", errors)
		}
	}

	return finalResult, nil
}

// processUninstallItem handles a single item from the uninstaller array
func processUninstallItem(uninstallItem catalog.InstallItem, item catalog.Item, cachePath string) (string, error) {
	switch strings.ToLower(uninstallItem.Type) {
	case "file":
		return processUninstallFile(uninstallItem, item)
	case "directory":
		return processUninstallDirectory(uninstallItem, item)
	case "registry":
		return processUninstallRegistry(uninstallItem, item)
	case "msi":
		return processUninstallMSI(uninstallItem, item)
	case "exe":
		return processUninstallEXE(uninstallItem, item, cachePath)
	case "ps1", "powershell":
		return processUninstallPowerShell(uninstallItem, item, cachePath)
	default:
		return "", fmt.Errorf("unsupported uninstall item type: %s", uninstallItem.Type)
	}
}

// processUninstallFile removes a specific file
func processUninstallFile(uninstallItem catalog.InstallItem, item catalog.Item) (string, error) {
	path := uninstallItem.Path

	// Check if file exists
	if _, err := os.Stat(path); os.IsNotExist(err) {
		logging.Debug("File already removed or doesn't exist", "item", item.Name, "path", path)
		return fmt.Sprintf("File already removed: %s", path), nil
	}

	// Remove the file
	err := os.Remove(path)
	if err != nil {
		return "", fmt.Errorf("failed to remove file %s: %v", path, err)
	}

	logging.Info("Successfully removed file", "item", item.Name, "path", path)
	return fmt.Sprintf("Removed file: %s", path), nil
}

// processUninstallDirectory removes a specific directory
func processUninstallDirectory(uninstallItem catalog.InstallItem, item catalog.Item) (string, error) {
	path := uninstallItem.Path

	// Check if directory exists
	if info, err := os.Stat(path); os.IsNotExist(err) {
		logging.Debug("Directory already removed or doesn't exist", "item", item.Name, "path", path)
		return fmt.Sprintf("Directory already removed: %s", path), nil
	} else if err != nil {
		return "", fmt.Errorf("error checking directory %s: %v", path, err)
	} else if !info.IsDir() {
		return "", fmt.Errorf("path exists but is not a directory: %s", path)
	}

	// Remove the directory and all contents
	err := os.RemoveAll(path)
	if err != nil {
		return "", fmt.Errorf("failed to remove directory %s: %v", path, err)
	}

	logging.Info("Successfully removed directory", "item", item.Name, "path", path)
	return fmt.Sprintf("Removed directory: %s", path), nil
}

// processUninstallRegistry removes registry keys/values
func processUninstallRegistry(uninstallItem catalog.InstallItem, item catalog.Item) (string, error) {
	// This would implement registry key removal
	// For now, return a placeholder - this can be enhanced later
	logging.Info("Registry uninstall operation", "item", item.Name, "path", uninstallItem.Path)
	return fmt.Sprintf("Registry operation: %s", uninstallItem.Path), nil
}

// processUninstallMSI handles MSI-specific uninstall operations
func processUninstallMSI(uninstallItem catalog.InstallItem, item catalog.Item) (string, error) {
	productCode := uninstallItem.ProductCode
	if productCode == "" {
		return "", fmt.Errorf("ProductCode required for MSI uninstall operation")
	}

	// Base MSI uninstall arguments
	args := []string{"/x", productCode, "/qn", "/norestart"}

	// Add switches (/ style arguments)
	for _, sw := range uninstallItem.Switches {
		if strings.Contains(sw, "=") {
			parts := strings.SplitN(sw, "=", 2)
			key, value := parts[0], parts[1]
			if strings.ContainsAny(value, " ") {
				value = fmt.Sprintf("\"%s\"", value)
			}
			args = append(args, fmt.Sprintf("/%s=%s", key, value))
		} else {
			args = append(args, fmt.Sprintf("/%s", sw))
		}
	}

	// Add flags with MSI-aware processing
	for _, flag := range uninstallItem.Flags {
		flag = strings.TrimSpace(flag)

		// Split flag on first equals sign
		var key, val string
		if strings.Contains(flag, "=") {
			parts := strings.SplitN(flag, "=", 2)
			key, val = parts[0], parts[1]
		} else {
			key = flag
		}

		// If user already specified dashes, preserve them exactly
		if strings.HasPrefix(key, "--") || strings.HasPrefix(key, "-") {
			if val != "" {
				if strings.ContainsAny(val, " ") {
					val = fmt.Sprintf("\"%s\"", val)
				}
				args = append(args, fmt.Sprintf("%s=%s", key, val))
			} else {
				args = append(args, key)
			}
			continue
		}

		// For MSI, detect appropriate flag style
		flagPrefix := detectMSIFlagStyle(key, val)

		if val != "" {
			if strings.ContainsAny(val, " ") {
				val = fmt.Sprintf("\"%s\"", val)
			}
			if flagPrefix == "" {
				// MSI property format: PROPERTY=VALUE (no prefix)
				args = append(args, fmt.Sprintf("%s=%s", key, val))
			} else {
				args = append(args, fmt.Sprintf("%s%s=%s", flagPrefix, key, val))
			}
		} else {
			args = append(args, fmt.Sprintf("%s%s", flagPrefix, key))
		}
	}

	logging.Debug("Running MSI uninstall", "item", item.Name, "productCode", productCode, "args", args)
	return runCMD(commandMsi, args)
}

// processUninstallEXE handles EXE-specific uninstall operations
func processUninstallEXE(uninstallItem catalog.InstallItem, item catalog.Item, cachePath string) (string, error) {
	exePath := uninstallItem.Path

	// Check if uninstaller exists
	if _, err := os.Stat(exePath); os.IsNotExist(err) {
		return "", fmt.Errorf("uninstaller executable not found: %s", exePath)
	}

	// Build arguments from switches, flags, and fallback to item uninstaller arguments
	var args []string

	// Add switches (/ style arguments)
	for _, sw := range uninstallItem.Switches {
		if strings.Contains(sw, "=") {
			parts := strings.SplitN(sw, "=", 2)
			args = append(args, fmt.Sprintf("/%s=%s", parts[0], quoteIfNeeded(parts[1])))
		} else if strings.Contains(sw, " ") {
			parts := strings.SplitN(sw, " ", 2)
			args = append(args, fmt.Sprintf("/%s", parts[0]), quoteIfNeeded(parts[1]))
		} else {
			args = append(args, fmt.Sprintf("/%s", sw))
		}
	}

	// Add flags (- style arguments)
	for _, flag := range uninstallItem.Flags {
		flag = strings.TrimSpace(flag)

		// Split flags only on the first whitespace or equals sign
		var key, val string
		if strings.Contains(flag, "=") {
			parts := strings.SplitN(flag, "=", 2)
			key, val = parts[0], parts[1]
		} else if strings.Contains(flag, " ") {
			parts := strings.SplitN(flag, " ", 2)
			key, val = parts[0], strings.TrimSpace(parts[1])
		} else {
			key = flag
		}

		// If user already specified dashes, preserve them exactly
		if strings.HasPrefix(key, "--") || strings.HasPrefix(key, "-") {
			if val != "" {
				if strings.Contains(flag, "=") {
					args = append(args, fmt.Sprintf("%s=%s", key, quoteIfNeeded(val)))
				} else {
					args = append(args, key, quoteIfNeeded(val))
				}
			} else {
				args = append(args, key)
			}
		} else {
			// Smart detection based on installer patterns and flag characteristics
			flagPrefix := detectFlagStyle(exePath, key)

			if val != "" {
				if shouldUseEqualsFormat(key, val) {
					args = append(args, fmt.Sprintf("%s%s=%s", flagPrefix, key, quoteIfNeeded(val)))
				} else {
					args = append(args, fmt.Sprintf("%s%s", flagPrefix, key), quoteIfNeeded(val))
				}
			} else {
				args = append(args, fmt.Sprintf("%s%s", flagPrefix, key))
			}
		}
	}

	// Note: Legacy single uninstaller arguments are not supported
	// Use uninstaller array for complex argument handling

	logging.Debug("Running EXE uninstaller", "item", item.Name, "exe", exePath, "args", args)
	return runCMD(exePath, args)
}

// processUninstallPowerShell handles PowerShell script uninstall operations
func processUninstallPowerShell(uninstallItem catalog.InstallItem, item catalog.Item, cachePath string) (string, error) {
	scriptPath := uninstallItem.Path

	// Check if script exists
	if _, err := os.Stat(scriptPath); os.IsNotExist(err) {
		return "", fmt.Errorf("PowerShell uninstall script not found: %s", scriptPath)
	}

	// Build PowerShell arguments starting with standard execution policy bypass
	psArgs := buildStandardPowerShellArgs("-File", scriptPath)

	// Add switches and flags as script parameters
	for _, sw := range uninstallItem.Switches {
		if strings.Contains(sw, "=") {
			parts := strings.SplitN(sw, "=", 2)
			psArgs = append(psArgs, fmt.Sprintf("-%s", parts[0]), parts[1])
		} else {
			psArgs = append(psArgs, fmt.Sprintf("-%s", sw))
		}
	}

	for _, flag := range uninstallItem.Flags {
		flag = strings.TrimSpace(flag)

		// Split flags on equals or space
		if strings.Contains(flag, "=") {
			parts := strings.SplitN(flag, "=", 2)
			psArgs = append(psArgs, fmt.Sprintf("-%s", parts[0]), parts[1])
		} else if strings.Contains(flag, " ") {
			parts := strings.SplitN(flag, " ", 2)
			psArgs = append(psArgs, fmt.Sprintf("-%s", parts[0]), strings.TrimSpace(parts[1]))
		} else {
			psArgs = append(psArgs, fmt.Sprintf("-%s", flag))
		}
	}

	logging.Debug("Running PowerShell uninstall script", "item", item.Name, "script", scriptPath, "args", psArgs)
	return runCMD(commandPs1, psArgs)
}

// processTraditionalUninstall handles the original uninstaller logic
func processTraditionalUninstall(item catalog.Item, cachePath string) (string, error) {
	relPath, fileName := path.Split(item.Installer.Location)
	absFile := filepath.Join(cachePath, relPath, fileName)
	if _, err := os.Stat(absFile); os.IsNotExist(err) {
		msg := fmt.Sprintf("Uninstall file not found: %s", absFile)
		logging.Warn(msg)
		return msg, nil
	}

	switch strings.ToLower(item.Installer.Type) {
	case "msi":
		return runMSIUninstaller(absFile, item)
	case "exe":
		return runEXEUninstaller(absFile, item)
	case "ps1":
		return runPS1Uninstaller(absFile)
	case "nupkg":
		return runNupkgUninstaller(absFile)
	case "msix":
		return runMSIXUninstaller(absFile, item)
	default:
		msg := fmt.Sprintf("Unsupported installer type for uninstall: %s", item.Installer.Type)
		logging.Warn(msg)
		return "", fmt.Errorf("%v", msg)
	}
}

func runMSIInstaller(item catalog.Item, localFile string, cfg *config.Configuration) (string, error) {
	// Base MSI installation arguments
	logPath := filepath.Join(logging.GetCurrentLogDir(), "msi_install.log")
	args := []string{
		"/i", localFile,
		"/quiet",
		"/norestart",
		"/l*v", logPath,
	}

	// Add installer switches (/ style arguments)
	for _, sw := range item.Installer.Switches {
		if strings.Contains(sw, "=") {
			parts := strings.SplitN(sw, "=", 2)
			key, value := parts[0], parts[1]
			if strings.ContainsAny(value, " ") {
				value = fmt.Sprintf("\"%s\"", value)
			}
			args = append(args, fmt.Sprintf("/%s=%s", key, value))
		} else {
			args = append(args, fmt.Sprintf("/%s", sw))
		}
	}

	// Smart installer-aware flag processing for MSI
	for _, flag := range item.Installer.Flags {
		flag = strings.TrimSpace(flag)

		// Split flag on first equals sign
		var key, val string
		if strings.Contains(flag, "=") {
			parts := strings.SplitN(flag, "=", 2)
			key, val = parts[0], parts[1]
		} else {
			key = flag
		}

		// If user already specified dashes, preserve them exactly
		if strings.HasPrefix(key, "--") || strings.HasPrefix(key, "-") {
			if val != "" {
				if strings.ContainsAny(val, " ") {
					val = fmt.Sprintf("\"%s\"", val)
				}
				args = append(args, fmt.Sprintf("%s=%s", key, val))
			} else {
				args = append(args, key)
			}
			continue
		}

		// For MSI, most flags work with single dash (msiexec standard)
		// but some custom properties work better with no prefix at all for PROPERTY=VALUE
		flagPrefix := detectMSIFlagStyle(key, val)

		if val != "" {
			if strings.ContainsAny(val, " ") {
				val = fmt.Sprintf("\"%s\"", val)
			}
			if flagPrefix == "" {
				// MSI property format: PROPERTY=VALUE (no prefix)
				args = append(args, fmt.Sprintf("%s=%s", key, val))
			} else {
				args = append(args, fmt.Sprintf("%s%s=%s", flagPrefix, key, val))
			}
		} else {
			args = append(args, fmt.Sprintf("%s%s", flagPrefix, key))
		}
	}

	logging.Info("Invoking MSI install with timeout",
		"msi", localFile, "item", item.Name, "extraArgs", args, "timeoutMinutes", cfg.InstallerTimeoutMinutes)

	// Use timeout-aware command execution
	output, err := runCMDWithTimeout(commandMsi, args, cfg.InstallerTimeoutMinutes)

	if err != nil {
		// Check if it was a timeout error
		if strings.Contains(err.Error(), "timed out") {
			logging.Error("MSI installer timed out - likely waiting for user interaction",
				"item", item.Name, "timeoutMinutes", cfg.InstallerTimeoutMinutes)
			
			// Log timeout as specific event for ReportMate
			timeoutErr := fmt.Errorf("MSI installer timed out after %d minutes", cfg.InstallerTimeoutMinutes)
			status := logging.StatusFromError("msi_timeout", timeoutErr)
			logging.LogEventEntry("install", "msi_timeout", status,
				fmt.Sprintf("MSI installer timed out after %d minutes - installer may have shown interactive dialog", cfg.InstallerTimeoutMinutes),
				logging.WithContext("item", item.Name),
				logging.WithContext("timeout_minutes", fmt.Sprintf("%d", cfg.InstallerTimeoutMinutes)),
				logging.WithContext("installer_path", localFile),
				logging.WithContext("likely_cause", "interactive_dialog"))
			
			return output, fmt.Errorf("MSI installer timed out after %d minutes - installer may have shown interactive dialog", cfg.InstallerTimeoutMinutes)
		}

		if strings.Contains(err.Error(), "exit code=") {
			// Parse exit code from error message
			parts := strings.Split(err.Error(), "exit code=")
			if len(parts) > 1 {
				codeStr := strings.Split(parts[1], " ")[0]
				if code, parseErr := strconv.Atoi(codeStr); parseErr == nil {
					switch code {
					case 1603:
						// Log MSI fatal error as specific event for ReportMate
						fatalErr := fmt.Errorf("MSI installer failed with exit code 1603 (fatal error)")
						status := logging.StatusFromError("msi_fatal", fatalErr)
						logging.LogEventEntry("install", "msi_fatal_error", status,
							"MSI installer failed with exit code 1603 (fatal error) - installation cannot proceed",
							logging.WithContext("item", item.Name),
							logging.WithContext("exit_code", "1603"),
							logging.WithContext("exit_meaning", "fatal_installation_error"),
							logging.WithContext("installer_path", localFile),
							logging.WithContext("recommended_action", "check_msi_logs_and_requirements"))
						return output, fmt.Errorf("msiexec exit code 1603 (fatal) - skipping re-try")
					case 1618:
						// Log MSI conflict error as specific event for ReportMate
						conflictErr := fmt.Errorf("MSI installer failed with exit code 1618 (another installation in progress)")
						status := logging.StatusFromError("msi_conflict", conflictErr)
						logging.LogEventEntry("install", "msi_conflict_error", status,
							"MSI installer failed with exit code 1618 (another installation in progress)",
							logging.WithContext("item", item.Name),
							logging.WithContext("exit_code", "1618"),
							logging.WithContext("exit_meaning", "another_installation_in_progress"),
							logging.WithContext("installer_path", localFile),
							logging.WithContext("recommended_action", "wait_and_retry_later"))
						return output, fmt.Errorf("msiexec exit code 1618 (another install in progress)")
					case 3010:
						logging.Warn("MSI installed but requires reboot (3010)", "item", item.Name)
						
						// Log MSI reboot required as warning event for ReportMate
						logging.LogEventEntry("install", "msi_reboot_required", logging.StatusWarning,
							"MSI installation completed successfully but requires system reboot",
							logging.WithContext("item", item.Name),
							logging.WithContext("exit_code", "3010"),
							logging.WithContext("exit_meaning", "success_reboot_required"),
							logging.WithContext("installer_path", localFile),
							logging.WithContext("recommended_action", "schedule_system_reboot"))
						return output, nil
					default:
						// Log other MSI exit codes as events for ReportMate
						exitCodeErr := fmt.Errorf("MSI installer failed with exit code %d", code)
						status := logging.StatusFromError("msi_exit", exitCodeErr)
						logging.LogEventEntry("install", "msi_exit_code_error", status,
							fmt.Sprintf("MSI installer failed with exit code %d", code),
							logging.WithContext("item", item.Name),
							logging.WithContext("exit_code", fmt.Sprintf("%d", code)),
							logging.WithContext("installer_path", localFile),
							logging.WithContext("recommended_action", "check_msi_documentation_for_exit_code"))
						return output, fmt.Errorf("msiexec exit code %d", code)
					}
				}
			}
		}
		return output, err
	}
	logging.Info("MSI installed successfully", "item", item.Name)
	return output, nil
}

func runMSIUninstaller(absFile string, item catalog.Item) (string, error) {
	args := []string{"/x", absFile, "/qn", "/norestart"}
	// Note: Legacy single uninstaller arguments are not supported
	// Use uninstaller array for complex argument handling

	logging.Debug("runMSIUninstaller => final command",
		"exe", commandMsi, "args", strings.Join(args, " "))

	return runCMD(commandMsi, args)
}

func runMSIXInstaller(item catalog.Item, localFile string, cfg *config.Configuration) (string, error) {
	// Silent installation arguments for MSIX packages
	args := []string{
		localFile,
		"-AllowUnsigned",            // Skip signature verification prompts
		"-ForceApplicationShutdown", // Close app if running
		"-Volume", "C:",             // Specify volume to avoid selection dialogs
		"-ForceUpdateFromAnyVersion", // Allow updates from any version
		"-DisableDevelopmentMode",    // Ensure not in dev mode
	}

	logging.Info("Invoking MSIX install with silent arguments and timeout", "msix", localFile, "item", item.Name, "args", args, "timeoutMinutes", cfg.InstallerTimeoutMinutes)

	// Use timeout-aware command execution
	output, err := runCMDWithTimeout("Add-AppxPackage", args, cfg.InstallerTimeoutMinutes)

	if err != nil {
		if strings.Contains(err.Error(), "timed out") {
			logging.Error("MSIX installer timed out despite silent arguments",
				"item", item.Name, "timeoutMinutes", cfg.InstallerTimeoutMinutes)
			return output, fmt.Errorf("MSIX installer timed out after %d minutes - check if package requires additional privileges or has dependency issues", cfg.InstallerTimeoutMinutes)
		}
		logging.Error("MSIX installation failed", "item", item.Name, "error", err, "output", output)
		return output, err
	}
	logging.Info("MSIX installed successfully", "item", item.Name)
	return output, nil
}

func runMSIXUninstaller(_ string, item catalog.Item) (string, error) {
	args := []string{}
	// Note: Legacy single uninstaller arguments are not supported
	// Use uninstaller array for complex argument handling

	logging.Info("Invoking MSIX uninstaller for", "item", item.Name)
	logging.Debug("runMSIXUninstaller => final command",
		"cmd", "Remove-AppxPackage", "args", strings.Join(args, " "))

	cmd := exec.Command("Remove-AppxPackage", args...)
	outputBytes, err := cmd.CombinedOutput()
	output := string(outputBytes)
	if err != nil {
		logging.Error("MSIX uninstallation failed", "item", item.Name, "error", err, "output", output)
		return output, err
	}
	logging.Info("MSIX uninstalled successfully", "item", item.Name)
	return output, nil
}

// runEXEInstaller: supports human-friendly syntax for installer flags in pkginfo YAML.
func runEXEInstaller(item catalog.Item, localFile string, cfg *config.Configuration) (string, error) {
	installerPath := localFile
	args := []string{}

	// Handle optional verb
	if item.Installer.Verb != "" {
		args = append(args, item.Installer.Verb)
	}

	// Handle switches (e.g., /silent)
	for _, sw := range item.Installer.Switches {
		if strings.Contains(sw, "=") {
			parts := strings.SplitN(sw, "=", 2)
			args = append(args, fmt.Sprintf("/%s=%s", parts[0], quoteIfNeeded(parts[1])))
		} else if strings.Contains(sw, " ") {
			parts := strings.SplitN(sw, " ", 2)
			args = append(args, fmt.Sprintf("/%s", parts[0]), quoteIfNeeded(parts[1]))
		} else {
			args = append(args, fmt.Sprintf("/%s", sw))
		}
	}

	// Smart installer-aware flag processing
	for _, flag := range item.Installer.Flags {
		flag = strings.TrimSpace(flag)

		// If user already provided dashes in their flag, preserve their exact format
		if strings.HasPrefix(flag, "--") || strings.HasPrefix(flag, "-") {
			if strings.Contains(flag, "=") {
				// User explicitly used = format (e.g., "--mode=unattended"), preserve it
				args = append(args, flag)
			} else if strings.Contains(flag, " ") {
				// User used space format (e.g., "--mode unattended"), preserve it
				parts := strings.SplitN(flag, " ", 2)
				args = append(args, parts[0], quoteIfNeeded(strings.TrimSpace(parts[1])))
			} else {
				// Single flag without value (e.g., "--silent")
				args = append(args, flag)
			}
			continue
		}

		// Split flags only on the first whitespace or equals sign for auto-detection
		var key, val string
		if strings.Contains(flag, "=") {
			parts := strings.SplitN(flag, "=", 2)
			key, val = parts[0], parts[1]
		} else if strings.Contains(flag, " ") {
			parts := strings.SplitN(flag, " ", 2)
			key, val = parts[0], strings.TrimSpace(parts[1])
		} else {
			key = flag
		}

		// Smart detection based on installer patterns and flag characteristics
		flagPrefix := detectFlagStyle(installerPath, key)

		if val != "" {
			if shouldUseEqualsFormat(key, val) {
				args = append(args, fmt.Sprintf("%s%s=%s", flagPrefix, key, quoteIfNeeded(val)))
			} else {
				args = append(args, fmt.Sprintf("%s%s", flagPrefix, key), quoteIfNeeded(val))
			}
		} else {
			args = append(args, fmt.Sprintf("%s%s", flagPrefix, key))
		}
	}

	logging.Info("Executing EXE installer with timeout", "path", installerPath, "args", args, "timeoutMinutes", cfg.InstallerTimeoutMinutes)

	// Use timeout-aware command execution
	output, err := runCMDWithTimeout(installerPath, args, cfg.InstallerTimeoutMinutes)
	if err != nil {
		if strings.Contains(err.Error(), "timed out") {
			logging.Error("EXE installer timed out - likely waiting for user interaction",
				"item", item.Name, "timeoutMinutes", cfg.InstallerTimeoutMinutes)
			
			// Log EXE timeout as specific event for ReportMate
			timeoutErr := fmt.Errorf("EXE installer timed out after %d minutes", cfg.InstallerTimeoutMinutes)
			status := logging.StatusFromError("exe_timeout", timeoutErr)
			logging.LogEventEntry("install", "exe_timeout", status,
				fmt.Sprintf("EXE installer timed out after %d minutes - installer may have shown interactive dialog", cfg.InstallerTimeoutMinutes),
				logging.WithContext("item", item.Name),
				logging.WithContext("timeout_minutes", fmt.Sprintf("%d", cfg.InstallerTimeoutMinutes)),
				logging.WithContext("installer_path", installerPath),
				logging.WithContext("likely_cause", "interactive_dialog"))
			
			return output, fmt.Errorf("EXE installer timed out after %d minutes - installer may have shown interactive dialog", cfg.InstallerTimeoutMinutes)
		}
		
		// Log general EXE execution failures with exit codes if available
		if strings.Contains(err.Error(), "exit code=") {
			parts := strings.Split(err.Error(), "exit code=")
			if len(parts) > 1 {
				codeStr := strings.Split(parts[1], " ")[0]
				exitCodeErr := fmt.Errorf("EXE installer failed with exit code %s", codeStr)
				status := logging.StatusFromError("exe_exit", exitCodeErr)
				logging.LogEventEntry("install", "exe_exit_code_error", status,
					fmt.Sprintf("EXE installer failed with exit code %s", codeStr),
					logging.WithContext("item", item.Name),
					logging.WithContext("exit_code", codeStr),
					logging.WithContext("installer_path", installerPath),
					logging.WithContext("installer_output", output))
			}
		} else {
			// Log other EXE execution errors
			status := logging.StatusFromError("exe_execution", err)
			logging.LogEventEntry("install", "exe_execution_error", status,
				fmt.Sprintf("EXE installer execution failed: %s", err.Error()),
				logging.WithContext("item", item.Name),
				logging.WithContext("installer_path", installerPath),
				logging.WithContext("error_details", err.Error()),
				logging.WithContext("installer_output", output))
		}
		
		logging.Error("EXE installer execution failed", "error", err, "output", output)
		return output, err
	}
	logging.Info("EXE installer executed successfully", "output", output)
	return output, nil
}

// detectMSIFlagStyle determines the correct flag format for MSI installers
func detectMSIFlagStyle(key, value string) string {
	// MSI properties (ALL_CAPS with underscores) typically use no prefix
	// These get passed directly to the MSI as PROPERTY=VALUE
	if isEnvironmentStyleFlag(key) {
		return "" // No prefix for MSI properties
	}

	// Standard msiexec flags use forward slash
	standardMSIFlags := map[string]bool{
		"quiet": true, "passive": true, "norestart": true, "forcerestart": true,
		"promptrestart": true, "uninstall": true, "repair": true, "advertise": true,
		"update": true, "package": true, "log": true, "logfile": true,
	}

	if standardMSIFlags[strings.ToLower(key)] {
		return "/"
	}

	// For other flags, use forward slash (msiexec standard)
	return "/"
}

// detectFlagStyle determines the correct flag prefix based on flag name analysis
func detectFlagStyle(installerPath, flagName string) string {
	// Flag name pattern analysis
	switch {
	// Environment-style variables (ALL_CAPS with underscores) use single dash
	case isEnvironmentStyleFlag(flagName):
		return "-"

	// Long descriptive flags use double-dash
	case len(flagName) > 8 && strings.Contains(flagName, "_"):
		return "--"

	// Short flags (≤3 chars) use single dash
	case len(flagName) <= 3:
		return "-"
	}

	// Default to double-dash for unknown patterns
	return "--"
}

// shouldUseEqualsFormat determines if flag should use KEY=VALUE or KEY VALUE format
func shouldUseEqualsFormat(key, value string) bool {
	// Environment-style variables (like LICENSE_METHOD) typically use equals
	if isEnvironmentStyleFlag(key) {
		return true
	}

	// If value contains spaces, prefer equals format to avoid parsing issues
	if strings.Contains(value, " ") {
		return true
	}

	// Short values without spaces can use either format, prefer equals for consistency
	return true
}

// isEnvironmentStyleFlag checks if flag looks like an environment variable
func isEnvironmentStyleFlag(flagName string) bool {
	// Check for ALL_CAPS with underscores pattern
	if !strings.Contains(flagName, "_") {
		return false
	}

	// Must be all uppercase letters, numbers, and underscores
	for _, r := range flagName {
		if !((r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '_') {
			return false
		}
	}

	return true
}

// quoteIfNeeded adds double quotes if the string contains spaces and doesn't already have them.
func quoteIfNeeded(s string) string {
	s = strings.Trim(s, `"'`) // remove accidental outer quotes
	if strings.ContainsAny(s, " \t") {
		return fmt.Sprintf(`"%s"`, s)
	}
	return s
}

func runEXEUninstaller(absFile string, item catalog.Item) (string, error) {
	args := []string{}
	// Note: Legacy single uninstaller arguments are not supported
	// Use uninstaller array for complex argument handling

	logging.Debug("runEXEUninstaller => final command",
		"exe", absFile, "args", strings.Join(args, " "))

	return runCMD(absFile, args)
}

// runPS1Installer: powershell -File <localFile>
func runPS1Installer(item catalog.Item, localFile string, cfg *config.Configuration) (string, error) {
	_ = item
	psArgs := buildPowerShellArgs(cfg, "-File", localFile)

	logging.Info("Executing PowerShell installer with timeout",
		"file", localFile, "args", psArgs, "timeoutMinutes", cfg.InstallerTimeoutMinutes)

	// Use timeout-aware command execution
	output, err := runCMDWithTimeout(commandPs1, psArgs, cfg.InstallerTimeoutMinutes)
	if err != nil {
		if strings.Contains(err.Error(), "timed out") {
			logging.Error("PowerShell installer timed out - likely waiting for user interaction",
				"item", item.Name, "timeoutMinutes", cfg.InstallerTimeoutMinutes)
			
			// Log PowerShell timeout as specific event for ReportMate
			timeoutErr := fmt.Errorf("PowerShell installer timed out after %d minutes", cfg.InstallerTimeoutMinutes)
			status := logging.StatusFromError("powershell_timeout", timeoutErr)
			logging.LogEventEntry("install", "powershell_timeout", status,
				fmt.Sprintf("PowerShell installer timed out after %d minutes - script may have shown interactive dialog", cfg.InstallerTimeoutMinutes),
				logging.WithContext("item", item.Name),
				logging.WithContext("timeout_minutes", fmt.Sprintf("%d", cfg.InstallerTimeoutMinutes)),
				logging.WithContext("script_path", localFile),
				logging.WithContext("likely_cause", "interactive_dialog"))
			
			return output, fmt.Errorf("PowerShell installer timed out after %d minutes - script may have shown interactive dialog", cfg.InstallerTimeoutMinutes)
		}
		
		// Log PowerShell execution errors with exit codes if available
		if strings.Contains(err.Error(), "exit code=") {
			parts := strings.Split(err.Error(), "exit code=")
			if len(parts) > 1 {
				codeStr := strings.Split(parts[1], " ")[0]
				exitCodeErr := fmt.Errorf("PowerShell installer failed with exit code %s", codeStr)
				status := logging.StatusFromError("powershell_exit", exitCodeErr)
				logging.LogEventEntry("install", "powershell_exit_code_error", status,
					fmt.Sprintf("PowerShell installer failed with exit code %s", codeStr),
					logging.WithContext("item", item.Name),
					logging.WithContext("exit_code", codeStr),
					logging.WithContext("script_path", localFile),
					logging.WithContext("script_output", output))
			}
		} else {
			// Log other PowerShell execution errors
			status := logging.StatusFromError("powershell_execution", err)
			logging.LogEventEntry("install", "powershell_execution_error", status,
				fmt.Sprintf("PowerShell installer execution failed: %s", err.Error()),
				logging.WithContext("item", item.Name),
				logging.WithContext("script_path", localFile),
				logging.WithContext("error_details", err.Error()),
				logging.WithContext("script_output", output))
		}
		
		logging.Error("PowerShell installer execution failed", "error", err, "output", output)
		return output, err
	}
	logging.Info("PowerShell installer executed successfully", "output", output)
	return output, nil
}

func runPS1Uninstaller(absFile string) (string, error) {
	psArgs := buildStandardPowerShellArgs("-NonInteractive", "-File", absFile)

	logging.Debug("runPS1Uninstaller => final command",
		"exe", commandPs1, "args", strings.Join(psArgs, " "))

	return runCMD(commandPs1, psArgs)
}

// runNupkgUninstaller: choco uninstall <pkgID> ...
func runNupkgUninstaller(absFile string) (string, error) {
	pkgID, pkgVer, err := extractNupkgMetadata(absFile)
	if err != nil {
		return "", fmt.Errorf("failed reading nupkg for uninstall: %w", err)
	}
	cacheDir := filepath.Dir(absFile)
	logPath := filepath.Join(logging.GetCurrentLogDir(), fmt.Sprintf("choco_uninstall_%s.log", pkgID))
	args := []string{
		"uninstall", pkgID,
		"--version", pkgVer,
		"--source", cacheDir,
		"-y",
		"--force",
		"--debug",
		fmt.Sprintf("--log-file=%s", logPath),
	}

	logging.Debug("runNupkgUninstaller => final command",
		"exe", chocolateyBin, "args", strings.Join(args, " "))

	return runCMD(chocolateyBin, args)
}

// runPreinstallScript detects .bat vs .ps1 and calls the proper function.
func runPreinstallScript(item catalog.Item, localFile, cachePath string) (string, error) {
	preScriptStr := string(item.PreScript)
	s := strings.ToLower(preScriptStr)
	if strings.Contains(s, "@echo off") || strings.HasPrefix(s, "rem ") || strings.HasPrefix(s, "::") {
		return runBatInstaller(item, localFile, cachePath)
	}
	return runPS1FromScript(item, localFile, cachePath)
}

// runBatInstaller writes PreScript to a .bat file, then runs it.
func runBatInstaller(item catalog.Item, localFile, cachePath string) (string, error) {
	_ = localFile
	batPath := filepath.Join(cachePath, "tmp_preinstall.bat")
	if err := os.WriteFile(batPath, []byte(item.PreScript), 0644); err != nil {
		return "", fmt.Errorf("failed writing .bat: %w", err)
	}
	defer os.Remove(batPath)
	cmd := exec.Command("cmd.exe", "/c", batPath)
	hideConsoleWindow(cmd)
	var out, stderr bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &stderr
	err := cmd.Run()
	if err != nil {
		return out.String(), fmt.Errorf("bat preinstall failed: %v - %s", err, stderr.String())
	}
	return out.String(), nil
}

// runPS1FromScript writes PreScript to a .ps1 file, then runs it.
func runPS1FromScript(item catalog.Item, localFile, cachePath string) (string, error) {
	_ = localFile
	psFile := filepath.Join(cachePath, "preinstall_tmp.ps1")
	if err := os.WriteFile(psFile, []byte(item.PreScript), 0644); err != nil {
		return "", fmt.Errorf("failed writing .ps1: %w", err)
	}
	defer os.Remove(psFile)

	psArgs := buildStandardPowerShellArgs("-File", psFile)
	cmd := exec.Command(commandPs1, psArgs...)
	hideConsoleWindow(cmd)
	var out, stderr bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &stderr
	err := cmd.Run()
	if err != nil {
		return out.String(), fmt.Errorf("ps1 preinstall failed: %v - %s", err, stderr.String())
	}
	return out.String(), nil
}

// runCMD runs a command, capturing stdout/stderr. Non-zero exit yields an error.
// On Windows, ensures proper elevation inheritance for admin privileges.
func runCMD(command string, arguments []string) (string, error) {
	logging.Debug("runCMD => about to run",
		"command", command, "args", strings.Join(arguments, " "))

	// On Windows, use PowerShell wrapper to ensure proper elevation inheritance
	if runtime.GOOS == "windows" {
		return runCMDWithWindowsElevation(command, arguments)
	}

	cmd := exec.Command(command, arguments...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	err := cmd.Run()
	outStr := stdout.String()
	errStr := stderr.String()

	if err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			exitCode := exitErr.ExitCode()
			logging.Error("Command failed",
				"command", command, "args", arguments, "exitCode", exitCode, "stderr", errStr)
			// Include stderr in the error message for better debugging
			if errStr != "" {
				return outStr, fmt.Errorf("command failed exit code=%d: %s", exitCode, strings.TrimSpace(errStr))
			}
			return outStr, fmt.Errorf("command failed exit code=%d", exitCode)
		}
		logging.Error("Failed to run cmd",
			"command", command, "args", arguments, "error", err)
		return outStr, err
	}
	return outStr, nil
}

// runCMDWithWindowsElevation runs commands through PowerShell to ensure proper elevation inheritance
func runCMDWithWindowsElevation(command string, arguments []string) (string, error) {
	// Build the command with proper argument escaping
	var argBuilder strings.Builder
	for i, arg := range arguments {
		if i > 0 {
			argBuilder.WriteString(" ")
		}
		// Escape arguments that contain spaces or special characters
		if strings.Contains(arg, " ") || strings.Contains(arg, "'") || strings.Contains(arg, "\"") {
			// Use PowerShell-style escaping
			escaped := strings.ReplaceAll(arg, "'", "''")
			argBuilder.WriteString("'")
			argBuilder.WriteString(escaped)
			argBuilder.WriteString("'")
		} else {
			argBuilder.WriteString(arg)
		}
	}

	// Create PowerShell command that inherits elevation properly
	psCommand := fmt.Sprintf("& '%s' %s", command, argBuilder.String())

	logging.Debug("runCMDWithWindowsElevation => PowerShell command",
		"psCommand", psCommand)

	cmd := exec.Command("powershell.exe", "-ExecutionPolicy", "Bypass", "-Command", psCommand)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	// Configure for proper Windows elevation inheritance
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow: true,
		// No special creation flags - let PowerShell handle elevation inheritance
	}

	err := cmd.Run()
	outStr := stdout.String()
	errStr := stderr.String()

	if err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			exitCode := exitErr.ExitCode()
			logging.Error("PowerShell command failed",
				"command", command, "args", arguments, "exitCode", exitCode,
				"stderr", errStr, "psCommand", psCommand)
			// Include stderr in the error message for better debugging
			if errStr != "" {
				return outStr, fmt.Errorf("command failed exit code=%d: %s", exitCode, strings.TrimSpace(errStr))
			}
			return outStr, fmt.Errorf("command failed exit code=%d", exitCode)
		}
		logging.Error("Failed to run PowerShell command",
			"command", command, "args", arguments, "error", err, "psCommand", psCommand)
		return outStr, err
	}

	logging.Debug("PowerShell command completed successfully",
		"command", command, "args", arguments, "output", outStr)
	return outStr, nil
}

// runCMDWithTimeout runs a command with a timeout to prevent hanging on interactive installers
func runCMDWithTimeout(command string, arguments []string, timeoutMinutes int) (string, error) {
	logging.Debug("runCMDWithTimeout => about to run with timeout",
		"command", command, "args", strings.Join(arguments, " "), "timeoutMinutes", timeoutMinutes)

	// On Windows, use PowerShell wrapper to ensure proper elevation inheritance
	if runtime.GOOS == "windows" {
		return runCMDWithTimeoutWindows(command, arguments, timeoutMinutes)
	}

	// Create context with timeout
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(timeoutMinutes)*time.Minute)
	defer cancel()

	cmd := exec.CommandContext(ctx, command, arguments...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	err := cmd.Run()
	outStr := stdout.String()
	errStr := stderr.String()

	if err != nil {
		// Check if it was a timeout
		if ctx.Err() == context.DeadlineExceeded {
			logging.Error("Command timed out after timeout period",
				"command", command, "args", arguments, "timeoutMinutes", timeoutMinutes)
			return outStr, fmt.Errorf("installer timed out after %d minutes - likely waiting for user interaction", timeoutMinutes)
		}

		if exitErr, ok := err.(*exec.ExitError); ok {
			exitCode := exitErr.ExitCode()
			logging.Error("Command failed",
				"command", command, "args", arguments, "exitCode", exitCode, "stderr", errStr)
			// Include stderr in the error message for better debugging
			if errStr != "" {
				return outStr, fmt.Errorf("command failed exit code=%d: %s", exitCode, strings.TrimSpace(errStr))
			}
			return outStr, fmt.Errorf("command failed exit code=%d", exitCode)
		}
		logging.Error("Failed to run cmd",
			"command", command, "args", arguments, "error", err)
		return outStr, err
	}
	
	logging.Debug("Command completed successfully",
		"command", command, "args", arguments, "output", outStr, "timeoutMinutes", timeoutMinutes)
	return outStr, nil
}

// runCMDWithTimeoutWindows runs commands through PowerShell with timeout on Windows
func runCMDWithTimeoutWindows(command string, arguments []string, timeoutMinutes int) (string, error) {
	// Build the command with proper argument escaping
	var argBuilder strings.Builder
	for i, arg := range arguments {
		if i > 0 {
			argBuilder.WriteString(" ")
		}
		// Escape arguments that contain spaces or special characters
		if strings.Contains(arg, " ") || strings.Contains(arg, "'") || strings.Contains(arg, "\"") {
			// Use PowerShell-style escaping
			escaped := strings.ReplaceAll(arg, "'", "''")
			argBuilder.WriteString("'")
			argBuilder.WriteString(escaped)
			argBuilder.WriteString("'")
		} else {
			argBuilder.WriteString(arg)
		}
	}

	// Create PowerShell command that inherits elevation properly
	psCommand := fmt.Sprintf("& '%s' %s", command, argBuilder.String())

	logging.Debug("runCMDWithTimeoutWindows => PowerShell command",
		"psCommand", psCommand, "timeoutMinutes", timeoutMinutes)

	// Create context with timeout
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(timeoutMinutes)*time.Minute)
	defer cancel()

	cmd := exec.CommandContext(ctx, "powershell.exe", "-ExecutionPolicy", "Bypass", "-Command", psCommand)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	// Set up process attributes to hide console window and prevent GUI inheritance
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: syscall.CREATE_NEW_PROCESS_GROUP, // Prevent GUI inheritance
	}

	err := cmd.Run()
	outStr := stdout.String()
	errStr := stderr.String()

	if err != nil {
		// Check if it was a timeout
		if ctx.Err() == context.DeadlineExceeded {
			logging.Error("PowerShell command timed out after timeout period",
				"command", command, "args", arguments, "timeoutMinutes", timeoutMinutes, "psCommand", psCommand)
			return outStr, fmt.Errorf("installer timed out after %d minutes - likely waiting for user interaction", timeoutMinutes)
		}

		if exitErr, ok := err.(*exec.ExitError); ok {
			exitCode := exitErr.ExitCode()
			logging.Error("PowerShell command failed",
				"command", command, "args", arguments, "exitCode", exitCode,
				"stderr", errStr, "psCommand", psCommand)
			// Include stderr in the error message for better debugging
			if errStr != "" {
				return outStr, fmt.Errorf("command failed exit code=%d: %s", exitCode, strings.TrimSpace(errStr))
			}
			return outStr, fmt.Errorf("command failed exit code=%d", exitCode)
		}
		logging.Error("Failed to run PowerShell command with timeout",
			"command", command, "args", arguments, "error", err, "psCommand", psCommand)
		return outStr, err
	}

	logging.Debug("PowerShell command with timeout completed successfully",
		"command", command, "args", arguments, "output", outStr, "timeoutMinutes", timeoutMinutes)
	return outStr, nil
}

// extractNupkgMetadata parses a .nuspec to find <id> and <version>.
func extractNupkgMetadata(nupkgPath string) (string, string, error) {
	r, err := zip.OpenReader(nupkgPath)
	if err != nil {
		return "", "", fmt.Errorf("failed to open nupkg: %w", err)
	}
	defer r.Close()
	for _, f := range r.File {
		if strings.HasSuffix(strings.ToLower(f.Name), ".nuspec") {
			rc, err := f.Open()
			if err != nil {
				return "", "", fmt.Errorf("failed to open nuspec: %w", err)
			}
			defer rc.Close()
			var meta struct {
				Metadata struct {
					ID      string `xml:"id"`
					Version string `xml:"version"`
				} `xml:"metadata"`
			}
			if decodeErr := xml.NewDecoder(rc).Decode(&meta); decodeErr != nil {
				return "", "", fmt.Errorf("failed to parse nuspec: %w", decodeErr)
			}
			return meta.Metadata.ID, meta.Metadata.Version, nil
		}
	}
	return "", "", fmt.Errorf("nuspec not found in nupkg")
}

// runPowerShellInline is used by needsUpdateOld.
func runPowerShellInline(script string) (int, error) {
	psExe := "powershell.exe"
	cmdArgs := buildStandardPowerShellArgs("-NonInteractive", "-Command", script)
	cmd := exec.Command(psExe, cmdArgs...)
	err := cmd.Run()
	if err == nil {
		return 0, nil
	}
	if exitErr, ok := err.(*exec.ExitError); ok {
		return exitErr.ExitCode(), nil
	}
	return -1, err
}

// hideConsoleWindow keeps the command window from popping up.
func hideConsoleWindow(cmd *exec.Cmd) {
	if runtime.GOOS == "windows" && cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	}
}

// extractAndRunChocolateyBeforeInstall inspects a .nupkg file for chocolateyBeforeInstall.ps1
// and runs it if found. This ensures preinstall scripts are always executed before installation,
// providing consistent behavior regardless of Chocolatey's internal decision tree logic.
//
// Unlike Chocolatey's standard behavior which has complex conditions for when to run
// chocolateyBeforeInstall.ps1, this function treats it as a mandatory preinstall script
// that should always be executed if present in the package.
func extractAndRunChocolateyBeforeInstall(nupkgPath string, item catalog.Item) error {
	// Open the .nupkg file as a zip archive
	r, err := zip.OpenReader(nupkgPath)
	if err != nil {
		logging.Debug("Failed to open .nupkg as zip", "file", nupkgPath, "error", err)
		return nil // Not fatal - continue with installation
	}
	defer r.Close()

	// Look for chocolateyBeforeInstall.ps1 in the tools/ directory
	var beforeInstallFile *zip.File
	for _, f := range r.File {
		if strings.EqualFold(f.Name, "tools/chocolateyBeforeInstall.ps1") {
			beforeInstallFile = f
			break
		}
	}

	if beforeInstallFile == nil {
		logging.Debug("No chocolateyBeforeInstall.ps1 found in .nupkg", "file", nupkgPath, "item", item.Name)
		return nil // No script to run
	}

	logging.Info("Found chocolateyBeforeInstall.ps1 in .nupkg, extracting and running", "item", item.Name)

	// Extract the script content
	rc, err := beforeInstallFile.Open()
	if err != nil {
		logging.Error("Failed to open chocolateyBeforeInstall.ps1 from .nupkg", "error", err)
		return nil // Not fatal - continue with installation
	}
	defer rc.Close()
	scriptContent, err := io.ReadAll(rc)
	if err != nil {
		logging.Error("Failed to read chocolateyBeforeInstall.ps1 content", "error", err)
		return nil // Not fatal - continue with installation
	}

	// Skip empty scripts
	if len(strings.TrimSpace(string(scriptContent))) == 0 {
		logging.Debug("chocolateyBeforeInstall.ps1 is empty, skipping execution", "item", item.Name)
		return nil
	}

	// Create a temporary script file
	tempDir := os.TempDir()
	tempScriptPath := filepath.Join(tempDir, fmt.Sprintf("chocolateyBeforeInstall_%s.ps1", item.Name))

	err = os.WriteFile(tempScriptPath, scriptContent, 0644)
	if err != nil {
		logging.Error("Failed to write temporary chocolateyBeforeInstall.ps1 script", "error", err)
		return nil // Not fatal - continue with installation
	}
	defer os.Remove(tempScriptPath) // Clean up

	// Run the PowerShell script
	logging.Info("Executing chocolateyBeforeInstall.ps1 script", "item", item.Name, "script", tempScriptPath)

	cmdArgs := buildStandardPowerShellArgs("-NonInteractive", "-File", tempScriptPath)

	out, err := runCMD(commandPs1, cmdArgs)
	if err != nil {
		logging.Error("chocolateyBeforeInstall.ps1 script execution failed",
			"item", item.Name, "error", err, "output", out)
		// Continue with installation even if preinstall script fails
		// This matches Chocolatey's behavior for failed before scripts
		return nil
	}

	logging.Info("chocolateyBeforeInstall.ps1 script completed successfully",
		"item", item.Name, "output", strings.TrimSpace(out))
	return nil
}

// immediateCleanupAfterInstall verifies installation using the installs array
// and immediately removes the cached installer file if verification passes.
// This provides instant cleanup for items with trackable installation verification.
func immediateCleanupAfterInstall(item catalog.Item, localFile string, cfg *config.Configuration) {
	// Only proceed if the item has an installs array for verification
	if len(item.Installs) == 0 {
		logging.Debug("No installs array found, skipping immediate cleanup", "item", item.Name)
		return
	}

	// IMPORTANT: Get the current catalog definition instead of using the potentially stale item
	// This prevents verification failures when the catalog has been updated since the item was cached
	if cfg == nil {
		logging.Warn("No configuration provided for catalog refresh, using original item", "item", item.Name)
	} else {
		catalogMap := catalog.AuthenticatedGetEnhanced(*cfg)
		if currentItem, found := catalog.GetItemByName(item.Name, catalogMap); found {
			logging.Debug("Using refreshed catalog item for verification",
				"item", item.Name, "originalVersion", item.Version, "currentVersion", currentItem.Version)
			item = currentItem
		} else {
			logging.Debug("Item not found in current catalog, using original item", "item", item.Name)
		}
	}

	// Verify the installation was successful using the current installs array
	needsAction, err := status.CheckStatus(item, "install", "")
	if err != nil {
		logging.Warn("Error verifying installation for immediate cleanup",
			"item", item.Name, "error", err)
		return
	}

	// If needsAction is false, it means the installation is properly detected
	if !needsAction {
		logging.Info("Installation verified via installs array, performing cache cleanup",
			"item", item.Name, "cachedFile", localFile)

		// Remove the cached installer file with retry logic for Windows file locking
		if localFile != "" && localFile != "." {
			retryConfig := retry.RetryConfig{
				MaxRetries:      3,
				InitialInterval: time.Second,
				Multiplier:      2.0,
			}
			err := retry.Retry(retryConfig, func() error {
				return os.Remove(localFile)
			})
			if err != nil {
				logging.Debug("Failed to remove cached installer file after retries, will be cleaned up at end of run",
					"item", item.Name, "file", localFile, "error", err)
			} else {
				logging.Info("Successfully removed cached installer file after installation verification",
					"item", item.Name, "file", localFile)
			}
		}
	} else {
		logging.Warn("Installation verification failed, retaining cached file for troubleshooting",
			"item", item.Name, "cachedFile", localFile)
	}
}

// runNopkgScript executes a PowerShell script for nopkg (script-only) items.
func runNopkgScript(script utils.LiteralString, cachePath, scriptType string) (string, error) {
	if string(script) == "" {
		return "", nil
	}

	// Create a temporary PowerShell script file
	tempDir := filepath.Join(cachePath, "temp")
	if err := os.MkdirAll(tempDir, 0755); err != nil {
		return "", fmt.Errorf("failed to create temp directory: %w", err)
	}

	tempFile := filepath.Join(tempDir, fmt.Sprintf("%s_script_%d.ps1", scriptType, time.Now().Unix()))
	if err := os.WriteFile(tempFile, []byte(string(script)), 0644); err != nil {
		return "", fmt.Errorf("failed to write temp script file: %w", err)
	}

	// Clean up temp file when done
	defer func() {
		if err := os.Remove(tempFile); err != nil {
			logging.Warn("Failed to clean up temp script file", "file", tempFile, "error", err)
		}
	}()

	// Execute the PowerShell script
	cmdArgs := buildStandardPowerShellArgs("-NonInteractive", "-File", tempFile)

	output, err := runCMD("powershell.exe", cmdArgs)
	if err != nil {
		return output, fmt.Errorf("%s script execution failed: %w", scriptType, err)
	}

	return output, nil
}

// cleanupInstallerFile safely removes installer files with retry logic for Windows file locking
func cleanupInstallerFile(filePath, itemName string) error {
	if filePath == "" || filePath == "." {
		return nil
	}
	
	// Check if file exists before attempting cleanup
	if _, err := os.Stat(filePath); os.IsNotExist(err) {
		logging.Debug("Installer file already cleaned up", "item", itemName, "file", filePath)
		return nil
	}
	
	retryConfig := retry.RetryConfig{
		MaxRetries:      5,
		InitialInterval: 500 * time.Millisecond,
		Multiplier:      2.0,
	}
	
	return retry.Retry(retryConfig, func() error {
		err := os.Remove(filePath)
		if err != nil {
			if os.IsNotExist(err) {
				// File was already removed, consider this success
				return nil
			}
			logging.Debug("Retrying file cleanup", "item", itemName, "file", filePath, "error", err)
			return err
		}
		logging.Debug("Successfully cleaned up installer file", "item", itemName, "file", filePath)
		return nil
	})
}
