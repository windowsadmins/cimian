package catalog

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/report"
	"github.com/windowsadmins/cimian/pkg/utils"
	"gopkg.in/yaml.v3"
)

// Item contains an individual entry from the catalog.
type Item struct {
	Name          string              `yaml:"name"`
	Dependencies  []string            `yaml:"dependencies"`
	DisplayName   string              `yaml:"display_name"`
	Identifier    string              `yaml:"identifier,omitempty"`
	Installer     InstallerItem       `yaml:"installer"`
	Check         InstallCheck        `yaml:"check"`
	Installs      []InstallItem       `yaml:"installs"`
	Uninstaller   InstallerItem       `yaml:"uninstaller"`
	Version       string              `yaml:"version"`
	BlockingApps  []string            `yaml:"blocking_apps"`
	PreScript     utils.LiteralString `yaml:"preinstall_script"`
	PostScript    utils.LiteralString `yaml:"postinstall_script"`
	SupportedArch []string            `yaml:"supported_architectures"`
	// OS version compatibility
	MinOSVersion string `yaml:"minimum_os_version,omitempty"` // Minimum Windows version required
	MaxOSVersion string `yaml:"maximum_os_version,omitempty"` // Maximum Windows version supported
	// Advanced dependency support
	Requires  []string `yaml:"requires,omitempty"`   // Prerequisites that must be installed first
	UpdateFor []string `yaml:"update_for,omitempty"` // Items this package is an update for

	// OnDemand functionality - items that can be run multiple times and never considered "installed"
	OnDemand bool `yaml:"OnDemand,omitempty"` // If true, item can be run on-demand and is never considered installed

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
	Type        string `yaml:"type"`        // "file" or "directory"
	Path        string `yaml:"path"`        // Path to file or directory
	Version     string `yaml:"version"`     // Expected version (file type only)
	MD5Checksum string `yaml:"md5checksum"` // Expected MD5 hash (file type only)
	ProductCode string `yaml:"product_code"`
	UpgradeCode string `yaml:"upgrade_code"`
}

// InstallerItem holds information about how to install a catalog item.
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
			fmt.Println(r)
			report.End()
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

		logging.Info("Downloading catalog", "url", catalogURL, "path", catalogFilePath)

		// Download the catalog file
		if err := download.DownloadFile(catalogURL, catalogFilePath, &cfg); err != nil {
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
		indexedItems := make(map[string]Item)
		for _, it := range wrapper.Items {
			if it.Name != "" {
				indexedItems[it.Name] = it
			}
		}
		catalogMap[catalogCount] = indexedItems

		logging.Info("Successfully processed catalog", catalogName, "items", len(indexedItems))
	}

	return catalogMap
}

// LookForUpdates searches for items that declare they are updates for the given item name.
// This handles updates that aren't simply later versions of the same item.
// For example, AdobeCameraRaw is an update for Adobe Photoshop, but doesn't update
// the version of Adobe Photoshop itself.
// Returns a list of catalog item names that are updates for manifestitem.
func LookForUpdates(itemName string, catalogMap map[int]map[string]Item) []string {
	logging.Debug("Looking for updates for item", "item", itemName)

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
		logging.Debug("Found updates", "count", len(updateList), "updates", updateList, "for", itemName)
	}

	return updateList
}

// LookForUpdatesForVersion searches for updates for a specific version of an item.
// Since these can appear in manifests as item-version or item--version, we search for both.
func LookForUpdatesForVersion(itemName, itemVersion string, catalogMap map[int]map[string]Item) []string {
	nameAndVersion := fmt.Sprintf("%s-%s", itemName, itemVersion)
	altNameAndVersion := fmt.Sprintf("%s--%s", itemName, itemVersion)

	updateList := LookForUpdates(nameAndVersion, catalogMap)
	updateList = append(updateList, LookForUpdates(altNameAndVersion, catalogMap)...)

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
					satisfied = true
					break
				}

				// If specific version required, check if it matches
				if reqVersion != "" && availableVersion != "" {
					// Simple exact version match for now
					// TODO: Implement more sophisticated version comparison
					if strings.EqualFold(availableVersion, reqVersion) {
						satisfied = true
						break
					}
				} else if reqVersion != "" && availableVersion == "" {
					// Required version specified but available item has no version
					// For now, assume it's satisfied if name matches
					satisfied = true
					break
				}
			}
		}

		if !satisfied {
			missingDeps = append(missingDeps, reqItem)
		}
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

	for _, catalog := range catalogMap {
		for name, item := range catalog {
			if strings.ToLower(name) == itemNameLower {
				return item, true
			}
		}
	}

	return Item{}, false
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
