// pkg/installer/installer.go contains the main logic for installing, updating, and uninstalling software items.

package installer

import (
	"archive/zip"
	"bytes"
	"encoding/xml"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"time"

	"golang.org/x/sys/windows/registry"

	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/manifest"
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
			return installOrUpgradeNupkg(item, localFile, cachePath, cfg)
		}

		// Otherwise, handle MSI/EXE/Powershell, etc.
		err := installNonNupkg(item, localFile, cachePath)
		if err != nil {
			logging.Error("Installation failed", "item", item.Name, "error", err)
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
		immediateCleanupAfterInstall(item, localFile)

		logging.Info("Installed item successfully", "item", item.Name)
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
func installNonNupkg(item catalog.Item, localFile, cachePath string) error {
	switch strings.ToLower(item.Installer.Type) {
	case "msi":
		out, err := runMSIInstaller(item, localFile)
		if err != nil {
			return err
		}
		logging.Debug("MSI install output", "output", out)
		return nil

	case "exe":
		// Run preinstall script if present
		if item.PreScript != "" {
			out, err := runPreinstallScript(item, localFile, cachePath)
			if err != nil {
				return err
			}
			logging.Debug("Preinstall script for EXE completed", "output", out)
		}
		// Always run the EXE afterwards
		out, err := runEXEInstaller(item, localFile)
		if err != nil {
			return err
		}
		logging.Debug("EXE install output", "output", out)
		return nil

	case "powershell":
		out, err := runPS1Installer(item, localFile)
		if err != nil {
			return err
		}
		logging.Debug("PS1 install output", "output", out)
		return nil

	case "msix":
		out, err := runMSIXInstaller(item, localFile)
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
		return doChocoInstall(downloadedFile, nupkgID, nupkgVer, cachePath, item)
	}

	if !installed {
		logging.Info("Nupkg not installed; installing", "pkgID", nupkgID)
		return doChocoInstall(downloadedFile, nupkgID, nupkgVer, cachePath, item)
	}

	logging.Info("Nupkg is installed; forcing upgrade/downgrade", "pkgID", nupkgID)
	return doChocoUpgrade(downloadedFile, nupkgID, nupkgVer, cachePath, item)
}

// doChocoInstall runs choco install with the given nupkg file.
func doChocoInstall(filePath, pkgID, pkgVer, cachePath string, item catalog.Item) (string, error) {
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
		return out, err
	}

	storeInstalledVersionInRegistry(catalog.Item{
		Name:    item.Name,
		Version: pkgVer,
	})

	// Attempt immediate cleanup if item has installs array for verification
	immediateCleanupAfterInstall(item, filePath)

	logging.Info("Choco install succeeded", "pkgID", pkgID)
	return out, nil
}

// doChocoUpgrade runs choco upgrade with the given nupkg file.
func doChocoUpgrade(filePath, pkgID, pkgVer, cachePath string, item catalog.Item) (string, error) {
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
		return out, err
	}

	storeInstalledVersionInRegistry(catalog.Item{
		Name:    item.Name,
		Version: pkgVer,
	})

	// Attempt immediate cleanup if item has installs array for verification
	immediateCleanupAfterInstall(item, filePath)

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

	// Build PowerShell arguments starting with basic execution policy
	psArgs := []string{"-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath}

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

func runMSIInstaller(item catalog.Item, localFile string) (string, error) {
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

	logging.Info("Invoking MSI install",
		"msi", localFile, "item", item.Name, "extraArgs", args)
	logging.Debug("runMSIInstaller => final command",
		"exe", commandMsi, "args", strings.Join(args, " "))

	cmd := exec.Command(commandMsi, args...)
	outputBytes, err := cmd.CombinedOutput()
	output := string(outputBytes)

	if err != nil {
		exitErr, ok := err.(*exec.ExitError)
		if !ok {
			logging.Error("Failed to run msiexec", "error", err, "stderr", output)
			return output, err
		}
		code := exitErr.ExitCode()
		logging.Error("MSI installation failed", "item", item.Name, "exitCode", code, "stderr", output)
		switch code {
		case 1603:
			return output, fmt.Errorf("msiexec exit code 1603 (fatal) - skipping re-try")
		case 1618:
			return output, fmt.Errorf("msiexec exit code 1618 (another install in progress)")
		case 3010:
			logging.Warn("MSI installed but requires reboot (3010)", "item", item.Name)
			return output, nil
		default:
			return output, fmt.Errorf("msiexec exit code %d", code)
		}
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

func runMSIXInstaller(item catalog.Item, localFile string) (string, error) {
	args := []string{localFile}
	logging.Info("Invoking MSIX install", "msix", localFile, "item", item.Name)
	logging.Debug("runMSIXInstaller => final command",
		"cmd", "Add-AppxPackage", "args", strings.Join(args, " "))

	cmd := exec.Command("Add-AppxPackage", args...)
	outputBytes, err := cmd.CombinedOutput()
	output := string(outputBytes)

	if err != nil {
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
func runEXEInstaller(item catalog.Item, localFile string) (string, error) {
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
			continue
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

	logging.Info("Executing EXE installer", "path", installerPath, "args", args)
	logging.Debug("runEXEInstaller => final command",
		"exe", installerPath, "args", strings.Join(args, " "))

	cmd := exec.Command(installerPath, args...)
	output, err := cmd.CombinedOutput()
	if err != nil {
		logging.Error("EXE installer execution failed", "error", err, "output", string(output))
		return string(output), err
	}
	logging.Info("EXE installer executed successfully", "output", string(output))
	return string(output), nil
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
func runPS1Installer(item catalog.Item, localFile string) (string, error) {
	_ = item
	psArgs := []string{"-NoProfile", "-ExecutionPolicy", "Bypass", "-File", localFile}

	logging.Debug("runPS1Installer => final command",
		"exe", commandPs1, "args", strings.Join(psArgs, " "))

	return runCMD(commandPs1, psArgs)
}

func runPS1Uninstaller(absFile string) (string, error) {
	psArgs := []string{"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", absFile}

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
	cmd := exec.Command(commandPs1, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", psFile)
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
func runCMD(command string, arguments []string) (string, error) {
	logging.Debug("runCMD => about to run",
		"command", command, "args", strings.Join(arguments, " "))

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
			return outStr, fmt.Errorf("command failed exit code=%d", exitCode)
		}
		logging.Error("Failed to run cmd",
			"command", command, "args", arguments, "error", err)
		return outStr, err
	}
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
	cmdArgs := []string{
		"-NoProfile",
		"-NonInteractive",
		"-ExecutionPolicy", "Bypass",
		"-Command", script,
	}
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

	cmdArgs := []string{
		"-ExecutionPolicy", "Bypass",
		"-NoProfile",
		"-NonInteractive",
		"-File", tempScriptPath,
	}

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
func immediateCleanupAfterInstall(item catalog.Item, localFile string) {
	// Only proceed if the item has an installs array for verification
	if len(item.Installs) == 0 {
		logging.Debug("No installs array found, skipping immediate cleanup", "item", item.Name)
		return
	}

	// Verify the installation was successful using the installs array
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

		// Remove the cached installer file
		if localFile != "" && localFile != "." {
			err := os.Remove(localFile)
			if err != nil {
				logging.Warn("Failed to remove cached installer file",
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
	cmdArgs := []string{
		"-ExecutionPolicy", "Bypass",
		"-NoProfile",
		"-NonInteractive",
		"-File", tempFile,
	}

	output, err := runCMD("powershell.exe", cmdArgs)
	if err != nil {
		return output, fmt.Errorf("%s script execution failed: %w", scriptType, err)
	}

	return output, nil
}
