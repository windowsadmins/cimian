package catalog

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/utils"
	"gopkg.in/yaml.v3"
)

// Item contains an individual entry from the catalog.
type Item struct {
	Name                 string              `yaml:"name"`
	Dependencies         []string            `yaml:"dependencies"`
	DisplayName          string              `yaml:"display_name"`
	Identifier           string              `yaml:"identifier,omitempty"`
	Installer            InstallerItem       `yaml:"installer"`
	Uninstaller          []InstallItem       `yaml:"uninstaller"`
	Check                InstallCheck        `yaml:"check"`
	Installs             []InstallItem       `yaml:"installs"`
	Version              string              `yaml:"version"`
	BlockingApps         []string            `yaml:"blocking_apps"`
	PreScript            utils.LiteralString `yaml:"preinstall_script"`
	PostScript           utils.LiteralString `yaml:"postinstall_script"`
	PreUninstallScript   utils.LiteralString `yaml:"preuninstall_script"`
	PostUninstallScript  utils.LiteralString `yaml:"postuninstall_script"`
	InstallCheckScript   utils.LiteralString `yaml:"installcheck_script"`
	UninstallCheckScript utils.LiteralString `yaml:"uninstallcheck_script"`
	SupportedArch        []string            `yaml:"supported_architectures"`
	// OS version compatibility
	MinOSVersion string `yaml:"minimum_os_version,omitempty"` // Minimum Windows version required
	MaxOSVersion string `yaml:"maximum_os_version,omitempty"` // Maximum Windows version supported
	// Advanced dependency support
	Requires  []string `yaml:"requires,omitempty"`   // Prerequisites that must be installed first
	UpdateFor []string `yaml:"update_for,omitempty"` // Items this package is an update for

	// OnDemand functionality - items that can be run multiple times and never considered "installed"
	OnDemand bool `yaml:"OnDemand,omitempty"` // If true, item can be run on-demand and is never considered installed

	// Unattended installation control for --auto mode and scheduled tasks
	UnattendedInstall   bool  `yaml:"unattended_install"`   // If false, item should not be installed automatically
	UnattendedUninstall bool  `yaml:"unattended_uninstall"` // If false, item should not be uninstalled automatically

	// Uninstallability - whether the package can be uninstalled
	Uninstallable *bool `yaml:"uninstallable,omitempty"` // If explicitly false, uninstall will be skipped; if nil, auto-determined

	// Icon support
	IconName string `yaml:"icon_name,omitempty"` // Icon filename in repository icons directory

	// Traceability fields - not persisted to YAML, used for runtime tracking
	SourceManifest string   `yaml:"-"` // Which manifest this item came from
	SourceType     string   `yaml:"-"` // "managed_installs", "managed_updates", "requires", "update_for", etc.
	SourceChain    []string `yaml:"-"` // Full dependency chain that led to this item
}

// InstallItem holds details for the "installs" array.
// Type can be "file" or "directory"
// - "file": checks if file exists, optionally validates MD5 checksum and version
// - "directory": checks if directory exists, if not installation is needed
type InstallItem struct {
	Type        string   `yaml:"type"`        // "file" or "directory"
	Path        string   `yaml:"path"`        // Path to file or directory
	Version     string   `yaml:"version"`     // Expected version (file type only)
	MD5Checksum string   `yaml:"md5checksum"` // Expected MD5 hash (file type only)
	ProductCode string   `yaml:"product_code"`
	UpgradeCode string   `yaml:"upgrade_code"`
	Switches    []string `yaml:"switches,omitempty"` // / style arguments (e.g., /silent)
	Flags       []string `yaml:"flags,omitempty"`    // - style arguments (e.g., --quiet)
} // InstallerItem holds information about how to install a catalog item.
type InstallerItem struct {
	Type        string   `yaml:"type"`
	Location    string   `yaml:"location"`
	Hash        string   `yaml:"hash"`
	Verb        string   `yaml:"verb,omitempty" json:"verb,omitempty"`
	Switches    []string `yaml:"switches,omitempty" json:"switches,omitempty"`
	Flags       []string `yaml:"flags,omitempty" json:"flags,omitempty"`
	Arguments   []string `yaml:"arguments"`
	ProductCode string   `yaml:"product_code,omitempty"`
}

// InstallCheck holds data for checking installation state.
type InstallCheck struct {
	File     []FileCheck `yaml:"file"`
	Script   string      `yaml:"script"`
	Registry RegCheck    `yaml:"registry"`
}

// FileCheck holds information about a single file check.
type FileCheck struct {
	Path        string `yaml:"path"`
	Version     string `yaml:"version"`
	ProductName string `yaml:"product_name"`
	Hash        string `yaml:"hash"`
}

// RegCheck holds information about registry-based checks.
type RegCheck struct {
	Name    string `yaml:"name"`
	Version string `yaml:"version"`
}

// AuthenticatedGet retrieves and parses the catalogs defined in config.
func AuthenticatedGet(cfg config.Configuration) map[int]map[string]Item {
	// catalogMap holds parsed catalog data from all configured catalogs.
	var catalogMap = make(map[int]map[string]Item)
	catalogCount := 0

	// Catch unexpected failures
	defer func() {
		if r := recover(); r != nil {
			logging.Error("Catalog processing failed with panic", "error", r)
			os.Exit(1)
		}
	}()

	// Ensure at least one catalog is defined
	if len(cfg.Catalogs) < 1 {
		logging.Error("Unable to continue, no catalogs assigned", "catalogs", cfg.Catalogs)
		return catalogMap
	}

	// Loop through each catalog name in config.Catalogs
	for _, catalogName := range cfg.Catalogs {
		catalogCount++

		// Build the catalog URL and local destination path
		catalogURL := fmt.Sprintf("%s/catalogs/%s.yaml",
			strings.TrimRight(cfg.SoftwareRepoURL, "/"),
			catalogName)
		catalogFilePath := filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`, catalogName+".yaml")

		logging.Debug("Downloading catalog", "url", catalogURL, "path", catalogFilePath)

		// Download the catalog file
		if err := download.DownloadFile(catalogURL, catalogFilePath, &cfg, 0, utils.NewNoOpReporter()); err != nil {
			logging.Error("Failed to download catalog", "url", catalogURL, "error", err)
			continue
		}

		// Read the downloaded YAML file
		yamlFile, err := os.ReadFile(catalogFilePath)
		if err != nil {
			logging.Error("Failed to read downloaded catalog file", "path", catalogFilePath, "error", err)
			continue
		}

		// Instead of unmarshaling directly into []Item,
		// define a wrapper for top-level "items: []"
		type catalogWrapper struct {
			Items []Item `yaml:"items"`
		}

		var wrapper catalogWrapper
		if err := yaml.Unmarshal(yamlFile, &wrapper); err != nil {
			logging.Error("unable to parse YAML", "path", catalogFilePath, "error", err)
			continue
		}

		// Convert the slice into a map keyed by item.Name
		// When duplicate items exist (same name), we must:
		// 1. Filter by architecture - only keep items matching system architecture
		// 2. Select highest version - when multiple arch-compatible items exist
		indexedItems := make(map[string]Item)
		sysArch := getSystemArch()
		
		for _, it := range wrapper.Items {
			if it.Name == "" {
				continue
			}
			
			// Check if we already have an item with this name
			existing, exists := indexedItems[it.Name]
			if !exists {
				// First item with this name - add it
				indexedItems[it.Name] = it
				logging.Debug("Added catalog item", "name", it.Name, "version", it.Version, 
					"arch", it.SupportedArch, "installer", it.Installer.Location)
				continue
			}
			
			// Duplicate detected - apply selection logic
			existingSupportsArch := supportsArch(existing, sysArch)
			candidateSupportsArch := supportsArch(it, sysArch)
			
			// Priority 1: Prefer arch-compatible items over non-compatible
			if candidateSupportsArch && !existingSupportsArch {
				indexedItems[it.Name] = it
				logging.Debug("Replaced catalog item (arch-compatible)", "name", it.Name,
					"oldVersion", existing.Version, "oldArch", existing.SupportedArch,
					"newVersion", it.Version, "newArch", it.SupportedArch, "sysArch", sysArch)
				continue
			}
			
			if !candidateSupportsArch && existingSupportsArch {
				// Keep existing arch-compatible item
				logging.Debug("Kept existing catalog item (arch-compatible)", "name", it.Name,
					"keptVersion", existing.Version, "keptArch", existing.SupportedArch,
					"rejectedVersion", it.Version, "rejectedArch", it.SupportedArch, "sysArch", sysArch)
				continue
			}
			
			// Priority 2: If both (or neither) support arch, select highest version
			// Use version comparison to determine which is newer
			existingNorm := normalizeVersionForComparison(existing.Version)
			candidateNorm := normalizeVersionForComparison(it.Version)
			
			// Simple semantic version comparison
			if isVersionNewer(candidateNorm, existingNorm) {
				indexedItems[it.Name] = it
				logging.Debug("Replaced catalog item (newer version)", "name", it.Name,
					"oldVersion", existing.Version, "newVersion", it.Version,
					"arch", it.SupportedArch, "sysArch", sysArch)
			} else {
				logging.Debug("Kept existing catalog item (newer or equal version)", "name", it.Name,
					"keptVersion", existing.Version, "rejectedVersion", it.Version,
					"arch", existing.SupportedArch, "sysArch", sysArch)
			}
		}
		catalogMap[catalogCount] = indexedItems

		logging.Debug("Successfully processed catalog", catalogName, "items", len(indexedItems))
	}

	return catalogMap
}

// LookForUpdates searches for items that declare they are updates for the given item name.
// This handles updates that aren't simply later versions of the same item.
// For example, AdobeCameraRaw is an update for Adobe Photoshop, but doesn't update
// the version of Adobe Photoshop itself.
// Returns a list of catalog item names that are updates for manifestitem.
func LookForUpdates(itemName string, catalogMap map[int]map[string]Item, installedItems []string) []string {
	logging.Debug("Looking for updates for item", "item", itemName, "installedItems", len(installedItems))

	// First check if the target item is actually installed
	// update_for means "install this update IF the target is installed"
	itemInstalled := false
	for _, installed := range installedItems {
		if installed == itemName {
			itemInstalled = true
			break
		}
	}

	if !itemInstalled {
		logging.Debug("Target item not installed, skipping update_for processing", "item", itemName)
		return []string{}
	}

	var updateList []string

	// Look through all catalogs for items that have update_for pointing to our item
	for _, catalog := range catalogMap {
		for _, catalogItem := range catalog {
			if catalogItem.UpdateFor != nil {
				for _, updateForItem := range catalogItem.UpdateFor {
					if updateForItem == itemName {
						updateList = append(updateList, catalogItem.Name)
						logging.Debug("Found update item", "update", catalogItem.Name, "for", itemName)
					}
				}
			}
		}
	}

	// Remove duplicates
	updateList = removeDuplicates(updateList)

	if len(updateList) > 0 {
		logging.Debug("Found updates for installed item", "count", len(updateList), "updates", updateList, "for", itemName)
	}

	return updateList
}

// LookForUpdatesForVersion searches for updates for a specific version of an item.
// Since these can appear in manifests as item-version or item--version, we search for both.
func LookForUpdatesForVersion(itemName, itemVersion string, catalogMap map[int]map[string]Item, installedItems []string) []string {
	nameAndVersion := fmt.Sprintf("%s-%s", itemName, itemVersion)
	altNameAndVersion := fmt.Sprintf("%s--%s", itemName, itemVersion)

	updateList := LookForUpdates(nameAndVersion, catalogMap, installedItems)
	updateList = append(updateList, LookForUpdates(altNameAndVersion, catalogMap, installedItems)...)

	// Remove duplicates
	updateList = removeDuplicates(updateList)

	return updateList
}

// CheckDependencies checks if all required dependencies for an item are installed or scheduled for install.
// Returns a list of missing dependencies.
func CheckDependencies(item Item, installedItems []string, scheduledItems []string) []string {
	if len(item.Requires) == 0 {
		return nil
	}

	var missingDeps []string

	// Combine installed and scheduled items
	allAvailableItems := append(installedItems, scheduledItems...)

	logging.Debug("Checking dependencies for item", "item", item.Name, "requires", item.Requires, 
		"installedItems", len(installedItems), "scheduledItems", len(scheduledItems))

	for _, reqItem := range item.Requires {
		// Parse the requirement to handle versioned dependencies
		reqName, reqVersion := SplitNameAndVersion(reqItem)

		satisfied := false
		for _, availableItem := range allAvailableItems {
			availableName, availableVersion := SplitNameAndVersion(availableItem)

			// Check if names match (case-insensitive)
			if strings.EqualFold(availableName, reqName) {
				// If no specific version required, any version satisfies
				if reqVersion == "" {
					logging.Debug("Dependency satisfied (no version constraint)", "item", item.Name, "dependency", reqName, "availableItem", availableItem)
					satisfied = true
					break
				}

				// If specific version required, check if it matches
				if reqVersion != "" && availableVersion != "" {
					// Simple exact version match for now
					// TODO: Implement more sophisticated version comparison
					if strings.EqualFold(availableVersion, reqVersion) {
						logging.Debug("Dependency satisfied (exact version match)", "item", item.Name, "dependency", reqName, "requiredVersion", reqVersion, "availableVersion", availableVersion)
						satisfied = true
						break
					}
				} else if reqVersion != "" && availableVersion == "" {
					// Required version specified but available item has no version
					// For now, assume it's satisfied if name matches
					logging.Debug("Dependency satisfied (no version info available)", "item", item.Name, "dependency", reqName, "requiredVersion", reqVersion)
					satisfied = true
					break
				}
			}
		}

		if !satisfied {
			logging.Info("Missing dependency detected", "item", item.Name, "missingDependency", reqItem, "parsedName", reqName, "parsedVersion", reqVersion)
			missingDeps = append(missingDeps, reqItem)
		}
	}

	if len(missingDeps) > 0 {
		logging.Info("Dependencies check result", "item", item.Name, "totalRequired", len(item.Requires), "missingCount", len(missingDeps), "missingDeps", missingDeps)
	} else {
		logging.Debug("All dependencies satisfied", "item", item.Name, "totalRequired", len(item.Requires))
	}

	return missingDeps
}

// FindItemsRequiring finds all items in catalogs that require the given item.
// This is used during removal to determine what dependent items also need to be removed.
func FindItemsRequiring(itemName string, catalogMap map[int]map[string]Item) []Item {
	var dependentItems []Item

	// Check different name formats that might be used in requires
	checkNames := []string{
		itemName,
		// TODO: Add versioned names if needed based on the item's version
	}

	for _, catalog := range catalogMap {
		for _, catalogItem := range catalog {
			if len(catalogItem.Requires) > 0 {
				for _, reqItem := range catalogItem.Requires {
					reqName, _ := SplitNameAndVersion(reqItem)

					for _, checkName := range checkNames {
						if strings.EqualFold(reqName, checkName) {
							dependentItems = append(dependentItems, catalogItem)
							logging.Debug("Found dependent item", "item", catalogItem.Name, "requires", itemName)
							break
						}
					}
				}
			}
		}
	}

	return dependentItems
}

// GetItemByName searches for an item by name across all catalogs.
// Returns the item and true if found, or an empty item and false if not found.
func GetItemByName(itemName string, catalogMap map[int]map[string]Item) (Item, bool) {
	itemNameLower := strings.ToLower(itemName)
	
	// Get system architecture using local function to avoid circular dependency
	sysArch := getSystemArch()
	
	var archCompatibleItem Item
	var fallbackItem Item
	var foundArchCompatible bool
	var foundFallback bool

	// IMPORTANT: Iterate through ALL items first to find arch-compatible version
	// Don't return early - map iteration order is random in Go!
	for _, catalog := range catalogMap {
		for name, item := range catalog {
			if strings.ToLower(name) == itemNameLower {
				// Check if this item supports the current system architecture
				if supportsArch(item, sysArch) {
					if !foundArchCompatible {
						archCompatibleItem = item
						foundArchCompatible = true
						logging.Debug("GetItemByName found arch-compatible item",
							"item", itemName, "sysArch", sysArch, "itemArch", item.SupportedArch,
							"installer", item.Installer.Location)
					}
				} else {
					// Keep a fallback in case no arch-compatible version exists
					if !foundFallback {
						fallbackItem = item
						foundFallback = true
						logging.Debug("GetItemByName found non-compatible item",
							"item", itemName, "sysArch", sysArch, "itemArch", item.SupportedArch,
							"installer", item.Installer.Location)
					}
				}
			}
		}
	}

	// Return arch-compatible version if found
	if foundArchCompatible {
		logging.Debug("GetItemByName returning arch-compatible item",
			"item", itemName, "sysArch", sysArch, "itemArch", archCompatibleItem.SupportedArch)
		return archCompatibleItem, true
	}

	// Return fallback if we found an item but no arch-compatible version exists
	if foundFallback {
		logging.Debug("GetItemByName returning fallback (no arch-compatible version found)",
			"item", itemName, "sysArch", sysArch, "fallbackArch", fallbackItem.SupportedArch)
		return fallbackItem, true
	}

	return Item{}, false
}

// getSystemArch returns the system architecture (duplicated from status to avoid circular dependency)
func getSystemArch() string {
	// Check PROCESSOR_IDENTIFIER first for ARM detection
	if procID := os.Getenv("PROCESSOR_IDENTIFIER"); procID != "" {
		if strings.Contains(strings.ToUpper(procID), "ARM") {
			return "arm64"
		}
	}
	
	// Check PROCESSOR_ARCHITEW6432 for native architecture on WoW64
	if nativeArch := os.Getenv("PROCESSOR_ARCHITEW6432"); nativeArch != "" {
		switch strings.ToUpper(nativeArch) {
		case "AMD64", "X86_64":
			return "x64"
		case "ARM64":
			return "arm64"
		}
	}
	
	// Check PROCESSOR_ARCHITECTURE
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
	
	// Fallback to runtime.GOARCH
	switch runtime.GOARCH {
	case "amd64", "x86_64":
		return "x64"
	case "386":
		return "x86"
	default:
		return runtime.GOARCH
	}
}

// supportsArch checks if an item supports the given architecture
func supportsArch(item Item, sysArch string) bool {
	if len(item.SupportedArch) == 0 {
		return true // No architecture restriction
	}
	
	sysArchNorm := normalizeArch(sysArch)
	for _, arch := range item.SupportedArch {
		if normalizeArch(arch) == sysArchNorm {
			return true
		}
	}
	return false
}

// normalizeArch normalizes architecture names
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

// removeDuplicates removes duplicate strings from a slice
func removeDuplicates(slice []string) []string {
	keys := make(map[string]bool)
	var result []string

	for _, item := range slice {
		if !keys[item] {
			keys[item] = true
			result = append(result, item)
		}
	}

	return result
}

// IsItemInstalled checks if an item is installed by comparing against a list of installed items
func IsItemInstalled(itemName string, installedItems []string) bool {
	for _, installed := range installedItems {
		installedName, _ := SplitNameAndVersion(installed)
		checkName, _ := SplitNameAndVersion(itemName)

		if strings.EqualFold(installedName, checkName) {
			return true
		}
	}
	return false
}

// FilterInstalledItems returns only the items from the input list that are actually installed
func FilterInstalledItems(itemNames []string, installedItems []string) []string {
	var result []string

	for _, itemName := range itemNames {
		if IsItemInstalled(itemName, installedItems) {
			result = append(result, itemName)
		}
	}

	return result
}

// GetVersionFromInstalledItems returns the version of an installed item, if available
func GetVersionFromInstalledItems(itemName string, installedItems []string) string {
	for _, installed := range installedItems {
		installedName, installedVersion := SplitNameAndVersion(installed)
		checkName, _ := SplitNameAndVersion(itemName)

		if strings.EqualFold(installedName, checkName) {
			return installedVersion
		}
	}
	return ""
}

// SplitNameAndVersion splits an item name that may contain a version suffix.
// It handles formats like "itemname-1.0.0" or "itemname--1.0.0"
// Returns the name and version separately.
func SplitNameAndVersion(nameWithVersion string) (string, string) {
	// Handle the double dash format first (itemname--version)
	if strings.Contains(nameWithVersion, "--") {
		parts := strings.SplitN(nameWithVersion, "--", 2)
		if len(parts) == 2 {
			return strings.TrimSpace(parts[0]), strings.TrimSpace(parts[1])
		}
	}

	// Handle the single dash format (itemname-version)
	// We need to be careful here as some item names might contain dashes
	// A simple heuristic: if the last part after the last dash looks like a version, split there
	if strings.Contains(nameWithVersion, "-") {
		parts := strings.Split(nameWithVersion, "-")
		if len(parts) >= 2 {
			lastPart := parts[len(parts)-1]
			// Simple version detection: contains digits and dots/underscores
			if strings.ContainsAny(lastPart, "0123456789") &&
				(strings.Contains(lastPart, ".") || strings.Contains(lastPart, "_")) {
				// Reconstruct name without the version part
				name := strings.Join(parts[:len(parts)-1], "-")
				return strings.TrimSpace(name), strings.TrimSpace(lastPart)
			}
		}
	}

	// No version found, return the whole string as name with empty version
	return strings.TrimSpace(nameWithVersion), ""
}

// ItemSource tracks where an item came from for better debugging and logging
type ItemSource struct {
	ItemName       string   // The actual item name
	SourceManifest string   // Which manifest file it originated from
	SourceType     string   // "managed_installs", "managed_updates", "requires", "update_for", etc.
	SourceChain    []string // Full dependency chain that led to this item
	ParentItem     string   // If this is a dependency, what item required it
}

// CreateItemSource creates a new ItemSource for tracking
func CreateItemSource(itemName, sourceManifest, sourceType string) ItemSource {
	return ItemSource{
		ItemName:       itemName,
		SourceManifest: sourceManifest,
		SourceType:     sourceType,
		SourceChain:    []string{sourceType + ":" + sourceManifest},
	}
}

// AddToChain adds a new link to the source chain
func (is *ItemSource) AddToChain(sourceType, sourceManifest, parentItem string) {
	is.SourceChain = append(is.SourceChain, sourceType+":"+sourceManifest+"->"+parentItem)
	is.ParentItem = parentItem
}

// GetSourceDescription returns a human-readable description of the item's source
func (is ItemSource) GetSourceDescription() string {
	if len(is.SourceChain) == 1 {
		return fmt.Sprintf("from %s in manifest '%s'", is.SourceType, is.SourceManifest)
	}

	description := fmt.Sprintf("from %s in manifest '%s'", is.SourceType, is.SourceManifest)
	for i := 1; i < len(is.SourceChain); i++ {
		description += " -> " + is.SourceChain[i]
	}
	return description
}

// IsUninstallable determines if an item can be uninstalled based on Munki-style logic
// Returns true if the item can be uninstalled, false otherwise
func (item Item) IsUninstallable() bool {
	// If explicitly set, honor that setting
	if item.Uninstallable != nil {
		return *item.Uninstallable
	}

	// OnDemand items are never considered "installed" so can't be uninstalled
	if item.OnDemand {
		return false
	}

	// Auto-determine based on available uninstall methods:

	// 1. If there's an explicit uninstaller array defined
	if len(item.Uninstaller) > 0 {
		return true
	}

	// 2. If installer type supports built-in uninstall (MSI, nupkg, MSIX)
	switch strings.ToLower(item.Installer.Type) {
	case "msi", "nupkg", "msix":
		return true
	}

	// 3. If there are receipts/registry checks that could be used for removal
	if item.Check.Registry.Name != "" && item.Check.Registry.Version != "" {
		return true
	}

	// 4. If there are install items that could be removed
	if len(item.Installs) > 0 {
		return true
	}

	// Default to not uninstallable if no clear uninstall method available
	return false
}

// normalizeVersionForComparison removes leading zeros and trailing .0 segments
// to prepare version strings for semantic comparison
func normalizeVersionForComparison(version string) string {
	if version == "" {
		return "0"
	}
	
	parts := strings.Split(version, ".")
	
	// Remove leading zeros from all segments
	for i, part := range parts {
		if part != "0" && len(part) > 1 {
			newPart := strings.TrimLeft(part, "0")
			if newPart == "" {
				newPart = "0"
			}
			parts[i] = newPart
		}
	}
	
	// Remove trailing ".0" segments
	for len(parts) > 1 && parts[len(parts)-1] == "0" {
		parts = parts[:len(parts)-1]
	}
	
	return strings.Join(parts, ".")
}

// isVersionNewer compares two normalized version strings and returns true if candidate > existing
// Uses semantic version comparison logic
func isVersionNewer(candidate, existing string) bool {
	if candidate == existing {
		return false
	}
	
	candParts := strings.Split(candidate, ".")
	existParts := strings.Split(existing, ".")
	
	// Compare each segment
	maxLen := len(candParts)
	if len(existParts) > maxLen {
		maxLen = len(existParts)
	}
	
	for i := 0; i < maxLen; i++ {
		candVal := 0
		existVal := 0
		
		if i < len(candParts) {
			// Parse as integer, ignore errors (treat as 0)
			fmt.Sscanf(candParts[i], "%d", &candVal)
		}
		
		if i < len(existParts) {
			fmt.Sscanf(existParts[i], "%d", &existVal)
		}
		
		if candVal > existVal {
			return true
		}
		if candVal < existVal {
			return false
		}
		// Equal, continue to next segment
	}
	
	// All segments equal
	return false
}
