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

// WindowsMetadata holds extended metadata from file version checks
type WindowsMetadata struct {
	productName   string
	versionString string
	versionMajor  int
	versionMinor  int
	versionPatch  int
	versionBuild  int
}

// RegistryItems caches the installed registry software for performance
var RegistryItems map[string]RegistryApplication

// execCommand is abstracted for testing
var execCommand = exec.Command

// GetInstalledVersion is an **exported** convenience wrapper to retrieve
// the installed version of a catalog item, or return an empty string if not found.
func GetInstalledVersion(item catalog.Item) (string, error) {
	return getLocalInstalledVersion(item)
}

// IsOlderVersion is an **exported** convenience wrapper to compare versions
// (returning true if `local` is strictly older than `remote`).
func IsOlderVersion(local, remote string) bool {
	return isOlderVersion(local, remote)
}

// CheckStatus determines if a catalogItem requires an install, update, or uninstall action.
// ...
func CheckStatus(catalogItem catalog.Item, installType, cachePath string) (bool, error) {
	// See if a script check is present
	if catalogItem.Check.Script != "" {
		logging.Info("Checking status via script:", catalogItem.DisplayName)
		return checkScript(catalogItem, cachePath, installType)
	}

	// See if a file check is defined
	if len(catalogItem.Check.File) > 0 {
		logging.Info("Checking status via file:", catalogItem.DisplayName)
		return checkPath(catalogItem, installType)
	}

	// See if a registry check is defined
	if catalogItem.Check.Registry.Version != "" {
		logging.Info("Checking status via registry:", catalogItem.DisplayName)
		return checkRegistry(catalogItem, installType)
	}

	// Fallback: check the .Installs array or simple version logic
	needed, err := checkInstalls(catalogItem, installType)
	if err != nil {
		return true, err
	}
	if needed {
		return true, nil
	}

	// Additional fallback:
	// If in managed_installs => install if not installed or older
	// If in managed_updates => update only if installed & older
	// If in managed_uninstalls => remove if installed
	localVersion, err := getLocalInstalledVersion(catalogItem)
	if err != nil {
		return true, fmt.Errorf("unable to detect local version: %v", err)
	}

	// If localVersion is "", that means “not installed at all”
	// => for “install” or “update,” we do want to proceed installing if it’s empty
	// => for “uninstall,” we only do it if localVersion != ""
	switch installType {
	case "install":
		if localVersion == "" {
			// Not installed => need install
			return true, nil
		}
		// If we do have localVersion, check if it’s older
		if isOlderVersion(localVersion, catalogItem.Version) {
			return true, nil
		}
		// Otherwise same or newer => no action
		return false, nil

	case "update":
		if localVersion == "" {
			// Not installed => no update
			return false, nil
		}
		// If installed but older => do update
		if isOlderVersion(localVersion, catalogItem.Version) {
			return true, nil
		}
		// Otherwise no action
		return false, nil

	case "uninstall":
		// Uninstall only if localVersion != ""
		return (localVersion != ""), nil

	default:
		// fallback
		return false, nil
	}
}

// getLocalInstalledVersion attempts to find the installed version from registry or file metadata.
func getLocalInstalledVersion(item catalog.Item) (string, error) {
	logging.Info("Checking local installed version", "itemName", item.Name)

	if len(RegistryItems) == 0 {
		var err error
		RegistryItems, err = getUninstallKeys()
		if err != nil {
			return "", err
		}
	}

	// Try an exact match or partial match in RegistryItems
	for _, regApp := range RegistryItems {
		if regApp.Name == item.Name {
			logging.Info("Exact registry match found", "itemName", item.Name, "version", regApp.Version)
			return regApp.Version, nil
		} else if strings.Contains(regApp.Name, item.Name) {
			logging.Info("Partial registry match found", "itemName", item.Name, "registryEntry", regApp.Name)
			return regApp.Version, nil
		}
	}

	// If this is an MSI with product code
	if item.Installer.Type == "msi" && item.Installer.ProductCode != "" {
		if ver := findMsiVersion(item.Installer.ProductCode); ver != "" {
			logging.Info("MSI product code match found", "itemName", item.Name, "version", ver)
			return ver, nil
		}
	}

	// Fall back to file-based checks
	for _, fc := range item.Check.File {
		if fc.Path != "" {
			meta := GetFileMetadata(fc.Path)
			if meta.versionString != "" {
				logging.Info("File-based version found", "filePath", fc.Path, "version", meta.versionString)
				return meta.versionString, nil
			}
		}
	}

	// Not found => empty version
	return "", nil
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

// checkInstalls loops through catalogItem.Installs to see if the item needs an action.
func checkInstalls(item catalog.Item, installType string) (bool, error) {
	if len(item.Installs) == 0 {
		return false, nil
	}

	for _, install := range item.Installs {
		if strings.ToLower(install.Type) == "file" {
			_, err := os.Stat(install.Path)
			if err != nil {
				if os.IsNotExist(err) {
					if installType == "install" || installType == "update" {
						// Missing file => need to install or update
						return true, nil
					}
					// If uninstall => file missing => no action
					continue
				}
				return false, fmt.Errorf("error checking file: %s: %v", install.Path, err)
			} else {
				// If the file exists => maybe uninstall
				if installType == "uninstall" {
					return true, nil
				}
				// Check hash
				if install.MD5Checksum != "" {
					match, err := verifyMD5(install.Path, install.MD5Checksum)
					if err != nil {
						return false, fmt.Errorf("failed md5 check: %v", err)
					}
					if !match {
						// mismatch => reinstall or update
						return true, nil
					}
				}
				// Check version
				if install.Version != "" {
					fileVersion, err := getFileVersion(install.Path)
					if err != nil {
						// If version check fails => treat as mismatch => needs action
						return true, nil
					}
					if isOlderVersion(fileVersion, install.Version) {
						return true, nil
					}
				}
			}
		}
	}
	return false, nil
}

// verifyMD5 returns true if the file's MD5 matches expected.
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
	return strings.EqualFold(computed, expected), nil
}

// getFileVersion returns file version if any (or empty if unknown).
func getFileVersion(filePath string) (string, error) {
	metadata := GetFileMetadata(filePath)
	if metadata.versionString == "" {
		return "", nil
	}
	return metadata.versionString, nil
}

// isOlderVersion checks if `local < remote` using semantic versioning where possible.
func isOlderVersion(local, remote string) bool {
	vLocal, errL := version.NewVersion(local)
	vRemote, errR := version.NewVersion(remote)
	if errL != nil || errR != nil {
		// fallback to naive string compare
		return local < remote
	}
	return vLocal.LessThan(vRemote)
}

// checkMsiProductCode queries registry for productCode, compares vs. checkVersion
func checkMsiProductCode(productCode, checkVersion string) (bool, bool) {
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

// findMsiVersion retrieves the DisplayVersion from registry for the MSI productCode
func findMsiVersion(productCode string) string {
	regPath := fmt.Sprintf("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\%s", productCode)
	versionStr, err := getRegistryValue(regPath, "DisplayVersion")
	if err != nil {
		return ""
	}
	return versionStr
}

// getRegistryValue reads a string value from local-machine registry
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

// checkRegistry checks if an item is installed/updated in local registry
func checkRegistry(catalogItem catalog.Item, installType string) (bool, error) {
	checkReg := catalogItem.Check.Registry
	catalogVersion, err := version.NewVersion(checkReg.Version)
	if err != nil {
		logging.Warn("Unable to parse new version:", checkReg.Version, err)
	}
	if len(RegistryItems) == 0 {
		RegistryItems, err = getUninstallKeys()
		if err != nil {
			return true, err
		}
	}
	var installed bool
	var versionMatch bool

	for _, regItem := range RegistryItems {
		// Try exact or partial
		if regItem.Name == checkReg.Name {
			logging.Info("Exact registry match", "catalogName", checkReg.Name, "registryName", regItem.Name)
			installed = true
			currentVersion, err := version.NewVersion(regItem.Version)
			if err == nil && !currentVersion.LessThan(catalogVersion) {
				versionMatch = true
			}
			break
		} else if strings.Contains(regItem.Name, checkReg.Name) {
			logging.Info("Partial registry match", "catalogName", checkReg.Name, "registryName", regItem.Name)
			installed = true
			currentVersion, err := version.NewVersion(regItem.Version)
			if err == nil && !currentVersion.LessThan(catalogVersion) {
				versionMatch = true
			}
			break
		}
	}

	// Also handle MSI productCode
	if checkReg.Name == "" && catalogItem.Installer.Type == "msi" && catalogItem.Installer.ProductCode != "" {
		installed, versionMatch = checkMsiProductCode(catalogItem.Installer.ProductCode, checkReg.Version)
		logging.Info("Compare registry vs catalog", "catalogVersion", checkReg.Version, "installed", installed, "versionMatch", versionMatch)
	}

	switch {
	case installType == "update" && !installed:
		return false, nil
	case installType == "uninstall":
		return installed, nil
	case installed && versionMatch:
		return false, nil
	default:
		return true, nil
	}
}

// checkScript runs a PowerShell script to decide if an item is installed.
func checkScript(catalogItem catalog.Item, cachePath string, installType string) (bool, error) {
	tmpScript := filepath.Join(cachePath, "tmpCheckScript.ps1")
	if err := ioutil.WriteFile(tmpScript, []byte(catalogItem.Check.Script), 0755); err != nil {
		return true, fmt.Errorf("failed to write check script: %w", err)
	}
	defer os.Remove(tmpScript)

	psExe := filepath.Join(os.Getenv("WINDIR"), "system32", "WindowsPowershell", "v1.0", "powershell.exe")
	psArgs := []string{"-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", tmpScript}

	cmd := execCommand(psExe, psArgs...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	err := cmd.Run()
	cmdSuccess := cmd.ProcessState != nil && cmd.ProcessState.Success()
	outStr, errStr := stdout.String(), stderr.String()

	logging.Debug("InstallCheck script output", "stdout", outStr, "stderr", errStr, "error", err)

	switch installType {
	case "uninstall":
		// If script exit code == 0 => script says "not installed" => no uninstall needed
		// so we invert the logic. Zero means "no uninstall needed"
		return !cmdSuccess, nil
	default:
		// For install or update: exit code == 0 => "not installed => install needed"
		return cmdSuccess, nil
	}
}

// getUninstallKeys enumerates registry for installed apps
func getUninstallKeys() (map[string]RegistryApplication, error) {
	installedApps := make(map[string]RegistryApplication)
	regPaths := []string{
		`Software\Microsoft\Windows\CurrentVersion\Uninstall`,
		`Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall`,
	}
	for _, rPath := range regPaths {
		key, err := registry.OpenKey(registry.LOCAL_MACHINE, rPath, registry.READ)
		if err != nil {
			logging.Warn("Unable to read registry key:", err)
			continue
		}
		defer key.Close()

		subKeys, err := key.ReadSubKeyNames(0)
		if err != nil {
			logging.Warn("Unable to read sub keys:", err)
			continue
		}
		for _, subKey := range subKeys {
			fullPath := rPath + `\` + subKey
			subKeyObj, err := registry.OpenKey(registry.LOCAL_MACHINE, fullPath, registry.READ)
			if err != nil {
				logging.Warn("Unable to open subKey:", err)
				continue
			}
			defer subKeyObj.Close()

			valNames, err := subKeyObj.ReadValueNames(0)
			if err != nil {
				logging.Warn("Unable to read value names:", err)
				continue
			}

			if !checkValues(valNames) {
				// skip if missing critical fields
				continue
			}
			var app RegistryApplication
			app.Key = fullPath

			if name, _, err := subKeyObj.GetStringValue("DisplayName"); err == nil {
				app.Name = name
			}
			if versionStr, _, err := subKeyObj.GetStringValue("DisplayVersion"); err == nil {
				app.Version = versionStr
			}
			if uninstallStr, _, err := subKeyObj.GetStringValue("UninstallString"); err == nil {
				app.Uninstall = uninstallStr
			}
			if app.Name != "" {
				installedApps[app.Name] = app
			}
		}
	}
	return installedApps, nil
}

// checkValues ensures the subkey has at least DisplayName / DisplayVersion / UninstallString
func checkValues(values []string) bool {
	var haveName, haveVersion, haveUninstall bool
	for _, v := range values {
		switch v {
		case "DisplayName":
			haveName = true
		case "DisplayVersion":
			haveVersion = true
		case "UninstallString":
			haveUninstall = true
		}
	}
	return haveName && haveVersion && haveUninstall
}
