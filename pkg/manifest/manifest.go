//pkg/manifest/manifest.go

package manifest

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/download"
	"github.com/windowsadmins/gorilla/pkg/logging"
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
	OptionalInstalls  []string `yaml:"optional_installs"`
	Catalogs          []string `yaml:"catalogs"`
}

// AuthenticatedGet retrieves manifests and downloads catalogs listed within them.
func AuthenticatedGet(cfg *config.Configuration) ([]Item, []string) {
	var manifestsList []string
	var manifests []Item
	var downloadedCatalogs []string
	visitedManifests := make(map[string]bool) // To track visited manifests

	manifestsList = append(manifestsList, cfg.ClientIdentifier) // Start with the client manifest

	for manifestsProcessed := 0; manifestsProcessed < len(manifestsList); manifestsProcessed++ {
		currentManifest := manifestsList[manifestsProcessed]

		// Skip already visited manifests
		if visitedManifests[currentManifest] {
			continue
		}

		visitedManifests[currentManifest] = true

		// Construct manifest URL and file path
		manifestURL := fmt.Sprintf("%s/manifests/%s.yaml", strings.TrimRight(cfg.SoftwareRepoURL, "/"), currentManifest)
		manifestFilePath := filepath.Join(`C:\ProgramData\ManagedInstalls\manifests`, currentManifest+".yaml")

		// Download the manifest
		if err := download.DownloadFile(manifestURL, manifestFilePath, cfg); err != nil {
			logging.Warn("Failed to download manifest", "url", manifestURL, "error", err)
			continue
		}

		// Parse the manifest file
		manifestContent, err := os.ReadFile(manifestFilePath)
		if err != nil {
			logging.Error("Failed to read manifest file", "path", manifestFilePath, "error", err)
			continue
		}

		newManifest := parseManifest(manifestContent, manifestFilePath)

		// Add to manifests list
		manifests = append(manifests, newManifest)
		logging.Info("Processed manifest", "name", newManifest.Name)

		// Process included manifests recursively
		for _, include := range newManifest.Includes {
			if !visitedManifests[include] {
				logging.Info("Including nested manifest", "parent", currentManifest, "nested", include)
				manifestsList = append(manifestsList, include)
			}
		}

		// Process catalogs
		for _, catalog := range newManifest.Catalogs {
			if contains(downloadedCatalogs, catalog) {
				continue
			}

			catalogURL := fmt.Sprintf("%s/catalogs/%s.yaml", strings.TrimRight(cfg.SoftwareRepoURL, "/"), catalog)
			catalogFilePath := filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`, catalog+".yaml")

			if err := download.DownloadFile(catalogURL, catalogFilePath, cfg); err != nil {
				logging.Warn("Failed to download catalog", "url", catalogURL, "error", err)
				continue
			}

			downloadedCatalogs = append(downloadedCatalogs, catalog)
			logging.Info("Downloaded catalog", "catalog", catalog, "path", catalogFilePath)
		}
	}

	return manifests, downloadedCatalogs
}

// Helper function to parse manifest content
func parseManifest(yamlContent []byte, source string) Item {
	var manifest Item
	if err := yaml.Unmarshal(yamlContent, &manifest); err != nil {
		logging.Error("Failed to parse manifest", "source", source, "error", err)
	}
	return manifest
}

// Helper function to check for duplicates
func contains(slice []string, item string) bool {
	for _, v := range slice {
		if v == item {
			return true
		}
	}
	return false
}
