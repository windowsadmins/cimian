// pkg/status/status.go - functions for managing package status.

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
	"runtime"
	"strings"

	goversion "github.com/hashicorp/go-version"
	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/logging"
	cimiversion "github.com/windowsadmins/cimian/pkg/version"
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
	localNormalized, remoteNormalized := cimiversion.Normalize(local), cimiversion.Normalize(remote)

	vLocal, errLocal := goversion.NewVersion(localNormalized)
	vRemote, errRemote := goversion.NewVersion(remoteNormalized)

	if errLocal != nil || errRemote != nil {
		logging.Debug("Parse error during version normalization",
			"localOriginal", local,
			"remoteOriginal", remote,
			"localNormalized", localNormalized,
			"remoteNormalized", remoteNormalized,
			"errLocal", errLocal,
			"errRemote", errRemote,
		)
		return false
	}
	return vLocal.LessThan(vRemote)
}

// getSystemArchitecture returns a normalized string for the local system arch
func GetSystemArchitecture() string {
	arch := runtime.GOARCH
	switch arch {
	case "amd64", "x86_64":
		return "x64"
	case "386":
		return "x86"
	default:
		// e.g. "arm64", or any other
		return arch
	}
}

// SupportsArchitecture checks if the systemArch is one of item.SupportedArch
func SupportsArchitecture(item catalog.Item, sysArch string) bool {
	// If the item has no “supported_arch” set, maybe default to “true”
	if len(item.SupportedArch) == 0 {
		return true
	}
	sysArchNormalized := normalizeArch(sysArch)

	for _, arch := range item.SupportedArch {
		if normalizeArch(arch) == sysArchNormalized {
			return true
		}
	}
	return false
}

// optional helper to handle synonyms
func normalizeArch(arch string) string {
	arch = strings.ToLower(arch)
	if arch == "amd64" || arch == "x86_64" {
		return "x64"
	}
	if arch == "386" {
		return "x86"
	}
	return arch
}

// CheckStatus determines if `catalogItem` requires an install, update, or uninstall.
//
// Returns (bool, error) => bool means “true => perform action,” or “false => skip.”
func CheckStatus(catalogItem catalog.Item, installType, cachePath string) (bool, error) {
	logging.Debug("CheckStatus starting", "item", catalogItem.Name, "installType", installType)

	if catalogItem.Check.Script != "" {
		logging.Info("Checking status via script", "item", catalogItem.Name)
		return checkScript(catalogItem, cachePath, installType)
	}

	if len(catalogItem.Check.File) > 0 {
		logging.Info("Checking status via file checks", "item", catalogItem.Name)
		return checkPath(catalogItem)
	}

	if catalogItem.Check.Registry.Version != "" {
		logging.Info("Checking status via registry checks", "item", catalogItem.Name)
		return checkRegistry(catalogItem, installType)
	}

	localVersion, err := getLocalInstalledVersion(catalogItem)
	if err != nil {
		logging.Warn("Failed retrieving local version, assuming action needed",
			"item", catalogItem.Name, "error", err)
		return true, err
	}

	sysArch := GetSystemArchitecture()
	if !SupportsArchitecture(catalogItem, sysArch) {
		logging.Warn("Architecture mismatch, skipping",
			"item", catalogItem.Name,
			"systemArch", sysArch,
			"supportedArch", catalogItem.SupportedArch,
		)
		return false, nil
	}

	logging.Debug("Comparing versions explicitly",
		"item", catalogItem.Name,
		"localVersion", localVersion,
		"repoVersion", catalogItem.Version,
	)

	switch installType {
	case "install", "update":
		if localVersion == "" {
			logging.Info("No local version found, installation needed", "item", catalogItem.Name)
			return true, nil
		}
		if IsOlderVersion(localVersion, catalogItem.Version) {
			logging.Info("Local version outdated, update needed",
				"item", catalogItem.Name,
				"localVersion", localVersion,
				"repoVersion", catalogItem.Version,
			)
			return true, nil
		}
		if IsOlderVersion(catalogItem.Version, localVersion) {
			logging.Warn("Refusing downgrade; local version newer",
				"item", catalogItem.Name,
				"localVersion", localVersion,
				"repoVersion", catalogItem.Version,
			)
			return false, nil
		}

		logging.Info("Versions match exactly; performing file presence, hash, and metadata verification",
			"item", catalogItem.Name,
			"localVersion", localVersion,
		)
		needed, err := checkInstalls(catalogItem, installType)
		if err != nil {
			logging.Warn("Error in file/install checks, assuming update needed",
				"item", catalogItem.Name, "error", err)
			return true, err
		}
		if needed {
			logging.Info("File/install checks indicate action needed",
				"item", catalogItem.Name,
			)
			return true, nil
		}

		logging.Debug("All explicit checks passed, no update needed", "item", catalogItem.Name)
		return false, nil

	case "uninstall":
		needed := localVersion != ""
		logging.Debug("Uninstall decision based on local version",
			"item", catalogItem.Name, "installed", needed)
		return needed, nil

	default:
		logging.Warn("Unknown install type provided",
			"item", catalogItem.Name, "installType", installType)
		return false, nil
	}
}

// readInstalledVersionFromRegistry returns the version we stored
func readInstalledVersionFromRegistry(name string) (string, error) {
	regPath := `Software\ManagedInstalls\` + name
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, regPath, registry.QUERY_VALUE)
	if err != nil {
		return "", err
	}
	defer k.Close()

	ver, _, err := k.GetStringValue("Version")
	if err != nil {
		return "", err
	}
	return ver, nil
}

// getLocalInstalledVersion attempts to find the installed version from registry or file metadata.
func getLocalInstalledVersion(item catalog.Item) (string, error) {
	logging.Debug("Reading local installed version from registry (if any)",
		"item", item.Name,
		"installerType", item.Installer.Type,
	)

	// 1) FIRST, check the Cimian-managed key (i.e. readInstalledVersionFromRegistry).
	//    This is where you store your "Wrote local installed version to registry item=Git version=2.47.1.1" etc.
	cimianVersion, errLocalReg := readInstalledVersionFromRegistry(item.Name)
	if errLocalReg == nil && cimianVersion != "" {
		logging.Info("Found Cimian-managed registry version",
			"item", item.Name,
			"registryVersion", cimianVersion,
		)
		return cimianVersion, nil
	}
	if errLocalReg != nil {
		logging.Debug("No Cimian version found in registry or error reading it",
			"item", item.Name,
			"error", errLocalReg,
		)
	}

	// 2) If not found in Cimian’s own key, proceed with enumerating the Windows Uninstall keys.
	if len(RegistryItems) == 0 {
		var err error
		RegistryItems, err = getUninstallKeys()
		if err != nil {
			return "", err
		}
	}

	for _, regApp := range RegistryItems {
		// EXACT MATCH
		if regApp.Name == item.Name {
			logging.Info("Exact registry match found",
				"item", item.Name,
				"registryVersion", regApp.Version,
			)
			return regApp.Version, nil
		}
		// PARTIAL MATCH
		if strings.Contains(regApp.Name, item.Name) {
			logging.Info("Partial registry match found",
				"item", item.Name,
				"registryEntry", regApp.Name,
				"registryVersion", regApp.Version,
			)
			return regApp.Version, nil
		}
	}

	// 3) If it's an MSI with a product code, check that:
	if item.Installer.Type == "msi" && item.Installer.ProductCode != "" {
		v := findMsiVersion(item.Installer.ProductCode)
		if v != "" {
			logging.Info("MSI product code match found",
				"item", item.Name,
				"registryVersion", v,
			)
			return v, nil
		}
	}

	// 4) No match => treat as not installed
	logging.Debug("No registry version found, returning empty",
		"item", item.Name,
	)
	return "", nil
}

func checkPath(catalogItem catalog.Item) (bool, error) {
	logging.Debug("File-based check initiated", "item", catalogItem.Name)

	for _, checkFile := range catalogItem.Check.File {
		path := filepath.Clean(checkFile.Path)
		_, err := os.Stat(path)

		if err != nil {
			if os.IsNotExist(err) {
				logging.Info("File missing, installation required", "item", catalogItem.Name, "path", path)
				return true, nil
			}
			continue
		}

		if checkFile.Hash != "" && !download.Verify(path, checkFile.Hash) {
			logging.Info("MD5 mismatch, installation/update required", "item", catalogItem.Name, "path", path)
			return true, nil
		}

		if checkFile.Version != "" {
			fileMetadata := GetFileMetadata(path)
			if IsOlderVersion(fileMetadata.versionString, checkFile.Version) {
				logging.Info("File version outdated, action needed",
					"item", catalogItem.Name,
					"path", path,
					"fileVersion", fileMetadata.versionString,
					"requiredVersion", checkFile.Version,
				)
				return true, nil
			}
		}
	}

	logging.Debug("File checks passed, no action required", "item", catalogItem.Name)
	return false, nil
}

// checkInstalls loops through catalogItem.Installs to see if the item needs an action.
func checkInstalls(item catalog.Item, installType string) (bool, error) {
	if len(item.Installs) == 0 {
		return false, nil
	}

	for _, install := range item.Installs {
		if strings.ToLower(install.Type) == "file" {
			fileInfo, err := os.Stat(install.Path)
			if err != nil {
				if os.IsNotExist(err) {
					logging.Info("Required file is missing, action needed",
						"item", item.Name, "missingPath", install.Path)
					return true, nil
				}
				logging.Warn("Unexpected error checking file existence",
					"item", item.Name, "path", install.Path, "error", err)
				return false, err
			}

			if installType == "uninstall" && fileInfo != nil {
				logging.Info("File present, uninstall required",
					"item", item.Name, "path", install.Path)
				return true, nil
			}

			if install.MD5Checksum != "" {
				match, computedMD5, err := verifyMD5WithHash(install.Path, install.MD5Checksum)
				if err != nil {
					logging.Warn("MD5 verification error",
						"item", item.Name, "path", install.Path, "error", err)
					return true, err
				}
				if !match {
					logging.Info("MD5 mismatch, action required",
						"item", item.Name,
						"path", install.Path,
						"localHash", computedMD5,
						"expectedHash", install.MD5Checksum,
					)
					return true, nil
				}
			}

			if install.Version != "" {
				fileVersion, err := getFileVersion(install.Path)
				if err != nil || fileVersion == "" {
					logging.Info("File version metadata unavailable or unreadable, action needed",
						"item", item.Name, "path", install.Path, "error", err)
					return true, nil
				}
				if IsOlderVersion(fileVersion, install.Version) {
					logging.Info("Installed file version outdated, action needed",
						"item", item.Name, "path", install.Path,
						"fileVersion", fileVersion,
						"requiredVersion", install.Version,
					)
					return true, nil
				}
			}
		}
	}
	logging.Debug("Install checks explicitly passed, no action needed", "item", item.Name)
	return false, nil
}

// verifyMD5WithHash computes MD5 hash and returns match status and computed hash explicitly.
func verifyMD5WithHash(filePath, expected string) (bool, string, error) {
	f, err := os.Open(filePath)
	if err != nil {
		return false, "", err
	}
	defer f.Close()

	hasher := md5.New()
	if _, err := io.Copy(hasher, f); err != nil {
		return false, "", err
	}

	computed := hex.EncodeToString(hasher.Sum(nil))
	return strings.EqualFold(computed, expected), computed, nil
}

// getFileVersion returns file version if any (or empty if unknown).
func getFileVersion(filePath string) (string, error) {
	metadata := GetFileMetadata(filePath)
	if metadata.versionString == "" {
		return "", nil
	}
	return metadata.versionString, nil
}

// checkMsiProductCode queries registry for productCode, compares vs. checkVersion
func checkMsiProductCode(productCode, checkVersion string) (bool, bool) {
	installedVersionStr := findMsiVersion(productCode)
	if installedVersionStr == "" {
		return false, false
	}

	installedVersion, err := goversion.NewVersion(installedVersionStr)
	if err != nil {
		logging.Warn("Could not parse installed MSI version",
			"productCode", productCode,
			"installedVersion", installedVersionStr,
			"error", err,
		)
		return true, false // Installed but unparseable version: treat as needing update
	}

	checkVer, err := goversion.NewVersion(checkVersion)
	if err != nil {
		logging.Warn("Could not parse required MSI version",
			"productCode", productCode,
			"requiredVersion", checkVersion,
			"error", err,
		)
		return true, false // Installed but unparseable required version: treat as needing update
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
func checkRegistry(catalogItem catalog.Item, _ string) (bool, error) {
	logging.Debug("Registry check started", "item", catalogItem.Name)

	checkReg := catalogItem.Check.Registry
	catalogVersion, err := goversion.NewVersion(checkReg.Version)
	if err != nil {
		logging.Warn("Failed parsing registry version, assuming action needed",
			"item", catalogItem.Name, "version", checkReg.Version, "error", err)
		return true, err
	}

	if len(RegistryItems) == 0 {
		RegistryItems, err = getUninstallKeys()
		if err != nil {
			logging.Warn("Failed retrieving uninstall keys, action needed",
				"item", catalogItem.Name, "error", err)
			return true, err
		}
	}

	var regVersionFound string
	registryMatched := false

	for _, regItem := range RegistryItems {
		if regItem.Name == checkReg.Name || strings.Contains(regItem.Name, checkReg.Name) {
			registryMatched = true
			regVersionFound = regItem.Version
			logging.Debug("Registry match found",
				"catalogName", checkReg.Name,
				"registryName", regItem.Name,
				"registryVersion", regItem.Version,
			)
			regVersion, err := goversion.NewVersion(regItem.Version)
			if err != nil || regVersion.LessThan(catalogVersion) {
				logging.Info("Registry version outdated, action needed",
					"item", catalogItem.Name,
					"registryVersion", regItem.Version,
					"requiredVersion", checkReg.Version,
				)
				return true, nil
			}
			break // Do not return yet; explicitly proceed to MSI check
		}
	}

	if catalogItem.Installer.Type == "msi" && catalogItem.Installer.ProductCode != "" {
		logging.Debug("Explicitly checking MSI ProductCode",
			"item", catalogItem.Name,
			"productCode", catalogItem.Installer.ProductCode,
		)
		installed, versionMatch := checkMsiProductCode(catalogItem.Installer.ProductCode, checkReg.Version)

		if !installed {
			logging.Info("MSI product code not installed; action required", "item", catalogItem.Name)
			return true, nil
		}

		if !versionMatch {
			logging.Info("MSI product code version outdated; action required",
				"item", catalogItem.Name,
				"requiredVersion", checkReg.Version,
			)
			return true, nil
		}

		logging.Debug("MSI product code matches required version; no MSI action needed",
			"item", catalogItem.Name,
		)
	} else if registryMatched {
		// Explicitly confirm that registry check alone is sufficient.
		logging.Debug("Registry check alone sufficient, no MSI installer present",
			"item", catalogItem.Name, "registryVersion", regVersionFound,
		)
		return false, nil
	} else {
		// Neither registry nor MSI match
		logging.Info("No registry or MSI match found; action needed", "item", catalogItem.Name)
		return true, nil
	}

	return false, nil
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
