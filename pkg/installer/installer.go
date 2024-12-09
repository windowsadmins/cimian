package installer

import (
	"archive/zip"
	"bufio"
	"bytes"
	"encoding/xml"
	"fmt"
	"io"
	"io/ioutil"
	"os"
	"os/exec"
	"path"
	"path/filepath"
	"strings"
	"sync"

	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/download"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/pkginfo"
	"github.com/windowsadmins/gorilla/pkg/report"
	"github.com/windowsadmins/gorilla/pkg/status"
)

var (
	commandNupkg = filepath.Join(os.Getenv("ProgramData"), "chocolatey", "bin", "choco.exe")
	commandMsi   = filepath.Join(os.Getenv("WINDIR"), "system32", "msiexec.exe")
	commandPs1   = filepath.Join(os.Getenv("WINDIR"), "system32", "WindowsPowershell", "v1.0", "powershell.exe")

	execCommand       = exec.Command
	statusCheckStatus = status.CheckStatus
	runCommand        = runCMD
)

// NupkgMetadata holds parsed data from the .nuspec file
type NupkgMetadata struct {
	XMLName     xml.Name `xml:"package"`
	MetadataTag struct {
		ID      string `xml:"id"`
		Version string `xml:"version"`
	} `xml:"metadata"`
}

// runCMD executes a command and its arguments in the CMD environment
func runCMD(command string, arguments []string) (string, error) {
	cmd := execCommand(command, arguments...)
	var cmdOutput string
	cmdReader, err := cmd.StdoutPipe()
	if err != nil {
		logging.Warn("command", command, "arguments", arguments, "Error creating pipe to stdout", err)
	}

	var wg sync.WaitGroup
	wg.Add(1)

	scanner := bufio.NewScanner(cmdReader)
	logging.Debug("command", command, "arguments", arguments)
	go func() {
		logging.Debug("Command Output:")
		logging.Debug("--------------------")
		for scanner.Scan() {
			line := scanner.Text()
			logging.Debug(line)
			cmdOutput = line
		}
		logging.Debug("--------------------")
		wg.Done()
	}()

	err = cmd.Start()
	if err != nil {
		logging.Warn("command", command, "arguments", arguments, "Error running command", err)
	}

	wg.Wait()
	waitErr := cmd.Wait()
	if waitErr != nil {
		logging.Warn("command", command, "arguments", arguments, "Command error", waitErr)
		return cmdOutput, waitErr
	}

	return cmdOutput, err
}

// extractNupkgMetadata extracts the nuspec file from a nupkg and returns the ID and Version.
func extractNupkgMetadata(nupkgPath string) (string, string, error) {
	r, err := zip.OpenReader(nupkgPath)
	if err != nil {
		return "", "", fmt.Errorf("failed to open nupkg: %w", err)
	}
	defer r.Close()

	var nuspecFile *zip.File
	for _, f := range r.File {
		if strings.HasSuffix(strings.ToLower(f.Name), ".nuspec") {
			nuspecFile = f
			break
		}
	}
	if nuspecFile == nil {
		return "", "", fmt.Errorf("nuspec file not found in nupkg")
	}

	rc, err := nuspecFile.Open()
	if err != nil {
		return "", "", fmt.Errorf("failed to open nuspec file: %w", err)
	}
	defer rc.Close()

	data, err := io.ReadAll(rc)
	if err != nil {
		return "", "", fmt.Errorf("failed to read nuspec: %w", err)
	}

	var meta NupkgMetadata
	if err := xml.Unmarshal(data, &meta); err != nil {
		return "", "", fmt.Errorf("failed to unmarshal nuspec: %w", err)
	}

	id := meta.MetadataTag.ID
	version := meta.MetadataTag.Version
	if id == "" || version == "" {
		return "", "", fmt.Errorf("invalid nuspec data: missing id/version")
	}

	return id, version, nil
}

// installNupkg handles nupkg installation by extracting ID/version from nuspec and then calling choco install
func installNupkg(absFile string, item catalog.Item) (string, error) {
	id, version, err := extractNupkgMetadata(absFile)
	if err != nil {
		return "", err
	}

	logging.Info("Installing Nupkg using choco", "id", id, "version", version)
	installCmd := commandNupkg
	nupkgDir := filepath.Dir(absFile)
	installArgs := []string{"install", id, "--version", version, "-s", nupkgDir, "-f", "-y", "-r"}

	out, runErr := runCommand(installCmd, installArgs)
	return out, runErr
}

// uninstallNupkg handles nupkg uninstallation by extracting ID/version from nuspec and then calling choco uninstall
func uninstallNupkg(absFile string, item catalog.Item) (string, error) {
	id, version, err := extractNupkgMetadata(absFile)
	if err != nil {
		return "", err
	}

	logging.Info("Uninstalling Nupkg using choco", "id", id, "version", version)
	uninstallCmd := commandNupkg
	nupkgDir := filepath.Dir(absFile)
	uninstallArgs := []string{"uninstall", id, "--version", version, "-s", nupkgDir, "-f", "-y", "-r"}

	out, runErr := runCommand(uninstallCmd, uninstallArgs)
	return out, runErr
}

// preinstallScript executes a pre-install script if provided
func preinstallScript(catalogItem catalog.Item, cachePath string) (bool, error) {
	if catalogItem.PreScript == "" {
		return true, nil
	}

	tmpScript := filepath.Join(cachePath, "tmpPreScript.ps1")
	err := ioutil.WriteFile(tmpScript, []byte(catalogItem.PreScript), 0755)
	if err != nil {
		logging.Error("Failed to write pre-install script:", "error", err)
		return false, err
	}

	psCmd := commandPs1
	psArgs := []string{"-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", tmpScript}

	cmd := execCommand(psCmd, psArgs...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err = cmd.Run()
	cmdSuccess := cmd.ProcessState != nil && cmd.ProcessState.Success()
	outStr, errStr := stdout.String(), stderr.String()

	os.Remove(tmpScript)

	logging.Debug("Pre-Install Command Error:", "error", err)
	logging.Debug("Pre-Install stdout:", "output", outStr)
	logging.Debug("Pre-Install stderr:", "error_output", errStr)

	return cmdSuccess, err
}

// postinstallScript executes a post-install script if provided
func postinstallScript(catalogItem catalog.Item, cachePath string) (bool, error) {
	if catalogItem.PostScript == "" {
		return true, nil
	}

	tmpScript := filepath.Join(cachePath, "tmpPostScript.ps1")
	err := ioutil.WriteFile(tmpScript, []byte(catalogItem.PostScript), 0755)
	if err != nil {
		logging.Error("Failed to write post-install script:", "error", err)
		return false, err
	}

	psCmd := commandPs1
	psArgs := []string{"-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", tmpScript}

	cmd := execCommand(psCmd, psArgs...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err = cmd.Run()
	cmdSuccess := cmd.ProcessState != nil && cmd.ProcessState.Success()
	outStr, errStr := stdout.String(), stderr.String()

	os.Remove(tmpScript)

	logging.Debug("Post-Install Command Error:", "error", err)
	logging.Debug("Post-Install stdout:", "output", outStr)
	logging.Debug("Post-Install stderr:", "error_output", errStr)

	return cmdSuccess, err
}

// installItem installs a catalog item using the provided configuration
func installItem(item catalog.Item, itemURL, cachePath string, cfg *config.Configuration) string {
	relPath, fileName := path.Split(item.Installer.Location)
	absPath := filepath.Join(cachePath, relPath)
	absFile := filepath.Join(absPath, fileName)

	valid := download.IfNeeded(absFile, itemURL, item.Installer.Hash, cfg)
	if !valid {
		msg := fmt.Sprintf("Unable to download valid file: %s", itemURL)
		logging.Warn(msg)
		return msg
	}

	var installCmd string
	var installArgs []string
	var installerOut string
	var errOut error

	switch strings.ToLower(item.Installer.Type) {
	case "nupkg":
		installerOut, errOut = installNupkg(absFile, item)
	case "msi":
		logging.Info("Installing MSI for", "display_name", item.DisplayName)
		installCmd = commandMsi
		installArgs = append([]string{"/i", absFile, "/qn", "/norestart"}, item.Installer.Arguments...)
		installerOut, errOut = runCommand(installCmd, installArgs)
	case "exe":
		logging.Info("Installing EXE for", "display_name", item.DisplayName)
		installCmd = absFile
		installArgs = item.Installer.Arguments
		installerOut, errOut = runCommand(installCmd, installArgs)
	case "ps1":
		logging.Info("Installing PS1 for", "display_name", item.DisplayName)
		installCmd = commandPs1
		installArgs = []string{"-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", absFile}
		installerOut, errOut = runCommand(installCmd, installArgs)
	default:
		msg := fmt.Sprintf("Unsupported installer type: %s", item.Installer.Type)
		logging.Warn(msg)
		return msg
	}

	if errOut != nil {
		logging.Warn("Installation FAILED", "display_name", item.DisplayName, "version", item.Version)
	} else {
		logging.Info("Installation SUCCESSFUL", "display_name", item.DisplayName, "version", item.Version)
	}

	report.InstalledItems = append(report.InstalledItems, item)
	return installerOut
}

// uninstallItem uninstalls a catalog item using the provided configuration
func uninstallItem(item catalog.Item, itemURL, cachePath string, cfg *config.Configuration) string {
	relPath, fileName := path.Split(item.Uninstaller.Location)
	absPath := filepath.Join(cachePath, relPath)
	absFile := filepath.Join(absPath, fileName)

	valid := download.IfNeeded(absFile, itemURL, item.Uninstaller.Hash, cfg)
	if !valid {
		msg := fmt.Sprintf("Unable to download valid file: %s", itemURL)
		logging.Warn(msg)
		return msg
	}

	var uninstallCmd string
	var uninstallArgs []string
	var uninstallerOut string
	var errOut error

	switch strings.ToLower(item.Uninstaller.Type) {
	case "nupkg":
		uninstallerOut, errOut = uninstallNupkg(absFile, item)
	case "msi":
		logging.Info("Uninstalling MSI for", "display_name", item.DisplayName)
		uninstallCmd = commandMsi
		uninstallArgs = []string{"/x", absFile, "/qn", "/norestart"}
		uninstallerOut, errOut = runCommand(uninstallCmd, uninstallArgs)
	case "exe":
		logging.Info("Uninstalling EXE for", "display_name", item.DisplayName)
		uninstallCmd = absFile
		uninstallArgs = item.Uninstaller.Arguments
		uninstallerOut, errOut = runCommand(uninstallCmd, uninstallArgs)
	case "ps1":
		logging.Info("Uninstalling PS1 for", "display_name", item.DisplayName)
		uninstallCmd = commandPs1
		uninstallArgs = []string{"-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", absFile}
		uninstallerOut, errOut = runCommand(uninstallCmd, uninstallArgs)
	default:
		msg := fmt.Sprintf("Unsupported uninstaller type: %s", item.Uninstaller.Type)
		logging.Warn(msg)
		return msg
	}

	if errOut != nil {
		logging.Warn("Uninstallation FAILED", "display_name", item.DisplayName, "version", item.Version)
	} else {
		logging.Info("Uninstallation SUCCESSFUL", "display_name", item.DisplayName, "version", item.Version)
	}

	report.UninstalledItems = append(report.UninstalledItems, item)
	return uninstallerOut
}

// Install determines if action needs to be taken on an item and then calls the appropriate function to install or uninstall
func Install(item catalog.Item, installerType, urlPackages, cachePath string, checkOnly bool, cfg *config.Configuration) string {
	actionNeeded, err := statusCheckStatus(item, installerType, cachePath)
	if err != nil {
		msg := fmt.Sprintf("Unable to check status: %v", err)
		logging.Warn(msg)
		return msg
	}

	if !actionNeeded {
		return "Item not needed"
	}

	if strings.ToLower(installerType) == "install" || strings.ToLower(installerType) == "update" {
		if checkOnly {
			report.InstalledItems = append(report.InstalledItems, item)
			logging.Info("[CHECK ONLY] Skipping actions for", "display_name", item.DisplayName)
			return "Check only enabled"
		} else {
			itemURL := urlPackages + item.Installer.Location
			// Pre-install script
			if item.PreScript != "" {
				logging.Info("Running Pre-Install script for", "display_name", item.DisplayName)
				preScriptSuccess, err := preinstallScript(item, cachePath)
				if !preScriptSuccess {
					logging.Error("Pre-Install script error:", "error", err)
					return "PreInstall-Script error"
				}
			}

			out := installItem(item, itemURL, cachePath, cfg)

			// Post-install script
			if item.PostScript != "" {
				logging.Info("Running Post-Install script for", "display_name", item.DisplayName)
				postScriptSuccess, err := postinstallScript(item, cachePath)
				if !postScriptSuccess {
					logging.Error("Post-Install script error:", "error", err)
					return "PostInstall-Script error"
				}
			}

			return out
		}
	} else if strings.ToLower(installerType) == "uninstall" {
		if checkOnly {
			report.InstalledItems = append(report.InstalledItems, item)
			logging.Info("[CHECK ONLY] Skipping actions for", "display_name", item.DisplayName)
			return "Check only enabled"
		} else {
			itemURL := urlPackages + item.Uninstaller.Location
			return uninstallItem(item, itemURL, cachePath, cfg)
		}
	} else {
		logging.Warn("Unsupported item type", "display_name", item.DisplayName, "type", installerType)
		return "Unsupported item type"
	}
}

// InstallPackage installs a package using its pkgsinfo metadata.
func InstallPackage(pkgInfoPath string, pkgsDir string, cfg *config.Configuration) error {
	// Read the pkgsinfo metadata
	pkgInfo, err := pkginfo.ReadPkgInfo(pkgInfoPath)
	if err != nil {
		return fmt.Errorf("failed to read pkgsinfo: %v", err)
	}

	// Extract relevant information from pkgInfo
	packageName, ok := pkgInfo["name"].(string)
	if !ok {
		return fmt.Errorf("invalid pkgsinfo format: missing 'name'")
	}
	// For now, assume .msi (could be extended if needed)
	installerPath := filepath.Join(pkgsDir, fmt.Sprintf("%s.msi", packageName))

	// Check if the installer exists
	if _, err := os.Stat(installerPath); os.IsNotExist(err) {
		return fmt.Errorf("installer not found: %s", installerPath)
	}

	// Execute the installer (example for MSI)
	cmd := execCommand("msiexec", "/i", installerPath, "/quiet", "/norestart")
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("failed to install package: %v", err)
	}

	logging.Info("Successfully installed package", "package_name", packageName)
	return nil
}
