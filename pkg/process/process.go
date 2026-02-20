// pkg/process/process.go - functions for processing install, uninstall, and update actions.

package process

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/installer"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/manifest"
	"github.com/windowsadmins/cimian/pkg/reporting"
	"github.com/windowsadmins/cimian/pkg/status"
	"github.com/windowsadmins/cimian/pkg/utils"
	"gopkg.in/yaml.v3"
)

// WarningError represents a warning-level error (not a hard failure)
// Use this for any non-fatal issues that should be logged as warnings
type WarningError struct {
	Message string
}

func (e WarningError) Error() string {
	return e.Message
}

// IsWarning checks if an error is a warning-level error
func IsWarning(err error) bool {
	_, ok := err.(WarningError)
	return ok
}

// constructPackageURL creates a robust URL for package installers
// This function ensures consistent URL construction across ALL functions in the codebase
func constructPackageURL(location string, cfg *config.Configuration) string {
	if location == "" {
		return ""
	}

	// If already a complete URL, return as-is
	if strings.HasPrefix(location, "http://") || strings.HasPrefix(location, "https://") {
		return location
	}

	// Normalize Windows backslashes to forward slashes for web URLs
	location = strings.ReplaceAll(location, "\\", "/")

	// Ensure base URL doesn't end with slash
	baseURL := strings.TrimRight(cfg.SoftwareRepoURL, "/")
	
	// Handle different path formats:
	if strings.HasPrefix(location, "/") {
		// Absolute path: /apps/software.exe -> baseURL/pkgs/apps/software.exe
		return baseURL + "/pkgs" + location
	} else {
		// Relative path: apps/software.exe -> baseURL/pkgs/apps/software.exe
		return baseURL + "/pkgs/" + location
	}
}

// Global map to track item sources for debugging and logging
var itemSources = make(map[string]catalog.ItemSource)

// SetItemSource records the source information for an item
func SetItemSource(itemName, sourceManifest, sourceType string) {
	itemSources[strings.ToLower(itemName)] = catalog.CreateItemSource(itemName, sourceManifest, sourceType)
}

// AddItemSourceChain adds to the source chain for dependency tracking
func AddItemSourceChain(itemName, sourceType, sourceManifest, parentItem string) {
	key := strings.ToLower(itemName)
	if source, exists := itemSources[key]; exists {
		source.AddToChain(sourceType, sourceManifest, parentItem)
		itemSources[key] = source
	} else {
		// Create new source if it doesn't exist
		source := catalog.CreateItemSource(itemName, sourceManifest, sourceType)
		source.ParentItem = parentItem
		itemSources[key] = source
	}
}

// GetItemSource retrieves the source information for an item
func GetItemSource(itemName string) (catalog.ItemSource, bool) {
	source, exists := itemSources[strings.ToLower(itemName)]
	return source, exists
}

// ClearItemSources clears the global item sources map - should be called at the start of each run
func ClearItemSources() {
	itemSources = make(map[string]catalog.ItemSource)
}

// LogItemSource logs the source information for an item if available
func LogItemSource(itemName string, logMessage string) {
	if source, exists := GetItemSource(itemName); exists {
		logging.Info(logMessage, "source", source.GetSourceDescription())
	} else {
		logging.Info(logMessage, "source", "unknown")
	}
}

// firstItem returns the first occurrence of an item in a map of catalogs
func firstItem(itemName string, catalogsMap map[int]map[string]catalog.Item, cfg *config.Configuration) (catalog.Item, error) {
	// Get the keys in the map and sort them so we can loop over them in order
	keys := make([]int, 0)
	for k := range catalogsMap {
		keys = append(keys, k)
	}
	sort.Ints(keys)

	// Track which catalogs were searched for better error reporting
	var searchedCatalogs []string
	var candidateItems []catalog.Item
	var foundButInvalidItems []catalog.Item // Track items found but lacking installation mechanism
	sysArch := status.GetSystemArchitecture()

	// Search for all items with matching name across all catalogs by reading YAML directly
	// This bypasses the map key collision issue where multiple items with same name overwrite each other
	if cfg != nil {
		for _, catalogName := range cfg.Catalogs {
			searchedCatalogs = append(searchedCatalogs, catalogName)
			
			// Read the catalog YAML file directly to find all items with matching name
			catalogPath := filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`, catalogName+".yaml")
			yamlFile, err := os.ReadFile(catalogPath)
			if err != nil {
				logging.Debug("Failed to read catalog file for architecture search", "catalog", catalogName, "error", err)
				continue
			}

			// Parse the catalog to get all items
			type catalogWrapper struct {
				Items []catalog.Item `yaml:"items"`
			}
			var wrapper catalogWrapper
			if err := yaml.Unmarshal(yamlFile, &wrapper); err != nil {
				logging.Debug("Failed to parse catalog YAML for architecture search", "catalog", catalogName, "error", err)
				continue
			}

			// Find all items with matching name
			for _, item := range wrapper.Items {
				if strings.EqualFold(item.Name, itemName) {
					logging.Debug("Found item in catalog", "item", itemName, "catalog", catalogName, "installer_location", item.Installer.Location, "supported_arch", item.SupportedArch)
					// Check if it's a valid install or uninstall item
					validInstallItem := (item.Installer.Type != "" && item.Installer.Location != "")
					validUninstallItem := len(item.Uninstaller) > 0
					validScriptOnlyItem := (string(item.InstallCheckScript) != "" || string(item.PreScript) != "" || string(item.PostScript) != "")
					// nopkg packages with installs checks are valid (protective packages)
					validNopkgWithInstalls := (item.Installer.Type == "nopkg" && len(item.Installs) > 0)

					if validInstallItem || validUninstallItem || validScriptOnlyItem || validNopkgWithInstalls {
						candidateItems = append(candidateItems, item)
						logging.Debug("Added candidate item", "item", itemName, "catalog", catalogName, "installer_location", item.Installer.Location, "candidate_count", len(candidateItems))
					} else {
						// Item found but has no installation mechanism - track for better error reporting
						foundButInvalidItems = append(foundButInvalidItems, item)
						logging.Warn("Item found but has no installation mechanism", 
							"item", itemName, 
							"catalog", catalogName,
							"installer_type", item.Installer.Type,
							"has_location", item.Installer.Location != "",
							"has_uninstallers", len(item.Uninstaller) > 0,
							"has_scripts", (string(item.InstallCheckScript) != "" || string(item.PreScript) != "" || string(item.PostScript) != ""),
							"has_installs_checks", len(item.Installs) > 0,
							"issue", "nopkg package must have either scripts or installs checks defined")
					}
				}
			}
		}
	}

	// If no items found using direct YAML search, fall back to the original map-based search
	if len(candidateItems) == 0 {
		// Loop through each catalog and collect all matching items
		for _, k := range keys {
			// Map catalog index to catalog name (1-based indexing from AuthenticatedGet)
			var catalogName string
			if cfg != nil && len(cfg.Catalogs) >= k && k >= 1 {
				catalogName = cfg.Catalogs[k-1] // k is 1-based, slice is 0-based
			} else {
				catalogName = fmt.Sprintf("catalog-%d", k)
			}
			if len(searchedCatalogs) == 0 {
				searchedCatalogs = append(searchedCatalogs, catalogName)
			}

			if item, exists := catalogsMap[k][itemName]; exists {
				logging.Debug("Found item in catalog (fallback)", "item", itemName, "catalog", catalogName, "installer_location", item.Installer.Location, "supported_arch", item.SupportedArch)
				// Check if it's a valid install or uninstall item
				validInstallItem := (item.Installer.Type != "" && item.Installer.Location != "")
				validUninstallItem := len(item.Uninstaller) > 0
				validScriptOnlyItem := (string(item.InstallCheckScript) != "" || string(item.PreScript) != "" || string(item.PostScript) != "")
				// nopkg packages with installs checks are valid (protective packages)
				validNopkgWithInstalls := (item.Installer.Type == "nopkg" && len(item.Installs) > 0)

				if validInstallItem || validUninstallItem || validScriptOnlyItem || validNopkgWithInstalls {
					candidateItems = append(candidateItems, item)
					logging.Debug("Added candidate item (fallback)", "item", itemName, "catalog", catalogName, "installer_location", item.Installer.Location, "candidate_count", len(candidateItems))
				} else {
					// Item found but has no installation mechanism - track for better error reporting
					foundButInvalidItems = append(foundButInvalidItems, item)
					logging.Warn("Item found but has no installation mechanism", 
						"item", itemName, 
						"catalog", catalogName,
						"installer_type", item.Installer.Type,
						"has_location", item.Installer.Location != "",
						"has_uninstallers", len(item.Uninstaller) > 0,
						"has_scripts", (string(item.InstallCheckScript) != "" || string(item.PreScript) != "" || string(item.PostScript) != ""),
						"has_installs_checks", len(item.Installs) > 0,
						"issue", "nopkg package must have either scripts or installs checks defined")
				}
			} else {
				logging.Debug("Item not found in catalog", "item", itemName, "catalog", catalogName)
			}
		}
	}

	// If no items found, return error
	if len(candidateItems) == 0 {
		catalogList := strings.Join(searchedCatalogs, ", ")
		
		// Check if we found items but they were invalid
		if len(foundButInvalidItems) > 0 {
			invalidItem := foundButInvalidItems[0]
			// Build detailed error message explaining why the item is invalid
			var reasons []string
			if invalidItem.Installer.Type == "nopkg" {
				reasons = append(reasons, "installer type is 'nopkg' (no package)")
				if invalidItem.Installer.Location == "" {
					reasons = append(reasons, "no installer location")
				}
				if len(invalidItem.Installs) == 0 {
					reasons = append(reasons, "no 'installs' checks defined")
				}
				if string(invalidItem.InstallCheckScript) == "" && string(invalidItem.PreScript) == "" && string(invalidItem.PostScript) == "" {
					reasons = append(reasons, "no installation scripts (preinstall_script, postinstall_script, or installcheck_script)")
				}
			}
			
			reasonStr := strings.Join(reasons, ", ")
			if source, exists := GetItemSource(itemName); exists {
				logging.Error("Item found but cannot be installed - missing installation mechanism", 
					"item", itemName, 
					"source", source.GetSourceDescription(), 
					"catalogs_searched", catalogList,
					"reasons", reasonStr)
				return catalog.Item{}, download.NonRetryableError{Err: fmt.Errorf("item %s found in catalog but cannot be installed: %s (source: %s). A 'nopkg' package must have either scripts (preinstall_script/postinstall_script/installcheck_script) or 'installs' checks defined", itemName, reasonStr, source.GetSourceDescription())}
			}
			
			logging.Error("Item found but cannot be installed - missing installation mechanism", 
				"item", itemName, 
				"source", "unknown - not tracked through manifest processing", 
				"catalogs_searched", catalogList,
				"reasons", reasonStr)
			return catalog.Item{}, download.NonRetryableError{Err: fmt.Errorf("item %s found in catalog but cannot be installed: %s. A 'nopkg' package must have either scripts (preinstall_script/postinstall_script/installcheck_script) or 'installs' checks defined", itemName, reasonStr)}
		}
		
		// Item truly not found in any catalog
		if source, exists := GetItemSource(itemName); exists {
			logging.Warn("Item not found in any catalog", "item", itemName, "source", source.GetSourceDescription(), "catalogs_searched", catalogList)
			return catalog.Item{}, WarningError{Message: fmt.Sprintf("item %s not found in any catalog (source: %s, searched catalogs: %s)", itemName, source.GetSourceDescription(), catalogList)}
		}

		// If no source information is available, provide generic error
		logging.Warn("Item not found in any catalog", "item", itemName, "source", "unknown - not tracked through manifest processing", "catalogs_searched", catalogList)
		return catalog.Item{}, WarningError{Message: fmt.Sprintf("item %s not found in any catalog (source: unknown, searched catalogs: %s)", itemName, catalogList)}
	}

	// Filter by architecture compatibility
	var compatibleItems []catalog.Item
	for _, item := range candidateItems {
		isCompatible := status.SupportsArchitecture(item, sysArch)
		logging.Debug("Checking architecture compatibility", "item", itemName, "system_arch", sysArch, "item_supported_arch", item.SupportedArch, "compatible", isCompatible, "installer_location", item.Installer.Location)
		if isCompatible {
			compatibleItems = append(compatibleItems, item)
		}
	}

	// Return the highest version architecture-compatible item
	if len(compatibleItems) > 0 {
		// Find the item with the highest version among compatible items
		selectedItem := compatibleItems[0]
		for i, candidate := range compatibleItems {
			if i == 0 {
				continue // Skip first item as it's already assigned
			}
			if status.IsOlderVersion(selectedItem.Version, candidate.Version) {
				logging.Debug("Found newer compatible version", "item", itemName, "old_version", selectedItem.Version, "new_version", candidate.Version, "old_installer", selectedItem.Installer.Location, "new_installer", candidate.Installer.Location)
				selectedItem = candidate
			}
		}
		logging.Debug("Selected architecture-compatible item", "item", itemName, "arch", sysArch, "supported_arch", selectedItem.SupportedArch, "version", selectedItem.Version, "installer_location", selectedItem.Installer.Location)
		return selectedItem, nil
	}

	// If no architecture-compatible items, return the highest version item with a warning
	selectedItem := candidateItems[0]
	for i, candidate := range candidateItems {
		if i == 0 {
			continue // Skip first item as it's already assigned
		}
		if status.IsOlderVersion(selectedItem.Version, candidate.Version) {
			selectedItem = candidate
		}
	}
	logging.Warn("No architecture-compatible version found, using highest version available", "item", itemName, "system_arch", sysArch, "selected_arch", selectedItem.SupportedArch, "version", selectedItem.Version, "installer_location", selectedItem.Installer.Location)
	return selectedItem, nil
}

// Manifests iterates through manifests, processes items from managed arrays, and ensures manifest names are excluded.
func Manifests(manifests []manifest.Item, catalogsMap map[int]map[string]catalog.Item) (installs, uninstalls, updates []string) {
	processedManifests := make(map[string]bool) // Track processed manifests to avoid loops

	// Helper function to add valid catalog items to the target list and track their sources
	addValidItems := func(items []string, target *[]string, sourceType, manifestName string) {
		for _, item := range items {
			if item == "" {
				continue
			}

			// Set the source information for this item
			SetItemSource(item, manifestName, sourceType)

			// Validate against the catalog
			valid := false
			for _, catalog := range catalogsMap {
				if _, exists := catalog[item]; exists {
					*target = append(*target, item)
					valid = true
					break
				}
			}
			if !valid {
				LogItemSource(item, "Item not found in catalog")
			}
		}
	}

	// Recursive function to process manifests
	var processManifest func(manifestItem manifest.Item)
	processManifest = func(manifestItem manifest.Item) {
		// Skip already processed manifests
		if processedManifests[manifestItem.Name] {
			return
		}
		processedManifests[manifestItem.Name] = true

		// Process managed arrays with source tracking
		addValidItems(manifestItem.ManagedInstalls, &installs, "managed_installs", manifestItem.Name)
		addValidItems(manifestItem.ManagedUninstalls, &uninstalls, "managed_uninstalls", manifestItem.Name)
		addValidItems(manifestItem.ManagedUpdates, &updates, "managed_updates", manifestItem.Name)
		addValidItems(manifestItem.OptionalInstalls, &installs, "optional_installs", manifestItem.Name)

		// Recursively process included manifests
		for _, included := range manifestItem.Includes {
			for _, nextManifest := range manifests {
				if nextManifest.Name == included {
					processManifest(nextManifest)
					break
				}
			}
		}
	}

	// Start processing manifests
	for _, manifestItem := range manifests {
		processManifest(manifestItem)
	}

	return
}

// This abstraction allows us to override when testing
var installerInstall = installer.Install

// supportsArchitecture checks if the package supports the given system architecture.
func supportsArchitecture(item catalog.Item, systemArch string) bool {
	for _, arch := range item.SupportedArch {
		if arch == systemArch {
			return true
		}
	}
	return false
}

// Installs prepares and then installs an array of items based on system architecture.
func Installs(installs []string, catalogsMap map[int]map[string]catalog.Item, _, cachePath string, CheckOnly bool, cfg *config.Configuration) {
	systemArch := status.GetSystemArchitecture()
	logging.Info("System architecture detected", "architecture", systemArch)

	// Iterate through the installs array, install dependencies, and then the item itself
	for _, item := range installs {
		// Get the first valid item from our catalogs
		validItem, err := firstItem(item, catalogsMap, cfg)
		if err != nil {
			logging.Error("Processing Error", "error", err)
			logging.Warn("Processing warning: failed to process install item", "error", err)
			continue
		}

		// Ensure the URL is properly constructed using robust function
		if validItem.Installer.Location != "" {
			validItem.Installer.Location = constructPackageURL(validItem.Installer.Location, cfg)
			logging.Debug("Package download URL", "url", validItem.Installer.Location)
		}

		// Check if the package supports the system architecture
		if !supportsArchitecture(validItem, systemArch) {
			logging.Info("Skipping installation due to architecture mismatch", "package", validItem.Name, "required_architectures", validItem.SupportedArch, "system_architecture", systemArch)
			continue
		}

		// Check for dependencies and install if found
		if len(validItem.Dependencies) > 0 {
			for _, dependency := range validItem.Dependencies {
				validDependency, err := firstItem(dependency, catalogsMap, cfg)
				if err != nil {
					logging.Error("Processing Error", "error", err)
					logging.Warn("Processing warning: failed to process dependency", "error", err)
					continue
				}

				// Check if the dependency supports the system architecture
				if !supportsArchitecture(validDependency, systemArch) {
					logging.Info("Skipping dependency installation due to architecture mismatch", "package", validDependency.Name, "required_architectures", validDependency.SupportedArch, "system_architecture", systemArch)
					continue
				}

				if isOnDemandItem(validDependency) {
					handleOnDemandInstall(validDependency, cachePath, CheckOnly, cfg)
				} else {
					// Download the file first for dependencies
					logging.Debug("Calling downloadItemFile from dependency processing", "dependency", validDependency.Name)
					localFile, err := downloadItemFile(validDependency, cfg, 0, nil)
					if err != nil {
						logging.Error("Failed to download dependency", "dependency", validDependency.Name, "error", err)
						continue
					}
					installerInstall(validDependency, "install", localFile, cachePath, CheckOnly, cfg)
				}
			}
		}

		// Install the item
		if isOnDemandItem(validItem) {
			handleOnDemandInstall(validItem, cachePath, CheckOnly, cfg)
		} else {
			// Debug logging for script-only detection
			logging.Debug("Script-only detection in process.go",
				"item", validItem.Name,
				"installer_type", validItem.Installer.Type,
				"installer_location", validItem.Installer.Location,
				"installcheck_script_len", len(string(validItem.InstallCheckScript)),
				"preinstall_script_len", len(string(validItem.PreScript)),
				"postinstall_script_len", len(string(validItem.PostScript)))

			// Check if this is a script-only item (no installer file needed)
			if validItem.Installer.Type == "" && validItem.Installer.Location == "" &&
				(string(validItem.InstallCheckScript) != "" || string(validItem.PreScript) != "" || string(validItem.PostScript) != "") {
				// Script-only item - call installer directly without downloading
				logging.Debug("Processing script-only item, skipping download", "item", validItem.Name)
				installerInstall(validItem, "install", "", cachePath, CheckOnly, cfg)
			} else {
				logging.Debug("Not script-only item, proceeding with download", "item", validItem.Name)
				localFile, err := downloadItemFile(validItem, cfg, 0, nil)
				if err != nil {
					logging.Error("Failed to download item", "item", validItem.Name, "error", err)
					continue
				}
				installerInstall(validItem, "install", localFile, cachePath, CheckOnly, cfg)
			}
		}
	}
}

// Uninstalls prepares and then uninstalls an array of items
func Uninstalls(uninstalls []string, catalogsMap map[int]map[string]catalog.Item, _, cachePath string, CheckOnly bool, cfg *config.Configuration) {
	// Iterate through the uninstalls array and uninstall the item
	for _, item := range uninstalls {
		// Get the first valid item from our catalogs
		validItem, err := firstItem(item, catalogsMap, cfg)
		if err != nil {
			logging.Error("Processing Error", "error", err)
			logging.Warn("Processing warning: failed to process uninstall item", "error", err)
			continue
		}
		// Uninstall the item
		installerInstall(validItem, "uninstall", "", cachePath, CheckOnly, cfg)
	}
}

// Updates prepares and then updates an array of items
func Updates(updates []string, catalogsMap map[int]map[string]catalog.Item, _, cachePath string, CheckOnly bool, cfg *config.Configuration) {
	// Iterate through the updates array
	for _, item := range updates {
		validItem, err := firstItem(item, catalogsMap, cfg)
		if err != nil {
			logging.Error("Processing Error", "error", err)
			continue
		}

		// Construct proper URL for the installer using robust function
		if validItem.Installer.Location != "" {
			validItem.Installer.Location = constructPackageURL(validItem.Installer.Location, cfg)
			logging.Debug("Package installer URL", "url", validItem.Installer.Location)
		}

		if isOnDemandItem(validItem) {
			handleOnDemandInstall(validItem, cachePath, CheckOnly, cfg)
		} else {
			installerInstall(validItem, "update", "", cachePath, CheckOnly, cfg)
		}
	}
}

// dirEmpty returns true if the directory is empty
func dirEmpty(path string) bool {
	f, err := os.Open(path)
	if err != nil {
		logging.Error("Processing Error", "error", err)
		return false
	}
	defer f.Close()

	// Try to get the first item in the directory
	_, err = f.Readdir(1)

	// If we receive an EOF error, the dir is empty
	return err == io.EOF
}

// fileOld returns true if the file is older than the configured retention period
func fileOld(info os.FileInfo, cfg *config.Configuration) bool {
	// Age of the file
	fileAge := time.Since(info.ModTime())

	// Use configured retention days, default to 1 day if not set
	days := cfg.CacheRetentionDays
	if days == 0 {
		days = 1 // Much more aggressive default - 1 day instead of 5
	}

	// Convert from days
	hours := days * 24
	ageLimit := time.Duration(hours) * time.Hour

	// If the file is older than our limit, return true
	return fileAge > ageLimit
}

// This abstraction allows us to override when testing
var osRemove = os.Remove

// CacheStatistics holds information about cache directory
type CacheStatistics struct {
	TotalSize     int64
	TotalFiles    int
	OldestFileAge time.Duration
}

// getCacheStatistics analyzes the cache directory and returns statistics
func getCacheStatistics(cachePath string) CacheStatistics {
	stats := CacheStatistics{}
	oldestTime := time.Now()

	filepath.Walk(cachePath, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}

		stats.TotalSize += info.Size()
		stats.TotalFiles++

		if info.ModTime().Before(oldestTime) {
			oldestTime = info.ModTime()
		}

		return nil
	})

	stats.OldestFileAge = time.Since(oldestTime)
	return stats
}

// getCurrentlyInstalledItems returns a map of currently installed software items
// This helps preserve cache files for software that's still installed
func getCurrentlyInstalledItems() map[string]bool {
	installed := make(map[string]bool)
	
	// Try to read the ManagedInstallReport.plist equivalent or use Windows registry
	// For now, we'll use a simple heuristic based on common installation tracking
	
	// Check Windows Programs and Features
	if installedFromRegistry := getInstalledFromRegistry(); len(installedFromRegistry) > 0 {
		for _, item := range installedFromRegistry {
			installed[strings.ToLower(item)] = true
		}
	}

	// TODO: Add logic to read Cimian's own installation tracking
	// This would be more accurate than registry-based detection

	return installed
}

// getInstalledFromRegistry gets a list of installed software from Windows Registry
func getInstalledFromRegistry() []string {
	var installed []string
	
	// This is a simplified version - in production you might want to read from
	// HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall
	// and HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall
	
	return installed
}

// shouldPreserveFile determines if a cache file should be preserved based on installed items
func shouldPreserveFile(fileName string, installedItems map[string]bool) bool {
	// Extract potential software names from filename
	// Common patterns: "SoftwareName-1.2.3.exe", "SoftwareName_x64.msi", etc.
	
	fileName = strings.ToLower(fileName)
	
	// Remove common file extensions and version patterns
	baseName := strings.TrimSuffix(fileName, filepath.Ext(fileName))
	
	// Split on common separators and check each part
	parts := strings.FieldsFunc(baseName, func(c rune) bool {
		return c == '-' || c == '_' || c == '.' || c == ' '
	})
	
	for _, part := range parts {
		if len(part) > 3 && installedItems[part] { // Only check parts longer than 3 chars
			return true
		}
	}
	
	return false
}

// performSizeBasedCleanup removes files to meet size constraints
func performSizeBasedCleanup(cachePath string, cfg *config.Configuration, maxSizeBytes int64, 
	installedItems map[string]bool, removedFiles *int, removedSize *int64) {
	
	logging.Info("Performing size-based cache cleanup", "targetSizeGB", cfg.CacheMaxSizeGB)
	
	// Collect all files with their info for sorting
	type fileInfo struct {
		path     string
		info     os.FileInfo
		preserve bool
	}
	
	var files []fileInfo
	
	filepath.Walk(cachePath, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		
		preserve := cfg.GetCachePreserveInstalledItems() && shouldPreserveFile(info.Name(), installedItems)
		files = append(files, fileInfo{
			path:     path,
			info:     info,
			preserve: preserve,
		})
		
		return nil
	})
	
	// Sort by modification time (oldest first) but put preserved files last
	sort.Slice(files, func(i, j int) bool {
		if files[i].preserve != files[j].preserve {
			return !files[i].preserve // Non-preserved files come first
		}
		return files[i].info.ModTime().Before(files[j].info.ModTime())
	})
	
	// Remove files until we're under the size limit
	currentSize := getCacheStatistics(cachePath).TotalSize
	
	for _, file := range files {
		if currentSize <= maxSizeBytes {
			break
		}
		
		if file.preserve {
			logging.Debug("Skipping preserved file during size-based cleanup", "file", file.info.Name())
			continue
		}
		
		if err := osRemove(file.path); err != nil {
			logging.Error("Failed to remove file during size-based cleanup", "file", file.path, "error", err)
		} else {
			*removedFiles++
			*removedSize += file.info.Size()
			currentSize -= file.info.Size()
			logging.Debug("Removed file for size constraint", "file", file.info.Name(), 
				"sizeMB", fmt.Sprintf("%.2f", float64(file.info.Size())/(1024*1024)))
		}
	}
}

// cleanupEmptyDirectories removes empty directories from the cache
func cleanupEmptyDirectories(cachePath string) {
	filepath.Walk(cachePath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}

		// If a dir and empty, delete
		if info.IsDir() && path != cachePath && dirEmpty(path) {
			logging.Debug("Cleaning empty directory", "directory", info.Name())
			if err := osRemove(path); err != nil {
				logging.Error("Failed to remove empty directory", "directory", path, "error", err)
			}
		}
		return nil
	})
}

// CleanUp performs intelligent cache cleanup based on configuration settings
// This now supports size-based and age-based cleanup with preservation of currently installed items
func CleanUp(cachePath string, cfg *config.Configuration) {
	logging.Info("Starting cache cleanup", "path", cachePath, "retentionDays", cfg.CacheRetentionDays, "maxSizeGB", cfg.CacheMaxSizeGB)
	
	// First, get current cache statistics
	cacheStats := getCacheStatistics(cachePath)
	logging.Info("Current cache statistics", 
		"totalSizeGB", fmt.Sprintf("%.2f", float64(cacheStats.TotalSize)/(1024*1024*1024)),
		"totalFiles", cacheStats.TotalFiles,
		"oldestFileAge", cacheStats.OldestFileAge.String())

	// Check if we need aggressive cleanup based on cache size
	maxSizeBytes := int64(cfg.CacheMaxSizeGB) * 1024 * 1024 * 1024
	needsSizeBasedCleanup := cacheStats.TotalSize > maxSizeBytes

	if needsSizeBasedCleanup {
		logging.Warn("Cache size exceeds maximum, performing aggressive cleanup", 
			"currentSizeGB", fmt.Sprintf("%.2f", float64(cacheStats.TotalSize)/(1024*1024*1024)),
			"maxSizeGB", cfg.CacheMaxSizeGB)
	}

	// Get list of currently installed software to preserve their cache if configured
	installedItems := make(map[string]bool)
	if cfg.GetCachePreserveInstalledItems() {
		installedItems = getCurrentlyInstalledItems()
		logging.Info("Cache preservation enabled", "installedItemsCount", len(installedItems))
	}

	var removedFiles int
	var removedSize int64
	var preservedFiles int

	// Age-based cleanup - remove old files first
	err := filepath.Walk(cachePath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			logging.Warn("Failed to access path during cleanup", "path", path, "error", err)
			return nil // Continue walking
		}
		
		// Skip directories
		if info.IsDir() {
			return nil
		}

		// Check if this file should be preserved based on installed items
		fileName := info.Name()
		if cfg.GetCachePreserveInstalledItems() && shouldPreserveFile(fileName, installedItems) {
			preservedFiles++
			logging.Debug("Preserving cache file for installed item", "file", fileName)
			return nil
		}

		// Check if file is old enough to be removed
		if fileOld(info, cfg) {
			logging.Debug("Removing old cached file", "file", fileName, "age", time.Since(info.ModTime()).String())
			if err := osRemove(path); err != nil {
				logging.Error("Failed to remove old file", "file", path, "error", err)
			} else {
				removedFiles++
				removedSize += info.Size()
			}
		}
		return nil
	})
	
	if err != nil {
		logging.Error("Error during age-based cleanup", "error", err)
	}

	// Size-based cleanup - if cache is still too large, remove more files
	if needsSizeBasedCleanup {
		// Get updated cache size after age-based cleanup
		updatedStats := getCacheStatistics(cachePath)
		if updatedStats.TotalSize > maxSizeBytes {
			performSizeBasedCleanup(cachePath, cfg, maxSizeBytes, installedItems, &removedFiles, &removedSize)
		}
	}

	// Clean up empty directories
	cleanupEmptyDirectories(cachePath)

	// Log cleanup results
	logging.Info("Cache cleanup completed", 
		"removedFiles", removedFiles,
		"removedSizeMB", fmt.Sprintf("%.2f", float64(removedSize)/(1024*1024)),
		"preservedFiles", preservedFiles)

	// Log final cache statistics
	finalStats := getCacheStatistics(cachePath)
	logging.Info("Final cache statistics", 
		"totalSizeGB", fmt.Sprintf("%.2f", float64(finalStats.TotalSize)/(1024*1024*1024)),
		"totalFiles", finalStats.TotalFiles)
}

// ProcessInstallWithDependencies processes an install item with full dependency handling
// including requires, update_for, and architecture checks
func ProcessInstallWithDependencies(itemName string, catalogsMap map[int]map[string]catalog.Item,
	installedItems []string, scheduledItems []string, cachePath string, checkOnly bool, cfg *config.Configuration) error {

	logging.Debug("Processing install with dependencies", "item", itemName)

	// Get the item from catalogs
	item, err := firstItem(itemName, catalogsMap, cfg)
	if err != nil {
		return fmt.Errorf("item not found in catalogs: %v", err)
	}

	systemArch := status.GetSystemArchitecture()

	// Check architecture support
	if !supportsArchitecture(item, systemArch) {
		logging.Info("Skipping installation due to architecture mismatch",
			"package", item.Name, "required_architectures", item.SupportedArch, "system_architecture", systemArch)
		return nil
	}

	// Check and install requires dependencies first
	if item.Requires != nil {
		logging.Debug("Processing requires dependencies", "item", itemName, "requiresCount", len(item.Requires), "requires", item.Requires)
		missingDeps := catalog.CheckDependencies(item, installedItems, scheduledItems)
		
		if len(missingDeps) > 0 {
			logging.Info("Found missing dependencies that need to be installed", "item", itemName, "missingDependencies", missingDeps)
		}
		
		for _, dep := range missingDeps {
			logging.Info("Installing required dependency", "dependency", dep, "for", itemName)

			// Check if the dependency exists in the catalog before trying to install it
			depName, _ := catalog.SplitNameAndVersion(dep)
			_, depErr := firstItem(depName, catalogsMap, cfg)
			if depErr != nil {
				logging.Error("Required dependency not found in catalog", "dependency", depName, "for", itemName, "error", depErr)
				return fmt.Errorf("required dependency %s not found in catalog for %s: %v", depName, itemName, depErr)
			}

			// Recursively process the dependency
			if err := ProcessInstallWithDependencies(dep, catalogsMap, installedItems,
				append(scheduledItems, dep), cachePath, checkOnly, cfg); err != nil {
				logging.Error("Failed to install required dependency", "dependency", dep, "error", err)
				return fmt.Errorf("failed to install required dependency %s: %v", dep, err)
			}
			// Add to scheduled items for future dependency checks
			scheduledItems = append(scheduledItems, dep)
			logging.Debug("Successfully processed dependency", "dependency", dep, "for", itemName)
		}
	} else {
		logging.Debug("No requires dependencies defined", "item", itemName)
	}

	// Handle legacy dependencies field as well
	if len(item.Dependencies) > 0 {
		for _, dependency := range item.Dependencies {
			validDependency, err := firstItem(dependency, catalogsMap, cfg)
			if err != nil {
				logging.Error("Failed to process legacy dependency", "dependency", dependency, "error", err)
				continue
			}

			if !supportsArchitecture(validDependency, systemArch) {
				logging.Info("Skipping legacy dependency installation due to architecture mismatch",
					"package", validDependency.Name, "required_architectures", validDependency.SupportedArch, "system_architecture", systemArch)
				continue
			}

			if isOnDemandItem(validDependency) {
				handleOnDemandInstall(validDependency, cachePath, checkOnly, cfg)
			} else {
				// Download the file first for dependencies too
				localFile, err := downloadItemFile(validDependency, cfg, 0, nil)
				if err != nil {
					logging.Error("Failed to download dependency", "dependency", validDependency.Name, "error", err)
					continue
				}
				installerInstall(validDependency, "install", localFile, cachePath, checkOnly, cfg)
			}
		}
	}

	// Install the main item
	logging.Info("Installing main item", "item", itemName)
	if isOnDemandItem(item) {
		handleOnDemandInstall(item, cachePath, checkOnly, cfg)
	} else {
		// Check if this is a script-only item (no installer file needed)
		if item.Installer.Type == "" && item.Installer.Location == "" &&
			(string(item.InstallCheckScript) != "" || string(item.PreScript) != "" || string(item.PostScript) != "") {
			// Script-only item - call installer directly without downloading
			logging.Debug("Processing script-only item in ProcessInstallWithDependencies", "item", item.Name)
			installerInstall(item, "install", "", cachePath, checkOnly, cfg)
		} else {
			// Download the file first
			logging.Debug("Not script-only item in ProcessInstallWithDependencies", "item", item.Name)
			localFile, err := downloadItemFile(item, cfg, 0, nil)
			if err != nil {
				logging.Warn("Failed to download item", "item", item.Name, "error", err)
				return WarningError{Message: fmt.Sprintf("failed to download item %s: %v", itemName, err)}
			}
			installerInstall(item, "install", localFile, cachePath, checkOnly, cfg)
		}
	}

	// Look for updates that should be applied after this install
	// Only process if the target item is actually installed
	updateList := catalog.LookForUpdates(itemName, catalogsMap, append(installedItems, itemName))
	for _, updateItem := range updateList {
		logging.Info("Installing update for item", "update", updateItem, "for", itemName)
		if err := ProcessInstallWithDependencies(updateItem, catalogsMap,
			append(installedItems, itemName), scheduledItems, cachePath, checkOnly, cfg); err != nil {
			logging.Warn("Failed to install update item", "update", updateItem, "error", err)
		}
	}

	return nil
}

// ProcessUninstallWithDependencies processes an uninstall item with dependency checking
// Removes dependent items first, then the main item
func ProcessUninstallWithDependencies(itemName string, catalogsMap map[int]map[string]catalog.Item,
	installedItems []string, cachePath string, checkOnly bool, cfg *config.Configuration) error {

	logging.Debug("Processing uninstall with dependencies", "item", itemName)

	// Find items that require this item
	dependentItems := catalog.FindItemsRequiring(itemName, catalogsMap)

	// Remove dependent items first
	for _, depItem := range dependentItems {
		// Check if dependent item is actually installed
		depInstalled := false
		for _, installed := range installedItems {
			if strings.EqualFold(installed, depItem.Name) {
				depInstalled = true
				break
			}
		}

		if depInstalled {
			logging.Info("Removing dependent item first", "dependent", depItem.Name, "required_by", itemName)
			if err := ProcessUninstallWithDependencies(depItem.Name, catalogsMap,
				installedItems, cachePath, checkOnly, cfg); err != nil {
				logging.Error("Failed to remove dependent item", "dependent", depItem.Name, "error", err)
				return fmt.Errorf("failed to remove dependent item %s: %v", depItem.Name, err)
			}
		}
	}

	// Get the main item and uninstall it
	item, err := firstItem(itemName, catalogsMap, cfg)
	if err != nil {
		return fmt.Errorf("item not found in catalogs: %v", err)
	}

	logging.Info("Uninstalling main item", "item", itemName)
	installerInstall(item, "uninstall", "", cachePath, checkOnly, cfg)

	// Look for and remove any updates for this item
	// Pass installedItems to check if the target item was installed before uninstall
	updateList := catalog.LookForUpdates(itemName, catalogsMap, installedItems)
	for _, updateItem := range updateList {
		// Check if update item is installed
		updateInstalled := false
		for _, installed := range installedItems {
			if strings.EqualFold(installed, updateItem) {
				updateInstalled = true
				break
			}
		}

		if updateInstalled {
			logging.Info("Removing update item", "update", updateItem, "for", itemName)
			if err := ProcessUninstallWithDependencies(updateItem, catalogsMap,
				installedItems, cachePath, checkOnly, cfg); err != nil {
				logging.Warn("Failed to remove update item", "update", updateItem, "error", err)
			}
		}
	}

	return nil
}

// InstallsWithDependencies processes installs with full dependency logic
// This function handles requires, update_for relationships, and proper dependency ordering
func InstallsWithDependencies(itemNames []string, catalogsMap map[int]map[string]catalog.Item,
	installedItems []string, cachePath string, checkOnly bool, cfg *config.Configuration) error {
	// Track processed items to avoid infinite loops
	processedInstalls := make(map[string]bool)
	// Process each item recursively with full dependency logic
	for _, itemName := range itemNames {
		if err := processInstallWithAdvancedLogic(itemName, catalogsMap, installedItems,
			processedInstalls, cachePath, checkOnly, cfg, 0, utils.NewNoOpReporter()); err != nil {
			LogItemSource(itemName, "Failed to process install of item: "+itemName)
			return err
		}
	}

	return nil
}

// UninstallsWithDependencies processes uninstalls with full dependency logic
// This function handles dependency checking and proper removal ordering
func UninstallsWithDependencies(itemNames []string, catalogsMap map[int]map[string]catalog.Item,
	installedItems []string, cachePath string, checkOnly bool, cfg *config.Configuration) error {

	// Track processed items to avoid infinite loops
	processedUninstalls := make(map[string]bool) // Process each item recursively with full dependency logic
	for _, itemName := range itemNames {
		if err := processUninstallWithAdvancedLogic(itemName, catalogsMap, installedItems,
			processedUninstalls, cachePath, checkOnly, cfg); err != nil {
			logging.Error("Failed to process uninstall with dependency logic", "item", itemName, "error", err)
			return err
		}
	}

	return nil
}

// InstallsWithAdvancedLogic processes installation of items with full dependency resolution
// Recursively handles requires and update_for relationships
// Returns error only if ALL items fail, continues processing other items if some fail
func InstallsWithAdvancedLogic(itemNames []string, catalogsMap map[int]map[string]catalog.Item,
	installedItems []string, cachePath string, checkOnly bool, cfg *config.Configuration, verbosity int, reporter utils.Reporter, exporter *reporting.DataExporter) error {
	// Track processed items to avoid infinite loops
	processedInstalls := make(map[string]bool)
	var failedItems []string
	var msiFailedItems []string // Track MSI-specific failures for service recovery
	var successCount int

	// PROGRESSIVE REPORTING: Use the provided exporter instance that has current session package data

	// Process each item recursively with full dependency logic
	for i, itemName := range itemNames {
		err := processInstallWithAdvancedLogic(itemName, catalogsMap, installedItems,
			processedInstalls, cachePath, checkOnly, cfg, verbosity, reporter)
		
		// PROGRESSIVE REPORTING: Update items.json after each item is processed
		var itemStatus string
		var errorMsg string
		var warningMsg string
		if err != nil {
			// Check if this is a warning-level error (non-fatal issue)
			if IsWarning(err) {
				itemStatus = "Warning"
				warningMsg = err.Error()
				errorMsg = "" // This is a warning, not an error
				// Don't add to failedItems since this is just a warning, not a hard failure
			} else {
				itemStatus = "failed"
				errorMsg = err.Error()
				// Error already logged by processInstallWithAdvancedLogic or firstItem, just track the failure
				failedItems = append(failedItems, itemName)
				
				// Check if this is an MSI service-related failure
				if isMSIServiceFailure(err) {
					msiFailedItems = append(msiFailedItems, itemName)
					logging.Warn("MSI service failure detected for item", "item", itemName, "error", err)
					
					// If there are more items to process, attempt MSI service recovery
					if i < len(itemNames)-1 {
						logging.Info("Attempting MSI service recovery before next item", 
							"failedItem", itemName, 
							"remainingItems", len(itemNames)-i-1,
							"nextItem", itemNames[i+1])
						
						if recoveryErr := recoverMSIServiceBetweenItems(cfg); recoveryErr != nil {
							logging.Warn("MSI service recovery failed", "error", recoveryErr)
						} else {
							logging.Info("MSI service recovery completed successfully")
						}
					}
				}
			}
		} else {
			itemStatus = "completed"
			errorMsg = "" // Clear any previous error
			successCount++
		}

		// PROGRESSIVE REPORTING: Export updated items.json after each item
		if !checkOnly { // Only during actual installations
			var exportErr error
			if warningMsg != "" {
				exportErr = exporter.ExportItemProgressUpdate(3, itemName, itemStatus, errorMsg, warningMsg)
			} else {
				exportErr = exporter.ExportItemProgressUpdate(3, itemName, itemStatus, errorMsg)
			}
			
			if exportErr != nil {
				logging.Debug("Failed to export progressive item update", "item", itemName, "error", exportErr)
				// Don't fail the installation if reporting fails
			} else {
				logging.Debug("Progressive item report updated", "item", itemName, "status", itemStatus, "progress", fmt.Sprintf("%d/%d", i+1, len(itemNames)))
			}
		}
	}

	// Log comprehensive summary of results
	if len(failedItems) > 0 {
		if len(msiFailedItems) > 0 {
			logging.Warn("Installation summary with MSI service issues",
				"failed", failedItems,
				"msiServiceFailures", msiFailedItems,
				"succeeded", successCount, 
				"total", len(itemNames),
				"recommendedAction", "check_system_for_pending_reboots_or_locked_installers")
		} else {
			logging.Warn("Installation summary", "failed", failedItems, "succeeded", successCount, "total", len(itemNames))
		}
		
		// Return error if ANY items failed to ensure proper session status tracking
		return fmt.Errorf("failed: %v succeeded: %d total: %d", failedItems, successCount, len(itemNames))
	} else {
		if successCount > 0 {
			logging.Info("All items installed successfully", "count", successCount)
		}
	}

	return nil
}

// UninstallsWithAdvancedLogic processes uninstalls with full dependency logic
// This function handles dependency checking and proper removal ordering
// Returns error only if ALL items fail, continues processing other items if some fail
func UninstallsWithAdvancedLogic(itemNames []string, catalogsMap map[int]map[string]catalog.Item,
	installedItems []string, cachePath string, checkOnly bool, cfg *config.Configuration) error {
	// Track processed items to avoid infinite loops
	processedUninstalls := make(map[string]bool)
	var failedItems []string
	var successCount int

	// Process each item recursively with full dependency logic
	for _, itemName := range itemNames {
		if err := processUninstallWithAdvancedLogic(itemName, catalogsMap, installedItems,
			processedUninstalls, cachePath, checkOnly, cfg); err != nil {
			LogItemSource(itemName, "Failed to process uninstall of item: "+itemName)
			logging.Error("Failed to uninstall item, continuing with others", "item", itemName, "error", err)
			failedItems = append(failedItems, itemName)
		} else {
			successCount++
		}
	}

	// Log summary of results
	if len(failedItems) > 0 {
		logging.Warn("Some items failed to uninstall:", "failed", failedItems, "succeeded", successCount, "total", len(itemNames))
		// Only return error if ALL items failed
		if successCount == 0 {
			return fmt.Errorf("all %d items failed to uninstall: %v", len(itemNames), failedItems)
		}
	} else {
		logging.Info("All items uninstalled successfully", "count", successCount)
	}

	return nil
}

// processInstallWithAdvancedLogic handles installation with full advanced dependency logic
func processInstallWithAdvancedLogic(itemName string, catalogsMap map[int]map[string]catalog.Item,
	installedItems []string, processedInstalls map[string]bool,
	cachePath string, checkOnly bool, cfg *config.Configuration, verbosity int, reporter utils.Reporter) error {

	// Check if already processed to avoid loops
	if processedInstalls[itemName] {
		logging.Debug("Item already processed for install", "item", itemName)
		return nil
	}

	LogItemSource(itemName, "Processing install of item: "+itemName)

	// Mark as processed early to avoid infinite recursion
	processedInstalls[itemName] = true

	// Get the main item
	item, err := firstItem(itemName, catalogsMap, cfg)
	if err != nil {
		// firstItem already logs the error with source information, just return the non-retryable error
		return err
	}

	// Process requires dependencies first
	if len(item.Requires) > 0 {
		logging.Debug("Processing requires dependencies", "item", itemName, "requires", item.Requires)

		for _, req := range item.Requires {
			reqItemName, _ := catalog.SplitNameAndVersion(req)

			// Track that this requirement came from the parent item
			AddItemSourceChain(reqItemName, "requires", "dependency-chain", itemName)

			// Check if requirement is already processed (installed or scheduled in this session)
			// Use processedInstalls map which is properly shared across recursive calls
			if processedInstalls[reqItemName] {
				logging.Debug("Requirement already processed in this session", "item", itemName, "requirement", req)
				continue
			}
			
			// Also check if requirement is in the installed items list (already installed before this session)
			if isItemInList(reqItemName, installedItems) {
				logging.Debug("Requirement already installed before this session - verifying status", "item", itemName, "requirement", req)
				// Do NOT skip - proceed to recursive call to verify status (CheckStatus will be called inside)
			}

			// Recursively install the required item
			LogItemSource(reqItemName, "Installing required dependency")

			if err := processInstallWithAdvancedLogic(reqItemName, catalogsMap, installedItems,
				processedInstalls, cachePath, checkOnly, cfg, verbosity, reporter); err != nil {
				logging.Error("Failed to install required dependency", "dependency", reqItemName, "error", err)
				return fmt.Errorf("failed to install required dependency %s: %v", reqItemName, err)
			}
		}
	}

	// Install the main item
	LogItemSource(itemName, "Installing item: "+itemName)

	// Check if the item needs to be installed
	// ALWAYS call CheckStatus to determine if install is actually needed,
	// regardless of whether the item appears in installedItems list.
	// This ensures items with installcheck_script are properly evaluated
	// even if they don't appear in registry-based detection.
	needs, err := status.CheckStatus(item, "install", cachePath)
	needsInstall := true
	if err != nil {
		logging.Warn("Failed to check status for item, assuming install needed", "item", itemName, "error", err)
	} else if !needs {
		logging.Info("Item is already installed and up to date, skipping install", "item", itemName)
		needsInstall = false
	} else {
		if isItemInList(itemName, installedItems) {
			logging.Info("Item is installed but needs update/repair", "item", itemName)
		} else {
			logging.Info("Item needs installation", "item", itemName)
		}
	}

	if needsInstall {
		if isOnDemandItem(item) {
			handleOnDemandInstall(item, cachePath, checkOnly, cfg)
		} else {
			// Check if this is a script-only item (no installer file needed)
			// This includes both empty installer type and explicit "nopkg" type
			if (item.Installer.Type == "" || item.Installer.Type == "nopkg") && item.Installer.Location == "" &&
				(string(item.InstallCheckScript) != "" || string(item.PreScript) != "" || string(item.PostScript) != "") {
				// Script-only item - call installer directly without downloading
				logging.Debug("Processing script-only item in processInstallWithAdvancedLogic", "item", item.Name, "type", item.Installer.Type)
				_, err := installerInstall(item, "install", "", cachePath, checkOnly, cfg)
				if err != nil {
					return fmt.Errorf("failed to install script-only item %s: %v", itemName, err)
				}
			} else {
				// Download the file first, then install it
				logging.Debug("Not script-only item in processInstallWithAdvancedLogic", "item", item.Name)
				logging.Debug("About to call downloadItemFile", "item", item.Name)
				localFile, err := downloadItemFile(item, cfg, verbosity, reporter)
				if err != nil {
					logging.Warn("Failed to download item", "item", item.Name, "error", err)
					logging.Debug("downloadItemFile returned", "item", item.Name, "localFile", localFile, "error", "FAILED")
					return WarningError{Message: fmt.Sprintf("failed to download item %s: %v", itemName, err)}
				}
				logging.Debug("downloadItemFile returned", "item", item.Name, "localFile", localFile, "error", "<nil>")

				// DEBUG: Add explicit logging before installer call
				logging.Info("About to call installerInstall", "item", item.Name, "localFile", localFile)
				logging.Debug("Calling installerInstall now", "item", item.Name, "action", "install", "localFile", localFile, "cachePath", cachePath, "checkOnly", checkOnly)
				result, err := installerInstall(item, "install", localFile, cachePath, checkOnly, cfg)
				if err != nil {
					// Log detailed error with proper event for reporting system
					logging.Error("Installation failed", "item", item.Name, "error", err)

					// Determine installer type from file path
					installerType := determineInstallerTypeFromPath(localFile)

					// Create detailed event for ReportMate with enhanced package context
					logging.LogEventEntry("install", "install_package", logging.StatusError,
						fmt.Sprintf("Installation failed: %v", err),
						logging.WithPackageEnhanced(
							generatePackageID(item.Name), // Package ID
							item.Name,                    // Package Name
							item.Version,                 // Package Version
							installerType,                // Installer Type
						),
						logging.WithContext("installer_path", localFile),
						logging.WithError(err))

					return fmt.Errorf("failed to install item %s: %v", itemName, err)
				}
				logging.Info("installerInstall completed successfully", "item", item.Name, "result", result)
			}
		}
	}

	// Process 'update_for' items (items that are updates for this item)
	// Only process if the target item is actually installed
	updateList := catalog.LookForUpdates(itemName, catalogsMap, installedItems)
	if len(updateList) > 0 {
		logging.Debug("Processing update_for items", "item", itemName, "updates", updateList)
		for _, updateItem := range updateList {
			// Track that this update item came from the parent item
			AddItemSourceChain(updateItem, "update_for", "dependency-chain", itemName)

			LogItemSource(updateItem, "Installing update item")

			if err := processInstallWithAdvancedLogic(updateItem, catalogsMap, installedItems,
				processedInstalls, cachePath, checkOnly, cfg, verbosity, reporter); err != nil {
				logging.Warn("Failed to install update item", "update", updateItem, "error", err)
				// Don't fail the main install if an update installation fails
			}
		}
	}

	return nil
}

// processUninstallWithAdvancedLogic handles uninstallation with full dependency logic
func processUninstallWithAdvancedLogic(itemName string, catalogsMap map[int]map[string]catalog.Item,
	installedItems []string, processedUninstalls map[string]bool,
	cachePath string, checkOnly bool, cfg *config.Configuration) error {

	// Check if already processed to avoid loops
	if processedUninstalls[itemName] {
		logging.Debug("Item already processed for uninstall", "item", itemName)
		return nil
	}

	LogItemSource(itemName, "Processing uninstall of item: "+itemName)

	// Mark as processed early to avoid infinite recursion
	processedUninstalls[itemName] = true

	// Find and process items that require this item (dependents)
	dependentItems := findItemsRequiringItem(itemName, catalogsMap, installedItems, cfg)
	if len(dependentItems) > 0 {
		logging.Debug("Processing dependent items for removal", "item", itemName, "dependents", dependentItems)

		for _, depItem := range dependentItems {
			// Track that this dependent removal came from the parent item
			AddItemSourceChain(depItem, "dependent_removal", "dependency-chain", itemName)

			LogItemSource(depItem, "Removing dependent item first")

			if err := processUninstallWithAdvancedLogic(depItem, catalogsMap, installedItems,
				processedUninstalls, cachePath, checkOnly, cfg); err != nil {
				logging.Error("Failed to remove dependent item", "dependent", depItem, "error", err)
				return fmt.Errorf("failed to remove dependent item %s: %v", depItem, err)
			}
		}
	}

	// Find and process update items for this item
	updateItems := findUpdateItemsInstalled(itemName, catalogsMap, installedItems)
	if len(updateItems) > 0 {
		logging.Debug("Processing update items for removal", "item", itemName, "updates", updateItems)

		for _, updateItem := range updateItems {
			// Track that this update removal came from the parent item
			AddItemSourceChain(updateItem, "update_removal", "dependency-chain", itemName)

			LogItemSource(updateItem, "Removing update item")

			if err := processUninstallWithAdvancedLogic(updateItem, catalogsMap, installedItems,
				processedUninstalls, cachePath, checkOnly, cfg); err != nil {
				logging.Warn("Failed to remove update item", "update", updateItem, "error", err)
				// Don't fail the main uninstall if an update removal fails
			}
		}
	}

	// Get the main item and uninstall it
	item, err := firstItem(itemName, catalogsMap, cfg)
	if err != nil {
		// Item might not be in catalogs anymore, but we should still try to uninstall
		logging.Warn("Item not found in catalogs during uninstall, continuing anyway", "item", itemName)
		// Create a minimal item for uninstall
		item = catalog.Item{
			Name: itemName,
			// We'll rely on the system's built-in uninstall mechanisms
		}
	}
	logging.Info("Uninstalling item:", "item", itemName)
	_, err = installerInstall(item, "uninstall", "", cachePath, checkOnly, cfg)
	if err != nil {
		logging.Error("Failed to uninstall item", "item", itemName, "error", err)
		// Continue anyway as partial removal is better than none
	}

	return nil
}

// Helper functions for advanced dependency processing

// isRequirementSatisfied checks if a requirement is already satisfied by installed or scheduled items
func isRequirementSatisfied(reqName, reqVersion string, installedItems, scheduledItems []string) bool {
	allItems := append(installedItems, scheduledItems...)
	for _, item := range allItems {
		itemName, itemVersion := catalog.SplitNameAndVersion(item)

		// Simple name match for now
		if strings.EqualFold(itemName, reqName) {
			// If no specific version required, any version satisfies
			if reqVersion == "" {
				return true
			}

			// If specific version required, check if it matches
			if reqVersion != "" && itemVersion != "" {
				// TODO: Implement proper version comparison
				if strings.EqualFold(itemVersion, reqVersion) {
					return true
				}
			}
		}
	}

	return false
}

// findItemsRequiringItem finds all installed items that require the given item
func findItemsRequiringItem(itemName string, catalogsMap map[int]map[string]catalog.Item, installedItems []string, cfg *config.Configuration) []string {
	var dependentItems []string

	// Check different name formats that might be used in requires
	checkNames := []string{
		itemName,
		// TODO: Add versioned names if needed
	}

	// Only check items that are actually installed
	for _, installedItem := range installedItems {
		// Find the catalog entry for this installed item
		if catalogItem, err := firstItem(installedItem, catalogsMap, cfg); err == nil {
			if catalogItem.Requires != nil {
				for _, reqItem := range catalogItem.Requires {
					reqName, _ := catalog.SplitNameAndVersion(reqItem)

					for _, checkName := range checkNames {
						if strings.EqualFold(reqName, checkName) {
							dependentItems = append(dependentItems, installedItem)
							logging.Debug("Found dependent item", "dependent", installedItem, "requires", itemName)
							break
						}
					}
				}
			}
		}
	}

	return removeDuplicatesLocal(dependentItems)
}

// removeDuplicatesLocal removes duplicate strings from a slice
func removeDuplicatesLocal(slice []string) []string {
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

// findUpdateItemsInstalled finds installed items that are updates for the given item
func findUpdateItemsInstalled(itemName string, catalogsMap map[int]map[string]catalog.Item, installedItems []string) []string {
	var updateItems []string

	// Find all items in catalogs that declare they are updates for this item
	// LookForUpdates now checks if itemName is installed before returning updates
	allUpdates := catalog.LookForUpdates(itemName, catalogsMap, installedItems)

	// Filter to only those that are actually installed
	for _, updateItem := range allUpdates {
		for _, installedItem := range installedItems {
			if strings.EqualFold(updateItem, installedItem) {
				updateItems = append(updateItems, updateItem)
				break
			}
		}
	}

	return updateItems
}

// isItemInList checks if an item name is in a list of items (case-insensitive)
func isItemInList(itemName string, itemList []string) bool {
	for _, item := range itemList {
		listItemName, _ := catalog.SplitNameAndVersion(item)
		if strings.EqualFold(itemName, listItemName) {
			return true
		}
	}
	return false
}

// isOnDemandItem checks if an item has the OnDemand flag set
func isOnDemandItem(item catalog.Item) bool {
	return item.OnDemand
}

// handleOnDemandInstall handles the special case of OnDemand item installation
func handleOnDemandInstall(item catalog.Item, cachePath string, checkOnly bool, cfg *config.Configuration) {
	logging.Info("Processing OnDemand item", "item", item.Name)

	var localFile string
	var err error

	// Check if this is a nopkg (script-only) item by checking if installer location is empty
	// For nopkg items, there is no installer section at all in the catalog
	if item.Installer.Location == "" {
		logging.Debug("OnDemand nopkg item - skipping download (no installer location)", "item", item.Name)
		localFile = "" // No file needed for script-only items
	} else {
		// Download the file first for OnDemand items with installers
		localFile, err = downloadItemFile(item, cfg, 0, nil)
		if err != nil {
			logging.Error("Failed to download OnDemand item", "item", item.Name, "error", err)
			return
		}
	}

	// OnDemand items are always available for execution
	_, err = installerInstall(item, "install", localFile, cachePath, checkOnly, cfg)
	if err != nil {
		logging.Error("OnDemand item execution failed", "item", item.Name, "error", err)
	} else {
		logging.Info("OnDemand item executed successfully", "item", item.Name)
	}
}

// downloadItemFile downloads the installer file for a single catalog item and returns the local file path
func downloadItemFile(item catalog.Item, cfg *config.Configuration, verbosity int, reporter utils.Reporter) (string, error) {
	logging.Debug("downloadItemFile called", "item", item.Name, "installer_location", item.Installer.Location, "caller", "check_calling_function")

	if item.Installer.Location == "" {
		return "", fmt.Errorf("no installer location found for item: %s", item.Name)
	}

	// Construct the full URL using robust function
	fullURL := constructPackageURL(item.Installer.Location, cfg)

	// Reconstruct the local file path (same logic as in download.InstallPendingUpdates)
	subPath := getSubPathFromURL(fullURL, cfg)
	localFilePath := filepath.Join(cfg.CachePath, subPath)
	localFilePath = filepath.Clean(localFilePath)

	logging.Debug("Downloading file for item", "item", item.Name, "url", fullURL)

	// Download the file with enhanced progress tracking and hash validation
	if err := download.DownloadFile(fullURL, "", cfg, verbosity, reporter, item.Installer.Hash); err != nil {
		return "", fmt.Errorf("failed to download %s: %v", item.Name, err)
	}

	logging.Debug("Downloaded file", "item", item.Name, "path", localFilePath)
	return localFilePath, nil
}

// getSubPathFromURL mirrors the logic in download.getSubPathFromURL
func getSubPathFromURL(url string, cfg *config.Configuration) string {
	lowerURL := strings.ToLower(url)
	var subPath string

	switch {
	case strings.Contains(lowerURL, "/catalogs/"):
		subPath = strings.SplitN(url, "/catalogs/", 2)[1]
	case strings.Contains(lowerURL, "/manifests/"):
		subPath = strings.SplitN(url, "/manifests/", 2)[1]
	case strings.Contains(lowerURL, "/pkgs/"):
		idx := strings.Index(lowerURL, "/pkgs/")
		subPath = url[idx+len("/pkgs/"):]
	default:
		subPath = filepath.Base(url)
	}
	return filepath.FromSlash(subPath)
}

// generatePackageID creates a standardized package ID for correlation
func generatePackageID(packageName string) string {
	if packageName == "" {
		return ""
	}
	// Convert to lowercase and replace spaces/special chars with hyphens
	id := strings.ToLower(packageName)
	id = regexp.MustCompile(`[^a-z0-9]+`).ReplaceAllString(id, "-")
	id = strings.Trim(id, "-")
	return id
}

// determineInstallerTypeFromPath extracts installer type from file path for enhanced reporting
func determineInstallerTypeFromPath(path string) string {
	if path == "" {
		return "unknown"
	}
	
	path = strings.ToLower(path)
	switch {
	case strings.Contains(path, ".nupkg"):
		return "nupkg"
	case strings.Contains(path, ".msi"):
		return "msi"
	case strings.Contains(path, ".exe"):
		return "exe"
	case strings.Contains(path, ".msix"):
		return "msix"
	case strings.Contains(path, ".appx"):
		return "appx"
	case strings.Contains(path, ".zip"):
		return "zip"
	case strings.Contains(path, ".ps1"):
		return "powershell"
	case strings.Contains(path, "chocolatey") || strings.Contains(path, "choco"):
		return "chocolatey"
	default:
		return "unknown"
	}
}

// isMSIServiceFailure checks if an error is related to MSI service availability issues
func isMSIServiceFailure(err error) bool {
	if err == nil {
		return false
	}
	
	errorMsg := strings.ToLower(err.Error())
	
	// Check for various MSI service-related error patterns
	msiServicePatterns := []string{
		"msi service unavailable",
		"msi service did not become available",
		"msi service appears to be locked",
		"another install in progress",
		"another installation in progress",
		"msiexec.*exit code.*1618", // MSI error code for "another install in progress"
		"windows installer service",
		"installer service unavailable",
		"msi service not available",
	}
	
	for _, pattern := range msiServicePatterns {
		if strings.Contains(errorMsg, pattern) {
			return true
		}
	}
	
	// Check for MSI exit code 1618 specifically
	if strings.Contains(errorMsg, "1618") && strings.Contains(errorMsg, "msi") {
		return true
	}
	
	return false
}

// recoverMSIServiceBetweenItems attempts to recover the MSI service between failed installations
func recoverMSIServiceBetweenItems(cfg *config.Configuration) error {
	logging.Info("Starting MSI service recovery between items")
	
	// Import the installer cleanup functionality
	cleanup := installer.GetGlobalCleanup(cfg)
	
	// Step 1: Check current MSI service status
	status, err := cleanup.CheckMSIServiceStatus()
	if err != nil {
		logging.Debug("Could not check MSI service status during recovery", "error", err)
	} else {
		logging.Info("Pre-recovery MSI service status",
			"isRunning", status.IsRunning,
			"isResponsive", status.IsResponsive,
			"processCount", status.ProcessCount,
			"serviceState", status.ServiceState)
	}
	
	// Step 2: Perform advanced cleanup
	logging.Info("Performing advanced MSI cleanup for service recovery")
	if err := cleanup.PerformAdvancedMSICleanup(); err != nil {
		logging.Warn("Advanced MSI cleanup during recovery had issues", "error", err)
	}
	
	// Step 3: If service restart is enabled, try restarting the service
	if cleanup.GetRetryConfig().ServiceRestartEnabled {
		logging.Info("Attempting Windows Installer service restart for recovery")
		if err := cleanup.RestartWindowsInstallerService(); err != nil {
			logging.Warn("Windows Installer service restart during recovery failed", "error", err)
		} else {
			logging.Info("Windows Installer service restarted successfully during recovery")
		}
	}
	
	// Step 4: Wait for service to become available with a reasonable timeout
	logging.Info("Waiting for MSI service to become available after recovery")
	waitErr := cleanup.WaitForMSIAvailable(3) // 3-minute timeout for recovery
	if waitErr != nil {
		logging.Error("MSI service recovery failed - service still unavailable", "error", waitErr)
		return fmt.Errorf("MSI service recovery failed: %w", waitErr)
	}
	
	// Step 5: Final status check
	finalStatus, err := cleanup.CheckMSIServiceStatus()
	if err != nil {
		logging.Debug("Could not check final MSI service status after recovery", "error", err)
	} else {
		logging.Info("Post-recovery MSI service status",
			"isRunning", finalStatus.IsRunning,
			"isResponsive", finalStatus.IsResponsive,
			"processCount", finalStatus.ProcessCount,
			"serviceState", finalStatus.ServiceState)
		
		if finalStatus.IsRunning && finalStatus.IsResponsive {
			logging.Info("MSI service recovery completed successfully")
			return nil
		} else {
			return fmt.Errorf("MSI service recovery incomplete - service not fully responsive")
		}
	}
	
	logging.Info("MSI service recovery completed")
	return nil
}
