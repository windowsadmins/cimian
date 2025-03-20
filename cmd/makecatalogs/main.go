// cmd\makecatalogs\main.go
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/version"
	"gopkg.in/yaml.v3"
)

var logger *logging.Logger

// Installer parallels cimianimport’s structure.
type Installer struct {
	Location    string   `yaml:"location"`
	Hash        string   `yaml:"hash"`
	Type        string   `yaml:"type"`
	Size        int64    `yaml:"size,omitempty"`
	Switches    []string `yaml:"switches,omitempty"`
	Flags       []string `yaml:"flags,omitempty"`
	Arguments   []string `yaml:"arguments,omitempty"`
	ProductCode string   `yaml:"product_code,omitempty"`
	UpgradeCode string   `yaml:"upgrade_code,omitempty"`
}

// InstallItem is the "installs" array item (if present).
type InstallItem struct {
	Type        string `yaml:"type"`
	Path        string `yaml:"path"`
	MD5Checksum string `yaml:"md5checksum,omitempty"`
	Version     string `yaml:"version,omitempty"`
}

// PkgsInfo matches your updated cimianimport pkginfo schema.
type PkgsInfo struct {
	Name                 string        `yaml:"name"`
	DisplayName          string        `yaml:"display_name,omitempty"`
	Identifier           string        `yaml:"identifier,omitempty"`
	Version              string        `yaml:"version"`
	Description          string        `yaml:"description,omitempty"`
	Catalogs             []string      `yaml:"catalogs"`
	Category             string        `yaml:"category,omitempty"`
	Developer            string        `yaml:"developer,omitempty"`
	Installs             []InstallItem `yaml:"installs,omitempty"`
	SupportedArch        []string      `yaml:"supported_architectures"`
	UnattendedInstall    bool          `yaml:"unattended_install"`
	UnattendedUninstall  bool          `yaml:"unattended_uninstall"`
	Installer            *Installer    `yaml:"installer,omitempty"`
	Uninstaller          *Installer    `yaml:"uninstaller,omitempty"`
	PreinstallScript     string        `yaml:"preinstall_script,omitempty"`
	PostinstallScript    string        `yaml:"postinstall_script,omitempty"`
	PreuninstallScript   string        `yaml:"preuninstall_script,omitempty"`
	PostuninstallScript  string        `yaml:"postuninstall_script,omitempty"`
	InstallCheckScript   string        `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string        `yaml:"uninstallcheck_script,omitempty"`

	// Not saved to YAML; only used for reference
	FilePath string `yaml:"-"`
}

// ----------------------------------------------------------------------------
// Basic scanning and reading
// ----------------------------------------------------------------------------

// loadConfig loads Cimian’s main config from the default path.
func loadConfig() (*config.Configuration, error) {
	return config.LoadConfig()
}

// scanRepo enumerates all .yaml files in <repo>/pkgsinfo
// and collects all PkgsInfo from each file's "items:" array.
func scanRepo(repoPath string) ([]PkgsInfo, error) {
	var results []PkgsInfo
	root := filepath.Join(repoPath, "pkgsinfo")

	err := filepath.Walk(root, func(path string, info os.FileInfo, werr error) error {
		if werr != nil {
			return werr
		}
		if info.IsDir() {
			return nil
		}
		if filepath.Ext(path) == ".yaml" {
			data, readErr := os.ReadFile(path)
			if readErr != nil {
				return fmt.Errorf("reading %s: %v", path, readErr)
			}

			var pkg PkgsInfo
			if yamlErr := yaml.Unmarshal(data, &pkg); yamlErr != nil {
				return fmt.Errorf("unmarshal error in %s: %v", path, yamlErr)
			}

			pkg.FilePath = path
			results = append(results, pkg)
		}
		return nil
	})

	if err != nil {
		return nil, err
	}
	return results, nil
}

// verifyPayload checks if .Installer.Location/.Uninstaller.Location exist under “pkgs/”.
// If missing => add a “missing payload” warning to the warnings slice.
func verifyPayload(repoPath string, items []PkgsInfo) ([]PkgsInfo, []string) {
	pkgsDir := filepath.Join(repoPath, "pkgs")

	// gather all existing files in /pkgs
	found := make(map[string]bool)
	filepath.Walk(pkgsDir, func(path string, info os.FileInfo, err error) error {
		if err == nil && !info.IsDir() {
			rel, _ := filepath.Rel(repoPath, path)
			found[strings.ToLower(rel)] = true
		}
		return nil
	})

	var warnings []string

	// We do not remove items if missing; we simply record warnings.
	for i := range items {
		p := &items[i]
		if p.Installer != nil && p.Installer.Location != "" {
			rel := filepath.Join("pkgs", p.Installer.Location)
			if !found[strings.ToLower(rel)] {
				warnings = append(warnings,
					fmt.Sprintf("WARNING: %s has missing installer => %s", p.FilePath, rel))
			}
		}
		if p.Uninstaller != nil && p.Uninstaller.Location != "" {
			rel := filepath.Join("pkgs", p.Uninstaller.Location)
			if !found[strings.ToLower(rel)] {
				warnings = append(warnings,
					fmt.Sprintf("WARNING: %s has missing uninstaller => %s", p.FilePath, rel))
			}
		}
	}
	return items, warnings
}

// buildCatalogs organizes items into catalogs, always including “All”.
func buildCatalogs(pkgs []PkgsInfo, silent bool) (map[string][]PkgsInfo, error) {
	cats := make(map[string][]PkgsInfo)
	cats["All"] = []PkgsInfo{}

	for _, pkg := range pkgs {
		// always add to All
		cats["All"] = append(cats["All"], pkg)

		// also add to each item’s .Catalogs
		for _, catName := range pkg.Catalogs {
			if !silent {
				logger.Debug("Adding %s to %s...", pkg.FilePath, catName)
			}
			cats[catName] = append(cats[catName], pkg)
		}
	}
	return cats, nil
}

// writeCatalogs writes each named catalog to <repo>/catalogs/<name>.yaml as an array under "items"
func writeCatalogs(repoPath string, catalogs map[string][]PkgsInfo, silent bool) error {
	catDir := filepath.Join(repoPath, "catalogs")
	if err := os.MkdirAll(catDir, 0755); err != nil {
		return fmt.Errorf("failed to create catalogs directory: %v", err)
	}

	// Remove any stale .yaml files not in our catalogs map
	dirEntries, _ := os.ReadDir(catDir)
	for _, e := range dirEntries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		base := strings.TrimSuffix(name, filepath.Ext(name))
		if _, ok := catalogs[base]; !ok {
			toRemove := filepath.Join(catDir, name)
			if rmErr := os.Remove(toRemove); rmErr == nil && !silent {
				logger.Warning("Removed stale catalog %s", toRemove)
			}
		}
	}

	for catName, items := range catalogs {
		outPath := filepath.Join(catDir, catName+".yaml")
		file, err := os.Create(outPath)
		if err != nil {
			return fmt.Errorf("creating %s: %v", outPath, err)
		}

		// Wrap items in a top-level key "items" => {"items": [...]}
		catalogWrapper := struct {
			Items []PkgsInfo `yaml:"items"`
		}{
			Items: items,
		}

		enc := yaml.NewEncoder(file)
		enc.SetIndent(2)
		if encodeErr := enc.Encode(catalogWrapper); encodeErr != nil {
			file.Close()
			return fmt.Errorf("yaml encode error for %s: %v", outPath, encodeErr)
		}
		file.Close()

		if !silent {
			logger.Success("Wrote catalog %s (%d items)", catName, len(items))
		}
	}

	return nil
}

func main() {
	repoFlag := flag.String("repo_path", "", "Path to the Cimian repo. If empty, uses config.")
	skipFlag := flag.Bool("skip_payload_check", false, "Disable checking for .Installer/.Uninstaller files.")
	silentFlag := flag.Bool("silent", false, "Minimize output.")
	showVersionFlag := flag.Bool("makecatalog_version", false, "Print version and exit.")
	flag.Parse()

	// Initialize logger
	logger = logging.New(!*silentFlag) // Enable verbose mode if not silent

	if *showVersionFlag {
		version.Print()
		os.Exit(0)
	}

	// figure out repo
	repo := *repoFlag
	if repo == "" {
		conf, err := loadConfig()
		if err != nil {
			logger.Error("Error loading config: %v", err)
			os.Exit(1)
		}
		if conf.RepoPath == "" {
			logger.Error("No repo_path found in config or via --repo_path.")
			os.Exit(1)
		}
		repo = conf.RepoPath
	}

	if !*silentFlag {
		logger.Printf("Scanning %s for .yaml pkginfo...", repo)
	}
	items, err := scanRepo(repo)
	// ----------------------------------------------------------------------------
	// main: orchestrates scanning, verifying, building, writing
	// ----------------------------------------------------------------------------

	if err != nil {
		logger.Error("Error scanning repo: %v", err)
		os.Exit(1)
	}

	var warnings []string
	finalItems := items

	// If skip_payload_check == false => do the check and accumulate warnings
	if !*skipFlag {
		finalItems, warnings = verifyPayload(repo, finalItems)
	}

	// Build catalogs
	catMap, err := buildCatalogs(finalItems, *silentFlag)
	if err != nil {
		logger.Error("Error building catalogs: %v", err)
		os.Exit(1)
	}

	// Write them
	if err := writeCatalogs(repo, catMap, *silentFlag); err != nil {
		logger.Error("Error writing catalogs: %v", err)
		os.Exit(1)
	}

	// Finally, print any collected warnings
	if len(warnings) > 0 {
		for _, w := range warnings {
			logger.Warning("%s", w)
		}
	}

	logger.Success("makecatalogs completed successfully.")
}
