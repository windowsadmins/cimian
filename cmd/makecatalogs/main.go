package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"gopkg.in/yaml.v3"
)

// PkgsInfo structure holds package metadata
type PkgsInfo struct {
	Name                string   `yaml:"name"`
	DisplayName         string   `yaml:"display_name"`
	Version             string   `yaml:"version"`
	Description         string   `yaml:"description"`
	Catalogs            []string `yaml:"catalogs"`
	Category            string   `yaml:"category"`
	Developer           string   `yaml:"developer"`
	UnattendedInstall   bool     `yaml:"unattended_install"`
	UnattendedUninstall bool     `yaml:"unattended_uninstall"`
	InstallerItemHash   string   `yaml:"installer_item_hash"`
	SupportedArch       []string `yaml:"supported_architectures"`
	ProductCode         string   `yaml:"product_code,omitempty"`
	UpgradeCode         string   `yaml:"upgrade_code,omitempty"`

	// New fields for Gorilla support
	Dependencies       []string     `yaml:"dependencies,omitempty"`
	Check              *Check       `yaml:"check,omitempty"`
	Installer          *Installer   `yaml:"installer,omitempty"`
	Uninstaller        *Installer   `yaml:"uninstaller,omitempty"`
	PreInstallScript   string       `yaml:"preinstall_script,omitempty"`
	PostInstallScript  string       `yaml:"postinstall_script,omitempty"`
}

// Check structure for file, script, and registry checks
type Check struct {
	File     []FileCheck   `yaml:"file,omitempty"`
	Script   string        `yaml:"script,omitempty"`
	Registry *RegistryCheck `yaml:"registry,omitempty"`
}

// FileCheck structure for checking files
type FileCheck struct {
	Path    string `yaml:"path"`
	Version string `yaml:"version,omitempty"`
	Hash    string `yaml:"hash,omitempty"`
}

// RegistryCheck structure for checking registry entries
type RegistryCheck struct {
	Name    string `yaml:"name"`
	Version string `yaml:"version,omitempty"`
}

// Installer structure for both installers and uninstallers
type Installer struct {
	Arguments []string `yaml:"arguments,omitempty"`
	Hash      string   `yaml:"hash"`
	Location  string   `yaml:"location"`
	Type      string   `yaml:"type"`
}

// Catalog structure holds a list of packages for each catalog
type Catalog struct {
	Packages []PkgsInfo `yaml:"packages"`
}

// CatalogsMap is a map where the key is the catalog name and the value is the list of packages
type CatalogsMap map[string]*Catalog

// Config structure holds the configuration settings
type Config struct {
	RepoPath       string `yaml:"repo_path"`
	CloudProvider  string `yaml:"cloud_provider"`
	CloudBucket    string `yaml:"cloud_bucket"`
	DefaultCatalog string `yaml:"default_catalog"`
	DefaultArch    string `yaml:"default_arch"`
}

// getConfigPath returns the appropriate configuration file path based on the OS
func getConfigPath() string {
	if runtime.GOOS == "darwin" {
		return filepath.Join(os.Getenv("HOME"), "Library/Preferences/com.github.gorilla.import.yaml")
	} else if runtime.GOOS == "windows" {
		return filepath.Join(os.Getenv("APPDATA"), "Gorilla", "import.yaml")
	}
	return "config.yaml" // Default path for other OSes
}

// loadConfig loads the configuration from a YAML file
func loadConfig(configPath string) (Config, error) {
	var config Config
	file, err := os.Open(configPath)
	if err != nil {
		return config, err
	}
	defer file.Close()

	yamlDecoder := yaml.NewDecoder(file)
	if err := yamlDecoder.Decode(&config); err != nil {
		return config, err
	}

	return config, nil
}

// scanRepo scans the pkgsinfo directory and reads all pkginfo YAML files
func scanRepo(repoPath string) ([]PkgsInfo, error) {
	var pkgsInfos []PkgsInfo

	err := filepath.Walk(repoPath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if filepath.Ext(path) == ".yaml" {
			fileContent, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			var pkgsInfo PkgsInfo
			if err := yaml.Unmarshal(fileContent, &pkgsInfo); err != nil {
				return err
			}
			pkgsInfos = append(pkgsInfos, pkgsInfo)
		}
		return nil
	})

	return pkgsInfos, err
}

// buildCatalogs builds catalogs based on the packages and their specified catalogs
func buildCatalogs(pkgsInfos []PkgsInfo) CatalogsMap {
	catalogs := make(CatalogsMap)
	allPackages := []PkgsInfo{}

	for _, pkg := range pkgsInfos {
		for _, catalogName := range pkg.Catalogs {
			if _, ok := catalogs[catalogName]; !ok {
				catalogs[catalogName] = &Catalog{}
			}
			catalogs[catalogName].Packages = append(catalogs[catalogName].Packages, pkg)
		}
		allPackages = append(allPackages, pkg)
	}

	// Add all packages to 'All.yaml'
	catalogs["All"] = &Catalog{Packages: allPackages}

	return catalogs
}

// writeCatalogs writes the catalogs to YAML files
func writeCatalogs(repoPath string, catalogs CatalogsMap) error {
	catalogsDir := filepath.Join(repoPath, "catalogs")
	if _, err := os.Stat(catalogsDir); os.IsNotExist(err) {
		if err := os.MkdirAll(catalogsDir, 0755); err != nil {
			return fmt.Errorf("failed to create catalogs directory: %v", err)
		}
	}

	for catalogName, catalog := range catalogs {
		outputFile := filepath.Join(catalogsDir, fmt.Sprintf("%s.yaml", catalogName))
		file, err := os.Create(outputFile)
		if err != nil {
			return fmt.Errorf("failed to create catalog file: %v", err)
		}
		defer file.Close()

		encoder := yaml.NewEncoder(file)
		if err := encoder.Encode(catalog); err != nil {
			return fmt.Errorf("failed to encode catalog to YAML: %v", err)
		}
	}

	return nil
}

// makeCatalogs is the main function that scans the repo, builds, and writes catalogs
func makeCatalogs(repoPath string, skipPkgCheck, force bool) error {
	// Scan the pkgsinfo directory for package info files
	pkgsInfos, err := scanRepo(filepath.Join(repoPath, "pkgsinfo"))
	if err != nil {
		return fmt.Errorf("error scanning repo: %v", err)
	}

	// Build the catalogs based on the package info files
	catalogs := buildCatalogs(pkgsInfos)

	// Write the catalogs to the repo/catalogs directory
	if err := writeCatalogs(repoPath, catalogs); err != nil {
		return fmt.Errorf("error writing catalogs: %v", err)
	}

	fmt.Println("Catalogs updated successfully.")
	return nil
}

// main handles the command-line arguments and runs the makecatalogs function
func main() {
	// Define the flags
	repoPath := flag.String("repo_url", "", "Path to the Gorilla repo.")
	force := flag.Bool("force", false, "Disable sanity checks.")
	skipPkgCheck := flag.Bool("skip-pkg-check", false, "Skip checking of pkg existence.")
	showVersion := flag.Bool("version", false, "Print the version and exit.")

	flag.Parse()

	if *showVersion {
		fmt.Println("gorilla makecatalogs version 1.0")
		return
	}

	// Get the config path based on the OS
	configPath := getConfigPath()

	// Load the config
	config, err := loadConfig(configPath)
	if err != nil {
		fmt.Printf("Error loading config: %v\n", err)
		os.Exit(1)
	}

	// Use the repo path from the config if not provided via the flag
	if *repoPath == "" {
		*repoPath = config.RepoPath
	}

	// Run the makeCatalogs function
	if err := makeCatalogs(*repoPath, *skipPkgCheck, *force); err != nil {
		fmt.Printf("Error: %v\n", err)
		os.Exit(1)
	}
}
