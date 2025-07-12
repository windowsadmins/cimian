// pkg/selfservice/selfservice.go - Functions for managing self-service manifests and OnDemand items.

package selfservice

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/windowsadmins/cimian/pkg/logging"
	"gopkg.in/yaml.v3"
)

const (
	// SelfServiceManifestPath is the path to the writable self-service manifest
	SelfServiceManifestPath = `C:\ProgramData\ManagedInstalls\SelfServeManifest.yaml`
)

// SelfServiceManifest represents the structure of the self-service manifest
type SelfServiceManifest struct {
	Name              string   `yaml:"name"`
	ManagedInstalls   []string `yaml:"managed_installs,omitempty"`
	ManagedUninstalls []string `yaml:"managed_uninstalls,omitempty"`
	OptionalInstalls  []string `yaml:"optional_installs,omitempty"`
}

// LoadSelfServiceManifest reads the self-service manifest from disk
func LoadSelfServiceManifest() (*SelfServiceManifest, error) {
	// If the file doesn't exist, return an empty manifest
	if _, err := os.Stat(SelfServiceManifestPath); os.IsNotExist(err) {
		logging.Debug("Self-service manifest does not exist, returning empty manifest")
		return &SelfServiceManifest{
			Name:              "SelfServeManifest",
			ManagedInstalls:   []string{},
			ManagedUninstalls: []string{},
			OptionalInstalls:  []string{},
		}, nil
	}

	data, err := os.ReadFile(SelfServiceManifestPath)
	if err != nil {
		return nil, fmt.Errorf("failed to read self-service manifest: %v", err)
	}

	var manifest SelfServiceManifest
	err = yaml.Unmarshal(data, &manifest)
	if err != nil {
		return nil, fmt.Errorf("failed to unmarshal self-service manifest: %v", err)
	}

	// Ensure the manifest has a name
	if manifest.Name == "" {
		manifest.Name = "SelfServeManifest"
	}

	logging.Debug("Loaded self-service manifest", "items", len(manifest.ManagedInstalls)+len(manifest.OptionalInstalls))
	return &manifest, nil
}

// SaveSelfServiceManifest writes the self-service manifest to disk
func SaveSelfServiceManifest(manifest *SelfServiceManifest) error {
	// Ensure the directory exists
	dir := filepath.Dir(SelfServiceManifestPath)
	if err := os.MkdirAll(dir, 0755); err != nil {
		return fmt.Errorf("failed to create directory %s: %v", dir, err)
	}

	data, err := yaml.Marshal(manifest)
	if err != nil {
		return fmt.Errorf("failed to marshal self-service manifest: %v", err)
	}

	err = os.WriteFile(SelfServiceManifestPath, data, 0644)
	if err != nil {
		return fmt.Errorf("failed to write self-service manifest: %v", err)
	}

	logging.Debug("Saved self-service manifest", "path", SelfServiceManifestPath)
	return nil
}

// RemoveFromSelfServiceInstalls removes an item from the managed_installs array in the self-service manifest
// This is used for OnDemand items that should be removed after successful execution
func RemoveFromSelfServiceInstalls(itemName string) error {
	logging.Info("Removing OnDemand item from self-service manifest", "item", itemName)

	manifest, err := LoadSelfServiceManifest()
	if err != nil {
		return fmt.Errorf("failed to load self-service manifest: %v", err)
	}

	// Remove from managed_installs
	originalCount := len(manifest.ManagedInstalls)
	manifest.ManagedInstalls = removeStringFromSlice(manifest.ManagedInstalls, itemName)
	removedFromManaged := originalCount != len(manifest.ManagedInstalls)

	// Remove from optional_installs as well (just in case)
	originalOptionalCount := len(manifest.OptionalInstalls)
	manifest.OptionalInstalls = removeStringFromSlice(manifest.OptionalInstalls, itemName)
	removedFromOptional := originalOptionalCount != len(manifest.OptionalInstalls)

	if !removedFromManaged && !removedFromOptional {
		logging.Debug("Item was not in self-service manifest", "item", itemName)
		return nil
	}

	err = SaveSelfServiceManifest(manifest)
	if err != nil {
		return fmt.Errorf("failed to save self-service manifest: %v", err)
	}

	logging.Info("Successfully removed OnDemand item from self-service manifest",
		"item", itemName,
		"removedFromManaged", removedFromManaged,
		"removedFromOptional", removedFromOptional)
	return nil
}

// AddToSelfServiceInstalls adds an item to the managed_installs array in the self-service manifest
// This can be used for user-requested installs
func AddToSelfServiceInstalls(itemName string) error {
	logging.Info("Adding item to self-service manifest", "item", itemName)

	manifest, err := LoadSelfServiceManifest()
	if err != nil {
		return fmt.Errorf("failed to load self-service manifest: %v", err)
	}

	// Check if item is already in managed_installs
	for _, existing := range manifest.ManagedInstalls {
		if strings.EqualFold(existing, itemName) {
			logging.Debug("Item already in self-service manifest", "item", itemName)
			return nil
		}
	}

	// Add to managed_installs
	manifest.ManagedInstalls = append(manifest.ManagedInstalls, itemName)

	err = SaveSelfServiceManifest(manifest)
	if err != nil {
		return fmt.Errorf("failed to save self-service manifest: %v", err)
	}

	logging.Info("Successfully added item to self-service manifest", "item", itemName)
	return nil
}

// removeStringFromSlice removes all occurrences of a string from a slice (case-insensitive)
func removeStringFromSlice(slice []string, item string) []string {
	result := make([]string, 0, len(slice))
	for _, s := range slice {
		if !strings.EqualFold(s, item) {
			result = append(result, s)
		}
	}
	return result
}

// IsItemInSelfServiceManifest checks if an item is present in the self-service manifest
func IsItemInSelfServiceManifest(itemName string) (bool, error) {
	manifest, err := LoadSelfServiceManifest()
	if err != nil {
		return false, fmt.Errorf("failed to load self-service manifest: %v", err)
	}

	// Check managed_installs
	for _, item := range manifest.ManagedInstalls {
		if strings.EqualFold(item, itemName) {
			return true, nil
		}
	}

	// Check optional_installs
	for _, item := range manifest.OptionalInstalls {
		if strings.EqualFold(item, itemName) {
			return true, nil
		}
	}

	return false, nil
}

// GetSelfServiceItems returns all items from the self-service manifest
func GetSelfServiceItems() (*SelfServiceManifest, error) {
	return LoadSelfServiceManifest()
}
