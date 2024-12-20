package process

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"time"

	"github.com/windowsadmins/gorilla/pkg/catalog"
	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/installer"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/manifest"
)

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
				return item, nil
			}
		}
	}

	// Return an empty catalog item if we didn't already find and return a match
	return catalog.Item{}, fmt.Errorf("did not find a valid item in any catalog; Item name: %v", itemName)
}

// Manifests iterates through manifests, processes items from managed arrays, and ensures manifest names are excluded.
func Manifests(manifests []manifest.Item, catalogsMap map[int]map[string]catalog.Item) (installs, uninstalls, updates []string) {
	processedManifests := make(map[string]bool) // Track processed manifests to avoid loops

	// Helper function to add valid catalog items to the target list
	addValidItems := func(items []string, target *[]string) {
		for _, item := range items {
			if item == "" {
				continue
			}
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
				logging.Error("Item not found in catalog", "item", item)
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

		// Process managed arrays only
		addValidItems(manifestItem.Installs, &installs)
		addValidItems(manifestItem.Uninstalls, &uninstalls)
		addValidItems(manifestItem.Updates, &updates)
		addValidItems(manifestItem.OptionalInstalls, &installs)

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
func Installs(installs []string, catalogsMap map[int]map[string]catalog.Item, urlPackages, cachePath string, CheckOnly bool, cfg *config.Configuration) {
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

				installerInstall(validDependency, "install", urlPackages, cachePath, CheckOnly, cfg)
			}
		}

		// Install the item
		installerInstall(validItem, "install", urlPackages, cachePath, CheckOnly, cfg)
	}
}

// Uninstalls prepares and then uninstalls an array of items
func Uninstalls(uninstalls []string, catalogsMap map[int]map[string]catalog.Item, urlPackages, cachePath string, CheckOnly bool, cfg *config.Configuration) {
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
		installerInstall(validItem, "uninstall", urlPackages, cachePath, CheckOnly, cfg)
	}
}

// Updates prepares and then updates an array of items
func Updates(updates []string, catalogsMap map[int]map[string]catalog.Item, urlPackages, cachePath string, CheckOnly bool, cfg *config.Configuration) {
	// Iterate through the updates array and update the item **if it is already installed**
	for _, item := range updates {
		// Get the first valid item from our catalogs
		validItem, err := firstItem(item, catalogsMap)
		if err != nil {
			logging.Error("Processing Error", "error", err)
			logging.Warn("Processing warning: failed to process update item", "error", err)
			continue
		}
		// Update the item
		installerInstall(validItem, "update", urlPackages, cachePath, CheckOnly, cfg)
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
