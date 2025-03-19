// pkg/installer/installer.go contains the main logic for installing, updating, and uninstalling software items.

package installer

import (
	"archive/zip"
	"bytes"
	"encoding/xml"
	"fmt"
	"os"
	"os/exec"
	"path"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"

	"golang.org/x/sys/windows/registry"

	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/manifest"
	"github.com/windowsadmins/cimian/pkg/status"
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

		// On success, store the installed version
		storeInstalledVersionInRegistry(item)
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

	default:
		return fmt.Errorf("unknown installer type: %s", item.Installer.Type)
	}
}

// installOrUpgradeNupkg handles local .nupkg installs/updates using Chocolatey.
func installOrUpgradeNupkg(item catalog.Item, downloadedFile, cachePath string, cfg *config.Configuration) (string, error) {
	nupkgID, nupkgVer, metaErr := extractNupkgMetadata(downloadedFile)
	_ = cfg
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

	if err := renameNupkgFile(downloadedFile, cachePath, nupkgID, nupkgVer); err != nil {
		logging.Error("Failed to rename .nupkg for choco", "err", err)
		return "", err
	}

	installed, checkErr := isNupkgInstalled(nupkgID)
	if checkErr != nil {
		logging.Warn("Could not detect if nupkg is installed; forcing install",
			"pkgID", nupkgID, "err", checkErr)
		return doChocoInstall(nupkgID, nupkgVer, cachePath, item)
	}

	if !installed {
		logging.Info("Nupkg not installed; forcing install", "pkgID", nupkgID)
		return doChocoInstall(nupkgID, nupkgVer, cachePath, item)
	}

	logging.Info("Nupkg is installed; forcing upgrade/downgrade", "pkgID", nupkgID)
	return doChocoUpgrade(nupkgID, nupkgVer, cachePath, item)
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

func doChocoInstall(pkgID, pkgVer, cachePath string, item catalog.Item) (string, error) {
	chocoLog := filepath.Join(cachePath, fmt.Sprintf("install_choco_%s.log", pkgID))
	logging.Info("Running choco install", "pkgID", pkgID, "version", pkgVer)
	cmdArgs := []string{
		"install", pkgID,
		"--version", pkgVer,
		"--source", cachePath,
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
	logging.Info("Choco install succeeded", "pkgID", pkgID)
	return out, nil
}

func doChocoUpgrade(pkgID, pkgVer, cachePath string, item catalog.Item) (string, error) {
	chocoLog := filepath.Join(cachePath, fmt.Sprintf("upgrade_choco_%s.log", pkgID))
	logging.Info("Running choco upgrade", "pkgID", pkgID, "version", pkgVer)
	cmdArgs := []string{
		"upgrade", pkgID,
		"--version", pkgVer,
		"--source", cachePath,
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
	logging.Info("Choco upgrade succeeded", "pkgID", pkgID)
	return out, nil
}

// uninstallItem decides how to uninstall MSI/EXE/PS1/nupkg/msix.
func uninstallItem(item catalog.Item, cachePath string) (string, error) {
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
	args := []string{
		"/i", localFile,
		"/quiet",
		"/norestart",
		"/l*v", `C:\ProgramData\ManagedInstalls\Logs\install.log`,
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

	// Add installer flags (-- style arguments)
	for _, flag := range item.Installer.Flags {
		if strings.Contains(flag, "=") {
			parts := strings.SplitN(flag, "=", 2)
			key, value := parts[0], parts[1]
			if strings.ContainsAny(value, " ") {
				value = fmt.Sprintf("\"%s\"", value)
			}
			args = append(args, fmt.Sprintf("--%s=%s", key, value))
		} else {
			args = append(args, fmt.Sprintf("--%s", flag))
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
	args = append(args, item.Uninstaller.Arguments...)

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
	args = append(args, item.Uninstaller.Arguments...)

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

// runEXEInstaller: supports mixing switches (/switch) and flags (--flag).
func runEXEInstaller(item catalog.Item, localFile string) (string, error) {
	installerPath := filepath.Join(localFile)
	args := []string{}

	// Add verb if present
	if item.Installer.Verb != "" {
		args = append(args, item.Installer.Verb)
	}

	// Handle switches (/ style)
	for _, sw := range item.Installer.Switches {
		if strings.Contains(sw, "=") {
			parts := strings.SplitN(sw, "=", 2)
			key, value := parts[0], parts[1]
			if strings.ContainsAny(value, " ") {
				value = "\"" + value + "\""
			}
			args = append(args, fmt.Sprintf("/%s=%s", key, value))
		} else {
			args = append(args, "/"+sw)
		}
	}

	// Handle flags (-- style)
	for _, flag := range item.Installer.Flags {
		if strings.Contains(flag, "=") {
			parts := strings.SplitN(flag, "=", 2)
			key, value := parts[0], parts[1]
			if strings.ContainsAny(value, " ") {
				value = "'" + value + "'"
			}
			args = append(args, fmt.Sprintf("--%s=%s", key, value))
		} else {
			args = append(args, "--"+flag)
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

func runEXEUninstaller(absFile string, item catalog.Item) (string, error) {
	args := item.Uninstaller.Arguments

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
	logPath := filepath.Join(cacheDir, fmt.Sprintf("uninstall_choco_%s.log", pkgID))
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
