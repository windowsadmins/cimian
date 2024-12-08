// cmd/makecatalogs/main.go

package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"runtime"

	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"gopkg.in/yaml.v3"
)

// Initialize the logger.
func initLogger(conf *config.Configuration) {
	if err := logging.Init(conf); err != nil {
		fmt.Printf("Error initializing logging: %v\n", err)
		os.Exit(1)
	}
}

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

func loadConfig(configPath string) (*config.Configuration, error) {
	return config.LoadConfig()
}

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

func buildCatalogs(pkgsInfos []PkgsInfo) (map[string][]PkgsInfo, error) {
	catalogs := make(map[string][]PkgsInfo)

	for _, pkg := range pkgsInfos {
		for _, catalog := range pkg.Catalogs {
			catalogs[catalog] = append(catalogs[catalog], pkg)
		}
	}

	return catalogs, nil
}

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
