// cmd/manifestutil/main.go

package main

import (
	"flag"
	"fmt"
	"io/ioutil"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/manifest"
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

// Self-service handler functions

func handleSelfServiceListAvailable() {
	fmt.Println("Fetching available packages...")

	// Load configuration
	cfg, err := config.LoadConfig()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error loading configuration: %v\n", err)
		os.Exit(1)
	}

	// Get manifest items to find optional_installs
	items, err := manifest.AuthenticatedGet(cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error fetching manifests: %v\n", err)
		os.Exit(1)
	}

	// Filter for optional items
	var optionalItems []manifest.Item
	for _, item := range items {
		if item.Action == "optional" {
			optionalItems = append(optionalItems, item)
		}
	}

	if len(optionalItems) == 0 {
		fmt.Println("No optional packages available for self-service installation.")
		return
	}

	fmt.Println("\nAvailable packages for self-service installation:")
	fmt.Println("=" + strings.Repeat("=", 50))

	// Sort by name for consistent output
	sort.Slice(optionalItems, func(i, j int) bool {
		return optionalItems[i].Name < optionalItems[j].Name
	})

	for _, item := range optionalItems {
		status := ""
		// Check if already in self-service manifest
		inSelfService, err := selfservice.IsItemInSelfServiceManifest(item.Name)
		if err == nil && inSelfService {
			status = " [SELECTED]"
		}

		fmt.Printf("  %s", item.Name)
		if item.Version != "" {
			fmt.Printf(" (v%s)", item.Version)
		}
		fmt.Printf("%s\n", status)
	}

	fmt.Printf("\nTotal: %d packages available\n", len(optionalItems))
	fmt.Println("\nUse 'manifestutil --selfservice-install PACKAGE' to request installation.")
}

func handleSelfServiceListInstalled() {
	manifest, err := selfservice.GetSelfServiceItems()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error loading self-service manifest: %v\n", err)
		os.Exit(1)
	}

	allItems := append(manifest.ManagedInstalls, manifest.OptionalInstalls...)

	if len(allItems) == 0 {
		fmt.Println("No packages currently selected for installation.")
		return
	}

	fmt.Println("Packages selected for installation:")
	fmt.Println("=" + strings.Repeat("=", 35))

	sort.Strings(allItems)
	for _, item := range allItems {
		fmt.Printf("  %s\n", item)
	}

	fmt.Printf("\nTotal: %d packages selected\n", len(allItems))
	fmt.Println("\nRun 'managedsoftwareupdate' to install selected packages.")
}

func handleSelfServiceInstall(packageName string) {
	fmt.Printf("Requesting installation of: %s\n", packageName)

	// Load configuration
	cfg, err := config.LoadConfig()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error loading configuration: %v\n", err)
		os.Exit(1)
	}

	// First verify the package is available as optional install
	items, err := manifest.AuthenticatedGet(cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error fetching manifests: %v\n", err)
		os.Exit(1)
	}

	packageAvailable := false
	for _, item := range items {
		if item.Action == "optional" && strings.EqualFold(item.Name, packageName) {
			packageAvailable = true
			packageName = item.Name // Use exact case from manifest
			break
		}
	}

	if !packageAvailable {
		fmt.Printf("Error: Package '%s' is not available for self-service installation.\n", packageName)
		fmt.Println("Use 'manifestutil --list-available' to see available packages.")
		os.Exit(1)
	}

	// Check if already selected
	inSelfService, err := selfservice.IsItemInSelfServiceManifest(packageName)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error checking self-service manifest: %v\n", err)
		os.Exit(1)
	}

	if inSelfService {
		fmt.Printf("Package '%s' is already selected for installation.\n", packageName)
		return
	}

	// Add to self-service manifest
	err = selfservice.AddToSelfServiceInstalls(packageName)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error adding package to self-service manifest: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf("✓ Successfully requested installation of '%s'\n", packageName)
	fmt.Println("Run 'managedsoftwareupdate' to install the package.")
}

func handleSelfServiceRemove(packageName string) {
	fmt.Printf("Removing installation request for: %s\n", packageName)

	// Check if package is in self-service manifest
	inSelfService, err := selfservice.IsItemInSelfServiceManifest(packageName)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error checking self-service manifest: %v\n", err)
		os.Exit(1)
	}

	if !inSelfService {
		fmt.Printf("Package '%s' is not currently selected for installation.\n", packageName)
		return
	}

	// Remove from self-service manifest
	err = selfservice.RemoveFromSelfServiceInstalls(packageName)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error removing package from self-service manifest: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf("✓ Successfully removed installation request for '%s'\n", packageName)
}

func handleSelfServiceStatus() {
	fmt.Println("Cimian Self-Service Status")
	fmt.Println("=" + strings.Repeat("=", 26))

	manifest, err := selfservice.GetSelfServiceItems()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error loading self-service manifest: %v\n", err)
		os.Exit(1)
	}

	allItems := append(manifest.ManagedInstalls, manifest.OptionalInstalls...)

	fmt.Printf("Self-service manifest: %s\n", selfservice.SelfServiceManifestPath)

	if len(allItems) == 0 {
		fmt.Printf("Selected packages: None\n")
	} else {
		fmt.Printf("Selected packages: %d\n", len(allItems))
		for _, item := range allItems {
			fmt.Printf("  - %s\n", item)
		}
	}

	fmt.Println()
	fmt.Println("Next steps:")
	fmt.Println("- Run 'manifestutil --list-available' to see available packages")
	fmt.Println("- Run 'manifestutil --selfservice-install PACKAGE' to request packages")
	fmt.Println("- Run 'managedsoftwareupdate' to install selected packages")
}

func main() {
	// Command-line arguments
	listManifests := flag.Bool("list-manifests", false, "List available manifests")
	newManifest := flag.String("new-manifest", "", "Create a new manifest")
	addPackage := flag.String("add-pkg", "", "Package to add to manifest")
	section := flag.String("section", "managed_installs", "Manifest section (managed_installs, managed_uninstalls, managed_updates)")
	manifestName := flag.String("manifest", "", "Manifest to operate on")
	removePackage := flag.String("remove-pkg", "", "Package to remove from manifest")
	showManifestUtilVersion := flag.Bool("manifestutil_version", false, "Print the version and exit.")

	// Self-service flags
	listAvailable := flag.Bool("list-available", false, "List packages available for self-service installation")
	listSelfService := flag.Bool("list-selfservice", false, "List packages in self-service manifest")
	selfServiceInstall := flag.String("selfservice-install", "", "Request package for self-service installation")
	selfServiceRemove := flag.String("selfservice-remove", "", "Remove package from self-service installation queue")
	selfServiceStatus := flag.Bool("selfservice-status", false, "Show self-service status")

	flag.Parse()

	// Handle --version flag
	if *showManifestUtilVersion {
		version.Print()
		return
	}

	// Handle self-service commands first (they use different config loading)
	switch {
	case *listAvailable:
		handleSelfServiceListAvailable()
		return
	case *listSelfService:
		handleSelfServiceListInstalled()
		return
	case *selfServiceInstall != "":
		handleSelfServiceInstall(*selfServiceInstall)
		return
	case *selfServiceRemove != "":
		handleSelfServiceRemove(*selfServiceRemove)
		return
	case *selfServiceStatus:
		handleSelfServiceStatus()
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
