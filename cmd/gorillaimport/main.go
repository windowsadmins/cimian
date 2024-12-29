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
	MD5Checksum SingleQuotedString `yaml:"md5checksum,omitempty"`
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

// gorillaImport ingests an installer, writes pkgsinfo, etc.
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

	metadata, err := extractInstallerMetadata(packagePath, conf)
	if err != nil {
		return false, fmt.Errorf("metadata extraction failed: %v", err)
	}
	if metadata.ID == "" {
		metadata.ID = parsePackageName(filepath.Base(packagePath))
	}

	// Read script contents
	preinstallScriptContent, _ := loadScriptContent(scripts.Preinstall)
	postinstallScriptContent, _ := loadScriptContent(scripts.Postinstall)
	preuninstallScriptContent, _ := loadScriptContent(scripts.Preuninstall)
	postuninstallScriptContent, _ := loadScriptContent(scripts.Postuninstall)
	installCheckScriptContent, _ := loadScriptContent(scripts.InstallCheck)
	uninstallCheckScriptContent, _ := loadScriptContent(scripts.UninstallCheck)

	uninstaller, err := processUninstaller(uninstallerPath, filepath.Join(conf.RepoPath, "pkgs"), "apps")
	if err != nil {
		return false, fmt.Errorf("uninstaller processing failed: %v", err)
	}

	// Hash + size
	fileHash, err := utils.FileSHA256(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to calculate file hash: %v", err)
	}
	stat, err := os.Stat(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to stat installer: %v", err)
	}
	fileSizeKB := stat.Size() / 1024

	// Prompt where to place in the repo (subfolders)
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

	// Build filenames
	nameForFilename := strings.ReplaceAll(metadata.ID, " ", "")
	versionForFilename := strings.ReplaceAll(metadata.Version, " ", "")

	for _, arch := range metadata.SupportedArch {
		var archTag string
		if arch == "x64" {
			archTag = "-x64-"
		} else if arch == "arm64" {
			archTag = "-arm64-"
		}

		installerFilename := nameForFilename + archTag + versionForFilename + filepath.Ext(packagePath)
		installerDest := filepath.Join(installerFolderPath, installerFilename)
		if _, err := copyFile(packagePath, installerDest); err != nil {
			return false, fmt.Errorf("failed to copy installer: %v", err)
		}

		pkgsInfo := PkgsInfo{
			Name:          metadata.ID,
			DisplayName:   metadata.Title,
			Version:       metadata.Version,
			Description:   metadata.Description,
			Category:      metadata.Category,
			Developer:     metadata.Developer,
			Catalogs:      []string{conf.DefaultCatalog},
			SupportedArch: []string{arch},
			Installer: &Installer{
				Location: filepath.Join(installerItemPath, installerFilename),
				Hash:     fileHash,
				Type:     metadata.InstallerType,
				Size:     fileSizeKB,
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

		// reference the main installer in `installs:`
		md5sum, _ := utils.FileMD5(packagePath)
		autoInstalls := []InstallItem{
			{
				Type:        SingleQuotedString("file"),
				Path:        SingleQuotedString(packagePath),
				MD5Checksum: SingleQuotedString(md5sum),
				Version:     SingleQuotedString(pkgsInfo.Version),
			},
		}

		for _, userPath := range installsArray {
			abs, _ := filepath.Abs(userPath)
			md5v, _ := utils.FileMD5(abs)
			ver := ""
			if runtime.GOOS == "windows" && strings.EqualFold(filepath.Ext(abs), ".exe") {
				version, _ := extract.ExeMetadata(abs)
				ver = version
			}
			autoInstalls = append(autoInstalls, InstallItem{
				Type:        SingleQuotedString("file"),
				Path:        SingleQuotedString(abs),
				MD5Checksum: SingleQuotedString(md5v),
				Version:     SingleQuotedString(ver),
			})
		}

		// If this is an .exe, we override with a fallback.
		if metadata.InstallerType == "exe" {
			fallbackExe := fmt.Sprintf(`C:\Program Files\%s\%s.exe`, pkgsInfo.Name, pkgsInfo.Name)
			fmt.Printf("Overriding .exe path with fallback: %s\n", fallbackExe)
			autoInstalls = append(autoInstalls, InstallItem{
				Type:    SingleQuotedString("file"),
				Path:    SingleQuotedString(fallbackExe),
				Version: SingleQuotedString(pkgsInfo.Version),
			})
		}

		// if we have a .bat preinstall, guess `--INSTALLDIR=`
		// If found, that guess overrides the fallback above.
		if ext := strings.ToLower(filepath.Ext(scripts.Preinstall)); ext == ".bat" || ext == ".cmd" {
			guessedPath := GuessInstallDirFromBat(scripts.Preinstall)
			if guessedPath != "" {
				autoInstalls[0].Path = SingleQuotedString(guessedPath)
			} else {
				fmt.Println("No --INSTALLDIR= or --INSTALLLOCATION= found in .bat script, using fallback or default.")
			}
		}

		// merge with user -f items
		userInstalls := buildInstallsArray(filePaths)
		pkgsInfo.Installs = append(autoInstalls, userInstalls...)

		// If display name is empty, use Name
		if strings.TrimSpace(pkgsInfo.DisplayName) == "" {
			pkgsInfo.DisplayName = pkgsInfo.Name
		}

		// Optionally see if there's an existing pkg with same product/upgrade code
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
}

// extractInstallerMetadata for the main installer
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
		metadata.InstallerType = "unknown"
		metadata.Title = parsePackageName(filepath.Base(packagePath))
		metadata.ID = metadata.Title
		metadata.Version = "1.0.0"
	}

	// Prompt user for overrides
	metadata = promptForAllMetadata(packagePath, metadata, conf)

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

// findMatchingItemInAllCatalog tries to see if there's an existing item with same product/upgrade code
func findMatchingItemInAllCatalog(repoPath, productCode, upgradeCode, currentFileHash string) (*PkgsInfo, bool, error) {
	allCatalogPath := filepath.Join(repoPath, "catalogs", "All.yaml")
	fileContent, err := os.ReadFile(allCatalogPath)
	if err != nil {
		if runErr := runMakeCatalogs(false); runErr != nil {
			return nil, false, fmt.Errorf("failed to run makecatalogs: %v", runErr)
		}
		// read again
		fileContent, err = os.ReadFile(allCatalogPath)
		if err != nil {
			return nil, false, fmt.Errorf("failed to read All.yaml after makecatalogs: %v", err)
		}
	}

	var allPackages []PkgsInfo
	if err := yaml.Unmarshal(fileContent, &allPackages); err != nil {
		return nil, false, fmt.Errorf("failed to unmarshal All.yaml: %v", err)
	}

	cleanedPC := strings.TrimSpace(strings.ToLower(productCode))
	cleanedUC := strings.TrimSpace(strings.ToLower(upgradeCode))
	cleanedHash := strings.TrimSpace(strings.ToLower(currentFileHash))

	for _, item := range allPackages {
		pc := strings.TrimSpace(strings.ToLower(item.ProductCode))
		uc := strings.TrimSpace(strings.ToLower(item.UpgradeCode))
		if pc == cleanedPC && uc == cleanedUC {
			if item.Installer != nil && cleanedHash != "" {
				if strings.TrimSpace(strings.ToLower(item.Installer.Hash)) == cleanedHash {
					return &item, true, nil
				}
			}
			return &item, false, nil
		}
	}
	return nil, false, nil
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
	fmt.Println("makecatalogs completed successfully.")
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
