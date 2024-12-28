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
	UnattendedInstall    bool          `yaml:"unattended_install"`
	UnattendedUninstall  bool          `yaml:"unattended_uninstall"`
	Installer            *Installer    `yaml:"installer"`
	Installs             []InstallItem `yaml:"installs,omitempty"`
	Uninstaller          *Installer    `yaml:"uninstaller,omitempty"`
	SupportedArch        []string      `yaml:"supported_architectures"`
	ProductCode          string        `yaml:"product_code,omitempty"`
	UpgradeCode          string        `yaml:"upgrade_code,omitempty"`
	PreinstallScript     string        `yaml:"preinstall_script,omitempty"`
	PostinstallScript    string        `yaml:"postinstall_script,omitempty"`
	PreuninstallScript   string        `yaml:"preuninstall_script,omitempty"`
	PostuninstallScript  string        `yaml:"postuninstall_script,omitempty"`
	InstallCheckScript   string        `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string        `yaml:"uninstallcheck_script,omitempty"`

	// Added for closer alignment with makepkginfo:
	InstallerItemHash     string `yaml:"installer_item_hash,omitempty"`
	InstallerItemSize     int64  `yaml:"installer_item_size,omitempty"`
	InstallerItemLocation string `yaml:"installer_item_location,omitempty"`
}

// Installer represents the structure of the installer and uninstaller in pkginfo.
type Installer struct {
	Location  string   `yaml:"location"`
	Hash      string   `yaml:"hash"`
	Type      string   `yaml:"type"`
	Arguments []string `yaml:"arguments,omitempty"`
}

type InstallItem struct {
	Type        SingleQuotedString `yaml:"type"`
	Path        SingleQuotedString `yaml:"path"`
	MD5Checksum SingleQuotedString `yaml:"md5checksum,omitempty"`
	Version     SingleQuotedString `yaml:"version,omitempty"`
}

// SingleQuotedString type for YAML
type SingleQuotedString string

func (s SingleQuotedString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Style: yaml.SingleQuotedStyle,
		Value: string(s),
	}
	return node, nil
}

type ScriptPaths struct {
	Preinstall     string
	Postinstall    string
	Preuninstall   string
	Postuninstall  string
	InstallCheck   string
	UninstallCheck string
}

type multiFlag []string

func (m *multiFlag) String() string { return strings.Join(*m, ", ") }
func (m *multiFlag) Set(value string) error {
	*m = append(*m, value)
	return nil
}

// Flag variables
var (
	configFlag               *string
	archFlag                 *string
	installerFlag            *string
	uninstallerFlag          *string
	installScriptFlag        *string
	postinstallScriptFlag    *string
	preuninstallScriptFlag   *string
	postuninstallScriptFlag  *string
	installCheckScriptFlag   *string
	uninstallCheckScriptFlag *string
)

func init() {
	// Initialize flags
	configFlag = flag.String("config", "", "Path to config file")
	archFlag = flag.String("arch", "", "Architecture")
	installerFlag = flag.String("installer", "", "Installer path")
	uninstallerFlag = flag.String("uninstaller", "", "Uninstaller path")
	installScriptFlag = flag.String("install-script", "", "Install script")
	postinstallScriptFlag = flag.String("postinstall-script", "", "Post-install script")
	preuninstallScriptFlag = flag.String("preuninstall-script", "", "Pre-uninstall script")
	postuninstallScriptFlag = flag.String("postuninstall-script", "", "Post-uninstall script")
	installCheckScriptFlag = flag.String("install-check", "", "Install check script")
	uninstallCheckScriptFlag = flag.String("uninstall-check", "", "Uninstall check script")
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

	showVersion := flag.Bool("version", false, "Print version and exit.")
	flag.Parse()

	if *showVersion {
		fmt.Println("gorillaimport 1.0.0 (example)")
		return
	}

	// Attempt to load configuration, if fails due to missing directories, create them.
	conf, err := loadOrCreateConfig()
	if err != nil {
		fmt.Printf("Error loading config: %v\n", err)
		os.Exit(1)
	}

	// Run interactive configuration setup if --config is provided.
	if *configFlag != "" {
		if err := configureGorillaImport(); err != nil {
			fmt.Printf("Failed to save config: %v\n", err)
			os.Exit(1)
		}
		fmt.Println("Configuration saved successfully.")
		return
	}

	// Override config values with flags if provided.
	if repoPath != "" {
		conf.RepoPath = repoPath
	}
	if *archFlag != "" {
		conf.DefaultArch = *archFlag
	}

	// Determine the installer path.
	packagePath := getInstallerPath(*installerFlag)
	if packagePath == "" {
		fmt.Println("Error: No installer provided.")
		os.Exit(1)
	}

	// Collect script paths into a struct.
	scripts := ScriptPaths{
		Preinstall:     *installScriptFlag,
		Postinstall:    *postinstallScriptFlag,
		Preuninstall:   *preuninstallScriptFlag,
		Postuninstall:  *postuninstallScriptFlag,
		InstallCheck:   *installCheckScriptFlag,
		UninstallCheck: *uninstallCheckScriptFlag,
	}

	// Perform the import.
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

	// If import was not successful or canceled, exit.
	if !importSuccess {
		os.Exit(0)
	}

	// Upload to cloud if needed.
	if conf.CloudProvider != "none" {
		if err := uploadToCloud(conf); err != nil {
			fmt.Printf("Error uploading to cloud: %v\n", err)
			os.Exit(1)
		}
	}

	// Always run makecatalogs without confirmation.
	if err := runMakeCatalogs(true); err != nil {
		fmt.Printf("makecatalogs error: %v\n", err)
		os.Exit(1)
	}

	// Sync icons and pkgs directories to cloud if needed.
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

// loadOrCreateConfig attempts to load the config, and if it fails due to missing directories,
// it will try to create them.
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

			// Try loading config again
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

func configureGorillaImport() error {
	conf := config.GetDefaultConfig()

	usr, err := user.Current()
	if err != nil {
		return fmt.Errorf("failed to get current user: %v", err)
	}

	// Construct the default repo path using the current user's home directory.
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

// getInstallerPath tries the --installer flag first, then a positional argument, then interactive.
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

// gorillaImport handles the main logic of ingesting an installer into the Gorilla repo.
func gorillaImport(
	packagePath string,
	conf *config.Configuration,
	scripts ScriptPaths,
	uninstallerPath string,
	filePaths []string,
) (bool, error) {

	if _, err := os.Stat(packagePath); os.IsNotExist(err) {
		return false, fmt.Errorf("package '%s' does not exist", packagePath)
	}
	fmt.Printf("Processing package: %s\n", packagePath)

	// Extract basic metadata
	metadata, err := extractInstallerMetadata(packagePath, conf)
	if err != nil {
		return false, fmt.Errorf("metadata extraction failed: %v", err)
	}
	if metadata.ID == "" {
		metadata.ID = parsePackageName(filepath.Base(packagePath))
	}

	// Prepare scripts
	preinstallScriptContent, err := loadScriptContent(scripts.Preinstall)
	if err != nil {
		return false, fmt.Errorf("failed to process preinstall script: %v", err)
	}
	postinstallScriptContent, err := loadScriptContent(scripts.Postinstall)
	if err != nil {
		return false, fmt.Errorf("failed to process postinstall script: %v", err)
	}
	preuninstallScriptContent, err := loadScriptContent(scripts.Preuninstall)
	if err != nil {
		return false, fmt.Errorf("failed to process preuninstall script: %v", err)
	}
	postuninstallScriptContent, err := loadScriptContent(scripts.Postuninstall)
	if err != nil {
		return false, fmt.Errorf("failed to process postuninstall script: %v", err)
	}
	installCheckScriptContent, err := loadScriptContent(scripts.InstallCheck)
	if err != nil {
		return false, fmt.Errorf("failed to process install-check script: %v", err)
	}
	uninstallCheckScriptContent, err := loadScriptContent(scripts.UninstallCheck)
	if err != nil {
		return false, fmt.Errorf("failed to process uninstall-check script: %v", err)
	}

	// Process uninstaller if provided
	uninstaller, err := processUninstaller(uninstallerPath, filepath.Join(conf.RepoPath, "pkgs"), "apps")
	if err != nil {
		return false, fmt.Errorf("uninstaller processing failed: %v", err)
	}

	// We'll compute a SHA256 for the main installer
	fileHash, err := utils.FileSHA256(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to calculate file hash: %v", err)
	}

	// Also compute the file size (in KB) for optional alignment with makepkginfo
	stat, err := os.Stat(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to stat installer: %v", err)
	}
	fileSizeKB := stat.Size() / 1024

	installerItemPath, err := promptInstallerItemPath()
	if err != nil {
		return false, fmt.Errorf("error processing installer item path: %v", err)
	}

	installerFolderPath := filepath.Join(conf.RepoPath, "pkgs", installerItemPath)
	pkginfoFolderPath := filepath.Join(conf.RepoPath, "pkgsinfo", installerItemPath)

	if err := os.MkdirAll(installerFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create installer directory: %v", err)
	}
	if err := os.MkdirAll(pkginfoFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create pkginfo directory: %v", err)
	}

	nameForFilename := strings.ReplaceAll(metadata.ID, " ", "")
	versionForFilename := strings.ReplaceAll(metadata.Version, " ", "")

	for _, arch := range metadata.SupportedArch {
		archTag := ""
		if arch == "x64" {
			archTag = "-x64-"
		} else if arch == "arm64" {
			archTag = "-arm64-"
		}

		installerFilename := nameForFilename + archTag + versionForFilename + filepath.Ext(packagePath)
		installerDest := filepath.Join(installerFolderPath, installerFilename)
		if _, err := copyFile(packagePath, installerDest); err != nil {
			return false, fmt.Errorf("failed to copy installer to repo: %v", err)
		}

		pkgsInfo := PkgsInfo{
			Name:                 metadata.ID,
			DisplayName:          metadata.Title, // fallback
			Version:              metadata.Version,
			Description:          metadata.Description,
			Category:             metadata.Category,
			Developer:            metadata.Developer,
			Catalogs:             []string{conf.DefaultCatalog},
			SupportedArch:        []string{arch},
			Installer:            &Installer{Location: filepath.Join(installerItemPath, installerFilename), Hash: fileHash, Type: metadata.InstallerType},
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

			// Fields added to align with makepkginfo
			InstallerItemHash:     fileHash,
			InstallerItemSize:     fileSizeKB,
			InstallerItemLocation: installerFilename,
		}

		// Build an InstallItem array referencing the main installer plus user-supplied -f files
		autoInstalls := []InstallItem{
			{
				Type:        SingleQuotedString("file"),
				Path:        SingleQuotedString(packagePath),
				MD5Checksum: SingleQuotedString(""), // or keep empty; or call utils.FileMD5 if you prefer
				Version:     SingleQuotedString(pkgsInfo.Version),
			},
		}
		userInstalls := buildInstallsArray(filePaths)
		pkgsInfo.Installs = append(autoInstalls, userInstalls...)

		if strings.TrimSpace(pkgsInfo.DisplayName) == "" {
			pkgsInfo.DisplayName = pkgsInfo.Name
		}

		existingPkg, exists, err := findMatchingItemInAllCatalog(conf.RepoPath, pkgsInfo.ProductCode, pkgsInfo.UpgradeCode, "")
		if err != nil {
			return false, fmt.Errorf("error checking existing packages: %v", err)
		}

		if exists && existingPkg != nil {
			fmt.Println("This item is similar to an existing item in the repo:")
			fmt.Printf("    Name: %s\n    Version: %s\n    Description: %s\n", existingPkg.Name, existingPkg.Version, existingPkg.Description)
			answer := getInput("Use existing item as a template? [y/N]: ", "N")
			if strings.ToLower(answer) == "y" {
				pkgsInfo.Name = existingPkg.Name
				pkgsInfo.DisplayName = existingPkg.DisplayName
				pkgsInfo.Category = existingPkg.Category
				pkgsInfo.Developer = existingPkg.Developer
				pkgsInfo.SupportedArch = existingPkg.SupportedArch
				pkgsInfo.Catalogs = existingPkg.Catalogs
			}
		}

		fmt.Println("\nPkginfo details:")
		fmt.Printf("    Name: %s\n", pkgsInfo.Name)
		fmt.Printf("    Display Name: %s\n", pkgsInfo.DisplayName)
		fmt.Printf("    Version: %s\n", pkgsInfo.Version)
		fmt.Printf("    Description: %s\n", pkgsInfo.Description)
		fmt.Printf("    Category: %s\n", pkgsInfo.Category)
		fmt.Printf("    Developer: %s\n", pkgsInfo.Developer)
		fmt.Printf("    Architectures: %s\n", strings.Join(pkgsInfo.SupportedArch, ", "))
		fmt.Printf("    Catalogs: %s\n", strings.Join(pkgsInfo.Catalogs, ", "))
		fmt.Println()

		confirm := getInput("Import this item? (y/n): ", "n")
		if strings.ToLower(confirm) != "y" {
			fmt.Println("Import canceled.")
			return false, nil
		}

		err = writePkgInfoFile(pkginfoFolderPath, pkgsInfo, nameForFilename, versionForFilename, archTag)
		if err != nil {
			return false, fmt.Errorf("failed to generate pkginfo: %v", err)
		}
	}

	return true, nil
}

// Metadata holds the extracted metadata from installer packages.
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

	// new for clarity
	InstallerType string
}

// extractInstallerMetadata extracts metadata from the installer package.
func extractInstallerMetadata(packagePath string, conf *config.Configuration) (Metadata, error) {
	ext := strings.ToLower(filepath.Ext(packagePath))
	var metadata Metadata

	switch ext {
	case ".nupkg":
		name, ver, dev, desc := extract.NupkgMetadata(packagePath)
		metadata.Title = name
		metadata.ID = name
		metadata.Version = ver
		metadata.Developer = dev
		metadata.Description = desc
		metadata.InstallerType = "nupkg"

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
		metadata.Title = parsePackageName(filepath.Base(packagePath))
		metadata.ID = metadata.Title
		metadata.Version = versionString
		if err != nil {
			metadata.Version = ""
		}
		metadata.Developer = ""
		metadata.Description = ""
		metadata.InstallerType = "exe"

	default:
		// allow .bat, .ps1, etc. to have no metadata
		metadata.InstallerType = "unknown"
		metadata.Title = parsePackageName(filepath.Base(packagePath))
		metadata.ID = metadata.Title
		metadata.Version = "1.0.0"
	}

	// Prompt for user overrides
	metadata = promptForAllMetadata(packagePath, metadata, conf)

	// Ensure the architecture array is set:
	if metadata.Architecture == "" {
		metadata.Architecture = conf.DefaultArch
	}
	metadata.SupportedArch = []string{metadata.Architecture}

	return metadata, nil
}

func promptForAllMetadata(packagePath string, m Metadata, conf *config.Configuration) Metadata {
	// Determine defaults
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

// loadScriptContent reads the script file's contents if provided, or returns empty
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

// processUninstaller copies the uninstaller to the repo if provided.
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

// findMatchingItemInAllCatalog looks for an existing item in All.yaml
func findMatchingItemInAllCatalog(repoPath, productCode, upgradeCode, currentFileHash string) (*PkgsInfo, bool, error) {
	allCatalogPath := filepath.Join(repoPath, "catalogs", "All.yaml")

	// Try reading All.yaml; if missing, run makecatalogs once
	fileContent, err := os.ReadFile(allCatalogPath)
	if err != nil {
		if runErr := runMakeCatalogs(false); runErr != nil {
			return nil, false, fmt.Errorf("failed to run makecatalogs: %v", runErr)
		}
		// Attempt to read All.yaml again
		fileContent, err = os.ReadFile(allCatalogPath)
		if err != nil {
			return nil, false, fmt.Errorf("failed to read All.yaml after makecatalogs: %v", err)
		}
	}

	var allPackages []PkgsInfo
	if err := yaml.Unmarshal(fileContent, &allPackages); err != nil {
		return nil, false, fmt.Errorf("failed to unmarshal All.yaml: %v", err)
	}

	cleanedProductCode := strings.TrimSpace(strings.ToLower(productCode))
	cleanedUpgradeCode := strings.TrimSpace(strings.ToLower(upgradeCode))
	cleanedCurrentHash := strings.TrimSpace(strings.ToLower(currentFileHash))

	// Iterate over all pkgs in All.yaml to find a match
	for _, item := range allPackages {
		itemProductCode := strings.TrimSpace(strings.ToLower(item.ProductCode))
		itemUpgradeCode := strings.TrimSpace(strings.ToLower(item.UpgradeCode))

		// First, check product & upgrade codes
		if itemProductCode == cleanedProductCode && itemUpgradeCode == cleanedUpgradeCode {
			// If we also care about matching the currentFileHash, do so here
			if item.Installer != nil && cleanedCurrentHash != "" {
				itemHash := strings.TrimSpace(strings.ToLower(item.Installer.Hash))
				if itemHash == cleanedCurrentHash {
					// Perfect match: product code, upgrade code, and hash all match
					return &item, true, nil
				}
			}

			// If we get here, codes match but hash does not
			// You can return a partial match, or keep looking. Typically:
			return &item, false, nil
		}
	}

	// No match found at all
	return nil, false, nil
}

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

// buildInstallsArray processes user-supplied -f paths into InstallItem structs.
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

// runMakeCatalogs runs the makecatalogs.exe if present.
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
	fmt.Println("makecatalogs completed successfully.")
	return nil
}

// uploadToCloud is a convenience function for AWS/Azure sync
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

// syncToCloud is used to sync a single directory to S3/Azure
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

// writePkgInfoFile encodes the final pkgsinfo struct and writes it to disk.
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
	if pkgsInfo.InstallerItemHash != "" {
		if openErr := maybeOpenFile(absOutputPath); openErr != nil {
			fmt.Printf("Warning: could not open pkginfo in an editor: %v\n", openErr)
		}
	}
	return nil
}

// maybeOpenFile optionally opens the YAML in VSCode or Notepad if OpenImportedYaml is set.
func maybeOpenFile(filePath string) error {
	// In this snippet, you’d check conf.OpenImportedYaml. If you need it, adjust accordingly.
	codeCmd, err := exec.LookPath("code.cmd")
	if err != nil {
		return exec.Command("notepad.exe", filePath).Start()
	}
	return exec.Command(codeCmd, filePath).Start()
}
