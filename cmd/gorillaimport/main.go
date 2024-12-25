// cmd/gorillaimport/main.go

package main

import (
	"bytes"
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
}

// Installer represents the structure of the installer and uninstaller in pkginfo.
type Installer struct {
	Location  string   `yaml:"location"`
	Hash      string   `yaml:"hash"`
	Type      string   `yaml:"type"`
	Arguments []string `yaml:"arguments,omitempty"`
}

type InstallItem struct {
	Type        string `yaml:"type"` // "file"
	Path        string `yaml:"path"`
	MD5Checksum string `yaml:"md5checksum,omitempty"`
	Version     string `yaml:"version,omitempty"`
}

// Metadata holds the extracted metadata from installer packages.
type Metadata struct {
	Title         string `xml:"title"`
	ID            string `xml:"id"`
	Version       string `xml:"version"`
	Developer     string `xml:"manufacturer"`
	Category      string `xml:"category"`
	Description   string `xml:"description"`
	Tags          string `xml:"tags,omitempty"`
	Readme        string `xml:"readme,omitempty"`
	ProductCode   string
	UpgradeCode   string
	Architecture  string
	SupportedArch []string
}

// ScriptPaths holds paths to various scripts.
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

func main() {
	var (
		archFlag              string
		repoPath              string
		uninstallerFlag       string
		installCheckScript    string
		uninstallCheckScript  string
		preinstallScript      string
		postinstallScript     string
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

	flag.StringVar(&archFlag, "arch", "x64", "Architecture e.g. x64, arm64")
	flag.StringVar(&repoPath, "repo_path", "", "Gorilla repo path")
	flag.StringVar(&uninstallerFlag, "uninstaller", "", "Uninstaller .exe/.msi (if any)")
	flag.StringVar(&installCheckScript, "installcheckscript", "", "Install check script path")
	flag.StringVar(&uninstallCheckScript, "uninstallcheckscript", "", "Uninstall check script path")
	flag.StringVar(&preinstallScript, "preinstallscript", "", "Preinstall script path")
	flag.StringVar(&postinstallScript, "postinstallscript", "", "Postinstall script path")

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
	if *configFlag {
		if err := configureGorillaImport(); err != nil {
			fmt.Printf("Failed to save config: %v\n", err)
			os.Exit(1)
		}
		fmt.Println("Configuration saved successfully.")
		return
	}

	// Override config values with flags if provided.
	if *repoPath != "" {
		conf.RepoPath = *repoPath
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

	// If import was not successful or canceled, exit without further actions.
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

// Instead of config.GetConfigPath(), define your config path here:
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

// auto-detect basic installer metadata
func autoDetectInstaller(path string) (iType, name, version, dev, desc string) {
	ext := strings.ToLower(filepath.Ext(path))

	switch ext {
	case ".msi":
		iType = "msi"
		n, v, d, s := extract.MsiMetadata(path)
		if n == "" {
			n = parseNameFromFile(path)
		}
		return iType, n, v, d, s

	case ".exe":
		iType = "exe"
		n, v, d, s := extract.ExeMetadata(path)
		if n == "" {
			n = parseNameFromFile(path)
		}
		return iType, n, v, d, s

	case ".nupkg":
		iType = "nupkg"
		n, v, d, s := extract.NupkgMetadata(path)
		if n == "" {
			n = parseNameFromFile(path)
		}
		return iType, n, v, d, s

	default:
		return "unknown", parseNameFromFile(path), "1.0", "", ""
	}
}

// parseNameFromFile is a fallback
func parseNameFromFile(path string) string {
	base := filepath.Base(path)
	return strings.TrimSuffix(base, filepath.Ext(base))
}

// advanced auto detection for installs from the installers
func autoBuildInstalls(installerPath, iType, version string) []InstallItem {
	switch iType {
	case "msi":
		// parse MSI File table or fallback
		// for now, just do 1 item referencing the MSI itself:
		sum, _ := utils.FileSHA256(installerPath)
		return []InstallItem{{
			Type:        "file",
			Path:        installerPath,
			MD5Checksum: "", // optional
			Version:     version,
		}}
	case "exe":
		// we only know about the EXE
		sum, _ := utils.FileSHA256(installerPath)
		return []InstallItem{{
			Type:    "file",
			Path:    installerPath,
			Version: version,
			// if you want MD5:
		}}
	case "nupkg":
		// parse .nupkg contents or fallback to 1 item
		sum, _ := utils.FileSHA256(installerPath)
		return []InstallItem{{
			Type: "file",
			Path: installerPath,
		}}
	}
	return nil
}

// merging -f
func buildInstallsArray(paths []string) []InstallItem {
	var arr []InstallItem
	for _, p := range paths {
		abs, _ := filepath.Abs(p)
		fi, err := os.Stat(abs)
		if err != nil || fi.IsDir() {
			fmt.Fprintf(os.Stderr, "Skipping -f path: '%s'\n", p)
			continue
		}
		md5v, _ := fileMd5(abs)
		var ver string
		if strings.EqualFold(filepath.Ext(abs), ".exe") && runtime.GOOS == "windows" {
			// parse version resource
			ver = "1.2.3.4" // placeholder
		}
		arr = append(arr, InstallItem{
			Type:        "file",
			Path:        abs,
			MD5Checksum: md5v,
			Version:     ver,
		})
	}
	return arr
}

func sanitize(s string) string {
	// remove spaces, special chars, etc.
	return strings.ReplaceAll(s, " ", "_")
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

func readFileOrEmpty(path string) (string, error) {
	if path == "" {
		return "", nil
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return string(b), nil
}

// extractInstallerMetadata extracts metadata from the installer package.
func extractInstallerMetadata(packagePath string, conf *config.Configuration) (Metadata, error) {
	ext := strings.ToLower(filepath.Ext(packagePath))
	var metadata Metadata

	switch ext {
	case ".nupkg":
		// Calls the pkg/extract function directly:
		name, ver, dev, desc := extract.NupkgMetadata(packagePath)
		metadata.Title = name
		metadata.ID = name
		metadata.Version = ver
		metadata.Developer = dev
		metadata.Description = desc

	case ".msi":
		name, ver, dev, desc := extract.MsiMetadata(packagePath)
		metadata.Title = name
		metadata.ID = name
		metadata.Version = ver
		metadata.Developer = dev
		metadata.Description = desc

	case ".exe":
		name, ver, dev, desc := extract.ExeMetadata(packagePath)
		metadata.Title = name
		metadata.ID = name
		metadata.Version = ver
		metadata.Developer = dev
		metadata.Description = desc

	default:
		// allow .bat, .ps1, etc. to have no metadata
		if ext == ".bat" || ext == ".ps1" {
			metadata = Metadata{} // basically empty
		} else {
			return Metadata{}, fmt.Errorf("unsupported installer type: %s", ext)
		}
	}

	// Prompt for user overrides:
	metadata = promptForAllMetadata(packagePath, metadata, conf)

	// Ensure the architecture array is set:
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

	// Developer, Description, and Category can remain empty if not provided
	defaultDeveloper := m.Developer
	defaultDescription := m.Description
	defaultCategory := m.Category

	fmt.Printf("Identifier [%s]: ", defaultID)
	var input string
	fmt.Scanln(&input)
	input = strings.TrimSpace(input)
	if input == "" {
		m.ID = defaultID
	} else {
		m.ID = input
	}

	// We do NOT prompt for Display Name. We'll set it equal to Name later.

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

	// Prompt for architecture
	defaultArch := conf.DefaultArch
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

func copyFile(src, dst string) (int64, error) {
	srcFile, err := os.Open(src)
	if err != nil {
		return 0, err
	}
	defer srcFile.Close()

	dstFile, err := os.Create(dst)
	if err != nil {
		return 0, err
	}
	defer dstFile.Close()

	nBytes, err := io.Copy(dstFile, srcFile)
	if err != nil {
		return 0, err
	}

	return nBytes, dstFile.Sync()
}

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

func findMatchingItemInAllCatalog(repoPath, productCode, upgradeCode, currentFileHash string) (*PkgsInfo, bool, error) {
	allCatalogPath := filepath.Join(repoPath, "catalogs", "All.yaml")
	fileContent, err := os.ReadFile(allCatalogPath)
	if err != nil {
		// Try running makecatalogs once if All.yaml is missing
		runMakeCatalogs(false)
		fileContent, err = os.ReadFile(allCatalogPath)
		if err != nil {
			return nil, false, fmt.Errorf("failed to read All.yaml even after makecatalogs: %v", err)
		}
	}

	var allPackages []PkgsInfo
	if err := yaml.Unmarshal(fileContent, &allPackages); err != nil {
		return nil, false, fmt.Errorf("failed to unmarshal All.yaml: %v", err)
	}

	cleanedProductCode := strings.TrimSpace(strings.ToLower(productCode))
	cleanedUpgradeCode := strings.TrimSpace(strings.ToLower(upgradeCode))

	for _, item := range allPackages {
		if strings.TrimSpace(strings.ToLower(item.ProductCode)) == cleanedProductCode &&
			strings.TrimSpace(strings.ToLower(item.UpgradeCode)) == cleanedUpgradeCode {
			return &item, item.Installer != nil && item.Installer.Hash == currentFileHash, nil
		}
	}
	return nil, false, nil
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

func buildInstallsArray(paths []string) []InstallItem {
	var arr []InstallItem
	for _, p := range paths {
		abs, _ := filepath.Abs(p)
		st, err := os.Stat(abs)
		if err != nil || st.IsDir() {
			fmt.Fprintf(os.Stderr, "Skipping -f path: '%s' (not found or directory)\n", p)
			continue
		}
		md5val, _ := fileMd5(abs)

		// If it's an EXE on Windows, optionally parse version:
		var ver string
		if runtime.GOOS == "windows" && strings.EqualFold(filepath.Ext(abs), ".exe") {
			ver = getExeVersion(abs)
		}

		arr = append(arr, InstallItem{
			Type:        "file",
			Path:        abs,
			MD5Checksum: md5val,
			Version:     ver,
		})
	}
	return arr
}

// For EXE version resource.
// You can reuse your 'extract.ExeMetadata' if you prefer:
func getExeVersion(exePath string) string {
	// Since ExeMetadata returns (name, version, dev, desc),
	// just pick off 'version':
	_, ver, _, _ := extract.ExeMetadata(exePath)

	return ver // empty string if no version
}

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

	// Extract metadata
	metadata, err := extractInstallerMetadata(packagePath, conf)
	if err != nil {
		return false, fmt.Errorf("metadata extraction failed: %v", err)
	}

	if metadata.ID == "" {
		metadata.ID = parsePackageName(filepath.Base(packagePath))
	}

	// Read and process script contents
	processScript := func(scriptPath string) (string, error) {
		if scriptPath == "" {
			return "", nil
		}
		content, err := readScriptContent(scriptPath)
		if err != nil {
			return "", err
		}
		ext := strings.ToLower(filepath.Ext(scriptPath))
		if ext == ".bat" {
			wrappedContent := generateWrapperScript(content, "bat")
			wrappedContent = strings.TrimSpace(wrappedContent)
			return wrappedContent, nil
		}
		// For .ps1 or other scripts, return as-is
		return content, nil
	}

	preinstallScriptContent, err := processScript(scripts.Preinstall)
	if err != nil {
		return false, fmt.Errorf("failed to process preinstall script: %v", err)
	}

	postinstallScriptContent, err := processScript(scripts.Postinstall)
	if err != nil {
		return false, fmt.Errorf("failed to process postinstall script: %v", err)
	}

	preuninstallScriptContent, err := processScript(scripts.Preuninstall)
	if err != nil {
		return false, fmt.Errorf("failed to process preuninstall script: %v", err)
	}

	postuninstallScriptContent, err := processScript(scripts.Postuninstall)
	if err != nil {
		return false, fmt.Errorf("failed to process postuninstall script: %v", err)
	}

	installCheckScriptContent, err := processScript(scripts.InstallCheck)
	if err != nil {
		return false, fmt.Errorf("failed to process install check script: %v", err)
	}

	uninstallCheckScriptContent, err := processScript(scripts.UninstallCheck)
	if err != nil {
		return false, fmt.Errorf("failed to process uninstall check script: %v", err)
	}

	// Process uninstaller
	uninstaller, err := processUninstaller(uninstallerPath, filepath.Join(conf.RepoPath, "pkgs"), "apps")
	if err != nil {
		return false, fmt.Errorf("uninstaller processing failed: %v", err)
	}

	installerType := strings.TrimPrefix(strings.ToLower(filepath.Ext(packagePath)), ".")
	fileHash, err := utils.FileSHA256(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to calculate file hash: %v", err)
	}

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

	// Sanitize name and version for filenames:
	nameForFilename := strings.ReplaceAll(metadata.ID, " ", "")
	versionForFilename := strings.ReplaceAll(metadata.Version, " ", "")

	for _, arch := range metadata.SupportedArch {
		archTag := ""
		if arch == "x64" {
			archTag = "-x64-"
		} else if arch == "arm64" {
			archTag = "-arm64-"
		}

		// Copy the installer file to the repo's /pkgs folder with sanitized filename
		installerFilename := nameForFilename + archTag + versionForFilename + filepath.Ext(packagePath)
		installerDest := filepath.Join(installerFolderPath, installerFilename)
		if _, err := copyFile(packagePath, installerDest); err != nil {
			// Handle error
		}

		pkgsInfo := PkgsInfo{
			Name:                 metadata.ID,
			DisplayName:          "", // Will set after this if needed
			Version:              metadata.Version,
			Description:          metadata.Description,
			Category:             metadata.Category,
			Developer:            metadata.Developer,
			Catalogs:             []string{conf.DefaultCatalog},
			SupportedArch:        []string{arch},
			Installer:            &Installer{Location: filepath.Join(installerItemPath, installerFilename), Hash: fileHash, Type: installerType},
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

		// Reference the main installer itself in the 'installs:' array:
		mainInstallerMD5, _ := fileMd5(packagePath)
		autoInstalls := []InstallItem{{
			Type:        "file",
			Path:        packagePath,
			MD5Checksum: mainInstallerMD5,
			Version:     pkgsInfo.Version,
		}}

		// Also gather user-supplied -f items:
		userInstalls := buildInstallsArray(filePaths)

		// Merge them:
		pkgsInfo.Installs = append(autoInstalls, userInstalls...)

		if strings.TrimSpace(metadata.Title) != "" {
			pkgsInfo.DisplayName = metadata.Title
		} else {
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

		err = createPkgsInfo(
			packagePath,
			pkginfoFolderPath,
			pkgsInfo.Name,
			pkgsInfo.DisplayName,
			pkgsInfo.Version,
			pkgsInfo.Description,
			pkgsInfo.Catalogs,
			pkgsInfo.Category,
			pkgsInfo.Developer,
			pkgsInfo.SupportedArch,
			installerItemPath,
			pkgsInfo.ProductCode,
			pkgsInfo.UpgradeCode,
			fileHash,
			pkgsInfo.UnattendedInstall,
			pkgsInfo.UnattendedUninstall,
			pkgsInfo.PreinstallScript,
			pkgsInfo.PostinstallScript,
			pkgsInfo.PreuninstallScript,
			pkgsInfo.PostuninstallScript,
			pkgsInfo.InstallCheckScript,
			pkgsInfo.UninstallCheckScript,
			pkgsInfo.Uninstaller,
			nameForFilename,
			versionForFilename,
			archTag,
		)
		if err != nil {
			return false, fmt.Errorf("failed to generate pkginfo: %v", err)
		}

		outputPath := filepath.Join(pkginfoFolderPath, nameForFilename+archTag+versionForFilename+".yaml")
		absOutputPath, err := filepath.Abs(outputPath)
		if err != nil {
			return true, fmt.Errorf("failed to get absolute path for pkginfo: %v", err)
		}
		fmt.Printf("Pkginfo created at: %s\n", absOutputPath)

		if conf.OpenImportedYaml {
			openFileInEditor(absOutputPath)
		}
	}

	return true, nil
}

func createPkgsInfo(
	filePath string,
	outputDir string,
	name string,
	displayName string,
	version string,
	description string,
	catalogs []string,
	category string,
	developer string,
	supportedArch []string,
	installerSubPath string,
	productCode string,
	upgradeCode string,
	fileHash string,
	unattendedInstall bool,
	unattendedUninstall bool,
	preinstallScript string,
	postinstallScript string,
	preuninstallScript string,
	postuninstallScript string,
	installCheckScript string,
	uninstallCheckScript string,
	uninstaller *Installer,
	sanitizedName string,
	sanitizedVersion string,
	arch string,
) error {
	installerType := strings.TrimPrefix(filepath.Ext(filePath), ".")
	installerFilename := sanitizedName + arch + sanitizedVersion + filepath.Ext(filePath)
	installerLocation := installerSubPath + "/" + installerFilename

	pkgsInfo := PkgsInfo{
		Name:                 name,
		DisplayName:          displayName,
		Version:              version,
		Developer:            developer,
		Category:             category,
		Description:          description,
		Catalogs:             catalogs,
		SupportedArch:        supportedArch,
		Installer:            &Installer{Location: installerLocation, Hash: fileHash, Type: installerType},
		Uninstaller:          uninstaller,
		UnattendedInstall:    unattendedInstall,
		UnattendedUninstall:  unattendedUninstall,
		ProductCode:          strings.TrimSpace(productCode),
		UpgradeCode:          strings.TrimSpace(upgradeCode),
		PreinstallScript:     preinstallScript,
		PostinstallScript:    postinstallScript,
		PreuninstallScript:   preuninstallScript,
		PostuninstallScript:  postuninstallScript,
		InstallCheckScript:   installCheckScript,
		UninstallCheckScript: uninstallCheckScript,
	}

	// If DisplayName is empty, set it to Name
	if pkgsInfo.DisplayName == "" {
		pkgsInfo.DisplayName = pkgsInfo.Name
	}

	outputPath := filepath.Join(outputDir, sanitizedName+arch+sanitizedVersion+".yaml")

	pkgsInfoContent, err := encodeWithSelectiveBlockScalars(pkgsInfo)
	if err != nil {
		return fmt.Errorf("failed to encode pkginfo: %v", err)
	}

	if err := os.WriteFile(outputPath, pkgsInfoContent, 0644); err != nil {
		return fmt.Errorf("failed to write pkginfo to file: %v", err)
	}

	return nil
}

func encodeWithSelectiveBlockScalars(pkgsInfo PkgsInfo) ([]byte, error) {
	var buf bytes.Buffer
	encoder := yaml.NewEncoder(&buf)
	encoder.SetIndent(2)

	if err := encoder.Encode(&pkgsInfo); err != nil {
		return nil, fmt.Errorf("failed to encode pkginfo: %v", err)
	}
	encoder.Close()

	node := &yaml.Node{}
	decoder := yaml.NewDecoder(&buf)
	if err := decoder.Decode(node); err != nil {
		return nil, fmt.Errorf("failed to decode encoded pkginfo: %v", err)
	}

	if node.Kind == yaml.DocumentNode && len(node.Content) > 0 {
		node = node.Content[0]
	}

	scriptFields := map[string]bool{
		"preinstall_script":     true,
		"postinstall_script":    true,
		"preuninstall_script":   true,
		"postuninstall_script":  true,
		"installcheck_script":   true,
		"uninstallcheck_script": true,
	}

	// Iterate over the mapping nodes to set Style
	for i := 0; i < len(node.Content); i += 2 {
		key := node.Content[i].Value
		if scriptFields[key] && node.Content[i+1].Kind == yaml.ScalarNode && node.Content[i+1].Value != "" {
			// Normalize line breaks to Unix style
			node.Content[i+1].Value = strings.ReplaceAll(node.Content[i+1].Value, "\r\n", "\n")
			node.Content[i+1].Value = strings.ReplaceAll(node.Content[i+1].Value, "\r", "\n")
			node.Content[i+1].Value = strings.TrimRight(node.Content[i+1].Value, "\n")
			node.Content[i+1].Style = yaml.LiteralStyle
		}
	}

	finalBuf := &bytes.Buffer{}
	finalEncoder := yaml.NewEncoder(finalBuf)
	finalEncoder.SetIndent(2)
	if err := finalEncoder.Encode(node); err != nil {
		return nil, fmt.Errorf("failed to re-encode pkginfo with block scalars: %v", err)
	}
	finalEncoder.Close()

	return finalBuf.Bytes(), nil
}

func generateWrapperScript(batchContent, scriptType string) string {
	if scriptType == "bat" {
		// Trim leading/trailing whitespace and replace CRLF with LF
		trimmedContent := strings.TrimSpace(batchContent)
		trimmedContent = strings.ReplaceAll(trimmedContent, "\r\n", "\n")

		// Return the PowerShell wrapper with actual line breaks
		return `$batchScriptContent = @'
` + trimmedContent + `
'@

$batchFile = "$env:TEMP\\temp_script.bat"
Set-Content -Path $batchFile -Value $batchScriptContent -Encoding UTF8
try {
    & cmd.exe /c $batchFile
} finally {
    Remove-Item $batchFile -ErrorAction SilentlyContinue
}`
	}
	// For .ps1 or other script types, return the content as-is
	return batchContent
}

func parsePackageName(filename string) string {
	base := strings.TrimSuffix(filename, filepath.Ext(filename))
	parts := strings.Split(base, "-")
	if len(parts) > 1 {
		return strings.Join(parts[:len(parts)-1], "-")
	}
	return base
}

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

func readScriptContent(scriptPath string) (string, error) {
	if scriptPath == "" {
		return "", nil
	}

	content, err := os.ReadFile(scriptPath)
	if err != nil {
		return "", fmt.Errorf("error reading script file: %v", err)
	}

	return string(content), nil
}

func openFileInEditor(filePath string) {
	codeCmd, err := exec.LookPath("code.cmd")
	if err != nil {
		exec.Command("notepad.exe", filePath).Start()
	} else {
		exec.Command(codeCmd, filePath).Start()
	}
}
