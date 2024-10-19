// cmd/makecatalogs/main.go

package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"gopkg.in/yaml.v3"
	"github.com/rodchristiansen/gorilla/pkg/config"
	"github.com/rodchristiansen/gorilla/pkg/logging"
)

// Initialize logger with configuration.
func initLogger(conf *config.Configuration) {
	logging.InitLogger(*conf)
}

// PkgsInfo represents the structure of a package's metadata.
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
	FilePath            string
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

// CatalogsMap stores catalogs with their respective package information.
type CatalogsMap map[string][]PkgsInfo

// Config structure holds the configuration settings
type Config struct {
	RepoPath       string `yaml:"repo_path"`
	CloudProvider  string `yaml:"cloud_provider"`
	CloudBucket    string `yaml:"cloud_bucket"`
	DefaultCatalog string `yaml:"default_catalog"`
	DefaultArch    string `yaml:"default_arch"`
}

// Get the appropriate configuration path based on the OS.
func getConfigPath() string {
	switch runtime.GOOS {
	case "darwin":
		return filepath.Join(os.Getenv("HOME"), "Library/Preferences/com.github.gorilla.import.yaml")
	case "windows":
		return filepath.Join(os.Getenv("APPDATA"), "Gorilla", "import.yaml")
	default:
		return "config.yaml"
	}
}

// Load the configuration from a YAML file.
func loadConfig(configPath string) (*config.Configuration, error) {
	return config.LoadConfig()
}

// Scan the pkgsinfo directory and read all pkginfo YAML files.
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
			pkgsInfo.FilePath = path
			pkgsInfos = append(pkgsInfos, pkgsInfo)
		}
		return nil
	})

	return pkgsInfos, err
}

// Build catalogs by processing the list of package information.
func buildCatalogs(pkgsInfos []PkgsInfo) (CatalogsMap, error) {
	catalogs := make(CatalogsMap)

	for _, pkg := range pkgsInfos {
		for _, catalog := range pkg.Catalogs {
			catalogs[catalog] = append(catalogs[catalog], pkg)
		}
	}

	return catalogs, nil
}

// Write the catalogs to YAML files in the output directory.
func writeCatalogs(catalogs CatalogsMap, outputDir string) error {
	if err := os.MkdirAll(outputDir, 0755); err != nil {
		return fmt.Errorf("failed to create output directory: %v", err)
	}

	for catalog, pkgs := range catalogs {
		filePath := filepath.Join(outputDir, catalog+".yaml")
		file, err := os.Create(filePath)
		if err != nil {
			return fmt.Errorf("failed to create file %s: %v", filePath, err)
		}
		defer file.Close()

		encoder := yaml.NewEncoder(file)
		if err := encoder.Encode(pkgs); err != nil {
			return fmt.Errorf("failed to write YAML to %s: %v", filePath, err)
		}
		encoder.Close()
		fmt.Printf("Catalog %s written to %s\n", catalog, filePath)
	}

	return nil
}

// Main function for building and writing catalogs.
func makeCatalogs(repoPath string, skipPkgCheck, force bool) error {
	fmt.Println("Getting list of pkgsinfo...")
	pkgsInfos, err := scanRepo(filepath.Join(repoPath, "pkgsinfo"))
	if err != nil {
		return fmt.Errorf("error scanning repo: %v", err)
	}

	catalogs, err := buildCatalogs(pkgsInfos)
	if err != nil {
		return fmt.Errorf("error building catalogs: %v", err)
	}

	if err := writeCatalogs(catalogs, filepath.Join(repoPath, "catalogs")); err != nil {
		return fmt.Errorf("error writing catalogs: %v", err)
	}

	return nil
}

// Main entry point.
func main() {
	configPath := getConfigPath()
	conf, err := loadConfig(configPath)
	if err != nil {
		fmt.Printf("Error loading config: %v\n", err)
		os.Exit(1)
	}

	initLogger(conf)

	repoPath := flag.String("repo_url", "", "Path to the Gorilla repo.")
	force := flag.Bool("force", false, "Disable sanity checks.")
	skipPkgCheck := flag.Bool("skip-pkg-check", false, "Skip checking of pkg existence.")
	showVersion := flag.Bool("version", false, "Print the version and exit.")
	flag.Parse()

	if *showVersion {
		fmt.Println("gorilla makecatalogs version 1.0")
		return
	}

	if *repoPath == "" {
	    *repoPath = conf.RepoPath
	}

	if err := makeCatalogs(*repoPath, *skipPkgCheck, *force); err != nil {
		fmt.Printf("Error: %v\n", err)
		os.Exit(1)
	}
}
