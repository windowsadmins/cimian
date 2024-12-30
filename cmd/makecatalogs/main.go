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

// Installer parallels your updated gorillaimport struct, with new location/hash/type/size.
type Installer struct {
	Location  string   `yaml:"location"`
	Hash      string   `yaml:"hash"`
	Type      string   `yaml:"type"`
	Size      int64    `yaml:"size,omitempty"`
	Arguments []string `yaml:"arguments,omitempty"`
}

// InstallItem for the "installs" array (still optional in pkginfo).
type InstallItem struct {
	Type        string `yaml:"type"`
	Path        string `yaml:"path"`
	MD5Checksum string `yaml:"md5checksum,omitempty"`
	Version     string `yaml:"version,omitempty"`
}

// PkgsInfo is your updated pkginfo structure, matching gorillaimport’s schema.
type PkgsInfo struct {
	Name                 string        `yaml:"name"`
	DisplayName          string        `yaml:"display_name,omitempty"`
	Version              string        `yaml:"version"`
	Description          string        `yaml:"description,omitempty"`
	Catalogs             []string      `yaml:"catalogs"` // always at least one
	Category             string        `yaml:"category,omitempty"`
	Developer            string        `yaml:"developer,omitempty"`
	Installs             []InstallItem `yaml:"installs,omitempty"`
	SupportedArch        []string      `yaml:"supported_architectures"`
	UnattendedInstall    bool          `yaml:"unattended_install"`
	UnattendedUninstall  bool          `yaml:"unattended_uninstall"`
	Installer            *Installer    `yaml:"installer,omitempty"`
	Uninstaller          *Installer    `yaml:"uninstaller,omitempty"`
	ProductCode          string        `yaml:"product_code,omitempty"`
	UpgradeCode          string        `yaml:"upgrade_code,omitempty"`
	PreinstallScript     string        `yaml:"preinstall_script,omitempty"`
	PostinstallScript    string        `yaml:"postinstall_script,omitempty"`
	PreuninstallScript   string        `yaml:"preuninstall_script,omitempty"`
	PostuninstallScript  string        `yaml:"postuninstall_script,omitempty"`
	InstallCheckScript   string        `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string        `yaml:"uninstallcheck_script,omitempty"`

	// Not typically stored in YAML, but we keep it in-memory for reference:
	FilePath string `yaml:"-"` // relative path to the .yaml in your repo
}

// ----------------------------------------------------------------------------
// Basic scanning and reading
// ----------------------------------------------------------------------------

// loadConfig is unchanged — loads config from your Gorilla config location.
func loadConfig() (*config.Configuration, error) {
	return config.LoadConfig()
}

// scanRepo enumerates all .yaml under <repoPath>/pkgsinfo and loads them into PkgsInfo objects.
func scanRepo(repoPath string) ([]PkgsInfo, error) {
	var results []PkgsInfo
	pkgsinfoRoot := filepath.Join(repoPath, "pkgsinfo")

	err := filepath.Walk(pkgsinfoRoot, func(path string, info os.FileInfo, werr error) error {
		if werr != nil {
			return werr
		}
		if info.IsDir() {
			return nil
		}
		if filepath.Ext(path) == ".yaml" {
			data, readErr := os.ReadFile(path)
			if readErr != nil {
				return fmt.Errorf("failed reading %s: %v", path, readErr)
			}
			var pkginfo PkgsInfo
			if unmarshalErr := yaml.Unmarshal(data, &pkginfo); unmarshalErr != nil {
				return fmt.Errorf("yaml unmarshal error in %s: %v", path, unmarshalErr)
			}
			// store relative path for reference
			rel, relErr := filepath.Rel(repoPath, path)
			if relErr != nil {
				return fmt.Errorf("failed computing relative path for %s: %v", path, relErr)
			}
			pkginfo.FilePath = rel
			results = append(results, pkginfo)
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	return results, nil
}

// ----------------------------------------------------------------------------
// Optional checks for existence of the actual installer/uninstaller files
// ----------------------------------------------------------------------------

// processPkgsInfos can skip or enforce payload checks. If not skipping, it tries to locate
// pkg.Installer.Location in <repo>/pkgs/... and skip the item if missing (unless forced).
func processPkgsInfos(repoPath string, pkgs []PkgsInfo, skipCheck bool, force bool) ([]PkgsInfo, error) {
	// if skipping, we return them as-is
	if skipCheck {
		return pkgs, nil
	}

	// map actual files in <repo>/pkgs
	pkgsDir := filepath.Join(repoPath, "pkgs")
	foundFiles := make(map[string]bool)

	// record all the files in lower-case for case-insensitive
	err := filepath.Walk(pkgsDir, func(path string, info os.FileInfo, werr error) error {
		if werr != nil {
			return werr
		}
		if !info.IsDir() {
			rel, _ := filepath.Rel(repoPath, path)
			foundFiles[strings.ToLower(rel)] = true
		}
		return nil
	})
	if err != nil {
		return nil, fmt.Errorf("failed scanning %s: %v", pkgsDir, err)
	}

	var verified []PkgsInfo
	for _, p := range pkgs {
		// Check .Installer
		if p.Installer != nil && p.Installer.Location != "" {
			// Construct relative path
			relPath := filepath.Join("pkgs", p.Installer.Location)
			relLower := strings.ToLower(relPath)
			if !foundFiles[relLower] {
				msg := fmt.Sprintf("WARNING: Missing installer file => %s", relPath)
				if force {
					fmt.Println(msg, "- ignoring due to --force")
				} else {
					fmt.Println(msg, "- skipping pkginfo")
					continue
				}
			}
		}
		// Check .Uninstaller
		if p.Uninstaller != nil && p.Uninstaller.Location != "" {
			relPath := filepath.Join("pkgs", p.Uninstaller.Location)
			relLower := strings.ToLower(relPath)
			if !foundFiles[relLower] {
				msg := fmt.Sprintf("WARNING: Missing uninstaller file => %s", relPath)
				if force {
					fmt.Println(msg, "- ignoring due to --force")
				} else {
					fmt.Println(msg, "- skipping pkginfo")
					continue
				}
			}
		}
		verified = append(verified, p)
	}
	return verified, nil
}

// ----------------------------------------------------------------------------
// Catalog building
// ----------------------------------------------------------------------------

// buildCatalogs organizes each PkgsInfo item into "All" plus the item’s .Catalogs
func buildCatalogs(pkgs []PkgsInfo, silent bool) (map[string][]PkgsInfo, error) {
	result := make(map[string][]PkgsInfo)
	// always have an "All"
	result["All"] = []PkgsInfo{}

	for _, pi := range pkgs {
		// add to "All"
		result["All"] = append(result["All"], pi)

		// also to any declared catalogs
		for _, cat := range pi.Catalogs {
			if !silent {
				fmt.Printf("Adding %s to %s...\n", pi.FilePath, cat)
			}
			result[cat] = append(result[cat], pi)
		}
	}
	return result, nil
}

// writeCatalogs writes each catalog to <repo>/catalogs/<catname>.yaml.
// Also prunes old .yaml files not in the new set.
func writeCatalogs(repoPath string, catalogs map[string][]PkgsInfo) error {
	catDir := filepath.Join(repoPath, "catalogs")
	if err := os.MkdirAll(catDir, 0755); err != nil {
		return fmt.Errorf("failed to create catalogs folder: %v", err)
	}

	// remove any stale .yaml catalogs not in our new set
	entries, _ := os.ReadDir(catDir)
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		base := strings.TrimSuffix(name, filepath.Ext(name)) // "All" from "All.yaml"
		if _, found := catalogs[base]; !found {
			toRemove := filepath.Join(catDir, name)
			os.Remove(toRemove)
			fmt.Printf("Removed stale catalog %s\n", toRemove)
		}
	}

	// now create or overwrite the catalogs we want
	for catName, items := range catalogs {
		filePath := filepath.Join(catDir, catName+".yaml")
		f, err := os.Create(filePath)
		if err != nil {
			return fmt.Errorf("failed creating %s: %v", filePath, err)
		}
		enc := yaml.NewEncoder(f)
		if e2 := enc.Encode(items); e2 != nil {
			f.Close()
			return fmt.Errorf("failed encoding YAML for %s: %v", filePath, e2)
		}
		f.Close()
		fmt.Printf("Wrote catalog %s (%d items)\n", catName, len(items))
	}
	return nil
}

// ----------------------------------------------------------------------------
// main: orchestrates scanning, verifying, building, writing
// ----------------------------------------------------------------------------

func main() {
	// flags
	repoFlag := flag.String("repo_path", "", "Path to Gorilla repo; if empty, uses config.")
	skipFlag := flag.Bool("skip_payload_check", false, "Skip verifying installer/uninstaller files.")
	forceFlag := flag.Bool("force", false, "Force ignoring missing payloads (not recommended).")
	silentFlag := flag.Bool("silent", false, "Silent mode - minimal output.")
	showVers := flag.Bool("makecatalog_version", false, "Print version and exit.")
	flag.Parse()

	if *showVers {
		version.Print() // or fmt.Println("makecatalogs 1.0.0")
		os.Exit(0)
	}

	// figure out the repo path
	repoPath := *repoFlag
	if repoPath == "" {
		cfg, cfgErr := loadConfig()
		if cfgErr != nil {
			fmt.Fprintf(os.Stderr, "Error loading config: %v\n", cfgErr)
			os.Exit(1)
		}
		if cfg.RepoPath == "" {
			fmt.Fprintln(os.Stderr, "No repo path found in config or -repo_path.")
			os.Exit(1)
		}
		repoPath = cfg.RepoPath
	}

	fmt.Printf("Scanning %s for pkginfo YAML...\n", repoPath)
	allPkgs, err := scanRepo(repoPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error scanning pkgsinfo: %v\n", err)
		os.Exit(1)
	}

	// optionally verify installer/uninstaller files
	verified, err := processPkgsInfos(repoPath, allPkgs, *skipFlag, *forceFlag)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error processing pkginfos: %v\n", err)
		os.Exit(1)
	}

	// build the catalogs
	catMap, err := buildCatalogs(verified, *silentFlag)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error building catalogs: %v\n", err)
		os.Exit(1)
	}

	// write them
	if err := writeCatalogs(repoPath, catMap); err != nil {
		fmt.Fprintf(os.Stderr, "Error writing catalogs: %v\n", err)
		os.Exit(1)
	}

	fmt.Println("makecatalogs completed successfully.")
}
