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
	"strings"

	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/download"
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
	cmd := execCommand(command, arguments...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	err := cmd.Run()
	output := stdout.String()
	if err != nil {
		logging.Warn("Command execution failed", "command", command, "error", err, "stderr", stderr.String())
		return output, err
	}

	return output, nil
}

// installItem installs a single catalog item.
func installItem(item catalog.Item, itemURL, cachePath string, cfg *config.Configuration) string {
	relPath, fileName := path.Split(item.Installer.Location)
	absFile := filepath.Join(cachePath, relPath, fileName)

	if !fileExists(absFile) {
		if err := download.DownloadFile(itemURL, absFile, cfg); err != nil {
			msg := fmt.Sprintf("Unable to download file: %s", itemURL)
			logging.Warn(msg, "error", err)
			return msg
		}
	}

	switch strings.ToLower(item.Installer.Type) {
	case "msi":
		return runMSIInstaller(absFile, item)
	case "exe":
		return runEXEInstaller(absFile, item)
	case "ps1":
		return runPS1Installer(absFile)
	case "nupkg":
		output, err := installNupkg(absFile)
		if err != nil {
			logging.Warn("Failed to install Nupkg", "error", err)
		}
		return output
	default:
		msg := fmt.Sprintf("Unsupported installer type: %s", item.Installer.Type)
		logging.Warn(msg)
		return msg
	}
}

// runMSIInstaller installs an MSI package.
func runMSIInstaller(absFile string, item catalog.Item) string {
	installArgs := append([]string{"/i", absFile, "/qn", "/norestart"}, item.Installer.Arguments...)
	output, err := runCMD(commandMsi, installArgs)
	if err != nil {
		logging.Warn("MSI installation failed", "file", absFile, "error", err)
	}
	return output
}

// runEXEInstaller installs an EXE package.
func runEXEInstaller(absFile string, item catalog.Item) string {
	output, err := runCMD(absFile, item.Installer.Arguments)
	if err != nil {
		logging.Warn("EXE installation failed", "file", absFile, "error", err)
	}
	return output
}

// runPS1Installer executes a PowerShell script.
func runPS1Installer(absFile string) string {
	psArgs := []string{"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", absFile}
	output, err := runCMD(commandPs1, psArgs)
	if err != nil {
		logging.Warn("PowerShell script execution failed", "file", absFile, "error", err)
	}
	return output
}

// installNupkg installs a Nupkg package using Chocolatey.
func installNupkg(absFile string) (string, error) {
	id, version, err := extractNupkgMetadata(absFile)
	if err != nil {
		return "", err
	}

	logging.Info("Installing Nupkg", "id", id, "version", version)
	nupkgDir := filepath.Dir(absFile)
	installArgs := []string{"install", id, "--version", version, "-s", nupkgDir, "-f", "-y", "-r"}
	return runCMD(commandNupkg, installArgs)
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
