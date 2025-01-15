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

	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/logging"
)

var (
	commandNupkg = filepath.Join(os.Getenv("ProgramData"), "chocolatey", "bin", "choco.exe")
	commandMsi   = filepath.Join(os.Getenv("WINDIR"), "system32", "msiexec.exe")
	commandPs1   = filepath.Join(os.Getenv("WINDIR"), "system32", "WindowsPowershell", "v1.0", "powershell.exe")
)

// fileExists checks if a file exists on the filesystem.
func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

// InstallPendingPackages walks through the cache directory and installs pending packages.
func InstallPendingPackages(cfg *config.Configuration) error {
	logging.Info("Starting pending installations...")

	err := filepath.Walk(cfg.CachePath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			logging.Error("Error accessing path", "path", path, "error", err)
			return err
		}

		if !info.IsDir() {
			logging.Info("Processing pending package", "file", path)
			item := catalog.Item{
				Name: info.Name(),
				Installer: catalog.InstallerItem{
					Type:     "msi",
					Location: path,
				},
			}
			if err := installItem(item, path, cfg.CachePath); err != nil {
				logging.Error("Failed to install package", "file", path, "error", err)
				return err
			}
			logging.Info("Successfully installed package", "file", path)
		}
		return nil
	})

	if err != nil {
		logging.Error("Error processing cache directory", "error", err)
		return err
	}

	logging.Info("Pending installations completed successfully.")
	return nil
}

// Windows constants from Win32 API
const CREATE_NO_WINDOW = 0x08000000

// runCMD executes a command and its arguments.
func runCMD(command string, arguments []string) (string, error) {
	cmd := exec.Command(command, arguments...)

	// Hide window & disable any GUI popups on Windows
	if runtime.GOOS == "windows" {
		cmd.SysProcAttr = &syscall.SysProcAttr{
			HideWindow:    true,
			CreationFlags: CREATE_NO_WINDOW, // same as syscall.CREATE_NO_WINDOW
		}
	}

	var out bytes.Buffer
	var stderr bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &stderr

	err := cmd.Run()
	if err != nil {
		return "", fmt.Errorf("command execution failed: %v - %s", err, stderr.String())
	}
	return out.String(), nil
}

// getSystemArchitecture returns a unified architecture string
func getSystemArchitecture() string {
	arch := runtime.GOARCH
	switch arch {
	case "amd64", "x86_64":
		return "x64"
	case "386":
		return "x86"
	default:
		return arch
	}
}

// unifyArch normalizes common synonyms to “x64”, “x86”, etc.
func unifyArch(arch string) string {
	lower := strings.ToLower(arch)
	if lower == "amd64" || lower == "x86_64" {
		return "x64"
	}
	if lower == "386" {
		return "x86"
	}
	return lower
}

func supportsArchitecture(item catalog.Item, systemArch string) bool {
	// Normalize systemArch up front
	systemArch = unifyArch(systemArch)

	for _, arch := range item.SupportedArch {
		if unifyArch(arch) == systemArch {
			return true
		}
	}
	return false
}

// installItem does the real installation logic.
func installItem(item catalog.Item, itemURL, cachePath string) error {
	// 1) Check architecture
	sysArch := getSystemArchitecture()
	if !supportsArchitecture(item, sysArch) {
		logging.Warn("Unsupported architecture for item",
			"item", item.Name,
			"supported_arch", item.SupportedArch,
			"system_arch", sysArch,
		)
		return fmt.Errorf("cannot install %s (system_arch=%s not in supported_arch=%v)",
			item.Name, sysArch, item.SupportedArch)
	}

	// 2) Decide runner:
	installerType := strings.ToLower(item.Installer.Type)
	switch installerType {
	case "msi":
		output, err := runMSIInstaller(item, itemURL, cachePath)
		if err != nil {
			return err
		}
		logging.Debug("MSI install output", "output", output)
		return nil

	case "exe":
		output, err := runEXEInstaller(item, itemURL, cachePath)
		if err != nil {
			return err
		}
		logging.Debug("EXE install output", "output", output)
		return nil

	case "powershell":
		output, err := runPS1Installer(item, itemURL, cachePath)
		if err != nil {
			return err
		}
		logging.Debug("PS1 install output", "output", output)
		return nil

	case "nupkg":
		output, err := installNupkg(item, itemURL, cachePath)
		if err != nil {
			return err
		}
		logging.Debug("Nupkg install output", "output", output)
		return nil

	default:
		logging.Warn("Unknown installer type", "type", item.Installer.Type)
		return fmt.Errorf("unknown installer type: %s", item.Installer.Type)
	}
}

// runMSIInstaller installs an MSI package.
func runMSIInstaller(item catalog.Item, itemURL, cachePath string) (string, error) {
	msiPath := filepath.Join(cachePath, filepath.Base(itemURL))
	cmdArgs := []string{"/i", msiPath, "/quiet", "/norestart"}

	output, err := runCMD(commandMsi, cmdArgs)
	if err != nil {
		// Return empty output + the error
		logging.Error("Failed to install MSI package", "package", item.Name, "error", err)
		return "", err
	}

	// If success, return the output + a nil error
	logging.Info("Successfully installed MSI package", "package", item.Name)
	return output, nil
}

// runEXEInstaller installs an EXE package.
func runEXEInstaller(item catalog.Item, itemURL, cachePath string) (string, error) {
	exePath := filepath.Join(cachePath, filepath.Base(itemURL))

	// Force silent argument by default (e.g. NSIS: /S).
	// Then append item.Installer.Arguments if your catalog might define extra flags:
	baseSilentArgs := []string{"/S"}

	// Merge user-specified arguments
	cmdArgs := append(baseSilentArgs, item.Installer.Arguments...)

	output, err := runCMD(exePath, cmdArgs)
	if err != nil {
		logging.Error("Failed to install EXE package", "package", item.Name, "error", err)
		return "", err
	}

	logging.Info("Successfully installed EXE package", "package", item.Name)
	return output, nil
}

// runPS1Installer executes a PowerShell script.
func runPS1Installer(item catalog.Item, itemURL, cachePath string) (string, error) {
	ps1Path := filepath.Join(cachePath, filepath.Base(itemURL))
	cmdArgs := []string{"-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps1Path}

	output, err := runCMD(commandPs1, cmdArgs)
	if err != nil {
		logging.Error("Failed to execute PowerShell script", "script", item.Name, "error", err)
		return "", err
	}

	logging.Info("Successfully executed PowerShell script", "script", item.Name)
	return output, nil
}

// installNupkg installs a Nupkg package using Chocolatey.
func installNupkg(item catalog.Item, itemURL, cachePath string) (string, error) {
	nupkgPath := filepath.Join(cachePath, filepath.Base(itemURL))
	cmdArgs := []string{"install", nupkgPath, "-y"}

	output, err := runCMD(commandNupkg, cmdArgs)
	if err != nil {
		logging.Error("Failed to install Nupkg package", "package", item.Name, "error", err)
		return "", err
	}

	logging.Info("Successfully installed Nupkg package", "package", item.Name)
	return output, nil
}

// extractNupkgMetadata extracts metadata from a Nupkg file.
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
			if err := xml.NewDecoder(rc).Decode(&meta); err != nil {
				return "", "", fmt.Errorf("failed to parse nuspec: %w", err)
			}

			return meta.Metadata.ID, meta.Metadata.Version, nil
		}
	}
	return "", "", fmt.Errorf("nuspec file not found in nupkg")
}

func Install(item catalog.Item, action, urlPackages, cachePath string, checkOnly bool, cfg *config.Configuration) string {
	if checkOnly {
		logging.Info("CheckOnly mode: would perform action", "action", action, "item", item.Name)
		return "CheckOnly: No action performed."
	}

	// Decide if we're installing or uninstalling
	switch action {
	case "install", "update":
		err := installItem(item, item.Installer.Location, cachePath)
		if err != nil {
			logging.Error("Installation failed", "item", item.Name, "error", err)
			// Return an error string so the caller can see it's not successful
			return fmt.Sprintf("Failed to install %s: %v", item.Name, err)
		}
		logging.Info("Installed item successfully", "item", item.Name)
		return "Install complete"

	case "uninstall":
		output, err := uninstallItem(item, cachePath)
		if err != nil {
			logging.Error("Uninstall failed", "item", item.Name, "error", err)
			return fmt.Sprintf("Failed to uninstall %s: %v", item.Name, err)
		}
		logging.Info("Uninstalled item successfully", "item", item.Name, "output", output)
		return "Uninstall complete"

	default:
		msg := fmt.Sprintf("Unsupported action: %s", action)
		logging.Warn(msg)
		return msg
	}
}

func uninstallItem(item catalog.Item, cachePath string) (string, error) {
	relPath, fileName := path.Split(item.Installer.Location)
	absFile := filepath.Join(cachePath, relPath, fileName)

	if !fileExists(absFile) {
		msg := fmt.Sprintf("Uninstall file does not exist: %s", absFile)
		logging.Warn(msg)
		// Return that string with no error
		return msg, nil
	}

	// Decide which “runXYZUninstaller” to call:
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
		return msg, nil
	}
}

func runMSIUninstaller(absFile string, item catalog.Item) (string, error) {
	uninstallArgs := append([]string{"/x", absFile, "/qn", "/norestart"}, item.Uninstaller.Arguments...)
	output, err := runCMD(commandMsi, uninstallArgs)
	if err != nil {
		logging.Warn("MSI uninstallation failed", "file", absFile, "error", err)
		return output, err // Return any partial output plus an error
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
	id, _, err := extractNupkgMetadata(absFile)
	if err != nil {
		msg := fmt.Sprintf("Failed to read nupkg metadata for uninstall: %v", err)
		logging.Warn(msg)
		return "", err
	}
	nupkgDir := filepath.Dir(absFile)
	uninstallArgs := []string{"uninstall", id, "-s", nupkgDir, "-y"}

	output, cmdErr := runCMD(commandNupkg, uninstallArgs)
	if cmdErr != nil {
		logging.Warn("Nupkg uninstallation failed", "file", absFile, "error", cmdErr)
		return output, cmdErr
	}
	return output, nil
}
