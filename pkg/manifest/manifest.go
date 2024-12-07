package manifest

import (
	"fmt"
	"os"

	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/download"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/report"
	"gopkg.in/yaml.v3"
)

// Item represents a single object from the manifest
type Item struct {
	Name              string   `yaml:"name"`
	Version           string   `yaml:"version"`
	InstallerLocation string   `yaml:"installer_location"`
	Includes          []string `yaml:"included_manifests"`
	Installs          []string `yaml:"managed_installs"`
	Uninstalls        []string `yaml:"managed_uninstalls"`
	Updates           []string `yaml:"managed_updates"`
	Catalogs          []string `yaml:"catalogs"`
}

// Get retrieves all manifest items and any new catalogs that need to be added to the configuration.
// It returns two slices:
// 1) All manifest objects
// 2) Additional catalogs to be added to the configuration
func Get(cfg *config.Configuration) ([]Item, []string) {
	// Initialize slices to store manifest names and new catalogs
	var manifestsList []string
	var manifests []Item
	var newCatalogs []string

	// Initialize counters for processing manifests
	manifestsTotal := 0
	manifestsProcessed := 0

	// Add the primary manifest to the list
	manifestsList = append(manifestsList, cfg.Manifest)

	// Deferred function to handle potential panics gracefully
	defer func() {
		if r := recover(); r != nil {
			logging.Error("Recovered from panic", "panic", r)
			report.End()
			os.Exit(1)
		}
	}()

	// Process each manifest in the list
	for manifestsProcessed < len(manifestsList) {
		currentManifest := manifestsList[manifestsProcessed]

		// Construct the URL for the current manifest
		manifestURL := fmt.Sprintf("%smanifests/%s.yaml", cfg.URL, currentManifest)
		logging.Info("Fetching Manifest", "url", manifestURL, "manifest_name", currentManifest)

		// Download the manifest YAML content
		yamlContent, err := download.Get(manifestURL, cfg)
		if err != nil {
			logging.Error("Failed to retrieve manifest",
				"url", manifestURL,
				"manifest_name", currentManifest,
				"error", err)
			manifestsProcessed++
			continue // Skip to the next manifest
		}

		// Parse the downloaded YAML content into an Item struct
		newManifest := parseManifest(manifestURL, yamlContent)

		// Add any included manifests to the manifestsList for processing
		for _, include := range newManifest.Includes {
			if !contains(manifestsList, include) {
				logging.Debug("Including nested manifest",
					"parent_manifest", currentManifest,
					"nested_manifest", include)
				manifestsList = append(manifestsList, include)
			} else {
				logging.Debug("Nested manifest already processed",
					"parent_manifest", currentManifest,
					"nested_manifest", include)
			}
		}

		// Append the new manifest to the manifests slice if it's unique
		if !containsManifest(manifests, newManifest.Name) {
			manifests = append(manifests, newManifest)
			logging.Info("Added new manifest",
				"name", newManifest.Name,
				"version", newManifest.Version)
		} else {
			logging.Debug("Manifest already exists",
				"name", newManifest.Name,
				"version", newManifest.Version)
		}

		// Process any new catalogs specified in the manifest
		for _, newCatalog := range newManifest.Catalogs {
			if !contains(cfg.Catalogs, newCatalog) && !contains(newCatalogs, newCatalog) {
				newCatalogs = append(newCatalogs, newCatalog)
				logging.Info("Detected new catalog",
					"catalog", newCatalog,
					"manifest", newManifest.Name)
			} else {
				logging.Debug("Catalog already exists or pending addition",
					"catalog", newCatalog)
			}
		}

		// Increment the processed counter
		manifestsProcessed++
	}

	// Handle local manifests specified in the configuration
	if len(cfg.LocalManifests) > 0 {
		for _, localManifestPath := range cfg.LocalManifests {
			logging.Info("Processing Local Manifest File", "path", localManifestPath)
			localYamlContent, err := os.ReadFile(localManifestPath)
			if err != nil {
				logging.Warn("Unable to read local manifest file",
					"path", localManifestPath,
					"error", err)
				continue // Skip to the next local manifest
			}

			localManifest := parseManifest(localManifestPath, localYamlContent)
			if !containsManifest(manifests, localManifest.Name) {
				manifests = append(manifests, localManifest)
				logging.Info("Added local manifest",
					"name", localManifest.Name,
					"version", localManifest.Version)
			} else {
				logging.Debug("Local manifest already exists",
					"name", localManifest.Name,
					"version", localManifest.Version)
			}
		}
	}

	return manifests, newCatalogs
}

// parseManifest unmarshals YAML content into an Item struct.
// It logs an error if unmarshalling fails.
func parseManifest(source string, yamlContent []byte) Item {
	var manifestItem Item
	err := yaml.Unmarshal(yamlContent, &manifestItem)
	if err != nil {
		logging.Error("Unable to parse YAML manifest",
			"source", source,
			"error", err)
	}
	return manifestItem
}

// contains checks if a slice of strings contains a specific string.
func contains(slice []string, item string) bool {
	for _, elem := range slice {
		if elem == item {
			return true
		}
	}
	return false
}

// containsManifest checks if a slice of Items contains a manifest with the specified name.
func containsManifest(manifests []Item, name string) bool {
	for _, manifest := range manifests {
		if manifest.Name == name {
			return true
		}
	}
	return false
}