// cmd/gorillaimport/main.go

//go:build windows
// +build windows

package main

import (
	"bytes"
	"fmt"
	"io"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"

	"gopkg.in/yaml.v3"

	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/extract"
	"github.com/windowsadmins/gorilla/pkg/utils"
)

// PkgsInfo represents the structure of the pkginfo YAML file.
type PkgsInfo struct {
	Name                 string        `yaml:"name"`
	DisplayName          string        `yaml:"display_name,omitempty"`
	Version              string        `yaml:"version"`
	Description          string        `yaml:"description,omitempty"`
	Catalogs             []string      `yaml:"catalogs"`
	Category             string        `yaml:"category,omitempty"`
	Developer            string        `yaml:"developer,omitempty"`
	Installs             []InstallItem `yaml:"installs,omitempty"`
	SupportedArch        []string      `yaml:"supported_architectures"`
	UnattendedInstall    bool          `yaml:"unattended_install"`
	UnattendedUninstall  bool          `yaml:"unattended_uninstall"`
	Installer            *Installer    `yaml:"installer"`
	Uninstaller          *Installer    `yaml:"uninstaller,omitempty"`
	ProductCode          string        `yaml:"product_code,omitempty"`
	UpgradeCode          string        `yaml:"upgrade_code,omitempty"`
	PreinstallScript     string        `yaml:"preinstall_script,omitempty"`
	PostinstallScript    string        `yaml:"postinstall_script,omitempty"`
	PreuninstallScript   string        `yaml:"preuninstall_script,omitempty"`
	PostuninstallScript  string        `yaml:"postuninstall_script,omitempty"`
	InstallCheckScript   string        `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string        `yaml:"uninstallcheck_script,omitempty"`
}

// Installer represents the structure of the installer/uninstaller in pkginfo.
type Installer struct {
	Location  string   `yaml:"location"`
	Hash      string   `yaml:"hash"`
	Type      string   `yaml:"type"`
	Size      int64    `yaml:"size,omitempty"`
	Arguments []string `yaml:"arguments,omitempty"`
}

// InstallItem for the "installs" array.
type InstallItem struct {
	Type        SingleQuotedString `yaml:"type"`
	Path        SingleQuotedString `yaml:"path"`
	MD5Checksum SingleQuotedString `yaml:"md5checksum"`
	Version     SingleQuotedString `yaml:"version,omitempty"`
}

// SingleQuotedString ensures single quotes in YAML output.
type SingleQuotedString string

func (s SingleQuotedString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Style: yaml.SingleQuotedStyle,
		Value: string(s),
	}
	return node, nil
}

// For capturing scripts.
type ScriptPaths struct {
	Preinstall     string
	Postinstall    string
	Preuninstall   string
	Postuninstall  string
	InstallCheck   string
	UninstallCheck string
}

// Metadata as in your code
type Metadata struct {
	Title         string
	ID            string
	Version       string
	Developer     string
	Category      string
	Description   string
	ProductCode   string
	UpgradeCode   string
	Architecture  string
	SupportedArch []string
	InstallerType string
	Installs      []InstallItem
}

// parseCustomArgs manually parses os.Args for:
//  1. The first non-flag => packagePath
//  2. -i or --installs-array => appended to filePaths
//  3. key=value flags => stored in otherFlags["key"] = "value"
//  4. single flags => stored in otherFlags["flag"] = "true" (boolean style)
//  5. a special boolean configRequested if user typed --config
//
// Returns (packagePath, filePaths, otherFlags, configRequested).
func parseCustomArgs(args []string) (string, []string, map[string]string, bool, bool, bool) {
	var packagePath string
	filePaths := []string{}
	otherFlags := make(map[string]string)
	configRequested := false
	helpRequested := false
	configAuto := false

	skipNext := false
	for i := 1; i < len(args); i++ {
		if skipNext {
			skipNext = false
			continue
		}
		arg := args[i]

		// Check --help or -h
		if arg == "--help" || arg == "-h" {
			helpRequested = true
			continue
		}
		// Check --config-auto
		if arg == "--config-auto" {
			// This signals we want non-interactive config
			configRequested = true
			configAuto = true
			continue
		}
		// Check --config
		if arg == "--config" {
			configRequested = true
			continue
		}

		switch {
		// -i or --installs-array => next token is file path
		case arg == "-i" || arg == "--installs-array":
			if i+1 < len(args) {
				filePaths = append(filePaths, args[i+1])
				skipNext = true
			}

		// If this is something like -arch=x64 or --arch=x64
		case strings.HasPrefix(arg, "-") && strings.Contains(arg, "="):
			// Split on the first '='
			parts := strings.SplitN(arg, "=", 2)
			// The first part might be "-arch" or "--arch"; strip leading '-'
			key := strings.TrimLeft(parts[0], "-")
			val := parts[1]
			otherFlags[key] = val

		// Otherwise, check if the next arg belongs to this flag
		case strings.HasPrefix(arg, "-"):
			// Maybe user typed: -arch x64 (no '=')
			// If the next arg doesn't start with '-', treat it as this flag's value
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") {
				key := strings.TrimLeft(arg, "-")
				otherFlags[key] = args[i+1]
				skipNext = true
			} else {
				// It's a standalone boolean flag
				key := strings.TrimLeft(arg, "-")
				otherFlags[key] = "true"
			}

		// First non-flag => packagePath (main installer path)
		default:
			if packagePath == "" {
				packagePath = arg
			}
		}
	}

	return packagePath, filePaths, otherFlags, configRequested, helpRequested, configAuto
}

func main() {
	// 1) Patch Windows args so spaces are preserved:
	utils.PatchWindowsArgs()

	// 2) Manually parse arguments for:
	//   - The first non-flag => main installer path
	//   - -i or --installs-array => user filePaths
	//   - everything else => otherFlags
	//   - and check if user typed --config / --help / --config-auto
	packagePath, filePaths, otherFlags, configRequested, helpRequested, configAuto := parseCustomArgs(os.Args)

	// If user typed --help or -h
	if helpRequested {
		showUsageAndExit()
	}

	// If user typed --config (with optional --config-auto)
	if configRequested {
		conf, err := loadOrCreateConfig()
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error loading config: %v\n", err)
			os.Exit(1)
		}

		// If user typed --config or --config-auto
		if configRequested {
			// If user also typed --config-auto, we *still* do interactive first
			// (i.e., `--config` overrides `--config-auto` if both are set).
			if !configAuto {
				// interactive config
				if err := configureGorillaImport(conf); err != nil {
					fmt.Fprintf(os.Stderr, "Failed to save config (interactive): %v\n", err)
					os.Exit(1)
				}
				fmt.Println("Configuration saved successfully.")
			} else {
				// non-interactive
				if err := configureGorillaImportNonInteractive(conf); err != nil {
					fmt.Fprintf(os.Stderr, "Failed to save config (auto): %v\n", err)
					os.Exit(1)
				}
			}
			// Either path => exit
			return
		}

		// In either case, we exit after config
		return
	}

	// If packagePath was empty, prompt user:
	if packagePath == "" {
		packagePath = getInstallerPathInteractive()
		if packagePath == "" {
			fmt.Fprintln(os.Stderr, "No installer path provided; exiting.")
			os.Exit(1)
		}
	}

	fmt.Printf("DEBUG: packagePath=%q, #filePaths=%d\n", packagePath, len(filePaths))

	// 3) Load config
	conf, err := loadOrCreateConfig()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error loading config: %v\n", err)
		os.Exit(1)
	}

	// 4) If user typed -arch=..., override conf.DefaultArch
	if val, ok := otherFlags["arch"]; ok && val != "" {
		conf.DefaultArch = val
		fmt.Println("DEBUG: Overriding conf.DefaultArch =", val)
	}
	// If user typed -repo_path=..., override conf.RepoPath
	if val, ok := otherFlags["repo_path"]; ok && val != "" {
		conf.RepoPath = val
		fmt.Println("DEBUG: Overriding conf.RepoPath =", val)
	}

	// 5) Build script paths from e.g. -preinstall-script=..., etc.
	scripts := ScriptPaths{
		Preinstall:     otherFlags["preinstall-script"],
		Postinstall:    otherFlags["postinstall-script"],
		Preuninstall:   otherFlags["preuninstall-script"],
		Postuninstall:  otherFlags["postuninstall-script"],
		InstallCheck:   otherFlags["install-check-script"],
		UninstallCheck: otherFlags["uninstall-check-script"],
	}
	// If user typed e.g. -uninstaller="C:\some\path"
	uninstallerPath := otherFlags["uninstaller"]

	// 6) Do the gorillaImport
	success, err := gorillaImport(
		packagePath, // the main installer
		conf,        // loaded config
		scripts,     // your script paths
		uninstallerPath,
		filePaths, // the array from -i flags
	)
	if err != nil {
		fmt.Fprintln(os.Stderr, "Error in gorillaImport:", err)
		os.Exit(1)
	}
	if !success {
		os.Exit(0) // user canceled
	}

	// 7) upload to cloud if set
	if conf.CloudProvider != "none" {
		if err := uploadToCloud(conf); err != nil {
			fmt.Fprintln(os.Stderr, "Error uploading to cloud:", err)
		}
	}

	// 8) Always run makecatalogs
	if err := runMakeCatalogs(true); err != nil {
		fmt.Fprintln(os.Stderr, "makecatalogs error:", err)
	}

	// 9) If AWS/Azure => optionally sync pkgs or icons
	if conf.CloudProvider == "aws" || conf.CloudProvider == "azure" {
		fmt.Println("Starting upload for pkgs")
		err = syncToCloud(conf, filepath.Join(conf.RepoPath, "pkgs"), "pkgs")
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error syncing pkgs to %s: %v\n", conf.CloudProvider, err)
		}
	}

	fmt.Println("Gorilla import completed successfully.")
}

// getConfigPath returns the path for the config file.
func getConfigPath() string {
	return `C:\ProgramData\ManagedInstalls\Config.yaml`
}

// loadOrCreateConfig loads or prompts. Similar to your existing code.
func loadOrCreateConfig() (*config.Configuration, error) {
	conf, err := config.LoadConfig()
	if err != nil {
		configPath := getConfigPath()
		configDir := filepath.Dir(configPath)
		if _, statErr := os.Stat(configDir); os.IsNotExist(statErr) {
			if err2 := os.MkdirAll(configDir, 0755); err2 != nil {
				return nil, fmt.Errorf("failed to create config directory: %v", err2)
			}
			conf, err = config.LoadConfig()
			if err != nil {
				return nil, fmt.Errorf("failed to load config after creating dirs: %v", err)
			}
		} else {
			return nil, err
		}
	}
	return conf, nil
}

// configureGorillaImport is the interactive wizard for config
func configureGorillaImport(conf *config.Configuration) error {
	// conf is already loaded.
	// Instead of creating a new conf or calling config.GetDefaultConfig(),
	// just use the one passed in.

	usr, err := user.Current()
	if err != nil {
		return fmt.Errorf("failed to get current user: %v", err)
	}

	defaultRepoPath := filepath.Join(usr.HomeDir, "DevOps", "Gorilla", "deployment")
	defaultCloudProvider := "none"
	defaultCatalog := "Development"
	defaultArch := "x64"

	fmt.Printf("Enter Repo Path [%s]: ", defaultRepoPath)
	var repoPathInput string
	fmt.Scanln(&repoPathInput)
	if strings.TrimSpace(repoPathInput) == "" {
		conf.RepoPath = defaultRepoPath
	} else {
		conf.RepoPath = repoPathInput
	}

	fmt.Printf("Enter Cloud Provider (aws/azure/none) [%s]: ", defaultCloudProvider)
	var cloudProviderInput string
	fmt.Scanln(&cloudProviderInput)
	if strings.TrimSpace(cloudProviderInput) == "" {
		conf.CloudProvider = defaultCloudProvider
	} else {
		conf.CloudProvider = cloudProviderInput
	}

	if conf.CloudProvider != "none" {
		fmt.Print("Enter Cloud Bucket: ")
		fmt.Scanln(&conf.CloudBucket)
	}

	fmt.Printf("Enter Default Catalog [%s]: ", defaultCatalog)
	var defaultCatalogInput string
	fmt.Scanln(&defaultCatalogInput)
	if strings.TrimSpace(defaultCatalogInput) == "" {
		conf.DefaultCatalog = defaultCatalog
	} else {
		conf.DefaultCatalog = defaultCatalogInput
	}

	fmt.Printf("Enter Default Architecture [%s]: ", defaultArch)
	var defaultArchInput string
	fmt.Scanln(&defaultArchInput)
	if strings.TrimSpace(defaultArchInput) == "" {
		conf.DefaultArch = defaultArch
	} else {
		conf.DefaultArch = defaultArchInput
	}

	fmt.Printf("Open imported YAML after creation? [true/false] (%v): ", conf.OpenImportedYaml)
	var openYamlInput string
	fmt.Scanln(&openYamlInput)
	if strings.TrimSpace(openYamlInput) != "" {
		val := strings.TrimSpace(strings.ToLower(openYamlInput))
		conf.OpenImportedYaml = (val == "true")
	}

	configPath := getConfigPath()
	configDir := filepath.Dir(configPath)
	if _, err := os.Stat(configDir); os.IsNotExist(err) {
		if mkErr := os.MkdirAll(configDir, 0755); mkErr != nil {
			return fmt.Errorf("failed to create config directory: %v", mkErr)
		}
	}
	if err := config.SaveConfig(conf); err != nil {
		return err
	}
	return nil
}

func configureGorillaImportNonInteractive(conf *config.Configuration) error {
	// Only fill in defaults if they're currently empty/zero:

	// If no RepoPath yet, fill from the user’s profile
	if conf.RepoPath == "" {
		userProfile := os.Getenv("USERPROFILE")
		if userProfile == "" {
			usr, err := user.Current()
			if err != nil {
				return fmt.Errorf("cannot determine user profile: %v", err)
			}
			userProfile = usr.HomeDir
		}
		conf.RepoPath = filepath.Join(userProfile, "DevOps", "Gorilla", "deployment")
	}
	if conf.CloudProvider == "" {
		conf.CloudProvider = "none"
	}
	if conf.DefaultCatalog == "" {
		conf.DefaultCatalog = "Development"
	}
	if conf.DefaultArch == "" {
		conf.DefaultArch = "x64"
	}
	if !conf.OpenImportedYaml {
		conf.OpenImportedYaml = true
	}

	configPath := getConfigPath()
	configDir := filepath.Dir(configPath)
	if _, err := os.Stat(configDir); os.IsNotExist(err) {
		if mkErr := os.MkdirAll(configDir, 0755); mkErr != nil {
			return fmt.Errorf("failed to create config directory: %v", mkErr)
		}
	}
	if err := config.SaveConfig(conf); err != nil {
		return fmt.Errorf("failed to save non-interactive config: %v", err)
	}
	return nil
}

func findMatchingItemInAllCatalog(repoPath string, newItemName string) (*PkgsInfo, bool, error) {
	// For Gorilla, your `All.yaml` is at:
	allCatalogPath := filepath.Join(repoPath, "catalogs", "All.yaml")

	// read All.yaml
	fileContent, err := os.ReadFile(allCatalogPath)
	if err != nil {
		// if this fails, you could run `runMakeCatalogs(false)` or just return
		return nil, false, fmt.Errorf("failed to read All.yaml: %v", err)
	}

	var allPackages []PkgsInfo
	if err := yaml.Unmarshal(fileContent, &allPackages); err != nil {
		return nil, false, fmt.Errorf("failed to unmarshal All.yaml: %v", err)
	}

	// Compare item.Name (or product code, or both)
	// In Munki, we compare on `name`; you might want to do the same or use `ProductCode`
	newNameLower := strings.TrimSpace(strings.ToLower(newItemName))
	for _, item := range allPackages {
		existingNameLower := strings.TrimSpace(strings.ToLower(item.Name))
		if existingNameLower == newNameLower {
			// Found a match with same Name
			return &item, true, nil
		}
	}
	return nil, false, nil
}

// getInstallerPathInteractive prompts the user for an installer path if none
// was passed on the command line. This ensures the function is used.
func getInstallerPathInteractive() string {
	fmt.Print("path to the installer file: ")
	var path string
	fmt.Scanln(&path)
	return strings.TrimSpace(path)
}

// A more robust pattern for capturing lines like:
//
//	--INSTALLDIR=somePath
//	--INSTALLDIR="C:\Some Path"
//	--INSTALLDIR='C:\Some Path'
//
// or the same with INSTALLLOCATION
var reInstallDir = regexp.MustCompile(`(?i)--INSTALL(?:DIR|LOCATION)\s*=\s*(?:"([^"]+)"|'([^']+)'|(\S+))`)

// GuessInstallDirFromBat tries to parse out an install directory from .bat lines
func GuessInstallDirFromBat(batPath string) string {
	data, err := os.ReadFile(batPath)
	if err != nil {
		return ""
	}
	lines := strings.Split(string(data), "\n")

	for _, line := range lines {
		line = strings.TrimSpace(line)
		if strings.Contains(strings.ToLower(line), "--installdir=") ||
			strings.Contains(strings.ToLower(line), "--installlocation=") {
			matches := reInstallDir.FindStringSubmatch(line)
			if len(matches) == 4 {
				// matches[1] => double-quoted path
				// matches[2] => single-quoted path
				// matches[3] => unquoted path
				if matches[1] != "" {
					return matches[1]
				}
				if matches[2] != "" {
					return matches[2]
				}
				if matches[3] != "" {
					return matches[3]
				}
			}
		}
	}
	return ""
}

func convertExtractItems(ei []extract.InstallItem) []InstallItem {
	converted := make([]InstallItem, 0, len(ei))
	for _, item := range ei {
		converted = append(converted, InstallItem{
			Type:        SingleQuotedString(item.Type),
			Path:        SingleQuotedString(item.Path),
			MD5Checksum: SingleQuotedString(item.MD5Checksum),
			Version:     SingleQuotedString(item.Version),
		})
	}
	return converted
}

// promptInstallerItemPath asks user "Repo location (default: /apps):"
// then returns e.g. "/apps" or "/utilities" etc.
func promptInstallerItemPath() (string, error) {
	fmt.Print("Repo location (default: /apps): ")
	var path string
	fmt.Scanln(&path)
	path = strings.TrimSpace(path)
	if path == "" {
		path = "/apps"
	}
	if !strings.HasPrefix(path, "/") {
		path = "/" + path
	}
	path = strings.TrimRight(path, "/")
	return path, nil
}

// gorillaImport ingests an installer, writes pkgsinfo, etc.
func gorillaImport(
	packagePath string,
	conf *config.Configuration,
	scripts ScriptPaths,
	uninstallerPath string,
	filePaths []string,
) (bool, error) {

	fmt.Println("DEBUG: gorillaImport: packagePath=", packagePath, " len(filePaths)=", len(filePaths))

	// Step 1: check file
	if _, err := os.Stat(packagePath); os.IsNotExist(err) {
		return false, fmt.Errorf("package '%s' does not exist", packagePath)
	}

	// Step 2: extract metadata
	metadata, err := extractInstallerMetadata(packagePath, conf)
	if err != nil {
		return false, fmt.Errorf("metadata extraction failed: %v", err)
	}
	if strings.TrimSpace(metadata.ID) == "" {
		metadata.ID = parsePackageName(filepath.Base(packagePath))
	}

	// Step 3: see if item already in All.yaml
	existingPkg, found, err := findMatchingItemInAllCatalog(conf.RepoPath, metadata.ID)
	if err != nil {
		fmt.Println("Warning: could not check existing items:", err)
	} else if found && existingPkg != nil {
		fmt.Println("This item has the same Name as an existing item in the repo:")
		fmt.Printf("    Name: %s\n    Version: %s\n    Description: %s\n",
			existingPkg.Name, existingPkg.Version, existingPkg.Description)
		ans := getInput("Use existing item as a template? [y/N]: ", "N")
		if strings.EqualFold(ans, "y") {
			metadata.ID = existingPkg.Name
			metadata.Title = existingPkg.DisplayName
			metadata.Version = existingPkg.Version
			metadata.Developer = existingPkg.Developer
			metadata.Description = existingPkg.Description
			metadata.Category = existingPkg.Category
			metadata.SupportedArch = existingPkg.SupportedArch
			if len(existingPkg.Catalogs) > 0 {
				conf.DefaultCatalog = existingPkg.Catalogs[0]
			}
		}
	}

	// Step 4: let user override some fields
	metadata = promptForAllMetadata(packagePath, metadata, conf)

	// Step 5: gather script contents
	preinstallScriptContent, _ := loadScriptContent(scripts.Preinstall)
	postinstallScriptContent, _ := loadScriptContent(scripts.Postinstall)
	preuninstallScriptContent, _ := loadScriptContent(scripts.Preuninstall)
	postuninstallScriptContent, _ := loadScriptContent(scripts.Postuninstall)
	installCheckScriptContent, _ := loadScriptContent(scripts.InstallCheck)
	uninstallCheckScriptContent, _ := loadScriptContent(scripts.UninstallCheck)

	// Step 6: handle uninstaller if any
	uninstaller, err := processUninstaller(uninstallerPath,
		filepath.Join(conf.RepoPath, "pkgs"), "apps")
	if err != nil {
		return false, fmt.Errorf("uninstaller processing failed: %v", err)
	}

	// Step 7: file hash + size
	fileHash, err := utils.FileSHA256(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to calculate file hash: %v", err)
	}
	stat, err := os.Stat(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to stat installer: %v", err)
	}
	fileSizeKB := stat.Size() / 1024

	// Step 8: build PkgsInfo
	pkgsInfo := PkgsInfo{
		Name:          metadata.ID,
		DisplayName:   metadata.ID,
		Version:       metadata.Version,
		Description:   metadata.Description,
		Category:      metadata.Category,
		Developer:     metadata.Developer,
		Catalogs:      []string{conf.DefaultCatalog},
		SupportedArch: metadata.SupportedArch,
		Installs:      []InstallItem{},

		Installer: &Installer{
			Hash: fileHash,
			Type: metadata.InstallerType,
			Size: fileSizeKB,
		},
		Uninstaller:         uninstaller,
		UnattendedInstall:   true,
		UnattendedUninstall: true,

		ProductCode:          strings.TrimSpace(metadata.ProductCode),
		UpgradeCode:          strings.TrimSpace(metadata.UpgradeCode),
		PreinstallScript:     preinstallScriptContent,
		PostinstallScript:    postinstallScriptContent,
		PreuninstallScript:   preuninstallScriptContent,
		PostuninstallScript:  postuninstallScriptContent,
		InstallCheckScript:   installCheckScriptContent,
		UninstallCheckScript: uninstallCheckScriptContent,
	}

	// Step 9: prompt user for subdir
	repoSubPath, err := promptInstallerItemPath()
	if err != nil {
		return false, err
	}

	// Step 10: decide final installs
	var finalInstalls []InstallItem
	if len(filePaths) > 0 {
		// user-provided -i => skip fallback
		finalInstalls = buildInstallsArray(filePaths)
	} else if metadata.InstallerType == "exe" {
		fallbackExe := fmt.Sprintf(`C:\Program Files\%s\%s.exe`,
			pkgsInfo.Name, pkgsInfo.Name)
		fmt.Println("Using fallback .exe =>", fallbackExe)
		finalInstalls = []InstallItem{{
			Type:    SingleQuotedString("file"),
			Path:    SingleQuotedString(fallbackExe),
			Version: SingleQuotedString(pkgsInfo.Version),
		}}
	}

	// If there's e.g. nupkg-based installs
	if len(metadata.Installs) > 0 {
		finalInstalls = append(finalInstalls, metadata.Installs...)
	}
	pkgsInfo.Installs = finalInstalls

	// Step 11: show final details
	fmt.Println("\nPkginfo details:")
	fmt.Printf("    Name: %s\n", pkgsInfo.Name)
	fmt.Printf("    Display Name: %s\n", pkgsInfo.DisplayName)
	fmt.Printf("    Version: %s\n", pkgsInfo.Version)
	fmt.Printf("    Description: %s\n", pkgsInfo.Description)
	fmt.Printf("    Category: %s\n", pkgsInfo.Category)
	fmt.Printf("    Developer: %s\n", pkgsInfo.Developer)
	fmt.Printf("    Architectures: %s\n", strings.Join(pkgsInfo.SupportedArch, ", "))
	fmt.Printf("    Catalogs: %s\n", strings.Join(pkgsInfo.Catalogs, ", "))
	fmt.Printf("    Installer Type: %s\n", pkgsInfo.Installer.Type)
	fmt.Println()

	confirm := getInput("Import this item? (y/n): ", "n")
	if !strings.EqualFold(confirm, "y") {
		fmt.Println("Import canceled.")
		return false, nil
	}

	// Step 12: copy installer to pkgs subdir
	installerFolderPath := filepath.Join(conf.RepoPath, "pkgs", repoSubPath)
	if err := os.MkdirAll(installerFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create installer directory: %v", err)
	}
	archTag := ""
	if len(pkgsInfo.SupportedArch) > 0 && pkgsInfo.SupportedArch[0] == "x64" {
		archTag = "-x64-"
	}
	installerFilename := pkgsInfo.Name + archTag + pkgsInfo.Version + filepath.Ext(packagePath)
	installerDest := filepath.Join(installerFolderPath, installerFilename)

	if _, err := copyFile(packagePath, installerDest); err != nil {
		return false, fmt.Errorf("failed to copy installer: %v", err)
	}
	pkgsInfo.Installer.Location = filepath.Join(repoSubPath, installerFilename)

	// Step 13: write pkginfo to pkgsinfo subdir
	pkginfoFolderPath := filepath.Join(conf.RepoPath, "pkgsinfo", repoSubPath)
	if err := os.MkdirAll(pkginfoFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create pkginfo directory: %v", err)
	}

	err = writePkgInfoFile(pkginfoFolderPath, pkgsInfo,
		pkgsInfo.Name, pkgsInfo.Version, archTag)
	if err != nil {
		return false, fmt.Errorf("failed to write final pkginfo: %v", err)
	}

	return true, nil
}

// extractInstallerMetadata for the main installer
func extractInstallerMetadata(packagePath string, conf *config.Configuration) (Metadata, error) {
	ext := strings.ToLower(filepath.Ext(packagePath))
	var metadata Metadata

	switch ext {
	case ".nupkg":
		name, ver, dev, desc := extract.NupkgMetadata(packagePath)

		// Truncate domain-based name => last segment
		truncated := extract.TruncateDomain(name)

		metadata.Title = truncated
		metadata.ID = truncated
		metadata.Version = ver
		metadata.Developer = dev
		metadata.Description = desc
		metadata.InstallerType = "nupkg"

		// Try gorillapkg paths
		gorillaItems, err := extract.BuildGorillaPkgInstalls(packagePath, metadata.ID, metadata.Version)
		if err != nil {
			fmt.Printf("Warning: BuildGorillaPkgInstalls failed: %v\n", err)
		}

		if len(gorillaItems) > 0 {
			// Great, we have "C:\\Program Files\\Gorilla..." style items
			metadata.Installs = convertExtractItems(gorillaItems)
		} else {
			// Not a gorillapkg-based .nupkg => fallback to standard chocolatey style
			chocoItems := extract.BuildNupkgInstalls(packagePath, metadata.ID, metadata.Version)
			metadata.Installs = convertExtractItems(chocoItems)
			fmt.Printf("No gorillapkg items found; using %d chocolatey items.\n", len(chocoItems))
		}

	case ".msi":
		name, ver, dev, desc, prodCode, upgCode := extract.MsiMetadata(packagePath)
		metadata.Title = name
		metadata.ID = name
		metadata.Version = ver
		metadata.Developer = dev
		metadata.Description = desc
		metadata.InstallerType = "msi"
		metadata.ProductCode = prodCode
		metadata.UpgradeCode = upgCode

	case ".exe":
		versionString, err := extract.ExeMetadata(packagePath)
		devName, devErr := extract.ExtractExeDeveloper(packagePath)

		metadata.Title = parsePackageName(filepath.Base(packagePath))
		metadata.ID = metadata.Title
		metadata.Version = versionString
		if err != nil {
			metadata.Version = ""
		}

		if devErr == nil && devName != "" {
			metadata.Developer = devName
		} else {
			metadata.Developer = ""
		}

		metadata.Description = ""
		metadata.InstallerType = "exe"

	default:
		metadata.InstallerType = "unknown"
		metadata.Title = parsePackageName(filepath.Base(packagePath))
		metadata.ID = metadata.Title
		metadata.Version = "1.0.0"
	}

	// Ensure architecture is set
	if metadata.Architecture == "" {
		metadata.Architecture = conf.DefaultArch
	}
	metadata.SupportedArch = []string{metadata.Architecture}
	return metadata, nil
}

func promptForAllMetadata(packagePath string, m Metadata, conf *config.Configuration) Metadata {
	defaultID := m.ID
	if defaultID == "" {
		defaultID = parsePackageName(filepath.Base(packagePath))
	}
	defaultVersion := m.Version
	if defaultVersion == "" {
		defaultVersion = "1.0.0"
	}
	defaultDeveloper := m.Developer
	defaultDescription := m.Description
	defaultCategory := m.Category
	defaultArch := conf.DefaultArch

	fmt.Printf("Identifier [%s]: ", defaultID)
	var input string
	fmt.Scanln(&input)
	input = strings.TrimSpace(input)
	if input == "" {
		m.ID = defaultID
	} else {
		m.ID = input
	}
	fmt.Printf("Version [%s]: ", defaultVersion)
	input = ""
	fmt.Scanln(&input)
	input = strings.TrimSpace(input)
	if input == "" {
		m.Version = defaultVersion
	} else {
		m.Version = input
	}
	fmt.Printf("Developer [%s]: ", defaultDeveloper)
	input = ""
	fmt.Scanln(&input)
	input = strings.TrimSpace(input)
	if input == "" {
		m.Developer = defaultDeveloper
	} else {
		m.Developer = input
	}
	fmt.Printf("Description [%s]: ", defaultDescription)
	input = ""
	fmt.Scanln(&input)
	input = strings.TrimSpace(input)
	if input == "" {
		m.Description = defaultDescription
	} else {
		m.Description = input
	}
	fmt.Printf("Category [%s]: ", defaultCategory)
	input = ""
	fmt.Scanln(&input)
	input = strings.TrimSpace(input)
	if input == "" {
		m.Category = defaultCategory
	} else {
		m.Category = input
	}
	fmt.Printf("Architecture(s) [%s]: ", defaultArch)
	input = ""
	fmt.Scanln(&input)
	input = strings.TrimSpace(input)
	if input == "" {
		m.Architecture = defaultArch
	} else {
		m.Architecture = input
	}
	return m
}

func parsePackageName(filename string) string {
	return strings.TrimSuffix(filename, filepath.Ext(filename))
}

// loadScriptContent reads script or returns empty
func loadScriptContent(path string) (string, error) {
	if path == "" {
		return "", nil
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return string(b), nil
}

// processUninstaller copies the uninstaller if provided
func processUninstaller(uninstallerPath, pkgsFolderPath, installerSubPath string) (*Installer, error) {
	if uninstallerPath == "" {
		return nil, nil
	}
	if _, err := os.Stat(uninstallerPath); os.IsNotExist(err) {
		return nil, fmt.Errorf("uninstaller '%s' does not exist", uninstallerPath)
	}
	uninstallerHash, err := utils.FileSHA256(uninstallerPath)
	if err != nil {
		return nil, fmt.Errorf("error calculating uninstaller hash: %v", err)
	}
	uninstallerFilename := filepath.Base(uninstallerPath)
	uninstallerDest := filepath.Join(pkgsFolderPath, uninstallerFilename)
	if _, err := copyFile(uninstallerPath, uninstallerDest); err != nil {
		return nil, fmt.Errorf("failed to copy uninstaller: %v", err)
	}
	return &Installer{
		Location: filepath.Join("/", installerSubPath, uninstallerFilename),
		Hash:     uninstallerHash,
		Type:     strings.TrimPrefix(filepath.Ext(uninstallerPath), "."),
	}, nil
}

// copyFile from src to dst
func copyFile(src, dst string) (int64, error) {
	in, err := os.Open(src)
	if err != nil {
		return 0, err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return 0, err
	}
	defer out.Close()
	n, err := io.Copy(out, in)
	if err != nil {
		return 0, err
	}
	return n, out.Sync()
}

// buildInstallsArray processes -i items
func buildInstallsArray(paths []string) []InstallItem {
	var arr []InstallItem
	for _, p := range paths {
		abs, _ := filepath.Abs(p)
		fi, err := os.Stat(abs)
		if err != nil || fi.IsDir() {
			fmt.Fprintf(os.Stderr, "Skipping -i path: '%s'\n", p)
			continue
		}
		md5v, _ := utils.FileMD5(abs)
		var ver string
		if runtime.GOOS == "windows" && strings.EqualFold(filepath.Ext(abs), ".exe") {
			if exev, err := extract.ExeMetadata(abs); err == nil {
				ver = exev
			}
		}
		arr = append(arr, InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(abs),
			MD5Checksum: SingleQuotedString(md5v),
			Version:     SingleQuotedString(ver),
		})
	}
	return arr
}

// getInput reads input with a default
func getInput(prompt, defaultVal string) string {
	fmt.Printf("%s [%s]: ", prompt, defaultVal)
	var input string
	fmt.Scanln(&input)
	input = strings.TrimSpace(input)
	if input == "" {
		return defaultVal
	}
	return input
}

// runMakeCatalogs runs makecatalogs.exe
func runMakeCatalogs(silent bool) error {
	makeCatalogsBinary := `C:\Program Files\Gorilla\makecatalogs.exe`
	if _, err := os.Stat(makeCatalogsBinary); os.IsNotExist(err) {
		return fmt.Errorf("makecatalogs binary not found at %s", makeCatalogsBinary)
	}
	args := []string{}
	if silent {
		args = append(args, "-silent")
	}
	cmd := exec.Command(makeCatalogsBinary, args...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	fmt.Printf("Running makecatalogs from: %s\n", makeCatalogsBinary)
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("makecatalogs execution failed: %v", err)
	}
	return nil
}

// uploadToCloud handles AWS/Azure sync
func uploadToCloud(conf *config.Configuration) error {
	localPkgsPath := filepath.Join(conf.RepoPath, "pkgs")
	localIconsPath := filepath.Join(conf.RepoPath, "icons")

	if conf.CloudProvider == "aws" {
		pkgsDestination := fmt.Sprintf("s3://%s/pkgs/", conf.CloudBucket)
		cmdPkgs := exec.Command("aws", "s3", "sync", localPkgsPath, pkgsDestination, "--exclude", "*.git/*", "--exclude", "**/.DS_Store")
		cmdPkgs.Stdout = os.Stdout
		cmdPkgs.Stderr = os.Stderr
		if err := cmdPkgs.Run(); err != nil {
			return fmt.Errorf("failed to sync pkgs to AWS S3: %v", err)
		}

		iconsDestination := fmt.Sprintf("s3://%s/icons/", conf.CloudBucket)
		cmdIcons := exec.Command("aws", "s3", "sync", localIconsPath, iconsDestination, "--exclude", "*.git/*", "--exclude", "**/.DS_Store")
		cmdIcons.Stdout = os.Stdout
		cmdIcons.Stderr = os.Stderr
		if err := cmdIcons.Run(); err != nil {
			return fmt.Errorf("failed to sync icons to AWS S3: %v", err)
		}
	} else if conf.CloudProvider == "azure" {
		pkgsDestination := fmt.Sprintf("https://%s/pkgs/", conf.CloudBucket)
		cmdPkgs := exec.Command("azcopy", "sync", localPkgsPath, pkgsDestination, "--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")
		cmdPkgs.Stdout = os.Stdout
		cmdPkgs.Stderr = os.Stderr
		if err := cmdPkgs.Run(); err != nil {
			return fmt.Errorf("failed to sync pkgs to Azure: %v", err)
		}

		iconsDestination := fmt.Sprintf("https://%s/icons/", conf.CloudBucket)
		cmdIcons := exec.Command("azcopy", "sync", localIconsPath, iconsDestination, "--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")
		cmdIcons.Stdout = os.Stdout
		cmdIcons.Stderr = os.Stderr
		if err := cmdIcons.Run(); err != nil {
			return fmt.Errorf("failed to sync icons to Azure: %v", err)
		}
	} else {
		return fmt.Errorf("unsupported cloud provider: %s", conf.CloudProvider)
	}
	return nil
}

// syncToCloud can handle a single subfolder
func syncToCloud(conf *config.Configuration, source, destinationSubPath string) error {
	var destination string
	if conf.CloudProvider == "aws" {
		destination = fmt.Sprintf("s3://%s/%s/", conf.CloudBucket, destinationSubPath)
	} else if conf.CloudProvider == "azure" {
		destination = fmt.Sprintf("https://%s/%s/", conf.CloudBucket, destinationSubPath)
	} else {
		return fmt.Errorf("unsupported cloud provider: %s", conf.CloudProvider)
	}

	if conf.CloudProvider == "aws" {
		cmd := exec.Command("aws", "s3", "sync", source, destination, "--exclude", "*.git/*", "--exclude", "**/.DS_Store")
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("error syncing %s to S3: %v", destinationSubPath, err)
		}
	} else if conf.CloudProvider == "azure" {
		cmd := exec.Command("azcopy", "sync", source, destination, "--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("error syncing %s to Azure: %v", destinationSubPath, err)
		}
	}

	fmt.Printf("Successfully synced %s to %s\n", source, destination)
	return nil
}

func encodeWithSelectiveBlockScalars(pkgsInfo PkgsInfo) ([]byte, error) {
	// We'll encode pkgsInfo -> YAML, then decode into a yaml.Node,
	// set the literal style for certain fields, and re-encode.

	var buf bytes.Buffer
	encoder := yaml.NewEncoder(&buf)
	encoder.SetIndent(2)

	if err := encoder.Encode(&pkgsInfo); err != nil {
		return nil, fmt.Errorf("failed to encode pkginfo: %v", err)
	}
	encoder.Close()

	// Decode into a root node
	node := &yaml.Node{}
	decoder := yaml.NewDecoder(&buf)
	if err := decoder.Decode(node); err != nil {
		return nil, fmt.Errorf("failed to decode re-encoded pkginfo: %v", err)
	}

	// The document node's actual content is usually node.Content[0]
	if node.Kind == yaml.DocumentNode && len(node.Content) > 0 {
		node = node.Content[0]
	}

	// Define which fields need literal block style
	scriptFields := map[string]bool{
		"preinstall_script":     true,
		"postinstall_script":    true,
		"preuninstall_script":   true,
		"postuninstall_script":  true,
		"installcheck_script":   true,
		"uninstallcheck_script": true,
	}

	// Iterate over top-level YAML mappings and set the style for any script fields
	for i := 0; i < len(node.Content); i += 2 {
		key := node.Content[i].Value
		if scriptFields[key] && i+1 < len(node.Content) {
			valNode := node.Content[i+1]
			if valNode.Kind == yaml.ScalarNode && valNode.Value != "" {
				// Normalize line breaks to Unix style
				valNode.Value = strings.ReplaceAll(valNode.Value, "\r\n", "\n")
				valNode.Value = strings.ReplaceAll(valNode.Value, "\r", "\n")
				// Trim trailing newlines
				valNode.Value = strings.TrimRight(valNode.Value, "\n")
				// Use literal style for multi-line
				valNode.Style = yaml.LiteralStyle
			}
		}
	}

	// Re-encode the modified node
	finalBuf := &bytes.Buffer{}
	finalEncoder := yaml.NewEncoder(finalBuf)
	finalEncoder.SetIndent(2)
	if err := finalEncoder.Encode(node); err != nil {
		return nil, fmt.Errorf("failed to re-encode pkginfo with block scalars: %v", err)
	}
	finalEncoder.Close()

	return finalBuf.Bytes(), nil
}

// writePkgInfoFile writes the final YAML
func writePkgInfoFile(outputDir string, pkgsInfo PkgsInfo, sanitizedName, sanitizedVersion, archTag string) error {
	outputPath := filepath.Join(outputDir, sanitizedName+archTag+sanitizedVersion+".yaml")
	yamlData, err := encodeWithSelectiveBlockScalars(pkgsInfo)
	if err != nil {
		return fmt.Errorf("failed to encode pkginfo with block scalars: %v", err)
	}
	if err := os.WriteFile(outputPath, yamlData, 0644); err != nil {
		return fmt.Errorf("failed to write pkginfo to file: %v", err)
	}

	absOutputPath, err := filepath.Abs(outputPath)
	if err == nil {
		fmt.Printf("Pkginfo created at: %s\n", absOutputPath)
	}

	if err := maybeOpenFile(absOutputPath); err != nil {
		fmt.Printf("Warning: could not open pkginfo in an editor: %v\n", err)
	}

	return nil
}

// maybeOpenFile tries code.cmd or notepad
func maybeOpenFile(filePath string) error {
	codeCmd, err := exec.LookPath("code.cmd")
	if err != nil {
		return exec.Command("notepad.exe", filePath).Start()
	}
	return exec.Command(codeCmd, filePath).Start()
}

func showUsageAndExit() {
	fmt.Println(`Usage: gorillaimport.exe [options] <installerPath>

Manual argument parsing is used, so the first non-flag argument is assumed 
to be the main installer path. Example:

  gorillaimport.exe "C:\Path With Spaces\SomeInstaller.exe" -i "C:\Also With Spaces\someFile.txt"

Options:
  -i, --installs-array <path>   Add a path to final 'installs' array (multiple OK)
  --repo_path=<path>            Override the Gorilla repo path
  --arch=<arch>                 Override architecture (e.g. x64)
  --uninstaller=<path>          Specify an optional uninstaller
  --install-check-script=<path> ...
  --uninstall-check-script=<path> ...
  --preinstall-script=<path>    ...
  --postinstall-script=<path>   ...
  --preuninstall-script=<path>  ...
  --postuninstall-script=<path> ...
  --config                      Run interactive configuration setup and exit
  -h, --help                    Show this usage and exit

If you specify both an installer path and one or more -i/--installs-array 
flags, the final PkgsInfo will incorporate your user-provided filePaths 
(and skip the .exe fallback).`)
	os.Exit(0)
}
