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
	Catalogs          []string `yaml:"catalogs"`
}

// AuthenticatedGet retrieves manifests from the server or local paths.
func AuthenticatedGet(cfg *config.Configuration) ([]Item, []string) {
	var manifestsList []string
	var manifests []Item
	var newCatalogs []string

	manifestsList = append(manifestsList, cfg.ClientIdentifier)

	defer func() {
		if r := recover(); r != nil {
			logging.Error("Recovered from panic", "panic", r)
			os.Exit(1)
		}
	}()

	for manifestsProcessed := 0; manifestsProcessed < len(manifestsList); manifestsProcessed++ {
		currentManifest := manifestsList[manifestsProcessed]

		// Construct the manifest URL
		manifestURL := fmt.Sprintf("%s/manifests/%s.yaml", strings.TrimRight(cfg.SoftwareRepoURL, "/"), currentManifest)
		manifestFilePath := filepath.Join(cfg.CachePath, "manifests", fmt.Sprintf("%s.yaml", currentManifest))

		// Download the manifest using download.go
		logging.Info("Downloading manifest", "url", manifestURL, "path", manifestFilePath)
		if err := download.DownloadFile(manifestURL, manifestFilePath, cfg); err != nil {
			logging.Warn("Failed to download manifest", "url", manifestURL, "error", err)
			continue
		}

		// Parse the downloaded manifest
		manifestContent, err := os.ReadFile(manifestFilePath)
		if err != nil {
			logging.Error("Failed to read manifest file", "path", manifestFilePath, "error", err)
			continue
		}

		newManifest := parseManifest(manifestContent, manifestFilePath)

		// Process includes (nested manifests)
		for _, include := range newManifest.Includes {
			if !contains(manifestsList, include) {
				logging.Info("Including nested manifest", "parent", currentManifest, "nested", include)
				manifestsList = append(manifestsList, include)
			}
		}

		// Avoid duplicates
		if !containsManifest(manifests, newManifest.Name) {
			manifests = append(manifests, newManifest)
			logging.Info("Added manifest", "name", newManifest.Name, "version", newManifest.Version)
		}

		// Process any new catalogs
		for _, catalog := range newManifest.Catalogs {
			if !contains(cfg.Catalogs, catalog) && !contains(newCatalogs, catalog) {
				newCatalogs = append(newCatalogs, catalog)
				logging.Info("Detected new catalog", "catalog", catalog)
			}
		}
	}

	// Handle local manifests
	for _, localPath := range cfg.LocalManifests {
		logging.Info("Processing local manifest", "path", localPath)
		localContent, err := os.ReadFile(localPath)
		if err != nil {
			logging.Warn("Unable to read local manifest file", "path", localPath, "error", err)
			continue
		}

		localManifest := parseManifest(localContent, localPath)
		if !containsManifest(manifests, localManifest.Name) {
			manifests = append(manifests, localManifest)
			logging.Info("Added local manifest", "name", localManifest.Name)
		}
	}

	return manifests, newCatalogs
}

// parseManifest unmarshals YAML content into an Item.
func parseManifest(yamlContent []byte, source string) Item {
	var manifestItem Item
	if err := yaml.Unmarshal(yamlContent, &manifestItem); err != nil {
		logging.Error("Failed to parse YAML manifest", "source", source, "error", err)
	}
	return manifestItem
}

// contains checks if a string exists in a slice.
func contains(slice []string, item string) bool {
	for _, v := range slice {
		if v == item {
			return true
		}
	}
	return false
}

// containsManifest checks if a manifest name exists in a list of Items.
func containsManifest(manifests []Item, name string) bool {
	for _, m := range manifests {
		if m.Name == name {
			return true
		}
	}
	return false
}
