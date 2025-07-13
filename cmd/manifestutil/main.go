// cmd/manifestutil/main.go

package main

import (
	"flag"
	"fmt"
	"io/ioutil"
	"os"
	"path/filepath"

	"github.com/windowsadmins/cimian/pkg/selfservice"
	"github.com/windowsadmins/cimian/pkg/version"
	"gopkg.in/yaml.v3"
)

// Manifest represents the structure of the manifest YAML files.
type Manifest struct {
	Name              string   `yaml:"name"`
	ManagedInstalls   []string `yaml:"managed_installs"`
	ManagedUninstalls []string `yaml:"managed_uninstalls"`
	ManagedUpdates    []string `yaml:"managed_updates"`
	OptionalInstalls  []string `yaml:"optional_installs"`
	IncludedManifests []string `yaml:"included_manifests"`
	Catalogs          []string `yaml:"catalogs"`
}

// Config represents the configuration structure
type Config struct {
	RepoPath string `yaml:"repo_path"`
}

// LoadConfig loads the configuration from the given path
func LoadConfig(configPath string) (Config, error) {
	var config Config
	data, err := ioutil.ReadFile(configPath)
	if err != nil {
		return config, fmt.Errorf("failed to read config file: %v", err)
	}
	err = yaml.Unmarshal(data, &config)
	if err != nil {
		return config, fmt.Errorf("failed to unmarshal config: %v", err)
	}
	return config, nil
}

// ListManifests lists all available manifests from the manifest directory.
func ListManifests(manifestDir string) ([]string, error) {
	files, err := ioutil.ReadDir(manifestDir)
	if err != nil {
		return nil, err
	}

	var manifests []string
	for _, file := range files {
		if filepath.Ext(file.Name()) == ".yaml" {
			manifests = append(manifests, file.Name())
		}
	}
	return manifests, nil
}

// GetManifest reads and parses a manifest from a YAML file.
func GetManifest(manifestPath string) (Manifest, error) {
	var manifest Manifest
	yamlFile, err := ioutil.ReadFile(manifestPath)
	if err != nil {
		return manifest, err
	}

	err = yaml.Unmarshal(yamlFile, &manifest)
	if err != nil {
		return manifest, err
	}

	// Normalize included_manifests paths to forward slashes
	for i, path := range manifest.IncludedManifests {
		manifest.IncludedManifests[i] = filepath.ToSlash(path)
	}

	return manifest, nil
}

// SaveManifest saves a manifest back to its YAML file.
func SaveManifest(manifestPath string, manifest Manifest) error {
	// Normalize included_manifests paths to forward slashes before saving
	for i, path := range manifest.IncludedManifests {
		manifest.IncludedManifests[i] = filepath.ToSlash(path)
	}

	data, err := yaml.Marshal(manifest)
	if err != nil {
		return err
	}

	err = ioutil.WriteFile(manifestPath, data, 0644)
	if err != nil {
		return err
	}
	return nil
}

// CreateNewManifest creates a new manifest file.
func CreateNewManifest(manifestPath, name string) error {
	newManifest := Manifest{
		Name:              name,
		ManagedInstalls:   nil,
		ManagedUninstalls: nil,
		ManagedUpdates:    nil,
		OptionalInstalls:  nil,
		IncludedManifests: nil,
		Catalogs:          nil,
	}
	return SaveManifest(manifestPath, newManifest)
}

// AddPackageToManifest adds a package to the specified section of a manifest.
func AddPackageToManifest(manifest *Manifest, pkg, section string) {
	switch section {
	case "managed_installs":
		manifest.ManagedInstalls = append(manifest.ManagedInstalls, pkg)
	case "managed_uninstalls":
		manifest.ManagedUninstalls = append(manifest.ManagedUninstalls, pkg)
	case "managed_updates":
		manifest.ManagedUpdates = append(manifest.ManagedUpdates, pkg)
	case "optional_installs":
		manifest.OptionalInstalls = append(manifest.OptionalInstalls, pkg)
	default:
		fmt.Printf("Invalid section: %s\n", section)
	}
}

// RemovePackageFromManifest removes a package from the specified section of a manifest.
func RemovePackageFromManifest(manifest *Manifest, pkg, section string) {
	switch section {
	case "managed_installs":
		manifest.ManagedInstalls = removeItem(manifest.ManagedInstalls, pkg)
	case "managed_uninstalls":
		manifest.ManagedUninstalls = removeItem(manifest.ManagedUninstalls, pkg)
	case "managed_updates":
		manifest.ManagedUpdates = removeItem(manifest.ManagedUpdates, pkg)
	case "optional_installs":
		manifest.OptionalInstalls = removeItem(manifest.OptionalInstalls, pkg)
	default:
		fmt.Printf("Invalid section: %s\n", section)
	}
}

// Helper function to remove an item from a slice.
func removeItem(slice []string, item string) []string {
	for i, v := range slice {
		if v == item {
			return append(slice[:i], slice[i+1:]...)
		}
	}
	return slice
}

// Simple self-service handler functions

func handleSelfServiceRequest(packageName string) {
	fmt.Printf("Adding package '%s' to self-service manifest...\n", packageName)

	// Add to self-service manifest
	err := selfservice.AddToSelfServiceInstalls(packageName)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error adding package to self-service manifest: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf("✓ Successfully added '%s' to self-service manifest\n", packageName)
	fmt.Println("Package will be processed on next 'managedsoftwareupdate' run.")
}

func handleSelfServiceRemove(packageName string) {
	fmt.Printf("Removing package '%s' from self-service manifest...\n", packageName)

	// Remove from self-service manifest
	err := selfservice.RemoveFromSelfServiceInstalls(packageName)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error removing package from self-service manifest: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf("✓ Successfully removed '%s' from self-service manifest\n", packageName)
}

func main() {
	// Command-line arguments
	listManifests := flag.Bool("list-manifests", false, "List available manifests")
	newManifest := flag.String("new-manifest", "", "Create a new manifest")
	addPackage := flag.String("add-pkg", "", "Package to add to manifest")
	section := flag.String("section", "managed_installs", "Manifest section (managed_installs, managed_uninstalls, managed_updates, optional_installs)")
	manifestName := flag.String("manifest", "", "Manifest to operate on")
	removePackage := flag.String("remove-pkg", "", "Package to remove from manifest")
	showManifestUtilVersion := flag.Bool("manifestutil_version", false, "Print the version and exit.")

	// Self-service flags
	selfServiceRequest := flag.String("selfservice-request", "", "Add package to self-service manifest for installation")
	selfServiceRemove := flag.String("selfservice-remove", "", "Remove package from self-service manifest")

	flag.Parse()

	// Handle --version flag
	if *showManifestUtilVersion {
		version.Print()
		return
	}

	// Handle self-service commands first (simple functionality)
	switch {
	case *selfServiceRequest != "":
		handleSelfServiceRequest(*selfServiceRequest)
		return
	case *selfServiceRemove != "":
		handleSelfServiceRemove(*selfServiceRemove)
		return
	}

	// Regular manifestutil functionality
	config, err := LoadConfig(`C:\ProgramData\ManagedInstalls\Config.yaml`)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error loading config: %v\n", err)
		os.Exit(1)
	}

	manifestPath := filepath.Join(config.RepoPath, "manifests")

	// List manifests
	if *listManifests {
		manifests, err := ListManifests(manifestPath)
		if err != nil {
			fmt.Println("Error listing manifests:", err)
			return
		}
		fmt.Println("Available manifests:")
		for _, manifest := range manifests {
			fmt.Println(manifest)
		}
		return
	}

	// Create a new manifest
	if *newManifest != "" {
		manifestFilePath := filepath.Join(manifestPath, *newManifest+".yaml")
		err := CreateNewManifest(manifestFilePath, *newManifest)
		if err != nil {
			fmt.Println("Error creating manifest:", err)
			return
		}
		fmt.Println("New manifest created:", manifestFilePath)
		return
	}

	// Load manifest to modify
	if *manifestName != "" {
		manifestFilePath := filepath.Join(manifestPath, *manifestName+".yaml")
		manifest, err := GetManifest(manifestFilePath)
		if err != nil {
			fmt.Println("Error loading manifest:", err)
			return
		}

		// Add a package to the manifest
		if *addPackage != "" {
			AddPackageToManifest(&manifest, *addPackage, *section)
			err = SaveManifest(manifestFilePath, manifest)
			if err != nil {
				fmt.Println("Error saving manifest:", err)
			} else {
				fmt.Printf("Added %s to %s in %s\n", *addPackage, *section, *manifestName)
			}
		}

		// Remove a package from the manifest
		if *removePackage != "" {
			RemovePackageFromManifest(&manifest, *removePackage, *section)
			err = SaveManifest(manifestFilePath, manifest)
			if err != nil {
				fmt.Println("Error saving manifest:", err)
			} else {
				fmt.Printf("Removed %s from %s in %s\n", *removePackage, *section, *manifestName)
			}
		}
	}
}
