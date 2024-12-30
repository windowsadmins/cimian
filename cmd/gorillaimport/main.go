// cmd/gorillaimport/main.go

package main

import (
	"flag"
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

// multiFlag for repeated -f arguments
type multiFlag []string

func (m *multiFlag) String() string { return strings.Join(*m, ", ") }
func (m *multiFlag) Set(value string) error {
	*m = append(*m, value)
	return nil
}

// Global flags
var (
	configBool               *bool
	archFlag                 *string
	installerFlag            *string
	uninstallerFlag          *string
	installScriptFlag        *string
	postinstallScriptFlag    *string
	preuninstallScriptFlag   *string
	postuninstallScriptFlag  *string
	installCheckScriptFlag   *string
	uninstallCheckScriptFlag *string
	installsArray            multiFlag
)

// init sets up the flags
func init() {
	configBool = flag.Bool("config", false, "Run interactive configuration setup")

	archFlag = flag.String("arch", "", "Architecture")
	installerFlag = flag.String("installer", "", "Installer path")
	uninstallerFlag = flag.String("uninstaller", "", "Uninstaller path")

	installScriptFlag = flag.String("install-script", "", "Install script")
	postinstallScriptFlag = flag.String("postinstall-script", "", "Post-install script")
	preuninstallScriptFlag = flag.String("preuninstall-script", "", "Pre-uninstall script")
	postuninstallScriptFlag = flag.String("postuninstall-script", "", "Post-uninstall script")

	installCheckScriptFlag = flag.String("install-check-script", "", "Path to install check script")
	uninstallCheckScriptFlag = flag.String("uninstall-check-script", "", "Path to uninstall check script")

	flag.Var(&installsArray, "i", "Add an installed path to final 'installs' array (multiple -i flags).") // Long: --installs-array
	flag.Var(&installsArray, "installs-array", "(alternative long form) Add an installed path to 'installs' array.")
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

func main() {
	var (
		repoPath              string
		category              string
		developer             string
		nameOverride          string
		displayNameOverride   string
		descriptionOverride   string
		catalogs              string
		versionOverride       string
		unattendedInstallFlag bool
	)
	var filePaths multiFlag

	flag.StringVar(&repoPath, "repo_path", "", "Gorilla repo path")
	flag.StringVar(&category, "category", "", "Category override")
	flag.StringVar(&developer, "developer", "", "Developer override")
	flag.StringVar(&nameOverride, "name", "", "Name override")
	flag.StringVar(&displayNameOverride, "displayname", "", "Display Name override")
	flag.StringVar(&descriptionOverride, "description", "", "Description override")
	flag.StringVar(&catalogs, "catalogs", "Production", "Comma-separated catalogs")
	flag.StringVar(&versionOverride, "version", "", "Version override")
	flag.BoolVar(&unattendedInstallFlag, "unattended_install", false, "Set 'unattended_install: true'")

	flag.Var(&filePaths, "f", "Add extra files to 'installs' array (multiple -f flags)")

	showGorillaImportVersion := flag.Bool("gorillaimport_version", false, "Print gorillaimport version and exit.")
	flag.Parse()

	if *showGorillaImportVersion {
		fmt.Println("gorillaimport 1.0.0 (example)")
		return
	}

	// Attempt to load configuration, or create directories if missing.
	conf, err := loadOrCreateConfig()
	if err != nil {
		fmt.Printf("Error loading config: %v\n", err)
		os.Exit(1)
	}

	// If --config with no argument => run interactive setup
	if *configBool {
		if err := configureGorillaImport(); err != nil {
			fmt.Printf("Failed to save config: %v\n", err)
			os.Exit(1)
		}
		fmt.Println("Configuration saved successfully.")
		return
	}

	// Override config with any explicit flags
	if repoPath != "" {
		conf.RepoPath = repoPath
	}
	if *archFlag != "" {
		conf.DefaultArch = *archFlag
	}

	// Figure out the main installer path
	packagePath := getInstallerPath(*installerFlag)
	if packagePath == "" {
		fmt.Println("Error: No installer provided.")
		os.Exit(1)
	}

	// Gather script paths
	scripts := ScriptPaths{
		Preinstall:     *installScriptFlag,
		Postinstall:    *postinstallScriptFlag,
		Preuninstall:   *preuninstallScriptFlag,
		Postuninstall:  *postuninstallScriptFlag,
		InstallCheck:   *installCheckScriptFlag,
		UninstallCheck: *uninstallCheckScriptFlag,
	}

	// Do the import
	importSuccess, err := gorillaImport(
		packagePath,
		conf,
		scripts,
		*uninstallerFlag,
		filePaths,
	)
	if err != nil {
		fmt.Printf("Error: %v\n", err)
		os.Exit(1)
	}

	if !importSuccess {
		os.Exit(0)
	}

	// Optionally upload to the cloud
	if conf.CloudProvider != "none" {
		if err := uploadToCloud(conf); err != nil {
			fmt.Printf("Error uploading to cloud: %v\n", err)
			os.Exit(1)
		}
	}

	// Always run makecatalogs
	if err := runMakeCatalogs(true); err != nil {
		fmt.Printf("makecatalogs error: %v\n", err)
		os.Exit(1)
	}

	// If using AWS/Azure, sync icons/pkgs
	if conf.CloudProvider == "aws" || conf.CloudProvider == "azure" {
		fmt.Println("Starting upload for icons")
		err = syncToCloud(conf, filepath.Join(conf.RepoPath, "icons"), "icons")
		if err != nil {
			fmt.Printf("Error syncing icons directory to %s: %v\n", conf.CloudProvider, err)
		}

		fmt.Println("Starting upload for pkgs")
		err = syncToCloud(conf, filepath.Join(conf.RepoPath, "pkgs"), "pkgs")
		if err != nil {
			fmt.Printf("Error syncing pkgs directory to %s: %v\n", conf.CloudProvider, err)
		}
	}

	fmt.Println("Gorilla import completed successfully.")
}

// getConfigPath returns the path for the config file.
func getConfigPath() string {
	return `C:\ProgramData\ManagedInstalls\Config.yaml`
}

// loadOrCreateConfig loads config or tries to create directories if missing
func loadOrCreateConfig() (*config.Configuration, error) {
	conf, err := config.LoadConfig()
	if err != nil {
		configPath := getConfigPath()
		configDir := filepath.Dir(configPath)
		if _, statErr := os.Stat(configDir); os.IsNotExist(statErr) {
			// Create the config directory
			if err := os.MkdirAll(configDir, 0755); err != nil {
				return nil, fmt.Errorf("failed to create config directory: %v", err)
			}
			// Try again
			conf, err = config.LoadConfig()
			if err != nil {
				return nil, fmt.Errorf("failed to load config after creating directories: %v", err)
			}
		} else {
			return nil, fmt.Errorf("failed to load config: %v", err)
		}
	}
	return conf, nil
}

// configureGorillaImport is the interactive wizard for config
func configureGorillaImport() error {
	conf := config.GetDefaultConfig()

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

// getInstallerPath tries --installer first, then position 0, then interactive
func getInstallerPath(installerFlag string) string {
	if installerFlag != "" {
		return installerFlag
	}
	if flag.NArg() > 0 {
		return flag.Arg(0)
	}
	fmt.Print("path to the installer file: ")
	var path string
	fmt.Scanln(&path)
	return path
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

	// Step 1: Basic file existence check
	if _, err := os.Stat(packagePath); os.IsNotExist(err) {
		return false, fmt.Errorf("package '%s' does not exist", packagePath)
	}
	fmt.Printf("Processing package: %s\n", packagePath)

	// Step 2: Extract minimal metadata (name, version, developer, etc.)
	metadata, err := extractInstallerMetadata(packagePath, conf)
	if err != nil {
		return false, fmt.Errorf("metadata extraction failed: %v", err)
	}
	if strings.TrimSpace(metadata.ID) == "" {
		// fallback if ID is empty
		metadata.ID = parsePackageName(filepath.Base(packagePath))
	}

	// Step 3: Immediately check for an existing item in All.yaml
	existingPkg, found, err := findMatchingItemInAllCatalog(conf.RepoPath, metadata.ID)
	if err != nil {
		fmt.Printf("Warning: could not check existing items: %v\n", err)
	} else if found && existingPkg != nil {
		fmt.Println("This item has the same Name as an existing item in the repo:")
		fmt.Printf("    Name: %s\n    Version: %s\n    Description: %s\n",
			existingPkg.Name, existingPkg.Version, existingPkg.Description)

		// Ask user if they want to reuse fields from the existing item
		answer := getInput("Use existing item as a template? [y/N]: ", "N")
		if strings.EqualFold(answer, "y") {
			// Overwrite these metadata fields
			metadata.ID = existingPkg.Name
			metadata.Title = existingPkg.DisplayName
			metadata.Version = existingPkg.Version
			metadata.Developer = existingPkg.Developer
			metadata.Description = existingPkg.Description
			metadata.Category = existingPkg.Category

			// Reuse entire array of SupportedArch
			metadata.SupportedArch = existingPkg.SupportedArch

			// If the old item had multiple catalogs, you can pick the first
			// or do something else. For simplicity, just pick the first:
			if len(existingPkg.Catalogs) > 0 {
				conf.DefaultCatalog = existingPkg.Catalogs[0]
			}
		}
	}

	// Step 4: Now do the normal interactive prompt for final overrides
	metadata = promptForAllMetadata(packagePath, metadata, conf)

	// Step 5: Gather script contents
	preinstallScriptContent, _ := loadScriptContent(scripts.Preinstall)
	postinstallScriptContent, _ := loadScriptContent(scripts.Postinstall)
	preuninstallScriptContent, _ := loadScriptContent(scripts.Preuninstall)
	postuninstallScriptContent, _ := loadScriptContent(scripts.Postuninstall)
	installCheckScriptContent, _ := loadScriptContent(scripts.InstallCheck)
	uninstallCheckScriptContent, _ := loadScriptContent(scripts.UninstallCheck)

	// Step 6: If there's an uninstaller
	uninstaller, err := processUninstaller(uninstallerPath, filepath.Join(conf.RepoPath, "pkgs"), "apps")
	if err != nil {
		return false, fmt.Errorf("uninstaller processing failed: %v", err)
	}

	// Step 7: File hash + size
	fileHash, err := utils.FileSHA256(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to calculate file hash: %v", err)
	}
	stat, err := os.Stat(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to stat installer: %v", err)
	}
	fileSizeKB := stat.Size() / 1024

	// Step 8: Build final PkgsInfo
	pkgsInfo := PkgsInfo{
		Name:          metadata.ID,
		DisplayName:   metadata.Title,
		Version:       metadata.Version,
		Description:   metadata.Description,
		Category:      metadata.Category,
		Developer:     metadata.Developer,
		Catalogs:      []string{conf.DefaultCatalog},
		Installs:      []InstallItem{},
		SupportedArch: metadata.SupportedArch,
		Installer: &Installer{
			Hash: fileHash,
			Type: metadata.InstallerType,
			Size: fileSizeKB,
		},
		Uninstaller:          uninstaller,
		UnattendedInstall:    true,
		UnattendedUninstall:  true,
		ProductCode:          strings.TrimSpace(metadata.ProductCode),
		UpgradeCode:          strings.TrimSpace(metadata.UpgradeCode),
		PreinstallScript:     preinstallScriptContent,
		PostinstallScript:    postinstallScriptContent,
		PreuninstallScript:   preuninstallScriptContent,
		PostuninstallScript:  postuninstallScriptContent,
		InstallCheckScript:   installCheckScriptContent,
		UninstallCheckScript: uninstallCheckScriptContent,
	}

	// Prompt user for where to store in repo
	repoSubPath, err := promptInstallerItemPath()
	if err != nil {
		return false, fmt.Errorf("error reading user subdirectory input: %v", err)
	}

	// Step 9: If .exe => fallback
	autoInstalls := []InstallItem{}
	if metadata.InstallerType == "exe" {
		fallbackExe := fmt.Sprintf(`C:\Program Files\%s\%s.exe`, pkgsInfo.Name, pkgsInfo.Name)
		fmt.Printf("Overriding .exe path with fallback: %s\n", fallbackExe)
		autoInstalls = append(autoInstalls, InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(fallbackExe),
			MD5Checksum: SingleQuotedString(""),
			Version:     SingleQuotedString(pkgsInfo.Version),
		})
	}

	// Step 10: If preinstall is .bat or .cmd, guess
	if ext := strings.ToLower(filepath.Ext(scripts.Preinstall)); ext == ".bat" || ext == ".cmd" {
		guessedPath := GuessInstallDirFromBat(scripts.Preinstall)
		if guessedPath != "" && len(autoInstalls) > 0 {
			autoInstalls[0].Path = SingleQuotedString(guessedPath)
		}
	}

	// Step 11: Merge user-provided -f items
	userInstalls := buildInstallsArray(filePaths)

	// Step 12: Also append .nupkg enumerations if present
	finalInstalls := append(autoInstalls, userInstalls...)
	if len(metadata.Installs) > 0 {
		finalInstalls = append(finalInstalls, metadata.Installs...)
		fmt.Printf("Appended %d .nupkg item(s) to final installs.\n", len(metadata.Installs))
	}

	// If we see \Program Files\Gorilla\ in the path, remove version
	for i := range finalInstalls {
		lowerPath := strings.ToLower(string(finalInstalls[i].Path))
		if strings.Contains(lowerPath, `\program files\gorilla\`) {
			finalInstalls[i].Version = SingleQuotedString("")
		}
	}
	pkgsInfo.Installs = finalInstalls

	// Step 13: Show final details
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

	// Step 14: Confirm
	confirm := getInput("Import this item? (y/n): ", "n")
	if !strings.EqualFold(confirm, "y") {
		fmt.Println("Import canceled.")
		return false, nil
	}

	// Step 15: Build the actual final folder within pkgs, using user-chosen subpath
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

	_, err = copyFile(packagePath, installerDest)
	if err != nil {
		return false, fmt.Errorf("failed to copy installer after confirmation: %v", err)
	}
	pkgsInfo.Installer.Location = filepath.Join(repoSubPath, installerFilename)

	// Step 16: Build the final folder for pkgsinfo, using the same subpath
	pkginfoFolderPath := filepath.Join(conf.RepoPath, "pkgsinfo", repoSubPath)
	if err := os.MkdirAll(pkginfoFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create pkginfo directory: %v", err)
	}
	err = writePkgInfoFile(pkginfoFolderPath, pkgsInfo, pkgsInfo.Name, pkgsInfo.Version, archTag)
	if err != nil {
		return false, fmt.Errorf("failed to write final pkginfo: %v", err)
	}

	return true, nil
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
		name, ver, dev, desc := extract.MsiMetadata(packagePath)
		metadata.Title = name
		metadata.ID = name
		metadata.Version = ver
		metadata.Developer = dev
		metadata.Description = desc
		metadata.InstallerType = "msi"

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

// buildInstallsArray processes -f items
func buildInstallsArray(paths []string) []InstallItem {
	var arr []InstallItem
	for _, p := range paths {
		abs, _ := filepath.Abs(p)
		fi, err := os.Stat(abs)
		if err != nil || fi.IsDir() {
			fmt.Fprintf(os.Stderr, "Skipping -f path: '%s'\n", p)
			continue
		}
		md5v, _ := utils.FileMD5(abs)
		ver := ""
		if strings.EqualFold(filepath.Ext(abs), ".exe") && runtime.GOOS == "windows" {
			version, _ := extract.ExeMetadata(abs)
			ver = version
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

// writePkgInfoFile writes the final YAML
func writePkgInfoFile(outputDir string, pkgsInfo PkgsInfo, sanitizedName, sanitizedVersion, archTag string) error {
	outputPath := filepath.Join(outputDir, sanitizedName+archTag+sanitizedVersion+".yaml")
	yamlData, err := yaml.Marshal(&pkgsInfo)
	if err != nil {
		return fmt.Errorf("failed to encode pkginfo: %v", err)
	}
	if err := os.WriteFile(outputPath, yamlData, 0644); err != nil {
		return fmt.Errorf("failed to write pkginfo to file: %v", err)
	}
	absOutputPath, err := filepath.Abs(outputPath)
	if err == nil {
		fmt.Printf("Pkginfo created at: %s\n", absOutputPath)
	}

	// If you want to open the file automatically if conf.OpenImportedYaml is true,
	// you'll need to pass conf or track it.
	// For demonstration, let's always open. Or skip if you only open on conf condition.
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
