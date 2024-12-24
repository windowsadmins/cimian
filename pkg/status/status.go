package status

import (
	"bytes"
	"crypto/md5"
	"encoding/hex"
	"fmt"
	"io"
	"io/ioutil"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	version "github.com/hashicorp/go-version"
	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/download"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"golang.org/x/sys/windows/registry"
)

// RegistryApplication contains attributes for an installed application
type RegistryApplication struct {
	Key       string
	Location  string
	Name      string
	Source    string
	Uninstall string
	Version   string
}

// WindowsMetadata contains extended metadata retrieved in the `properties.go`
type WindowsMetadata struct {
	productName   string
	versionString string
	versionMajor  int
	versionMinor  int
	versionPatch  int
	versionBuild  int
}

var (
	// RegistryItems contains the status of all of the applications in the registry
	RegistryItems map[string]RegistryApplication

	// Abstracted functions so we can override these in unit tests
	execCommand = exec.Command
)

// checkRegistry iterates through the local registry and compiles all installed software
func checkRegistry(catalogItem catalog.Item, installType string) (actionNeeded bool, checkErr error) {
	// Iterate through the reg keys to compare with the catalog
	checkReg := catalogItem.Check.Registry
	catalogVersion, err := version.NewVersion(checkReg.Version)
	if err != nil {
		logging.Warn("Unable to parse new version: ", checkReg.Version, err)
	}

	logging.Debug("Check registry version:", checkReg.Version)
	// If needed, populate applications status from the registry
	if len(RegistryItems) == 0 {
		RegistryItems, checkErr = getUninstallKeys()
	}

	var installed bool
	var versionMatch bool
	// Instead of only matching partial, try both exact and partial matches
	for _, regItem := range RegistryItems {
		if regItem.Name == checkReg.Name {
			logging.Info("Exact registry match", "catalogName", checkReg.Name, "registryName", regItem.Name)
			installed = true
			logging.Debug("Current installed version:", regItem.Version)

			// Check if the catalog version matches the registry
			currentVersion, err := version.NewVersion(regItem.Version)
			if err != nil {
				logging.Warn("Unable to parse current version", err)
			}
			outdated := currentVersion.LessThan(catalogVersion)
			if !outdated {
				versionMatch = true
			}
			break
		} else if strings.Contains(regItem.Name, checkReg.Name) {
			logging.Info("Partial registry match", "catalogName", checkReg.Name, "registryName", regItem.Name)
			installed = true
			logging.Debug("Current installed version:", regItem.Version)

			// Check if the catalog version matches the registry
			currentVersion, err := version.NewVersion(regItem.Version)
			if err != nil {
				logging.Warn("Unable to parse current version", err)
			}
			outdated := currentVersion.LessThan(catalogVersion)
			if !outdated {
				versionMatch = true
			}
			break
		}
	}

	if checkReg.Name == "" && catalogItem.Installer.Type == "msi" && catalogItem.Installer.ProductCode != "" {
		logging.Debug("Checking registry by product_code:", catalogItem.Installer.ProductCode)
		installed, versionMatch = checkMsiProductCode(catalogItem.Installer.ProductCode, checkReg.Version)
		logging.Info("Compare registry vs catalog", "catalogVersion", checkReg.Version, "installed", installed, "versionMatch", versionMatch)
	}

	logging.Info("Compare registry vs catalog", "catalogVersion", checkReg.Version, "installed", installed, "versionMatch", versionMatch)

	if installType == "update" && !installed {
		actionNeeded = false
	} else if installType == "uninstall" {
		actionNeeded = installed
	} else if installed && versionMatch {
		actionNeeded = false
	} else {
		actionNeeded = true
	}

	return actionNeeded, checkErr
}

func checkScript(catalogItem catalog.Item, cachePath string, installType string) (actionNeeded bool, checkErr error) {

	// Write InstallCheckScript to disk as a Powershell file
	tmpScript := filepath.Join(cachePath, "tmpCheckScript.ps1")
	ioutil.WriteFile(tmpScript, []byte(catalogItem.Check.Script), 0755)

	// Build the command to execute the script
	psCmd := filepath.Join(os.Getenv("WINDIR"), "system32/", "WindowsPowershell", "v1.0", "powershell.exe")
	psArgs := []string{"-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", tmpScript}

	// Execute the script
	cmd := execCommand(psCmd, psArgs...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err := cmd.Run()
	cmdSuccess := cmd.ProcessState.Success()
	outStr, errStr := stdout.String(), stderr.String()

	// Delete the temporary script
	os.Remove(tmpScript)

	// Log results
	logging.Debug("Command Error:", err)
	logging.Debug("stdout:", outStr)
	logging.Debug("stderr:", errStr)

	actionNeeded = false
	// Application not installed if exit 0
	if installType == "uninstall" {
		actionNeeded = !cmdSuccess
	} else if installType == "install" {
		actionNeeded = cmdSuccess
	}

	return actionNeeded, checkErr
}

func checkPath(catalogItem catalog.Item, installType string) (actionNeeded bool, checkErr error) {
	var actionStore []bool

	// Iterate through all file provided paths
	for _, checkFile := range catalogItem.Check.File {
		path := filepath.Clean(checkFile.Path)
		logging.Debug("Check file path:", path)
		logging.Info("File check", "filePath", path)
		_, err := os.Stat(path)
		if err != nil {
			if os.IsNotExist(err) {

				// when doing an install, and the file path does not exist
				// perform an install
				if installType == "install" {
					actionStore = append(actionStore, true)
					break
				}

				// When doing an update or uninstall, and the file path does
				// not exist, do nothing
				if installType == "update" || installType == "uninstall" {
					logging.Debug("No action needed: Install type is", installType)
					break
				}
			}
			logging.Warn("Unable to check path:", path, err)
			break

		} else {

			// When doing an uninstall, and the path exists
			// perform uninstall
			if installType == "uninstall" {
				actionStore = append(actionStore, true)
			}
		}

		// If a hash is not blank, verify it matches the file
		// if the hash does not match, we need to install
		if checkFile.Hash != "" {
			logging.Debug("Check file hash:", checkFile.Hash)
			hashMatch := download.Verify(path, checkFile.Hash)
			if !hashMatch {
				actionStore = append(actionStore, true)
				break
			}
		}

		if checkFile.Version != "" {
			logging.Debug("Check file version:", checkFile.Version)
			metadata := GetFileMetadata(path)
			logging.Info("Comparing file version with catalog version", "fileVersion", metadata.versionString, "catalogVersion", checkFile.Version)

			// Get the file metadata, and check that it has a value
			if metadata.versionString == "" {
				break
			}
			logging.Debug("Current installed version:", metadata.versionString)

			// Convert both strings to a `Version` object
			versionHave, err := version.NewVersion(metadata.versionString)
			if err != nil {
				logging.Warn("Unable to compare version:", metadata.versionString)
				actionStore = append(actionStore, true)
				break
			}
			versionWant, err := version.NewVersion(checkFile.Version)
			if err != nil {
				logging.Warn("Unable to compare version:", checkFile.Version)
				actionStore = append(actionStore, true)
				break
			}

			// Compare the versions
			outdated := versionHave.LessThan(versionWant)
			if outdated {
				actionStore = append(actionStore, true)
				break
			}
		}
	}

	for _, item := range actionStore {
		if item {
			actionNeeded = true
			return
		}
	}
	actionNeeded = false
	return actionNeeded, checkErr
}

// CheckStatus determines if a catalogItem requires an install, update, or uninstall action.
// It checks script, file, or registry data based on the 'Check' field in the catalog item.
func CheckStatus(catalogItem catalog.Item, installType, cachePath string) (bool, error) {
	// Check script if provided
	if catalogItem.Check.Script != "" {
		logging.Info("Checking status via script:", catalogItem.DisplayName)
		return checkScript(catalogItem, cachePath, installType)
	}

	// Check file if present
	if len(catalogItem.Check.File) > 0 {
		logging.Info("Checking status via file:", catalogItem.DisplayName)
		return checkPath(catalogItem, installType)
	}

	// Check registry if version is specified
	if catalogItem.Check.Registry.Version != "" {
		logging.Info("Checking status via registry:", catalogItem.DisplayName)
		return checkRegistry(catalogItem, installType)
	}

	// Check the installs array
	needed, err := checkInstalls(catalogItem, installType)
	if err != nil {
		return true, err
	}
	if needed {
		return true, nil
	}

	// If in managed_installs => install if not installed or older
	// If in managed_updates => update only if already installed and older
	// If in managed_uninstalls => remove if installed

	localVersion, err := getLocalInstalledVersion(catalogItem)
	if err != nil {
		return true, fmt.Errorf("unable to detect local version: %v", err)
	}

	actionNeeded := false
	switch installType {
	case "install":
		if localVersion == "" {
			actionNeeded = true
		} else if isOlderVersion(localVersion, catalogItem.Version) {
			actionNeeded = true
		}
	case "update":
		if localVersion != "" && isOlderVersion(localVersion, catalogItem.Version) {
			actionNeeded = true
		}
	case "uninstall":
		if localVersion != "" {
			actionNeeded = true
		}
	}

	if !actionNeeded {
		// Use catalogItem.Name if no DisplayName is provided
		displayName := catalogItem.DisplayName
		if displayName == "" {
			displayName = catalogItem.Name
		}
		logging.Warn("Not enough data to check the current status:", displayName)
	}
	return actionNeeded, nil
}

// getLocalInstalledVersion attempts to find the installed version (via registry or file metadata).
// Returns an empty string if the item is not found locally.
func getLocalInstalledVersion(item catalog.Item) (string, error) {
	logging.Info("Checking local installed version", "itemName", item.Name)
	if len(RegistryItems) == 0 {
		var err error
		RegistryItems, err = getUninstallKeys()
		if err != nil {
			return "", err
		}
	}

	// In case the name doesn’t match directly, also try partial matching across RegistryItems
	for _, regApp := range RegistryItems {
		if regApp.Name == item.Name {
			logging.Info("Registry match found", "itemName", item.Name, "version", regApp.Version)
			return regApp.Version, nil
		} else if strings.Contains(regApp.Name, item.Name) {
			logging.Info("Partial registry match in getLocalInstalledVersion", "itemName", item.Name, "registryEntry", regApp.Name)
			return regApp.Version, nil
		}
	}

	if item.Installer.Type == "msi" && item.Installer.ProductCode != "" {
		if ver := findMsiVersion(item.Installer.ProductCode); ver != "" {
			logging.Info("MSI product code match found", "itemName", item.Name, "version", ver)
			return ver, nil
		}
	}

	// Fall back to file-based path check if provided
	for _, fc := range item.Check.File {
		if fc.Path != "" {
			meta := GetFileMetadata(fc.Path)
			if meta.versionString != "" {
				logging.Info("File-based version found", "filePath", fc.Path, "version", meta.versionString)
				return meta.versionString, nil
			}
		}
	}

	return "", nil
}

// checkInstalls iterates through the 'installs' array in the catalog Item
// and returns true if we need to install/update.
func checkInstalls(item catalog.Item, installType string) (bool, error) {
	if len(item.Installs) == 0 {
		// If no installs array, no action from this function
		return false, nil
	}

	var actionNeeded bool

	for _, install := range item.Installs {
		// For now we only handle files. Extend as needed.
		if strings.ToLower(install.Type) == "file" {
			// Does the file exist?
			fi, err := os.Stat(install.Path)
			if err != nil {
				if os.IsNotExist(err) {
					// File missing => if installType is 'install' or 'update' => we do it
					if installType == "install" || installType == "update" {
						actionNeeded = true
						break
					}
					// If it's an uninstall, missing file is no action needed
					continue
				} else {
					return false, fmt.Errorf("error checking file: %s: %v", install.Path, err)
				}
			} else {
				// File exists
				if installType == "uninstall" {
					// we want to remove it
					actionNeeded = true
					break
				}

				// If we have an md5checksum, verify
				if install.MD5Checksum != "" {
					match, err := verifyMD5(install.Path, install.MD5Checksum)
					if err != nil {
						return false, fmt.Errorf("failed md5 check: %v", err)
					}
					if !match {
						// mismatch => we need to re-install or update
						actionNeeded = true
						break
					}
				}

				// If we have a version, compare
				if install.Version != "" {
					fileVersion, err := getFileVersion(install.Path)
					if err != nil {
						// If we can't get a version, we consider that a mismatch => install
						actionNeeded = true
						break
					}
					// Compare semantic or simple string
					if isOlderVersion(fileVersion, install.Version) {
						actionNeeded = true
						break
					}
				}
			}
		}
	}

	return actionNeeded, nil
}

// verifyMD5 returns true if the file's md5 matches the expected string.
func verifyMD5(filePath, expected string) (bool, error) {
	f, err := os.Open(filePath)
	if err != nil {
		return false, err
	}
	defer f.Close()

	hasher := md5.New()
	if _, err := io.Copy(hasher, f); err != nil {
		return false, err
	}

	computed := hex.EncodeToString(hasher.Sum(nil))
	// compare in lowercase
	return strings.EqualFold(computed, expected), nil
}

// getFileVersion can be as simple as reading file metadata or
// just returning an empty string if we don't track versions.
func getFileVersion(filePath string) (string, error) {
	// For EXEs, you can use the same approach as GetFileMetadata from your code.
	// Example:
	metadata := GetFileMetadata(filePath)
	if metadata.versionString == "" {
		// or parse resources, or custom logic
		return "", nil
	}
	return metadata.versionString, nil
}

// isOlderVersion compares two version strings (similar to code you already have).
func isOlderVersion(local, remote string) bool {
	// reuse your existing version comparison logic, e.g.
	// 'github.com/hashicorp/go-version' or fallback to simple string compare
	vLocal, errL := version.NewVersion(local)
	vRemote, errR := version.NewVersion(remote)
	if errL != nil || errR != nil {
		// fallback to naive string
		return local < remote
	}
	return vLocal.LessThan(vRemote)
}

func checkMsiProductCode(productCode, checkVersion string) (bool, bool) {
	// This function queries the MSI registry for productCode and compares its version
	installedVersionStr := findMsiVersion(productCode)
	if installedVersionStr == "" {
		return false, false
	}

	installedVersion, err := version.NewVersion(installedVersionStr)
	if err != nil {
		return false, false
	}

	checkVer, err := version.NewVersion(checkVersion)
	if err != nil {
		return false, false
	}

	versionMatch := !installedVersion.LessThan(checkVer)
	return true, versionMatch
}

// findMsiVersion retrieves the installed version of the MSI package using the productCode.
func findMsiVersion(productCode string) string {
	regPath := fmt.Sprintf("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\%s", productCode)
	versionStr, err := getRegistryValue(regPath, "DisplayVersion")
	if err != nil {
		return ""
	}
	return versionStr
}

// getRegistryValue retrieves a string value from the specified registry key.
func getRegistryValue(keyPath, valueName string) (string, error) {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, keyPath, registry.QUERY_VALUE)
	if err != nil {
		return "", err
	}
	defer k.Close()

	val, _, err := k.GetStringValue(valueName)
	if err != nil {
		return "", err
	}
	return val, nil
}
