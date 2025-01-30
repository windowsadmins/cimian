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

var (
	commandNupkg = filepath.Join(os.Getenv("ProgramData"), "chocolatey", "bin", "choco.exe")
	commandMsi   = filepath.Join(os.Getenv("WINDIR"), "system32", "msiexec.exe")
	commandPs1   = filepath.Join(os.Getenv("WINDIR"), "system32", "WindowsPowershell", "v1.0", "powershell.exe")
)

func storeInstalledVersionInRegistry(item catalog.Item) {
	regPath := `Software\ManagedInstalls\` + item.Name
	k, _, err := registry.CreateKey(registry.LOCAL_MACHINE, regPath, registry.SET_VALUE)
	if err != nil {
		logging.Warn("Failed to create registry key for installed version",
			"key", regPath, "error", err)
		return
	}
	defer k.Close()

	// Ensure the “Version” is never empty
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

func removeInstalledVersionFromRegistry(item catalog.Item) {
	regPath := `Software\ManagedInstalls\` + item.Name
	// We might delete the entire subkey for this item:
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

// Install is the main entry point for installing/updating/uninstalling a catalog item
func Install(item catalog.Item, action, localFile, cachePath string, checkOnly bool, cfg *config.Configuration) (string, error) {
	if checkOnly {
		logging.Info("CheckOnly mode: would perform action", "action", action, "item", item.Name)
		return "CheckOnly: No action performed.", nil
	}

	switch strings.ToLower(action) {
	case "install", "update":
		if item.Installer.Type == "nupkg" {
			return installOrUpgradeNupkg(item, localFile)
		}
		err := installItem(item, localFile, cachePath)
		if err != nil {
			logging.Error("Installation failed", "item", item.Name, "error", err)
			return "", err
		}
		logging.Info("Installed item successfully", "item", item.Name)
		storeInstalledVersionInRegistry(item)
		return "Installation success", nil

	case "uninstall":
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

// localNeedsUpdate determines if a manifest item needs an update based on the local catalog.
func LocalNeedsUpdate(m manifest.Item, catMap map[string]catalog.Item, cfg *config.Configuration) bool {
	key := strings.ToLower(m.Name)
	catItem, found := catMap[key]
	if !found {
		// Fallback to the old manifest-based logic if not found in the catalog
		logging.Debug("Item not found in local catalog; fallback to old approach", "item", m.Name)
		return needsUpdateOld(m, cfg)
	}
	// Use status.CheckStatus to determine if an install is needed
	needed, err := status.CheckStatus(catItem, "install", cfg.CachePath)
	if err != nil {
		return true
	}
	return needed
}

// needsUpdateOld retains the original logic for determining if an update is needed.
func needsUpdateOld(item manifest.Item, cfg *config.Configuration) bool {
	// If item has an InstallCheckScript, run it
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
	// If item has installs, check each file
	if len(item.Installs) > 0 {
		for _, detail := range item.Installs {
			if fileNeedsUpdate(detail) {
				return true
			}
		}
		return false
	}
	// Fallback to CheckStatus with a minimal catalog item
	citem := status.ToCatalogItem(item)
	needed, err := status.CheckStatus(citem, "install", cfg.CachePath)
	if err != nil {
		return true
	}
	return needed
}

// fileNeedsUpdate checks if a specific file needs an update based on its presence, MD5 checksum, and version.
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
	// Check MD5 checksum if provided
	if d.MD5Checksum != "" {
		match := download.Verify(d.Path, d.MD5Checksum)
		if !match {
			logging.Debug("MD5 checksum mismatch => update needed", "file", d.Path)
			return true
		}
	}
	// Check version if provided
	if d.Version != "" {
		myItem := catalog.Item{Name: filepath.Base(d.Path)}
		localVersion, err := status.GetInstalledVersion(myItem)
		if err != nil {
			logging.Warn("Failed to get installed version => update needed", "file", d.Path, "error", err)
			return true
		}
		if status.IsOlderVersion(localVersion, d.Version) {
			logging.Debug("Installed version is older => update needed", "file", d.Path, "local_version", localVersion, "required_version", d.Version)
			return true
		}
	}
	return false
}

// runPowerShellInline executes a PowerShell script and returns its exit code.
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

// installItem decides how to install or update an item that is NOT a .nupkg
func installItem(item catalog.Item, localFile, cachePath string) error {
	sysArch := getSystemArchitecture()
	if !supportsArchitecture(item, sysArch) {
		return fmt.Errorf("system arch %s not in supported_arch=%v for item %s", sysArch, item.SupportedArch, item.Name)
	}

	installerType := strings.ToLower(item.Installer.Type)
	switch installerType {
	case "msi":
		output, err := runMSIInstaller(item, localFile)
		if err != nil {
			return err
		}
		logging.Debug("MSI install output", "output", output)
		return nil

	case "exe":
		// If a preinstall_script is provided, detect if it’s .bat or PowerShell style
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

	case "nupkg":
		// Fallback logic if we ever get here, though we handle nupkg above
		output, err := installOrUpgradeNupkg(item, localFile)
		if err != nil {
			return err
		}
		logging.Debug("Nupkg install output", "output", output)
		return nil

	default:
		return fmt.Errorf("unknown installer type: %s", item.Installer.Type)
	}
}

// handle .nupkg: check if installed => upgrade, else => install
func installOrUpgradeNupkg(item catalog.Item, localFile string) (string, error) {
	// 1) Choose the pkgID we’ll use for 'choco list'
	pkgID := strings.TrimSpace(item.Identifier)
	if pkgID == "" {
		pkgID = strings.TrimSpace(item.Name)
	}

	// 2) Attempt to parse .nupkg file for ID & version, if desired.
	//    If you prefer to override the “pkgID” from the .nuspec <id>,
	//    you can do that here:
	nupkgID, nupkgVer, metaErr := extractNupkgMetadata(localFile)
	if metaErr != nil {
		logging.Warn("Failed to extract nupkg metadata, continuing with item.Identifier or item.Name",
			"pkgID", pkgID, "error", metaErr)
	} else {
		logging.Debug("Nupkg metadata extracted", "nupkgID", nupkgID, "nupkgVer", nupkgVer)
		// If you want to *override* the item’s ID with the .nuspec’s <id>, do it here:
		if nupkgID != "" {
			pkgID = nupkgID
		}
		// item.Version could also be set from nupkgVer, if you want.
		// item.Version = nupkgVer
	}

	logging.Info("Installing or upgrading nupkg", "pkgID", pkgID)

	// 3) Check if installed: `choco list --local-only <pkgID>`
	checkCmdArgs := []string{"list", "--local-only", pkgID}
	out, checkErr := runCMD(commandNupkg, checkCmdArgs)
	if checkErr != nil {
		logging.Warn("Failed to check if nupkg is installed, forcing install",
			"pkgID", pkgID, "error", checkErr)
		return runChocoInstall(localFile)
	}
	lines := strings.Split(strings.ToLower(out), "\n")

	// We assume it's "installed" if we see a line that starts with "<pkgid>|"
	installed := false
	prefix := strings.ToLower(pkgID) + "|"
	for _, line := range lines {
		if strings.HasPrefix(line, prefix) {
			installed = true
			break
		}
	}

	if installed {
		logging.Info("Nupkg is installed, upgrading...", "pkgID", pkgID)
		return runChocoUpgrade(localFile)
	}

	logging.Info("Nupkg not installed, installing...", "pkgID", pkgID)
	return runChocoInstall(localFile)
}

const chocolateyBin = `C:\ProgramData\chocolatey\bin\choco.exe`

func runChocoInstall(nupkgFile string) (string, error) {
	cmdArgs := []string{
		"install",
		nupkgFile,
		"-y",
		"--log-file=C:\\ProgramData\\ManagedInstalls\\Logs\\install.log",
		"--debug",
	}
	return runCMD(chocolateyBin, cmdArgs)
}

func runChocoUpgrade(nupkgFile string) (string, error) {
	cmdArgs := []string{
		"upgrade",
		nupkgFile,
		"-y",
		"--log-file=C:\\ProgramData\\ManagedInstalls\\Logs\\install.log",
		"--debug",
	}
	return runCMD(chocolateyBin, cmdArgs)
}

func runMSIInstaller(item catalog.Item, localFile string) (string, error) {
	cmdArgs := []string{
		"/i", localFile,
		"/quiet",
		"/norestart",
		"/l*v", `C:\ProgramData\ManagedInstalls\Logs\install.log`,
	}

	output, err := runCMD("msiexec.exe", cmdArgs)
	if err != nil {
		// Logging already improved in runCMD()
		return output, err
	}
	logging.Info("Successfully installed MSI package", "package", item.Name)
	return output, nil
}

// runPreinstallScript decides if item.PreScript is a BAT or PowerShell script, then calls the appropriate function.
func runPreinstallScript(item catalog.Item, localFile, cachePath string) (string, error) {
	// If the script content appears to start with @echo or any typical .bat syntax, call runBatInstaller
	// Otherwise, do runPS1InstallerFromScript
	scriptLower := strings.ToLower(item.PreScript)
	if strings.Contains(scriptLower, "@echo off") ||
		strings.HasPrefix(scriptLower, "rem ") ||
		strings.HasPrefix(scriptLower, "::") {
		// Looks like a BAT script
		return runBatInstaller(item, localFile, cachePath)
	}
	// else assume PowerShell
	return runPS1InstallerFromScript(item, localFile, cachePath)
}

// runBatInstaller writes item.PreScript to a .bat file, then calls it with cmd.exe /c
func runBatInstaller(item catalog.Item, localFile, cachePath string) (string, error) {
	// If we truly don't need localFile, explicitly ignore it to silence 'unused param'
	_ = localFile

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
		// Use %v for error message
		return output, fmt.Errorf("command execution failed: %v - %s", runErr, stderr.String())
	}
	return output, nil
}

// runPS1InstallerFromScript writes item.PreScript to a .ps1 file, then calls it with powershell
func runPS1InstallerFromScript(item catalog.Item, localFile, cachePath string) (string, error) {
	_ = localFile // not used here
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

// runEXEInstaller for direct silent .exe (no preinstall script)
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

// runCMD ensures we do NOT treat a failing command as success
func runCMD(command string, arguments []string) (string, error) {
	cmd := exec.Command(command, arguments...)

	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	err := cmd.Run()
	outStr := stdout.String()
	errStr := stderr.String()

	if err != nil {
		// If the error is from a non-zero exit code, log that code explicitly
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
		// Or some other error (like “file not found”)
		logging.Error("Failed to run cmd", "command", command, "args", arguments, "error", err)
		return outStr, err
	}

	// If we get here => exitCode=0 => success
	return outStr, nil
}

// uninstallItem decides how to uninstall an item
func uninstallItem(item catalog.Item, cachePath string) (string, error) {
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

// hideConsoleWindow is used by runBatInstaller
func hideConsoleWindow(cmd *exec.Cmd) {
	if runtime.GOOS == "windows" {
		if cmd.SysProcAttr == nil {
			cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
		} else {
			cmd.SysProcAttr.HideWindow = true
		}
	}
}

// getSystemArchitecture returns a unified architecture string
// unifyArch is now used in supportsArchitecture
func unifyArch(arch string) string {
	a := strings.ToLower(arch)
	if a == "amd64" || a == "x86_64" {
		return "x64"
	}
	if a == "386" {
		return "x86"
	}
	return a
}

func getSystemArchitecture() string {
	return unifyArch(runtime.GOARCH)
}

func supportsArchitecture(item catalog.Item, systemArch string) bool {
	for _, arch := range item.SupportedArch {
		if unifyArch(arch) == systemArch {
			return true
		}
	}
	return false
}
