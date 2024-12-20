// cmd/makecatalogs/main.go

package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"

	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/version"

	"gopkg.in/yaml.v3"
)

// PkgsInfo represents the structure of the pkginfo YAML file.
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

// loadConfig loads the configuration using config.LoadConfig without any parameters.
func loadConfig() (*config.Configuration, error) {
	return config.LoadConfig()
}

// scanRepo scans the repoPath for pkginfo YAML files and returns a slice of PkgsInfo.
func scanRepo(repoPath string) ([]PkgsInfo, error) {
	var pkgsInfos []PkgsInfo

	pkgsinfoPath := filepath.Join(repoPath, "pkgsinfo")

	err := filepath.Walk(pkgsinfoPath, func(path string, info os.FileInfo, err error) error {
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
			// Set FilePath relative to RepoPath
			relativePath, err := filepath.Rel(repoPath, path)
			if err != nil {
				return fmt.Errorf("failed to compute relative path for %s: %v", path, err)
			}
			pkgsInfo.FilePath = relativePath
			pkgsInfos = append(pkgsInfos, pkgsInfo)
		}
		return nil
	})

	return pkgsInfos, err
}

// buildCatalogs organizes pkgsInfos into catalogs.
func buildCatalogs(pkgsInfos []PkgsInfo) (map[string][]PkgsInfo, error) {
	catalogs := make(map[string][]PkgsInfo)

	for _, pkg := range pkgsInfos {
		for _, catalog := range pkg.Catalogs {
			fmt.Printf("Adding %s to %s...\n", pkg.FilePath, catalog)
			catalogs[catalog] = append(catalogs[catalog], pkg)
		}
	}

	return catalogs, nil
}

// writeCatalogs writes each catalog to its respective YAML file in outputDir.
func writeCatalogs(catalogs map[string][]PkgsInfo, outputDir string) error {
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

// makeCatalogs orchestrates the process of scanning the repo and building catalogs.
func makeCatalogs(repoPath string) error {
	fmt.Println("Getting list of pkgsinfo...")
	pkgsInfos, err := scanRepo(repoPath)
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

func main() {
	// Parse flags first
	repoPath := flag.String("repo_url", "", "Path to the Gorilla repo.")
	showVersion := flag.Bool("version", false, "Print the version and exit.")
	flag.Parse()

	// If repo_url not specified, then load from config
	if *repoPath == "" {
		conf, err := loadConfig()
		if err != nil {
			fmt.Printf("Error loading config: %v\n", err)
			os.Exit(1)
		}
		*repoPath = conf.RepoPath
	}

	// flag to show version 
	if *showVersion {
		version.Print()
		return
	}

	// Execute the makeCatalogs function.
	if err := makeCatalogs(*repoPath); err != nil {
		fmt.Printf("Error: %v\n", err)
		os.Exit(1)
	}
}