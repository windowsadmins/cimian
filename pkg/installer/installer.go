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

	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/download"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/manifest"
	"github.com/windowsadmins/gorilla/pkg/status"
)

// We rely on these from the environment or Windows folder:
var (
	commandNupkg = filepath.Join(os.Getenv("ProgramData"), "chocolatey", "bin", "choco.exe")
	commandMsi   = filepath.Join(os.Getenv("WINDIR"), "system32", "msiexec.exe")
	commandPs1   = filepath.Join(os.Getenv("WINDIR"), "system32", "WindowsPowershell", "v1.0", "powershell.exe")
)

// storeInstalledVersionInRegistry saves the “Version” into HKLM\Software\ManagedInstalls\<Name>
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

// removeInstalledVersionFromRegistry removes HKLM\Software\ManagedInstalls\<Name> entirely
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
	logging.Debug("Removed registry key after uninstall", "item", item.Name, "key", regPath)
}

// Install is the main entry point for installing/updating/uninstalling a catalog item.
// This is invoked by "managedsoftware" to do the actual action.
func Install(item catalog.Item, action, localFile, cachePath string, checkOnly bool, cfg *config.Configuration) (string, error) {
	if checkOnly {
		logging.Info("CheckOnly mode: would perform action", "action", action, "item", item.Name)
		return "CheckOnly: No action performed.", nil
	}

	// 1) Check for a pending reboot before any actual install/update/uninstall
	//    so that user can choose to reboot or skip.
	if isRebootPending() {
		logging.Warn("A system reboot is pending before continuing", "item", item.Name)
		userChoseReboot := offerRebootPrompt()
		if userChoseReboot {
			rebootSystem()
			return "System is rebooting now.", fmt.Errorf("reboot in progress")
		} else {
			logging.Warn("User declined reboot; continuing with the selected action anyway.")
		}
	}

	// 2) Handle the specified action
	switch strings.ToLower(action) {
	case "install", "update":
		// We do an architecture check here using status.* so we don't duplicate logic
		sysArch := status.GetSystemArchitecture()
		if !status.SupportsArchitecture(item, sysArch) {
			return "", fmt.Errorf("system arch %s not in supported_arch=%v for item %s",
				sysArch, item.SupportedArch, item.Name)
		}

		if item.Installer.Type == "nupkg" {
			// For .nupkg => special function
			return installOrUpgradeNupkg(item, localFile, cfg)
		}

		// For MSI, EXE, PS1 => normal path
		err := installItem(item, localFile, cachePath)
		if err != nil {
			logging.Error("Installation failed", "item", item.Name, "error", err)
			return "", err
		}
		logging.Info("Installed item successfully", "item", item.Name)
		storeInstalledVersionInRegistry(item)
		return "Installation success", nil

	case "uninstall":
		// If you also want a reboot check for uninstall, do it here:
		sysArch := status.GetSystemArchitecture()
		if !status.SupportsArchitecture(item, sysArch) {
			// Possibly skip or fail if arch mismatch
			logging.Warn("Skipping uninstall because system arch not matched",
				"item", item.Name, "arch", sysArch)
		}

		output, err := uninstallItem(item, cachePath)
		if err != nil {
			logging.Error("Uninstall failed", "item", item.Name, "error", err)
			return output, err
		}
		logging.Info("Uninstalled item successfully", "item", item.Name)
		removeInstalledVersionFromRegistry(item)
		return output, nil

	default:
		msg := fmt.Sprintf("Unsupported action: %s", action)
		logging.Warn(msg)
		return "", fmt.Errorf("%v", msg)
	}
}

// localNeedsUpdate is your existing "decide if an update is needed" check:
func LocalNeedsUpdate(m manifest.Item, catMap map[string]catalog.Item, cfg *config.Configuration) bool {
	// The logic here is unchanged from your snippet
	key := strings.ToLower(m.Name)
	catItem, found := catMap[key]
	if !found {
		logging.Debug("Item not found in local catalog; fallback to old approach", "item", m.Name)
		return needsUpdateOld(m, cfg)
	}
	needed, err := status.CheckStatus(catItem, "install", cfg.CachePath)
	if err != nil {
		return true
	}
	return needed
}

// needsUpdateOld is your legacy fallback approach (unchanged).
func needsUpdateOld(item manifest.Item, cfg *config.Configuration) bool {
	if item.InstallCheckScript != "" {
		exitCode, err := runPowerShellInline(item.InstallCheckScript)
		if err != nil {
			logging.Warn("InstallCheckScript failed => default to install", "item", item.Name, "error", err)
			return true
		}
		if exitCode == 0 {
			logging.Debug("installcheck => 0 => not installed => update needed", "item", item.Name)
			return true
		}
		logging.Debug("installcheck => !=0 => installed => no update", "item", item.Name, "exitCode", exitCode)
		return false
	}
	if len(item.Installs) > 0 {
		for _, detail := range item.Installs {
			if fileNeedsUpdate(detail) {
				return true
			}
		}
		return false
	}
	citem := status.ToCatalogItem(item)
	needed, err := status.CheckStatus(citem, "install", cfg.CachePath)
	if err != nil {
		return true
	}
	return needed
}

// fileNeedsUpdate is your original "check file presence/hashes/versions".
func fileNeedsUpdate(d manifest.InstallDetail) bool {
	fi, err := os.Stat(d.Path)
	if err != nil {
		if os.IsNotExist(err) {
			logging.Debug("File missing => update needed", "file", d.Path)
			return true
		}
		logging.Warn("Stat error => update anyway", "file", d.Path, "error", err)
		return true
	}
	if fi.IsDir() {
		logging.Warn("Path is directory => need update", "file", d.Path)
		return true
	}
	if d.MD5Checksum != "" {
		match := download.Verify(d.Path, d.MD5Checksum)
		if !match {
			logging.Debug("MD5 checksum mismatch => update needed", "file", d.Path)
			return true
		}
	}
	if d.Version != "" {
		// We'll do a normal status-based version check
		myItem := catalog.Item{Name: filepath.Base(d.Path)}
		localVersion, err := status.GetInstalledVersion(myItem)
		if err != nil {
			logging.Warn("Failed to get installed version => update needed", "file", d.Path, "error", err)
			return true
		}
		if status.IsOlderVersion(localVersion, d.Version) {
			logging.Debug("Installed version is older => update needed",
				"file", d.Path, "local_version", localVersion, "required_version", d.Version)
			return true
		}
	}
	return false
}

// runPowerShellInline is your existing helper for .ps1 scripts
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

// installItem installs items that are NOT .nupkg (MSI, EXE, powershell).
func installItem(item catalog.Item, localFile, cachePath string) error {
	switch strings.ToLower(item.Installer.Type) {
	case "msi":
		output, err := runMSIInstaller(item, localFile)
		if err != nil {
			return err
		}
		logging.Debug("MSI install output", "output", output)
		return nil

	case "exe":
		if item.PreScript != "" {
			output, err := runPreinstallScript(item, localFile, cachePath)
			if err != nil {
				return err
			}
			logging.Debug("Preinstall script for EXE completed", "output", output)
			return nil
		} else {
			// Normal silent EXE
			output, err := runEXEInstaller(item, localFile)
			if err != nil {
				return err
			}
			logging.Debug("EXE install output", "output", output)
			return nil
		}

	case "powershell":
		output, err := runPS1Installer(item, localFile)
		if err != nil {
			return err
		}
		logging.Debug("PS1 install output", "output", output)
		return nil

	default:
		// If it's actually "nupkg," we handle that in installOrUpgradeNupkg
		return fmt.Errorf("unknown installer type: %s", item.Installer.Type)
	}
}

// We define a single function that forcibly uses --force --allowdowngrade
// to ensure Gorilla's decision is final.

func installOrUpgradeNupkg(item catalog.Item, localFile string, cfg *config.Configuration) (string, error) {
	// Mark cfg as “used” so it doesn't trigger unusedparams:
	_ = cfg

	// 1) Extract .nuspec metadata for pkgID & version
	nupkgID, nupkgVer, metaErr := extractNupkgMetadata(localFile)
	if metaErr != nil {
		logging.Warn("Failed to parse .nuspec metadata; falling back to item.Name",
			"file", localFile, "error", metaErr)
		nupkgID = strings.TrimSpace(item.Identifier)
		if nupkgID == "" {
			nupkgID = strings.TrimSpace(item.Name)
		}
		if nupkgID == "" {
			nupkgID = "unknown-nupkgID"
		}
		nupkgVer = "0.0.0"
	}
	logging.Debug("Parsed .nuspec metadata", "nupkgID", nupkgID, "nupkgVer", nupkgVer, "localFile", localFile)

	// 2) Check if installed
	installed, checkErr := isNupkgInstalled(nupkgID)
	if checkErr != nil {
		logging.Warn("Could not detect if nupkg is installed; forcing install",
			"pkgID", nupkgID, "error", checkErr)
		return doChocoInstall(item, nupkgID, nupkgVer, localFile)
	}

	if !installed {
		// not installed => choco install
		logging.Info("Nupkg not installed; proceeding with forced install", "pkgID", nupkgID)
		return doChocoInstall(item, nupkgID, nupkgVer, localFile)
	}

	// installed => choco upgrade
	logging.Info("Nupkg is installed; forcing upgrade/downgrade", "pkgID", nupkgID)
	return doChocoUpgrade(item, nupkgID, nupkgVer, localFile)
}

func isNupkgInstalled(pkgID string) (bool, error) {
	cmdArgs := []string{"list", "--local-only", "--limit-output", "--exact", pkgID}
	out, err := runCMD(commandNupkg, cmdArgs)
	if err != nil {
		return false, err
	}
	lines := strings.Split(strings.TrimSpace(out), "\n")
	for _, line := range lines {
		line = strings.ToLower(strings.TrimSpace(line))
		if strings.HasPrefix(line, strings.ToLower(pkgID)+"|") {
			return true, nil
		}
	}
	return false, nil
}

func doChocoInstall(item catalog.Item, pkgID, pkgVersion, localFile string) (string, error) {
	logging.Info("Running choco install with --force", "pkgID", pkgID, "file", localFile)
	cmdArgs := []string{
		"install", localFile,
		"-y",
		"--force",
		"--allowdowngrade",
		"--debug",
		"--log-file=C:\\ProgramData\\ManagedInstalls\\Logs\\install.log",
	}
	out, err := runCMD(commandNupkg, cmdArgs)
	if err != nil {
		logging.Error("Choco install failed", "pkgID", pkgID, "error", err)
		return out, err
	}
	storeInstalledVersionInRegistry(catalog.Item{
		Name:       item.Name,
		Identifier: pkgID,
		Version:    pkgVersion,
	})
	logging.Info("Choco install succeeded", "pkgID", pkgID)
	return out, nil
}

func doChocoUpgrade(item catalog.Item, pkgID, pkgVersion, localFile string) (string, error) {
	logging.Info("Running choco upgrade with --force", "pkgID", pkgID, "file", localFile)
	cmdArgs := []string{
		"upgrade", localFile,
		"-y",
		"--force",
		"--allowdowngrade",
		"--debug",
		"--log-file=C:\\ProgramData\\ManagedInstalls\\Logs\\install.log",
	}
	out, err := runCMD(commandNupkg, cmdArgs)
	if err != nil {
		logging.Error("Choco upgrade failed", "pkgID", pkgID, "error", err)
		return out, err
	}
	storeInstalledVersionInRegistry(catalog.Item{
		Name:       item.Name,
		Identifier: pkgID,
		Version:    pkgVersion,
	})
	logging.Info("Choco upgrade succeeded", "pkgID", pkgID)
	return out, nil
}

// runMSIInstaller
func runMSIInstaller(item catalog.Item, localFile string) (string, error) {
	cmdArgs := []string{
		"/i", localFile,
		"/quiet",
		"/norestart",
		"/l*v", `C:\ProgramData\ManagedInstalls\Logs\install.log`,
	}
	output, err := runCMD(commandMsi, cmdArgs)
	if err != nil {
		return output, err
	}
	logging.Info("Successfully installed MSI package", "package", item.Name)
	return output, nil
}

// runPreinstallScript detects .bat vs PowerShell style
func runPreinstallScript(item catalog.Item, localFile, cachePath string) (string, error) {
	scriptLower := strings.ToLower(item.PreScript)
	if strings.Contains(scriptLower, "@echo off") || strings.HasPrefix(scriptLower, "rem ") ||
		strings.HasPrefix(scriptLower, "::") {
		// Looks like a BAT script
		return runBatInstaller(item, localFile, cachePath)
	}
	// else assume PS
	return runPS1InstallerFromScript(item, localFile, cachePath)
}

// runBatInstaller
func runBatInstaller(item catalog.Item, localFile, cachePath string) (string, error) {
	_ = localFile // ignore
	batPath := filepath.Join(cachePath, "tmp_preinstall.bat")
	err := os.WriteFile(batPath, []byte(item.PreScript), 0o755)
	if err != nil {
		return "", fmt.Errorf("failed to write .bat preinstall script: %v", err)
	}
	defer os.Remove(batPath)

	cmd := exec.Command("cmd.exe", "/c", batPath)
	hideConsoleWindow(cmd)

	var out, stderr bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &stderr

	runErr := cmd.Run()
	output := out.String()
	if runErr != nil {
		return output, fmt.Errorf("command execution failed: %v - %s", runErr, stderr.String())
	}
	return output, nil
}

// runPS1InstallerFromScript
func runPS1InstallerFromScript(item catalog.Item, localFile, cachePath string) (string, error) {
	_ = localFile
	psFile := filepath.Join(cachePath, "preinstall_tmp.ps1")
	err := os.WriteFile(psFile, []byte(item.PreScript), 0o755)
	if err != nil {
		return "", fmt.Errorf("failed to write preinstall ps1: %v", err)
	}
	defer os.Remove(psFile)

	cmd := exec.Command(commandPs1, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", psFile)
	hideConsoleWindow(cmd)

	var out, stderr bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &stderr

	runErr := cmd.Run()
	output := out.String()
	if runErr != nil {
		return output, fmt.Errorf("preinstall ps1 failed: %v | stderr: %s", runErr, stderr.String())
	}
	return output, nil
}

// runEXEInstaller for direct silent .exe
func runEXEInstaller(item catalog.Item, localFile string) (string, error) {
	baseSilentArgs := []string{"/S"}
	cmdArgs := append(baseSilentArgs, item.Installer.Arguments...)
	output, err := runCMD(localFile, cmdArgs)
	if err != nil {
		logging.Error("Failed to install EXE package", "package", item.Name, "error", err)
		return output, err
	}
	logging.Info("Successfully installed EXE package", "package", item.Name)
	return output, nil
}

// runPS1Installer (for a main PS1 installer)
func runPS1Installer(item catalog.Item, localFile string) (string, error) {
	cmdArgs := []string{"-NoProfile", "-ExecutionPolicy", "Bypass", "-File", localFile}
	output, err := runCMD(commandPs1, cmdArgs)
	if err != nil {
		logging.Error("Failed to execute PowerShell script", "script", item.Name, "error", err)
		return "", err
	}
	logging.Info("Successfully executed PowerShell script", "script", item.Name)
	return output, nil
}

// -------------------- UNINSTALL LOGIC --------------------

func uninstallItem(item catalog.Item, cachePath string) (string, error) {
	// Typically we look for the same localFile in cache
	relPath, fileName := path.Split(item.Installer.Location)
	absFile := filepath.Join(cachePath, relPath, fileName)

	// If not present, just skip
	if _, err := os.Stat(absFile); os.IsNotExist(err) {
		msg := fmt.Sprintf("Uninstall file does not exist: %s", absFile)
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
	default:
		msg := fmt.Sprintf("Unsupported installer type for uninstall: %s", item.Installer.Type)
		logging.Warn(msg)
		return "", nil
	}
}

func runMSIUninstaller(absFile string, item catalog.Item) (string, error) {
	uninstallArgs := append([]string{"/x", absFile, "/qn", "/norestart"}, item.Uninstaller.Arguments...)
	output, err := runCMD(commandMsi, uninstallArgs)
	if err != nil {
		logging.Warn("MSI uninstallation failed", "file", absFile, "error", err)
		return output, err
	}
	return output, nil
}

func runEXEUninstaller(absFile string, item catalog.Item) (string, error) {
	output, err := runCMD(absFile, item.Uninstaller.Arguments)
	if err != nil {
		logging.Warn("EXE uninstallation failed", "file", absFile, "error", err)
		return output, err
	}
	return output, nil
}

func runPS1Uninstaller(absFile string) (string, error) {
	psArgs := []string{"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", absFile}
	output, err := runCMD(commandPs1, psArgs)
	if err != nil {
		logging.Warn("PowerShell script uninstallation failed", "file", absFile, "error", err)
		return output, err
	}
	return output, nil
}

func runNupkgUninstaller(absFile string) (string, error) {
	pkgID, _, err := extractNupkgMetadata(absFile)
	if err != nil {
		msg := fmt.Sprintf("Failed to read nupkg metadata for uninstall: %v", err)
		logging.Warn(msg)
		return "", err
	}
	nupkgDir := filepath.Dir(absFile)
	uninstallArgs := []string{"uninstall", pkgID, "-s", nupkgDir, "-y", "--force"}
	output, cmdErr := runCMD(commandNupkg, uninstallArgs)
	if cmdErr != nil {
		logging.Warn("Nupkg uninstallation failed", "file", absFile, "error", cmdErr)
		return output, cmdErr
	}
	return output, nil
}

// -------------------- SHARED UTILITY FUNCS --------------------

// runCMD ensures a non-zero exit code => error
func runCMD(command string, arguments []string) (string, error) {
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
				"command", command,
				"args", arguments,
				"exitCode", exitCode,
				"stderr", errStr,
			)
			return outStr, fmt.Errorf("command failed with exit code=%d", exitCode)
		}
		logging.Error("Failed to run cmd", "command", command, "args", arguments, "error", err)
		return outStr, err
	}
	// success => exitCode=0
	return outStr, nil
}

// extractNupkgMetadata tries to open .nupkg => read .nuspec => return (id, version).
func extractNupkgMetadata(nupkgPath string) (string, string, error) {
	r, err := zip.OpenReader(nupkgPath)
	if err != nil {
		return "", "", fmt.Errorf("failed to open nupkg: %v", err)
	}
	defer r.Close()

	for _, f := range r.File {
		if strings.HasSuffix(strings.ToLower(f.Name), ".nuspec") {
			rc, err := f.Open()
			if err != nil {
				return "", "", fmt.Errorf("failed to open nuspec: %v", err)
			}
			defer rc.Close()

			var meta struct {
				Metadata struct {
					ID      string `xml:"id"`
					Version string `xml:"version"`
				} `xml:"metadata"`
			}
			if err := xml.NewDecoder(rc).Decode(&meta); err != nil {
				return "", "", fmt.Errorf("failed to parse nuspec: %v", err)
			}
			return meta.Metadata.ID, meta.Metadata.Version, nil
		}
	}
	return "", "", fmt.Errorf("nuspec file not found in nupkg")
}

// hideConsoleWindow is used for .bat or .ps1 so it doesn't pop a cmd window.
func hideConsoleWindow(cmd *exec.Cmd) {
	if runtime.GOOS == "windows" {
		if cmd.SysProcAttr == nil {
			cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
		} else {
			cmd.SysProcAttr.HideWindow = true
		}
	}
}

// isRebootPending does a simple check for known "pending reboot" keys (example).
func isRebootPending() bool {
	// This is a simplified approach; adapt as needed.
	if registryKeyExists(`SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired`) {
		return true
	}
	if registryValueExists(`SYSTEM\CurrentControlSet\Control\Session Manager`, `PendingFileRenameOperations`) {
		return true
	}
	return false
}

// offerRebootPrompt is a placeholder that prompts user to choose Y/N for an immediate reboot.
func offerRebootPrompt() bool {
	fmt.Print("A system reboot is pending. Reboot now? (y/n): ")
	var answer string
	_, _ = fmt.Scanln(&answer)
	answer = strings.ToLower(strings.TrimSpace(answer))
	return (answer == "y" || answer == "yes")
}

// rebootSystem is a placeholder for forcibly rebooting. You can also schedule it, etc.
func rebootSystem() {
	logging.Warn("Rebooting system now (placeholder).")
	// Example:
	// exec.Command("shutdown", "/r", "/t", "0").Run()
}

func registryKeyExists(path string) bool {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, path, registry.READ)
	if err == nil {
		_ = k.Close()
		return true
	}
	return false
}

func registryValueExists(path, valueName string) bool {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, path, registry.READ)
	if err != nil {
		return false
	}
	defer k.Close()
	_, valType, err := k.GetValue(valueName, nil)
	return (err == nil && valType != registry.NONE)
}
