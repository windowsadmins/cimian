package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"

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
	Notes               string   `yaml:"notes,omitempty"`
	InstallerLocation   string   `yaml:"installer_item_location,omitempty"`
	UninstallerLocation string   `yaml:"uninstaller_item_location,omitempty"`
	FilePath string
}

// loadConfig loads the configuration using config.LoadConfig without any parameters.
func loadConfig() (*config.Configuration, error) {
	return config.LoadConfig()
}

// scanRepo scans the repoPath for pkgsinfo YAML files and returns a slice of PkgsInfo.
func scanRepo(repoPath string) ([]PkgsInfo, error) {
	var pkgsInfos []PkgsInfo
	pkgsinfoPath := filepath.Join(repoPath, "pkgsinfo")

	err := filepath.Walk(pkgsinfoPath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if filepath.Ext(path) == ".yaml" {
			fileContent, readErr := os.ReadFile(path)
			if readErr != nil {
				return readErr
			}
			var pkgsInfo PkgsInfo
			if yamlErr := yaml.Unmarshal(fileContent, &pkgsInfo); yamlErr != nil {
				return yamlErr
			}
			relPath, relErr := filepath.Rel(repoPath, path)
			if relErr != nil {
				return fmt.Errorf("failed to compute relative path for %s: %v", path, relErr)
			}
			pkgsInfo.FilePath = relPath
			pkgsInfos = append(pkgsInfos, pkgsInfo)
		}
		return nil
	})
	return pkgsInfos, err
}

// processPkgsInfos handles optional verification of installer/uninstaller files.
func processPkgsInfos(repoPath string, pkgsInfos []PkgsInfo, skipPayloadCheck bool, force bool) ([]PkgsInfo, error) {
	var verifiedList []PkgsInfo

	// Map actual package files under repoPath/pkgs.
	pkgsDir := filepath.Join(repoPath, "pkgs")
	allPkgFiles := make(map[string]bool)

	err := filepath.Walk(pkgsDir, func(path string, info os.FileInfo, werr error) error {
		if werr != nil {
			return werr
		}
		if !info.IsDir() {
			rel, _ := filepath.Rel(repoPath, path)
			allPkgFiles[strings.ToLower(rel)] = true
		}
		return nil
	})
	if err != nil {
		return nil, fmt.Errorf("failed scanning pkgs: %v", err)
	}

	for _, pkg := range pkgsInfos {
		// Remove notes or anything else you don't want in final catalogs.
		pkg.Notes = ""

		// If skipping checks, accept immediately.
		if skipPayloadCheck {
			verifiedList = append(verifiedList, pkg)
			continue
		}

		// Check the installer item, if declared.
		if pkg.InstallerLocation != "" {
			rel := filepath.Join("pkgs", pkg.InstallerLocation)
			relLower := strings.ToLower(rel)
			if !allPkgFiles[relLower] {
				msg := fmt.Sprintf("WARNING: Missing installer_item_location: %s", rel)
				if force {
					fmt.Println(msg, "- continuing due to force")
				} else {
					fmt.Println(msg, "- skipping pkginfo because not forced")
					continue
				}
			}
		}

		// Check the uninstaller item, if declared.
		if pkg.UninstallerLocation != "" {
			rel := filepath.Join("pkgs", pkg.UninstallerLocation)
			relLower := strings.ToLower(rel)
			if !allPkgFiles[relLower] {
				msg := fmt.Sprintf("WARNING: Missing uninstaller_item_location: %s", rel)
				if force {
					fmt.Println(msg, "- continuing due to force")
				} else {
					fmt.Println(msg, "- skipping pkginfo because not forced")
					continue
				}
			}
		}

		verifiedList = append(verifiedList, pkg)
	}
	return verifiedList, nil
}

// buildCatalogs organizes pkgsInfos into catalogs, always including them in "All".
func buildCatalogs(pkgsInfos []PkgsInfo, silent bool) (map[string][]PkgsInfo, error) {
	catalogs := make(map[string][]PkgsInfo)
	catalogs["All"] = []PkgsInfo{}

	for _, pkg := range pkgsInfos {
		// Always add to an "All" catalog (distinct from Munki's internal "all").
		catalogs["All"] = append(catalogs["All"], pkg)

		// Add to any other catalogs the pkg references.
		for _, catName := range pkg.Catalogs {
			if !silent {
				fmt.Printf("Adding %s to %s...\n", pkg.FilePath, catName)
			}
			catalogs[catName] = append(catalogs[catName], pkg)
		}
	}
	return catalogs, nil
}

// writeCatalogs writes each catalog to YAML, removing stale catalogs not in the new set.
func writeCatalogs(repoPath string, catalogs map[string][]PkgsInfo) error {
	catalogDir := filepath.Join(repoPath, "catalogs")
	if err := os.MkdirAll(catalogDir, 0755); err != nil {
		return fmt.Errorf("failed to create catalogs dir: %v", err)
	}

	// Remove any existing catalog files that aren't in our new set.
	entries, _ := os.ReadDir(catalogDir)
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		base := strings.TrimSuffix(name, filepath.Ext(name)) // e.g. "All" from "All.yaml"
		if _, ok := catalogs[base]; !ok {
			removePath := filepath.Join(catalogDir, name)
			if rmErr := os.Remove(removePath); rmErr == nil {
				fmt.Printf("Removed stale catalog %s\n", removePath)
			}
		}
	}

	// Write out the updated catalogs.
	for catName, pkgs := range catalogs {
		filePath := filepath.Join(catalogDir, catName+".yaml")
		file, err := os.Create(filePath)
		if err != nil {
			return fmt.Errorf("failed creating catalog file %s: %v", filePath, err)
		}
		enc := yaml.NewEncoder(file)
		if encErr := enc.Encode(pkgs); encErr != nil {
			file.Close()
			return fmt.Errorf("failed encoding yaml for %s: %v", filePath, encErr)
		}
		file.Close()
		fmt.Printf("Wrote catalog %s (%d items)\n", catName, len(pkgs))
	}

	return nil
}

// main is your entry point, handling flags and orchestrating the process.
func main() {
	// Flags
	repoPathFlag := flag.String("repo_path", "",
		"Path to the Gorilla repo (if empty, config is used).")
	skipCheckFlag := flag.Bool("skip_payload_check", false,
		"Skip checking that installer/uninstaller items exist.")
	forceFlag := flag.Bool("force", false,
		"Allow missing installer/uninstaller items (not recommended).")
	versionFlag := flag.Bool("version", false,
		"Print the version of Gorilla and exit.")
	silentFlag := flag.Bool("silent", false,
		"Suppress output for a silent run.")
	flag.Parse()

	// Handle version request
	if *versionFlag {
		version.Print()
		os.Exit(0)
	}

	// Resolve repo path
	var repoPath string
	if *repoPathFlag == "" {
		conf, confErr := loadConfig()
		if confErr != nil {
			fmt.Fprintf(os.Stderr, "Error loading config: %v\n", confErr)
			os.Exit(1)
		}
		repoPath = conf.RepoPath
		if repoPath == "" {
			fmt.Fprintln(os.Stderr, "No repo path provided, and none found in config.")
			os.Exit(1)
		}
	} else {
		repoPath = *repoPathFlag
	}

	// Step 1: Scan pkgsinfo
	fmt.Printf("Scanning %s for YAML...\n", repoPath)
	pkgsInfos, err := scanRepo(repoPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error scanning pkgsinfo: %v\n", err)
		os.Exit(1)
	}

	// Step 2: Verify references (unless skipping)
	verifiedPkgs, err := processPkgsInfos(repoPath, pkgsInfos, *skipCheckFlag, *forceFlag)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error processing pkgsInfos: %v\n", err)
		os.Exit(1)
	}

	// Step 3: Build catalogs (respecting -silent)
	catalogs, err := buildCatalogs(verifiedPkgs, *silentFlag)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error building catalogs: %v\n", err)
		os.Exit(1)
	}

	// Step 4: Write catalogs
	if err := writeCatalogs(repoPath, catalogs); err != nil {
		fmt.Fprintf(os.Stderr, "Error writing catalogs: %v\n", err)
		os.Exit(1)
	}

	fmt.Println("makecatalogs completed successfully.")
}