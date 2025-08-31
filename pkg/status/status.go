// pkg/status/status.go - functions for managing package status.

package status

import (
	"bytes"
	"crypto/md5"
	"crypto/sha1"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"

	goversion "github.com/hashicorp/go-version"
	"github.com/shirou/gopsutil/v3/host"
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

// CheckResult represents the result of a status check with both action and status information
type CheckResult struct {
	NeedsAction bool   // Whether action is needed (install/update)
	Status      string // The actual status: "installed", "warning", "error", "pending", "removed"
	Reason      string // Human-readable reason for the status
	Error       error  // Any error that occurred
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
	// On Windows, use PROCESSOR_ARCHITECTURE to get the actual system architecture
	// rather than runtime.GOARCH which reflects the binary's compilation target
	if arch := os.Getenv("PROCESSOR_ARCHITECTURE"); arch != "" {
		switch strings.ToUpper(arch) {
		case "AMD64", "X86_64":
			return "x64"
		case "X86", "386":
			return "x86"
		case "ARM64":
			return "arm64"
		default:
			return strings.ToLower(arch)
		}
	}

	// Fallback to runtime.GOARCH if environment variable is not available
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
	logging.Debug("CheckStatus starting", "item", catalogItem.Name, "installType", installType, "OnDemand", catalogItem.OnDemand)

	// OnDemand items should always be available for installation/execution
	// They are never considered "installed" so they can be run repeatedly
	if catalogItem.OnDemand && (installType == "install" || installType == "update") {
		logging.Info("OnDemand item requested - always available for execution", "item", catalogItem.Name)
		return true, nil
	}

	// OnDemand items cannot be uninstalled since they're never considered "installed"
	if catalogItem.OnDemand && installType == "uninstall" {
		logging.Info("OnDemand item cannot be uninstalled (never marked as installed)", "item", catalogItem.Name)
		return false, nil
	}

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

	// Check OS version compatibility
	osCompatible, err := SupportsOSVersion(catalogItem)
	if err != nil {
		logging.Warn("Failed to check OS version compatibility, proceeding anyway",
			"item", catalogItem.Name, "error", err)
	} else if !osCompatible {
		logging.Warn("OS version incompatible, skipping",
			"item", catalogItem.Name,
			"minVersion", catalogItem.MinOSVersion,
			"maxVersion", catalogItem.MaxOSVersion,
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
			logging.Info("Not installed on device", "item", catalogItem.Name)
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
			logging.Info("Installation verification checks indicate reinstallation needed",
				"item", catalogItem.Name,
			)
			return true, nil
		}

		logging.Debug("All explicit checks passed, no update needed", "item", catalogItem.Name)
		return false, nil

	case "uninstall":
		// Check if item is uninstallable
		if !catalogItem.IsUninstallable() {
			logging.Info("Item is marked as not uninstallable, skipping",
				"item", catalogItem.Name)
			return false, nil
		}

		// First check if we have an uninstaller array - use it for more precise uninstall detection
		if len(catalogItem.Uninstaller) > 0 {
			needed, err := checkUninstaller(catalogItem, installType)
			if err != nil {
				logging.Warn("Error in uninstaller array checks, falling back to registry check",
					"item", catalogItem.Name, "error", err)
			} else {
				logging.Debug("Uninstall decision based on uninstaller array",
					"item", catalogItem.Name, "needed", needed)
				return needed, nil
			}
		}

		// Fall back to checking installs array if no uninstaller array is defined
		if len(catalogItem.Installs) > 0 {
			needed, err := checkInstalls(catalogItem, installType)
			if err != nil {
				logging.Warn("Error in installs array checks for uninstall, falling back to registry check",
					"item", catalogItem.Name, "error", err)
			} else {
				logging.Debug("Uninstall decision based on installs array",
					"item", catalogItem.Name, "needed", needed)
				return needed, nil
			}
		}

		// Final fallback to registry-based check
		needed := localVersion != ""
		logging.Debug("Uninstall decision based on local version registry check",
			"item", catalogItem.Name, "installed", needed)
		return needed, nil

	default:
		logging.Warn("Unknown install type provided",
			"item", catalogItem.Name, "installType", installType)
		return false, nil
	}
}

// CheckStatusWithResult performs status checking and returns detailed result information
// This function provides proper status classification for all scenarios including warnings
func CheckStatusWithResult(catalogItem catalog.Item, installType, cachePath string) CheckResult {
	return CheckStatusWithResultQuiet(catalogItem, installType, cachePath, false)
}

// CheckStatusWithResultQuiet performs status checking with optional quiet mode (no debug logging)
func CheckStatusWithResultQuiet(catalogItem catalog.Item, installType, cachePath string, quiet bool) CheckResult {
	if !quiet {
		logging.Debug("CheckStatusWithResult starting", "item", catalogItem.Name, "installType", installType, "OnDemand", catalogItem.OnDemand)
	}

	// OnDemand items should always be available for installation/execution
	if catalogItem.OnDemand && (installType == "install" || installType == "update") {
		logging.Info("OnDemand item requested - always available for execution", "item", catalogItem.Name)
		return CheckResult{
			NeedsAction: true,
			Status:      "pending",
			Reason:      "On-demand item ready for execution",
			Error:       nil,
		}
	}

	// OnDemand items cannot be uninstalled since they're never considered "installed"
	if catalogItem.OnDemand && installType == "uninstall" {
		logging.Info("OnDemand item cannot be uninstalled (never marked as installed)", "item", catalogItem.Name)
		return CheckResult{
			NeedsAction: false,
			Status:      "warning",
			Reason:      "On-demand items cannot be uninstalled",
			Error:       nil,
		}
	}

	// Handle special check types
	if catalogItem.Check.Script != "" {
		needsAction, err := checkScriptQuiet(catalogItem, cachePath, installType, quiet)
		status := "installed"
		reason := "Script check indicates installed"
		if needsAction {
			status = "pending"
			reason = "Script check indicates action needed"
		}
		if err != nil {
			status = "error"
			reason = fmt.Sprintf("Script check failed: %v", err)
		}
		return CheckResult{
			NeedsAction: needsAction,
			Status:      status,
			Reason:      reason,
			Error:       err,
		}
	}

	if len(catalogItem.Check.File) > 0 {
		needsAction, err := checkPathQuiet(catalogItem, quiet)
		status := "installed"
		reason := "File check indicates installed"
		if needsAction {
			status = "pending"
			reason = "File check indicates action needed"
		}
		if err != nil {
			status = "error"
			reason = fmt.Sprintf("File check failed: %v", err)
		}
		return CheckResult{
			NeedsAction: needsAction,
			Status:      status,
			Reason:      reason,
			Error:       err,
		}
	}

	if catalogItem.Check.Registry.Version != "" {
		needsAction, err := checkRegistryQuiet(catalogItem, installType, quiet)
		status := "installed"
		reason := "Registry check indicates installed"
		if needsAction {
			status = "pending"
			reason = "Registry check indicates action needed"
		}
		if err != nil {
			status = "error"
			reason = fmt.Sprintf("Registry check failed: %v", err)
		}
		return CheckResult{
			NeedsAction: needsAction,
			Status:      status,
			Reason:      reason,
			Error:       err,
		}
	}

	// Get local version
	var localVersion string
	var err error
	if quiet {
		localVersion, err = getLocalInstalledVersionQuiet(catalogItem)
	} else {
		localVersion, err = getLocalInstalledVersion(catalogItem)
	}
	if err != nil {
		if !quiet {
			logging.Warn("Failed retrieving local version, assuming action needed",
				"item", catalogItem.Name, "error", err)
		}
		return CheckResult{
			NeedsAction: true,
			Status:      "error",
			Reason:      fmt.Sprintf("Failed to get local version: %v", err),
			Error:       err,
		}
	}

	// Architecture compatibility check
	sysArch := GetSystemArchitecture()
	if !SupportsArchitecture(catalogItem, sysArch) {
		if !quiet {
			logging.Warn("Architecture mismatch, skipping",
				"item", catalogItem.Name,
				"systemArch", sysArch,
				"supportedArch", catalogItem.SupportedArch,
			)
		}
		return CheckResult{
			NeedsAction: false,
			Status:      "warning",
			Reason:      fmt.Sprintf("Architecture mismatch: system is %s, package requires %v", sysArch, catalogItem.SupportedArch),
			Error:       nil,
		}
	}

	// OS version compatibility check
	osCompatible, err := SupportsOSVersionQuiet(catalogItem, quiet)
	if err != nil {
		if !quiet {
			logging.Warn("Failed to check OS version compatibility, proceeding anyway",
				"item", catalogItem.Name, "error", err)
		}
	} else if !osCompatible {
		if !quiet {
			logging.Warn("OS version incompatible, skipping",
				"item", catalogItem.Name,
				"minVersion", catalogItem.MinOSVersion,
				"maxVersion", catalogItem.MaxOSVersion,
			)
		}
		return CheckResult{
			NeedsAction: false,
			Status:      "warning",
			Reason:      fmt.Sprintf("OS version incompatible (requires %s - %s)", catalogItem.MinOSVersion, catalogItem.MaxOSVersion),
			Error:       nil,
		}
	}

	if !quiet {
		logging.Debug("Comparing versions explicitly",
			"item", catalogItem.Name,
			"localVersion", localVersion,
			"repoVersion", catalogItem.Version,
		)
	}

	switch installType {
	case "install", "update":
		if localVersion == "" {
			if !quiet {
				logging.Info("Not installed on device", "item", catalogItem.Name)
			}
			return CheckResult{
				NeedsAction: true,
				Status:      "pending",
				Reason:      "Not installed",
				Error:       nil,
			}
		}
		if IsOlderVersion(localVersion, catalogItem.Version) {
			if !quiet {
				logging.Info("Local version outdated, update needed",
					"item", catalogItem.Name,
					"localVersion", localVersion,
					"repoVersion", catalogItem.Version,
				)
			}
			return CheckResult{
				NeedsAction: true,
				Status:      "pending",
				Reason:      fmt.Sprintf("Update needed: %s → %s", localVersion, catalogItem.Version),
				Error:       nil,
			}
		}
		if IsOlderVersion(catalogItem.Version, localVersion) {
			if !quiet {
				logging.Warn("Refusing downgrade; local version newer",
					"item", catalogItem.Name,
					"localVersion", localVersion,
					"repoVersion", catalogItem.Version,
				)
			}
			return CheckResult{
				NeedsAction: false,
				Status:      "warning",
				Reason:      fmt.Sprintf("Downgrade refused: local %s newer than repo %s", localVersion, catalogItem.Version),
				Error:       nil,
			}
		}

		if !quiet {
			logging.Info("Versions match exactly; performing file presence, hash, and metadata verification",
				"item", catalogItem.Name,
				"localVersion", localVersion,
			)
		}
		needed, err := checkInstallsQuiet(catalogItem, installType, quiet)
		if err != nil {
			if !quiet {
				logging.Warn("Error in file/install checks, assuming update needed",
					"item", catalogItem.Name, "error", err)
			}
			return CheckResult{
				NeedsAction: true,
				Status:      "error",
				Reason:      fmt.Sprintf("File verification failed: %v", err),
				Error:       err,
			}
		}
		if needed {
			if !quiet {
				logging.Info("Installation verification checks indicate reinstallation needed",
					"item", catalogItem.Name,
				)
			}
			return CheckResult{
				NeedsAction: true,
				Status:      "pending",
				Reason:      "File verification indicates reinstallation needed",
				Error:       nil,
			}
		}

		if !quiet {
			logging.Debug("All explicit checks passed, no update needed", "item", catalogItem.Name)
		}
		return CheckResult{
			NeedsAction: false,
			Status:      "installed",
			Reason:      "Successfully installed and verified",
			Error:       nil,
		}

	case "uninstall":
		// Check if item is uninstallable
		if !catalogItem.IsUninstallable() {
			if !quiet {
				logging.Info("Item is marked as not uninstallable, skipping",
					"item", catalogItem.Name)
			}
			return CheckResult{
				NeedsAction: false,
				Status:      "warning",
				Reason:      "Item marked as not uninstallable",
				Error:       nil,
			}
		}

		// Uninstall logic (simplified for this context)
		needed := localVersion != ""
		status := "removed"
		reason := "Not installed"
		if needed {
			status = "pending"
			reason = "Needs uninstallation"
		}
		return CheckResult{
			NeedsAction: needed,
			Status:      status,
			Reason:      reason,
			Error:       nil,
		}

	default:
		if !quiet {
			logging.Warn("Unknown install type provided",
				"item", catalogItem.Name, "installType", installType)
		}
		return CheckResult{
			NeedsAction: false,
			Status:      "error",
			Reason:      fmt.Sprintf("Unknown install type: %s", installType),
			Error:       fmt.Errorf("unknown install type: %s", installType),
		}
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

// getLocalInstalledVersionQuiet is like getLocalInstalledVersion but without producing log output
func getLocalInstalledVersionQuiet(item catalog.Item) (string, error) {
	// 1) FIRST, check the Cimian-managed key (i.e. readInstalledVersionFromRegistry).
	//    This is where you store your "Wrote local installed version to registry item=Git version=2.47.1.1" etc.
	cimianVersion, errLocalReg := readInstalledVersionFromRegistry(item.Name)
	if errLocalReg == nil && cimianVersion != "" {
		return cimianVersion, nil
	}

	// 2) If not found in Cimian's own key, proceed with enumerating the Windows Uninstall keys.
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
			return regApp.Version, nil
		}
		// PARTIAL MATCH
		if strings.Contains(regApp.Name, item.Name) {
			return regApp.Version, nil
		}
	}

	// 3) If it's an MSI with a product code, check that:
	if item.Installer.Type == "msi" && item.Installer.ProductCode != "" {
		v := findMsiVersion(item.Installer.ProductCode)
		if v != "" {
			return v, nil
		}
	}

	// 4) No match => treat as not installed
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
			logging.Info("Hash mismatch, installation/update required", "item", catalogItem.Name, "path", path)
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

// checkPathQuiet is like checkPath but with optional quiet mode (no logging)
func checkPathQuiet(catalogItem catalog.Item, quiet bool) (bool, error) {
	if !quiet {
		logging.Debug("File-based check initiated", "item", catalogItem.Name)
	}

	for _, checkFile := range catalogItem.Check.File {
		path := filepath.Clean(checkFile.Path)
		_, err := os.Stat(path)

		if err != nil {
			if os.IsNotExist(err) {
				if !quiet {
					logging.Info("File missing, installation required", "item", catalogItem.Name, "path", path)
				}
				return true, nil
			}
			continue
		}

		if checkFile.Hash != "" && !download.Verify(path, checkFile.Hash) {
			if !quiet {
				logging.Info("Hash mismatch, installation/update required", "item", catalogItem.Name, "path", path)
			}
			return true, nil
		}

		if checkFile.Version != "" {
			fileMetadata := GetFileMetadataQuiet(path, quiet)
			if IsOlderVersion(fileMetadata.versionString, checkFile.Version) {
				if !quiet {
					logging.Info("File version outdated, action needed",
						"item", catalogItem.Name,
						"path", path,
						"fileVersion", fileMetadata.versionString,
						"requiredVersion", checkFile.Version,
					)
				}
				return true, nil
			}
		}
	}

	if !quiet {
		logging.Debug("File checks passed, no action required", "item", catalogItem.Name)
	}
	return false, nil
}

// checkInstalls verifies installation by checking files/directories listed in the "installs" array.
// This is used for installation verification, not dependency checking.
// For "install"/"update": returns true if any tracked file is missing/outdated (reinstallation needed)
// For "uninstall": returns true if any tracked file exists (uninstallation needed)
func checkInstalls(item catalog.Item, installType string) (bool, error) {
	if len(item.Installs) == 0 {
		return false, nil
	}

	for _, install := range item.Installs {
		if strings.ToLower(install.Type) == "file" {
			fileInfo, err := os.Stat(install.Path)
			if err != nil {
				if os.IsNotExist(err) {
					if installType == "uninstall" {
						logging.Info("Tracked file not found, item may already be uninstalled",
							"item", item.Name, "missingPath", install.Path)
						return false, nil
					} else {
						logging.Info("Installs array verification failed - tracked file missing, reinstallation needed",
							"item", item.Name, "missingPath", install.Path)
						return true, nil
					}
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

			var hashVerificationPassed bool
			if install.MD5Checksum != "" {
				match, computedHash, err := verifyMD5WithHash(install.Path, install.MD5Checksum)
				if err != nil {
					logging.Warn("Hash verification error",
						"item", item.Name, "path", install.Path, "error", err)
					return true, err
				}
				if !match {
					logging.Info("Installs array verification failed - hash mismatch, reinstallation needed",
						"item", item.Name,
						"path", install.Path,
						"localHash", computedHash,
						"expectedHash", install.MD5Checksum,
					)
					return true, nil
				}
				hashVerificationPassed = true
				logging.Info("Hash verification passed",
					"item", item.Name, "path", install.Path, "hash", install.MD5Checksum)
			}

			if install.Version != "" {
				fileVersion, err := getFileVersion(install.Path)
				if err != nil || fileVersion == "" {
					if hashVerificationPassed {
						logging.Info("File version metadata unavailable - executable doesn't have embedded version info (normal for some executables), but MD5 verification passed - accepting installation",
							"item", item.Name, "path", install.Path, "error", err)
					} else {
						logging.Info("File version metadata unavailable or unreadable, action needed",
							"item", item.Name, "path", install.Path, "error", err)
						return true, nil
					}
				} else if IsOlderVersion(fileVersion, install.Version) {
					if hashVerificationPassed {
						logging.Info("File version appears outdated, but hash verification passed - accepting installation",
							"item", item.Name, "path", install.Path,
							"fileVersion", fileVersion,
							"expectedVersion", install.Version)
					} else {
						logging.Info("Installs array verification failed - file version outdated, reinstallation needed",
							"item", item.Name, "path", install.Path,
							"fileVersion", fileVersion,
							"expectedVersion", install.Version,
						)
						return true, nil
					}
				}
			}
		} else if strings.ToLower(install.Type) == "directory" {
			dirInfo, err := os.Stat(install.Path)
			if err != nil {
				if os.IsNotExist(err) {
					if installType == "uninstall" {
						logging.Info("Tracked directory not found, item may already be uninstalled",
							"item", item.Name, "missingPath", install.Path)
						return false, nil
					} else {
						logging.Info("Installs array verification failed - tracked directory missing, reinstallation needed",
							"item", item.Name, "missingPath", install.Path)
						return true, nil
					}
				}
				logging.Warn("Unexpected error checking directory existence",
					"item", item.Name, "path", install.Path, "error", err)
				return false, err
			}

			// Check if path exists but is not a directory
			if !dirInfo.IsDir() {
				logging.Info("Path exists but is not a directory, action needed",
					"item", item.Name, "path", install.Path)
				return true, nil
			}

			if installType == "uninstall" && dirInfo != nil && dirInfo.IsDir() {
				logging.Info("Directory present, uninstall required",
					"item", item.Name, "path", install.Path)
				return true, nil
			}
		}
	}
	logging.Debug("Installation verification checks passed, no action needed", "item", item.Name)
	return false, nil
}

// checkInstallsQuiet is like checkInstalls but with optional quiet mode (no logging)
func checkInstallsQuiet(item catalog.Item, installType string, quiet bool) (bool, error) {
	if len(item.Installs) == 0 {
		return false, nil
	}

	for _, install := range item.Installs {
		if strings.ToLower(install.Type) == "file" {
			fileInfo, err := os.Stat(install.Path)
			if err != nil {
				if os.IsNotExist(err) {
					if installType == "uninstall" {
						if !quiet {
							logging.Info("Tracked file not found, item may already be uninstalled",
								"item", item.Name, "missingPath", install.Path)
						}
						return false, nil
					} else {
						if !quiet {
							logging.Info("Installs array verification failed - tracked file missing, reinstallation needed",
								"item", item.Name, "missingPath", install.Path)
						}
						return true, nil
					}
				}
				if !quiet {
					logging.Warn("Unexpected error checking file existence",
						"item", item.Name, "path", install.Path, "error", err)
				}
				return false, err
			}

			if installType == "uninstall" && fileInfo != nil {
				if !quiet {
					logging.Info("File present, uninstall required",
						"item", item.Name, "path", install.Path)
				}
				return true, nil
			}

			var hashVerificationPassed bool
			if install.MD5Checksum != "" {
				match, computedHash, err := verifyMD5WithHash(install.Path, install.MD5Checksum)
				if err != nil {
					if !quiet {
						logging.Warn("Hash verification error",
							"item", item.Name, "path", install.Path, "error", err)
					}
					return true, err
				}
				if !match {
					if !quiet {
						logging.Info("Installs array verification failed - hash mismatch, reinstallation needed",
							"item", item.Name,
							"path", install.Path,
							"localHash", computedHash,
							"expectedHash", install.MD5Checksum,
						)
					}
					return true, nil
				}
				hashVerificationPassed = true
				if !quiet {
					logging.Info("Hash verification passed",
						"item", item.Name, "path", install.Path, "hash", install.MD5Checksum)
				}
			}

			if install.Version != "" {
				fileVersion, err := getFileVersionQuiet(install.Path, quiet)
				if err != nil || fileVersion == "" {
					if hashVerificationPassed {
						if !quiet {
							logging.Info("File version metadata unavailable - executable doesn't have embedded version info (normal for some executables), but MD5 verification passed - accepting installation",
								"item", item.Name, "path", install.Path, "error", err)
						}
					} else {
						if !quiet {
							logging.Info("File version metadata unavailable or unreadable, action needed",
								"item", item.Name, "path", install.Path, "error", err)
						}
						return true, nil
					}
				} else if IsOlderVersion(fileVersion, install.Version) {
					if hashVerificationPassed {
						if !quiet {
							logging.Info("File version appears outdated, but hash verification passed - accepting installation",
								"item", item.Name, "path", install.Path,
								"fileVersion", fileVersion,
								"expectedVersion", install.Version)
						}
					} else {
						if !quiet {
							logging.Info("Installs array verification failed - file version outdated, reinstallation needed",
								"item", item.Name, "path", install.Path,
								"fileVersion", fileVersion,
								"expectedVersion", install.Version,
							)
						}
						return true, nil
					}
				}
			}
		} else if strings.ToLower(install.Type) == "directory" {
			dirInfo, err := os.Stat(install.Path)
			if err != nil {
				if os.IsNotExist(err) {
					if installType == "uninstall" {
						if !quiet {
							logging.Info("Tracked directory not found, item may already be uninstalled",
								"item", item.Name, "missingPath", install.Path)
						}
						return false, nil
					} else {
						if !quiet {
							logging.Info("Installs array verification failed - tracked directory missing, reinstallation needed",
								"item", item.Name, "missingPath", install.Path)
						}
						return true, nil
					}
				}
				if !quiet {
					logging.Warn("Unexpected error checking directory existence",
						"item", item.Name, "path", install.Path, "error", err)
				}
				return false, err
			}

			// Check if path exists but is not a directory
			if !dirInfo.IsDir() {
				if !quiet {
					logging.Info("Path exists but is not a directory, action needed",
						"item", item.Name, "path", install.Path)
				}
				return true, nil
			}

			if installType == "uninstall" && dirInfo != nil && dirInfo.IsDir() {
				if !quiet {
					logging.Info("Directory present, uninstall required",
						"item", item.Name, "path", install.Path)
				}
				return true, nil
			}
		}
	}
	if !quiet {
		logging.Debug("Installation verification checks passed, no action needed", "item", item.Name)
	}
	return false, nil
}

// checkUninstaller verifies uninstallation by checking files/directories listed in the "uninstaller" array.
// This is used for uninstall verification - returns true if any tracked file exists (uninstallation needed)
// The uninstaller array allows specifying different files/paths than the installs array for removal
func checkUninstaller(item catalog.Item, installType string) (bool, error) {
	if len(item.Uninstaller) == 0 {
		return false, nil
	}

	// For uninstall operations, check if any file in the uninstaller array exists
	if installType == "uninstall" {
		for _, uninstall := range item.Uninstaller {
			if strings.ToLower(uninstall.Type) == "file" {
				_, err := os.Stat(uninstall.Path)
				if err != nil {
					if os.IsNotExist(err) {
						logging.Debug("Uninstaller array file not found, continuing check",
							"item", item.Name, "missingPath", uninstall.Path)
						continue
					}
					logging.Warn("Unexpected error checking uninstaller file existence",
						"item", item.Name, "path", uninstall.Path, "error", err)
					continue
				}

				// File exists, uninstall is needed
				logging.Info("Uninstaller array file present, uninstall required",
					"item", item.Name, "path", uninstall.Path)
				return true, nil

			} else if strings.ToLower(uninstall.Type) == "directory" {
				dirInfo, err := os.Stat(uninstall.Path)
				if err != nil {
					if os.IsNotExist(err) {
						logging.Debug("Uninstaller array directory not found, continuing check",
							"item", item.Name, "missingPath", uninstall.Path)
						continue
					}
					logging.Warn("Unexpected error checking uninstaller directory existence",
						"item", item.Name, "path", uninstall.Path, "error", err)
					continue
				}

				// Directory exists and is actually a directory, uninstall is needed
				if dirInfo.IsDir() {
					logging.Info("Uninstaller array directory present, uninstall required",
						"item", item.Name, "path", uninstall.Path)
					return true, nil
				}
			}
		}

		// No files/directories from uninstaller array were found
		logging.Debug("No uninstaller array items found, item may already be uninstalled", "item", item.Name)
		return false, nil
	}

	// For install/update operations, the uninstalls array is not used
	return false, nil
}

// verifyMD5WithHash computes hash (MD5, SHA1, or SHA256) based on expected hash length and returns match status and computed hash explicitly.
func verifyMD5WithHash(filePath, expected string) (bool, string, error) {
	f, err := os.Open(filePath)
	if err != nil {
		return false, "", err
	}
	defer f.Close()

	// Detect hash type by length and compute appropriate hash
	expectedLen := len(expected)
	
	var computed string
	switch expectedLen {
	case 32: // MD5
		hasher := md5.New()
		if _, err := io.Copy(hasher, f); err != nil {
			return false, "", err
		}
		computed = hex.EncodeToString(hasher.Sum(nil))
	case 40: // SHA1
		hasher := sha1.New()
		if _, err := io.Copy(hasher, f); err != nil {
			return false, "", err
		}
		computed = hex.EncodeToString(hasher.Sum(nil))
	case 64: // SHA256
		hasher := sha256.New()
		if _, err := io.Copy(hasher, f); err != nil {
			return false, "", err
		}
		computed = hex.EncodeToString(hasher.Sum(nil))
	default:
		// Default to MD5 for backward compatibility
		hasher := md5.New()
		if _, err := io.Copy(hasher, f); err != nil {
			return false, "", err
		}
		computed = hex.EncodeToString(hasher.Sum(nil))
	}

	return strings.EqualFold(computed, expected), computed, nil
}

// getFileVersion returns file version if any (or empty if unknown).
func getFileVersion(filePath string) (string, error) {
	return getFileVersionQuiet(filePath, false)
}

// getFileVersionQuiet is like getFileVersion but with optional quiet mode (no logging)
func getFileVersionQuiet(filePath string, quiet bool) (string, error) {
	metadata := GetFileMetadataQuiet(filePath, quiet)
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

// checkRegistryQuiet is like checkRegistry but with optional quiet mode (no logging)
func checkRegistryQuiet(catalogItem catalog.Item, _ string, quiet bool) (bool, error) {
	if !quiet {
		logging.Debug("Registry check started", "item", catalogItem.Name)
	}

	checkReg := catalogItem.Check.Registry
	catalogVersion, err := goversion.NewVersion(checkReg.Version)
	if err != nil {
		if !quiet {
			logging.Warn("Failed parsing registry version, assuming action needed",
				"item", catalogItem.Name, "version", checkReg.Version, "error", err)
		}
		return true, err
	}

	if len(RegistryItems) == 0 {
		RegistryItems, err = getUninstallKeys()
		if err != nil {
			if !quiet {
				logging.Warn("Failed retrieving uninstall keys, action needed",
					"item", catalogItem.Name, "error", err)
			}
			return true, err
		}
	}

	var regVersionFound string
	registryMatched := false

	for _, regItem := range RegistryItems {
		if regItem.Name == checkReg.Name || strings.Contains(regItem.Name, checkReg.Name) {
			registryMatched = true
			regVersionFound = regItem.Version
			if !quiet {
				logging.Debug("Registry match found",
					"catalogName", checkReg.Name,
					"registryName", regItem.Name,
					"registryVersion", regItem.Version,
				)
			}
			regVersion, err := goversion.NewVersion(regItem.Version)
			if err != nil || regVersion.LessThan(catalogVersion) {
				if !quiet {
					logging.Info("Registry version outdated, action needed",
						"item", catalogItem.Name,
						"registryVersion", regItem.Version,
						"requiredVersion", checkReg.Version,
					)
				}
				return true, nil
			}
			break // Do not return yet; explicitly proceed to MSI check
		}
	}

	if catalogItem.Installer.Type == "msi" && catalogItem.Installer.ProductCode != "" {
		if !quiet {
			logging.Debug("Explicitly checking MSI ProductCode",
				"item", catalogItem.Name,
				"productCode", catalogItem.Installer.ProductCode,
			)
		}
		installed, versionMatch := checkMsiProductCode(catalogItem.Installer.ProductCode, checkReg.Version)

		if !installed {
			if !quiet {
				logging.Info("MSI product code not installed; action required", "item", catalogItem.Name)
			}
			return true, nil
		}

		if !versionMatch {
			if !quiet {
				logging.Info("MSI product code version outdated; action required",
					"item", catalogItem.Name,
					"requiredVersion", checkReg.Version,
				)
			}
			return true, nil
		}

		if !quiet {
			logging.Debug("MSI product code matches required version; no MSI action needed",
				"item", catalogItem.Name,
			)
		}
	} else if registryMatched {
		// Explicitly confirm that registry check alone is sufficient.
		if !quiet {
			logging.Debug("Registry check alone sufficient, no MSI installer present",
				"item", catalogItem.Name, "registryVersion", regVersionFound,
			)
		}
		return false, nil
	} else {
		// Neither registry nor MSI match
		if !quiet {
			logging.Info("No registry or MSI match found; action needed", "item", catalogItem.Name)
		}
		return true, nil
	}

	return false, nil
}

// checkScript runs a PowerShell script to decide if an item is installed.
func checkScript(catalogItem catalog.Item, cachePath string, installType string) (bool, error) {
	tmpScript := filepath.Join(cachePath, "tmpCheckScript.ps1")
	if err := os.WriteFile(tmpScript, []byte(catalogItem.Check.Script), 0755); err != nil {
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

// checkScriptQuiet is like checkScript but with optional quiet mode (no logging)
func checkScriptQuiet(catalogItem catalog.Item, cachePath string, installType string, quiet bool) (bool, error) {
	tmpScript := filepath.Join(cachePath, "tmpCheckScript.ps1")
	if err := os.WriteFile(tmpScript, []byte(catalogItem.Check.Script), 0755); err != nil {
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

	if !quiet {
		logging.Debug("InstallCheck script output", "stdout", outStr, "stderr", errStr, "error", err)
	}

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

// GetWindowsVersion returns the Windows OS version in a format suitable for version comparison
// Returns version strings like "10.0.19041" for Windows 10 build 19041 or "11.0.22000" for Windows 11 build 22000
// This function properly handles Windows 11 detection by examining build numbers and returning logical version numbers
func GetWindowsVersion() (string, error) {
	if runtime.GOOS != "windows" {
		return "", fmt.Errorf("not running on Windows")
	}

	info, err := host.Info()
	if err != nil {
		return "", fmt.Errorf("failed to get host info: %v", err)
	}

	// For Windows, gopsutil returns kernel version which maps to Windows version
	// However, Windows 11 still reports kernel version 10.0.x but can be distinguished by build number
	kernelVersion := info.KernelVersion
	
	// Parse the kernel version to extract build number
	versionParts := strings.Split(kernelVersion, ".")
	if len(versionParts) >= 3 {
		major := versionParts[0]
		minor := versionParts[1] 
		buildStr := versionParts[2]
		
		// Convert build number to integer for comparison
		if buildNum, err := strconv.Atoi(buildStr); err == nil {
			// Windows 11 logic: kernel 10.0 with build >= 22000 is Windows 11
			if major == "10" && minor == "0" && buildNum >= 22000 {
				// Return as Windows 11 version
				return fmt.Sprintf("11.0.%s", buildStr), nil
			}
		}
	}
	
	// For all other cases (Windows 10, older versions), return the original kernel version
	return kernelVersion, nil
}

// SupportsOSVersion checks if the current OS version is compatible with the catalog item
func SupportsOSVersion(item catalog.Item) (bool, error) {
	// If no OS version constraints are specified, assume compatible
	if item.MinOSVersion == "" && item.MaxOSVersion == "" {
		return true, nil
	}

	currentVersion, err := GetWindowsVersion()
	if err != nil {
		logging.Warn("Failed to get Windows version, assuming compatible",
			"item", item.Name, "error", err)
		return true, nil
	}

	// Check minimum OS version requirement
	if item.MinOSVersion != "" {
		compatible, err := isVersionCompatible(currentVersion, item.MinOSVersion, "minimum")
		if err != nil {
			logging.Warn("Failed to compare minimum OS version, assuming compatible",
				"item", item.Name, "currentVersion", currentVersion,
				"minVersion", item.MinOSVersion, "error", err)
			return true, nil
		}
		if !compatible {
			logging.Info("OS version too old for package",
				"item", item.Name, "currentVersion", currentVersion,
				"minVersion", item.MinOSVersion)
			return false, nil
		}
	}

	// Check maximum OS version requirement
	if item.MaxOSVersion != "" {
		compatible, err := isVersionCompatible(currentVersion, item.MaxOSVersion, "maximum")
		if err != nil {
			logging.Warn("Failed to compare maximum OS version, assuming compatible",
				"item", item.Name, "currentVersion", currentVersion,
				"maxVersion", item.MaxOSVersion, "error", err)
			return true, nil
		}
		if !compatible {
			logging.Info("OS version too new for package",
				"item", item.Name, "currentVersion", currentVersion,
				"maxVersion", item.MaxOSVersion)
			return false, nil
		}
	}

	return true, nil
}

// SupportsOSVersionQuiet is like SupportsOSVersion but with optional quiet mode (no logging)
func SupportsOSVersionQuiet(item catalog.Item, quiet bool) (bool, error) {
	// If no OS version constraints are specified, assume compatible
	if item.MinOSVersion == "" && item.MaxOSVersion == "" {
		return true, nil
	}

	currentVersion, err := GetWindowsVersion()
	if err != nil {
		if !quiet {
			logging.Warn("Failed to get Windows version, assuming compatible",
				"item", item.Name, "error", err)
		}
		return true, nil
	}

	// Check minimum OS version requirement
	if item.MinOSVersion != "" {
		compatible, err := isVersionCompatible(currentVersion, item.MinOSVersion, "minimum")
		if err != nil {
			// Suppress all logging in quiet mode - no exceptions
			if !quiet {
				logging.Warn("Failed to compare minimum OS version, assuming compatible",
					"item", item.Name, "currentVersion", currentVersion,
					"minVersion", item.MinOSVersion, "error", err)
			}
			return true, nil
		}
		if !compatible {
			if !quiet {
				logging.Info("OS version too old for package",
					"item", item.Name, "currentVersion", currentVersion,
					"minVersion", item.MinOSVersion)
			}
			return false, nil
		}
	}

	// Check maximum OS version requirement
	if item.MaxOSVersion != "" {
		compatible, err := isVersionCompatible(currentVersion, item.MaxOSVersion, "maximum")
		if err != nil {
			if !quiet {
				logging.Warn("Failed to compare maximum OS version, assuming compatible",
					"item", item.Name, "currentVersion", currentVersion,
					"maxVersion", item.MaxOSVersion, "error", err)
			}
			return true, nil
		}
		if !compatible {
			if !quiet {
				logging.Info("OS version too new for package",
					"item", item.Name, "currentVersion", currentVersion,
					"maxVersion", item.MaxOSVersion)
			}
			return false, nil
		}
	}

	return true, nil
}

// normalizeWindowsVersionForComparison handles Windows version normalization for compatibility checks
// This function understands Windows 11's quirky versioning where the kernel reports 10.0.x but logically it's 11.0.x
func normalizeWindowsVersionForComparison(version string) (string, error) {
	// Handle the case where someone specifies "11.0" as a minimum version
	// We need to convert this to the actual Windows 11 threshold build
	versionParts := strings.Split(version, ".")
	if len(versionParts) >= 2 {
		major := versionParts[0]
		minor := versionParts[1]
		
		// If someone specifies "11.0" (or "11.0.x"), convert to Windows 11 threshold
		if major == "11" && minor == "0" {
			if len(versionParts) == 2 {
				// Convert "11.0" to "11.0.22000" (minimum Windows 11 build)
				return "11.0.22000", nil
			} else if len(versionParts) >= 3 {
				// "11.0.x" - keep as is, it's already in the correct format
				return version, nil
			}
		}
		
		// If someone specifies "10.0" and it's a high build number >= 22000, treat as Windows 11
		if major == "10" && minor == "0" && len(versionParts) >= 3 {
			buildStr := versionParts[2]
			if buildNum, err := strconv.Atoi(buildStr); err == nil && buildNum >= 22000 {
				// Convert Windows 11 builds from "10.0.x" to "11.0.x" format for consistency
				return fmt.Sprintf("11.0.%s", buildStr), nil
			}
		}
	}
	
	// For all other cases, return the version as-is
	return version, nil
}

// isVersionCompatible compares current OS version against required version
// For minimum: returns true if current >= required
// For maximum: returns true if current <= required
func isVersionCompatible(current, required, checkType string) (bool, error) {
	// Normalize both versions for Windows-specific logic
	currentNormalized, err := normalizeWindowsVersionForComparison(current)
	if err != nil {
		return false, fmt.Errorf("failed to normalize current OS version %s: %v", current, err)
	}
	
	requiredNormalized, err := normalizeWindowsVersionForComparison(required)
	if err != nil {
		return false, fmt.Errorf("failed to normalize required OS version %s: %v", required, err)
	}
	
	// Apply Cimian's version normalization (removes trailing .0s)
	currentVersionNormalized := cimiversion.Normalize(currentNormalized)
	requiredVersionNormalized := cimiversion.Normalize(requiredNormalized)

	currentVer, err := goversion.NewVersion(currentVersionNormalized)
	if err != nil {
		return false, fmt.Errorf("failed to parse current OS version %s (normalized: %s): %v", current, currentVersionNormalized, err)
	}

	requiredVer, err := goversion.NewVersion(requiredVersionNormalized)
	if err != nil {
		return false, fmt.Errorf("failed to parse required OS version %s (normalized: %s): %v", required, requiredVersionNormalized, err)
	}

	if checkType == "minimum" {
		// For minimum version: current must be >= required
		return !currentVer.LessThan(requiredVer), nil
	} else if checkType == "maximum" {
		// For maximum version: current must be <= required
		return currentVer.LessThan(requiredVer) || currentVer.Equal(requiredVer), nil
	}

	return false, fmt.Errorf("invalid check type: %s", checkType)
}
