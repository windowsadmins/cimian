// pkg/process/process.go - functions for processing install, uninstall, and update actions.

package process

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/installer"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/manifest"
)

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
		logging.Info(logMessage, "item", itemName, "source", source.GetSourceDescription())
	} else {
		logging.Info(logMessage, "item", itemName, "source", "unknown")
	}
}

// firstItem returns the first occurrence of an item in a map of catalogs
func firstItem(itemName string, catalogsMap map[int]map[string]catalog.Item) (catalog.Item, error) {
	// Get the keys in the map and sort them so we can loop over them in order
	keys := make([]int, 0)
	for k := range catalogsMap {
		keys = append(keys, k)
	}
	sort.Ints(keys)

	// Loop through each catalog and return if we find a match
	for _, k := range keys {
		if item, exists := catalogsMap[k][itemName]; exists {
			// Check if it's a valid install or uninstall item
			validInstallItem := (item.Installer.Type != "" && item.Installer.Location != "")
			validUninstallItem := (item.Uninstaller.Type != "" && item.Uninstaller.Location != "")

			if validInstallItem || validUninstallItem {
				// Don't modify the location here, return it as-is
				return item, nil
			}
		}
	}

	// Log source information when item is not found
	if source, exists := GetItemSource(itemName); exists {
		logging.Error("Item not found in any catalog", "item", itemName, "source", source.GetSourceDescription())
		return catalog.Item{}, fmt.Errorf("item %s not found in any catalog (source: %s)", itemName, source.GetSourceDescription())
	}

	// If no source information is available, provide generic error
	logging.Error("Item not found in any catalog", "item", itemName, "source", "unknown - not tracked through manifest processing")
	return catalog.Item{}, fmt.Errorf("item %s not found in any catalog (source: unknown)", itemName)
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

// getSystemArchitecture returns the architecture of the system.
func getSystemArchitecture() string {
	switch runtime.GOARCH {
	case "amd64":
		return "x64"
	case "arm64":
		return "arm64"
	default:
		return runtime.GOARCH
	}
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

// Installs prepares and then installs an array of items based on system architecture.
func Installs(installs []string, catalogsMap map[int]map[string]catalog.Item, _, cachePath string, CheckOnly bool, cfg *config.Configuration) {
	systemArch := getSystemArchitecture()
	logging.Info("System architecture detected", "architecture", systemArch)

	// Iterate through the installs array, install dependencies, and then the item itself
	for _, item := range installs {
		// Get the first valid item from our catalogs
		validItem, err := firstItem(item, catalogsMap)
		if err != nil {
			logging.Error("Processing Error", "error", err)
			logging.Warn("Processing warning: failed to process install item", "error", err)
			continue
		}

		// Ensure the URL is properly constructed
		if validItem.Installer.Location != "" {
			if validItem.Installer.Location[0] == '/' {
				validItem.Installer.Location = cfg.SoftwareRepoURL + validItem.Installer.Location
			}
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
				validDependency, err := firstItem(dependency, catalogsMap)
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

				installerInstall(validDependency, "install", "", cachePath, CheckOnly, cfg)
			}
		}

		// Install the item
		installerInstall(validItem, "install", "", cachePath, CheckOnly, cfg)
	}
}

// Uninstalls prepares and then uninstalls an array of items
func Uninstalls(uninstalls []string, catalogsMap map[int]map[string]catalog.Item, _, cachePath string, CheckOnly bool, cfg *config.Configuration) {
	// Iterate through the uninstalls array and uninstall the item
	for _, item := range uninstalls {
		// Get the first valid item from our catalogs
		validItem, err := firstItem(item, catalogsMap)
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
		validItem, err := firstItem(item, catalogsMap)
		if err != nil {
			logging.Error("Processing Error", "error", err)
			continue
		}

		// Construct proper URL for the installer
		if validItem.Installer.Location != "" {
			if strings.HasPrefix(validItem.Installer.Location, "/") {
				validItem.Installer.Location = cfg.SoftwareRepoURL + "/pkgs" + validItem.Installer.Location
			}
			logging.Debug("Package installer URL", "url", validItem.Installer.Location)
		}

		installerInstall(validItem, "update", "", cachePath, CheckOnly, cfg)
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

// fileOld returns true if the file is older than
// the limit defined in the variable `days`
func fileOld(info os.FileInfo) bool {
	// Age of the file
	fileAge := time.Since(info.ModTime())

	// Our limit
	days := 5

	// Convert from days
	hours := days * 24
	ageLimit := time.Duration(hours) * time.Hour

	// If the file is older than our limit, return true
	return fileAge > ageLimit
}

// This abstraction allows us to override when testing
var osRemove = os.Remove

// CleanUp checks the age of items in the cache and removes if older than 5 days
func CleanUp(cachePath string, cfg *config.Configuration) {
	// Clean up old files
	err := filepath.Walk(cachePath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			logging.Error("Processing Error", "error", err)
			logging.Warn("Failed to access path", "path", path, "error", err)
			return err
		}
		// If not a directory and older than our limit, delete
		if !info.IsDir() && fileOld(info) {
			logging.Info("Cleaning old cached file", "file", info.Name())
			err := osRemove(path)
			if err != nil {
				logging.Error("Failed to remove file", "file", path, "error", err)
			}
			return nil
		}
		return nil
	})
	if err != nil {
		logging.Error("Processing Error", "error", err)
		logging.Warn("Error walking path", "path", cachePath, "error", err)
		return
	}

	// Clean up empty directories
	err = filepath.Walk(cachePath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			logging.Error("Processing Error", "error", err)
			logging.Warn("Failed to access path", "path", path, "error", err)
			return err
		}

		// If a dir and empty, delete
		if info.IsDir() && dirEmpty(path) {
			logging.Info("Cleaning empty directory", "directory", info.Name())
			err := osRemove(path)
			if err != nil {
				logging.Error("Failed to remove directory", "directory", path, "error", err)
			}
			return nil
		}
		return nil
	})
	if err != nil {
		logging.Error("Processing Error", "error", err)
		logging.Warn("Error walking path", "path", cachePath, "error", err)
		return
	}
}

// ProcessInstallWithDependencies processes an install item with full dependency handling
// including requires, update_for, and architecture checks
func ProcessInstallWithDependencies(itemName string, catalogsMap map[int]map[string]catalog.Item,
	installedItems []string, scheduledItems []string, cachePath string, checkOnly bool, cfg *config.Configuration) error {

	logging.Debug("Processing install with dependencies", "item", itemName)

	// Get the item from catalogs
	item, err := firstItem(itemName, catalogsMap)
	if err != nil {
		return fmt.Errorf("item not found in catalogs: %v", err)
	}

	systemArch := getSystemArchitecture()

	// Check architecture support
	if !supportsArchitecture(item, systemArch) {
		logging.Info("Skipping installation due to architecture mismatch",
			"package", item.Name, "required_architectures", item.SupportedArch, "system_architecture", systemArch)
		return nil
	}

	// Check and install requires dependencies first
	if item.Requires != nil {
		missingDeps := catalog.CheckDependencies(item, installedItems, scheduledItems)
		for _, dep := range missingDeps {
			logging.Info("Installing required dependency", "dependency", dep, "for", itemName)

			// Recursively process the dependency
			if err := ProcessInstallWithDependencies(dep, catalogsMap, installedItems,
				append(scheduledItems, dep), cachePath, checkOnly, cfg); err != nil {
				logging.Error("Failed to install required dependency", "dependency", dep, "error", err)
				return fmt.Errorf("failed to install required dependency %s: %v", dep, err)
			}
			// Add to scheduled items for future dependency checks
			scheduledItems = append(scheduledItems, dep)
		}
	}

	// Handle legacy dependencies field as well
	if len(item.Dependencies) > 0 {
		for _, dependency := range item.Dependencies {
			validDependency, err := firstItem(dependency, catalogsMap)
			if err != nil {
				logging.Error("Failed to process legacy dependency", "dependency", dependency, "error", err)
				continue
			}

			if !supportsArchitecture(validDependency, systemArch) {
				logging.Info("Skipping legacy dependency installation due to architecture mismatch",
					"package", validDependency.Name, "required_architectures", validDependency.SupportedArch, "system_architecture", systemArch)
				continue
			}

			installerInstall(validDependency, "install", "", cachePath, checkOnly, cfg)
		}
	}

	// Install the main item
	logging.Info("Installing main item", "item", itemName)
	installerInstall(item, "install", "", cachePath, checkOnly, cfg)

	// Look for updates that should be applied after this install
	updateList := catalog.LookForUpdates(itemName, catalogsMap)
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
	item, err := firstItem(itemName, catalogsMap)
	if err != nil {
		return fmt.Errorf("item not found in catalogs: %v", err)
	}

	logging.Info("Uninstalling main item", "item", itemName)
	installerInstall(item, "uninstall", "", cachePath, checkOnly, cfg)

	// Look for and remove any updates for this item
	updateList := catalog.LookForUpdates(itemName, catalogsMap)
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
			processedInstalls, cachePath, checkOnly, cfg); err != nil {
			LogItemSource(itemName, "Failed to process install with advanced dependency logic")
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
	installedItems []string, cachePath string, checkOnly bool, cfg *config.Configuration) error {
	// Track processed items to avoid infinite loops
	processedInstalls := make(map[string]bool)
	var failedItems []string
	var successCount int

	// Process each item recursively with full dependency logic
	for _, itemName := range itemNames {
		if err := processInstallWithAdvancedLogic(itemName, catalogsMap, installedItems,
			processedInstalls, cachePath, checkOnly, cfg); err != nil {
			LogItemSource(itemName, "Failed to process install with advanced dependency logic")
			logging.Error("Failed to install item, continuing with others", "item", itemName, "error", err)
			failedItems = append(failedItems, itemName)
		} else {
			successCount++
		}
	}

	// Log summary of results
	if len(failedItems) > 0 {
		logging.Warn("Some items failed to install", "failed", failedItems, "succeeded", successCount, "total", len(itemNames))
		// Only return error if ALL items failed
		if successCount == 0 {
			return fmt.Errorf("all %d items failed to install: %v", len(itemNames), failedItems)
		}
	} else {
		logging.Info("All items installed successfully", "count", successCount)
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
			LogItemSource(itemName, "Failed to process uninstall with advanced dependency logic")
			logging.Error("Failed to uninstall item, continuing with others", "item", itemName, "error", err)
			failedItems = append(failedItems, itemName)
		} else {
			successCount++
		}
	}

	// Log summary of results
	if len(failedItems) > 0 {
		logging.Warn("Some items failed to uninstall", "failed", failedItems, "succeeded", successCount, "total", len(itemNames))
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
	cachePath string, checkOnly bool, cfg *config.Configuration) error {

	// Check if already processed to avoid loops
	if processedInstalls[itemName] {
		logging.Debug("Item already processed for install", "item", itemName)
		return nil
	}

	LogItemSource(itemName, "Processing install with advanced dependency logic")

	// Mark as processed early to avoid infinite recursion
	processedInstalls[itemName] = true

	// Get the main item
	item, err := firstItem(itemName, catalogsMap)
	if err != nil {
		LogItemSource(itemName, "Item not found in any catalog")
		return fmt.Errorf("item %s not found in any catalog", itemName)
	}

	// Track items scheduled for installation during this operation
	var scheduledItems []string

	// Process requires dependencies first
	if len(item.Requires) > 0 {
		logging.Debug("Processing requires dependencies", "item", itemName, "requires", item.Requires)

		for _, req := range item.Requires {
			reqItemName, reqVersion := catalog.SplitNameAndVersion(req)

			// Track that this requirement came from the parent item
			AddItemSourceChain(reqItemName, "requires", "dependency-chain", itemName)

			// Check if requirement is already satisfied
			if isRequirementSatisfied(reqItemName, reqVersion, installedItems, scheduledItems) {
				logging.Debug("Requirement already satisfied", "item", itemName, "requirement", req)
				continue
			}

			// Recursively install the required item
			LogItemSource(reqItemName, "Installing required dependency")

			if err := processInstallWithAdvancedLogic(reqItemName, catalogsMap, installedItems,
				processedInstalls, cachePath, checkOnly, cfg); err != nil {
				logging.Error("Failed to install required dependency", "dependency", reqItemName, "error", err)
				return fmt.Errorf("failed to install required dependency %s: %v", reqItemName, err)
			}

			// Add to scheduled items for future dependency checks
			scheduledItems = append(scheduledItems, reqItemName)
		}
	}

	// Install the main item
	LogItemSource(itemName, "Installing item with advanced dependency logic")
	_, err = installerInstall(item, "install", "", cachePath, checkOnly, cfg)
	if err != nil {
		return fmt.Errorf("failed to install item %s: %v", itemName, err)
	}

	// Process 'update_for' items (items that are updates for this item)
	updateList := catalog.LookForUpdates(itemName, catalogsMap)
	if len(updateList) > 0 {
		logging.Debug("Processing update_for items", "item", itemName, "updates", updateList)
		for _, updateItem := range updateList {
			// Track that this update item came from the parent item
			AddItemSourceChain(updateItem, "update_for", "dependency-chain", itemName)

			LogItemSource(updateItem, "Installing update item")

			if err := processInstallWithAdvancedLogic(updateItem, catalogsMap, installedItems,
				processedInstalls, cachePath, checkOnly, cfg); err != nil {
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

	LogItemSource(itemName, "Processing uninstall with advanced dependency logic")

	// Mark as processed early to avoid infinite recursion
	processedUninstalls[itemName] = true

	// Find and process items that require this item (dependents)
	dependentItems := findItemsRequiringItem(itemName, catalogsMap, installedItems)
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
	item, err := firstItem(itemName, catalogsMap)
	if err != nil {
		// Item might not be in catalogs anymore, but we should still try to uninstall
		logging.Warn("Item not found in catalogs during uninstall, continuing anyway", "item", itemName)
		// Create a minimal item for uninstall
		item = catalog.Item{
			Name: itemName,
			// We'll rely on the system's built-in uninstall mechanisms
		}
	}
	logging.Info("Uninstalling item with advanced dependency logic", "item", itemName)
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
func findItemsRequiringItem(itemName string, catalogsMap map[int]map[string]catalog.Item, installedItems []string) []string {
	var dependentItems []string

	// Check different name formats that might be used in requires
	checkNames := []string{
		itemName,
		// TODO: Add versioned names if needed
	}

	// Only check items that are actually installed
	for _, installedItem := range installedItems {
		// Find the catalog entry for this installed item
		if catalogItem, err := firstItem(installedItem, catalogsMap); err == nil {
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
	allUpdates := catalog.LookForUpdates(itemName, catalogsMap)

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
