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

	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/logging"
)

var (
	commandNupkg = filepath.Join(os.Getenv("ProgramData"), "chocolatey", "bin", "choco.exe")
	commandMsi   = filepath.Join(os.Getenv("WINDIR"), "system32", "msiexec.exe")
	commandPs1   = filepath.Join(os.Getenv("WINDIR"), "system32", "WindowsPowershell", "v1.0", "powershell.exe")

	execCommand = exec.Command
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
			output := installItem(item, path, cfg.CachePath, cfg)
			logging.Info("Install output", "output", output)
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

// runCMD executes a command and its arguments.
func runCMD(command string, arguments []string) (string, error) {
	cmd := exec.Command(command, arguments...)
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

// getSystemArchitecture returns the architecture of the system.
func getSystemArchitecture() string {
	return runtime.GOARCH
}

// supportsArchitecture checks if the package supports the given system architecture.
func supportsArchitecture(item catalog.Item, systemArch string) bool {
	for _, arch := range item.SupportedArch {
		if arch == systemArch {
			return true
		}
	}
	return false
}

// installItem installs a single catalog item based on the system architecture.
func installItem(item catalog.Item, itemURL, cachePath string, cfg *config.Configuration) string {
	systemArch := getSystemArchitecture()
	if !supportsArchitecture(item, systemArch) {
		logging.Warn("Unsupported architecture for item", "item", item.Name, "architecture", systemArch)
		return ""
	}

	installerType := strings.ToLower(item.Installer.Type)
	switch installerType {
	case "msi":
		return runMSIInstaller(item, itemURL, cachePath, cfg)
	case "exe":
		return runEXEInstaller(item, itemURL, cachePath, cfg)
	case "powershell":
		return runPS1Installer(item, itemURL, cachePath, cfg)
	case "nupkg":
		return installNupkg(item, itemURL, cachePath, cfg)
	default:
		logging.Warn("Unknown installer type", "type", item.Installer.Type)
		return ""
	}
}

// runMSIInstaller installs an MSI package.
func runMSIInstaller(item catalog.Item, itemURL, cachePath string, cfg *config.Configuration) string {
	msiPath := filepath.Join(cachePath, filepath.Base(itemURL))
	cmdArgs := []string{"/i", msiPath, "/quiet", "/norestart"}
	output, err := runCMD(commandMsi, cmdArgs)
	if err != nil {
		logging.Error("Failed to install MSI package", "package", item.Name, "error", err)
		return ""
	}
	logging.Info("Successfully installed MSI package", "package", item.Name)
	return output
}

// runEXEInstaller installs an EXE package.
func runEXEInstaller(item catalog.Item, itemURL, cachePath string, cfg *config.Configuration) string {
	exePath := filepath.Join(cachePath, filepath.Base(itemURL))
	cmdArgs := append([]string{"/S"}, item.Installer.Arguments...)
	output, err := runCMD(exePath, cmdArgs)
	if err != nil {
		logging.Error("Failed to install EXE package", "package", item.Name, "error", err)
		return ""
	}
	logging.Info("Successfully installed EXE package", "package", item.Name)
	return output
}

// runPS1Installer executes a PowerShell script.
func runPS1Installer(item catalog.Item, itemURL, cachePath string, cfg *config.Configuration) string {
	ps1Path := filepath.Join(cachePath, filepath.Base(itemURL))
	cmdArgs := []string{"-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps1Path}
	output, err := runCMD(commandPs1, cmdArgs)
	if err != nil {
		logging.Error("Failed to execute PowerShell script", "script", item.Name, "error", err)
		return ""
	}
	logging.Info("Successfully executed PowerShell script", "script", item.Name)
	return output
}

// installNupkg installs a Nupkg package using Chocolatey.
func installNupkg(item catalog.Item, itemURL, cachePath string, cfg *config.Configuration) string {
	nupkgPath := filepath.Join(cachePath, filepath.Base(itemURL))
	cmdArgs := []string{"install", nupkgPath, "-y"}
	output, err := runCMD(commandNupkg, cmdArgs)
	if err != nil {
		logging.Error("Failed to install Nupkg package", "package", item.Name, "error", err)
		return ""
	}
	logging.Info("Successfully installed Nupkg package", "package", item.Name)
	return output
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

func Install(item catalog.Item, action, urlPackages, cachePath string, CheckOnly bool, cfg *config.Configuration) string {
	if CheckOnly {
		logging.Info("CheckOnly mode: would perform action", "action", action, "item", item.Name)
		return "CheckOnly: No action performed."
	}

	itemURL := item.Installer.Location
	switch action {
	case "install", "update":
		return installItem(item, itemURL, cachePath, cfg)
	case "uninstall":
		return uninstallItem(item, cachePath)
	default:
		msg := fmt.Sprintf("Unsupported action: %s", action)
		logging.Warn(msg)
		return msg
	}
}

func uninstallItem(item catalog.Item, cachePath string) string {
	relPath, fileName := path.Split(item.Installer.Location)
	absFile := filepath.Join(cachePath, relPath, fileName)

	if !fileExists(absFile) {
		msg := fmt.Sprintf("Uninstall file does not exist: %s", absFile)
		logging.Warn(msg)
		return msg
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
		return msg
	}
}

func runMSIUninstaller(absFile string, item catalog.Item) string {
	uninstallArgs := append([]string{"/x", absFile, "/qn", "/norestart"}, item.Uninstaller.Arguments...)
	output, err := runCMD(commandMsi, uninstallArgs)
	if err != nil {
		logging.Warn("MSI uninstallation failed", "file", absFile, "error", err)
	}
	return output
}

func runEXEUninstaller(absFile string, item catalog.Item) string {
	output, err := runCMD(absFile, item.Uninstaller.Arguments)
	if err != nil {
		logging.Warn("EXE uninstallation failed", "file", absFile, "error", err)
	}
	return output
}

// Removed the cfg parameter here since it was unused.
func runPS1Uninstaller(absFile string) string {
	psArgs := []string{"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", absFile}
	output, err := runCMD(commandPs1, psArgs)
	if err != nil {
		logging.Warn("PowerShell script uninstallation failed", "file", absFile, "error", err)
	}
	return output
}

func runNupkgUninstaller(absFile string) string {
	id, _, err := extractNupkgMetadata(absFile)
	if err != nil {
		msg := fmt.Sprintf("Failed to read nupkg metadata for uninstall: %v", err)
		logging.Warn(msg)
		return msg
	}

	nupkgDir := filepath.Dir(absFile)
	uninstallArgs := []string{"uninstall", id, "-s", nupkgDir, "-y"}
	output, err := runCMD(commandNupkg, uninstallArgs)
	if err != nil {
		logging.Warn("Nupkg uninstallation failed", "file", absFile, "error", err)
	}
	return output
}
