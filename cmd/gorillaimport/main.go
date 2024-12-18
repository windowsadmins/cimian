// cmd/gorillaimport/main.go

package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/json"
	"encoding/xml"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"unsafe"

	"github.com/AlecAivazis/survey/v2"
	"gopkg.in/yaml.v3"

	"github.com/windowsadmins/gorilla/pkg/config"
)

// PkgsInfo represents the structure of the pkginfo YAML file.
type PkgsInfo struct {
	Name                 string     `yaml:"name"`
	DisplayName          string     `yaml:"display_name"`
	Version              string     `yaml:"version"`
	Description          string     `yaml:"description"`
	Catalogs             []string   `yaml:"catalogs"`
	Category             string     `yaml:"category"`
	Developer            string     `yaml:"developer"`
	UnattendedInstall    bool       `yaml:"unattended_install"`
	UnattendedUninstall  bool       `yaml:"unattended_uninstall"`
	Installer            *Installer `yaml:"installer"`
	Uninstaller          *Installer `yaml:"uninstaller,omitempty"`
	SupportedArch        []string   `yaml:"supported_architectures"`
	ProductCode          string     `yaml:"product_code,omitempty"`
	UpgradeCode          string     `yaml:"upgrade_code,omitempty"`
	PreinstallScript     string     `yaml:"preinstall_script,omitempty"`
	PostinstallScript    string     `yaml:"postinstall_script,omitempty"`
	PreuninstallScript   string     `yaml:"preuninstall_script,omitempty"`
	PostuninstallScript  string     `yaml:"postuninstall_script,omitempty"`
	InstallCheckScript   string     `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string     `yaml:"uninstallcheck_script,omitempty"`
}

// Installer represents the structure of the installer and uninstaller in pkginfo.
type Installer struct {
	Location  string   `yaml:"location"`
	Hash      string   `yaml:"hash"`
	Arguments []string `yaml:"arguments,omitempty"`
	Type      string   `yaml:"type"`
}

// Metadata holds the extracted metadata from installer packages.
type Metadata struct {
	Title       string `xml:"title"`
	ID          string `xml:"id"`
	Version     string `xml:"version"`
	Developer   string `xml:"manufacturer"` // Mapped from MSI's Manufacturer property
	Category    string `xml:"category"`
	Description string `xml:"description"`
	Tags        string `xml:"tags,omitempty"`
	Readme      string `xml:"readme,omitempty"`
	ProductCode string // For MSI packages
	UpgradeCode string // For MSI packages
}

func main() {
	// Load configuration.
	conf, err := config.LoadConfig()
	if err != nil {
		fmt.Printf("Error loading config: %v\n", err)
		os.Exit(1)
	}

	// Parse command-line flags.
	configFlag := flag.Bool("config", false, "Run interactive configuration setup.")
	archFlag := flag.String("arch", "", "Specify the architecture (e.g., x86_64, arm64)")
	repoPath := flag.String("repo_path", "", "Path to the Gorilla repo.")
	installerFlag := flag.String("installer", "", "Path to the installer .exe or .msi file.")
	uninstallerFlag := flag.String("uninstaller", "", "Path to the uninstaller .exe or .msi file.")
	installScriptFlag := flag.String("installscript", "", "Path to the install script (.bat or .ps1).")
	preuninstallScriptFlag := flag.String("preuninstallscript", "", "Path to the preuninstall script.")
	postuninstallScriptFlag := flag.String("postuninstallscript", "", "Path to the postuninstall script.")
	postinstallScriptFlag := flag.String("postinstallscript", "", "Path to the post-install script.")
	installCheckScriptFlag := flag.String("installcheckscript", "", "Path to the install check script.")
	uninstallCheckScriptFlag := flag.String("uninstallcheckscript", "", "Path to the uninstall check script.")
	flag.Parse()

	// Run interactive configuration setup if --config is provided.
	if *configFlag {
		configureGorillaImport()
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

	// Perform the import.
	importSuccess, err := gorillaImport(
		packagePath, conf, *installScriptFlag, *preuninstallScriptFlag,
		*postuninstallScriptFlag, *postinstallScriptFlag, *uninstallerFlag,
		*installCheckScriptFlag, *uninstallCheckScriptFlag,
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
	if err := runMakeCatalogs(); err != nil {
		fmt.Printf("makecatalogs error: %v\n", err)
		os.Exit(1)
	}

	// Sync icons and pkgs directories to cloud.
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

// configureGorillaImport runs the interactive configuration setup for Gorilla import.
func configureGorillaImport() {
	conf := config.GetDefaultConfig()

	fmt.Print("Enter Repo Path: ")
	fmt.Scanln(&conf.RepoPath)

	fmt.Print("Enter Cloud Provider (aws/azure/none): ")
	fmt.Scanln(&conf.CloudProvider)

	if conf.CloudProvider != "none" {
		fmt.Print("Enter Cloud Bucket: ")
		fmt.Scanln(&conf.CloudBucket)
	}

	fmt.Print("Enter Default Catalog: ")
	fmt.Scanln(&conf.DefaultCatalog)

	fmt.Print("Enter Default Architecture: ")
	fmt.Scanln(&conf.DefaultArch)

	if err := config.SaveConfig(conf); err != nil {
		fmt.Printf("Failed to save config: %v\n", err)
		os.Exit(1)
	}
}

// extractInstallerMetadata extracts metadata from the installer package based on its type.
func extractInstallerMetadata(packagePath string) (Metadata, error) {
	ext := strings.ToLower(filepath.Ext(packagePath))
	switch ext {
	case ".nupkg":
		return extractNuGetMetadata(packagePath)
	case ".msi":
		metadata, err := extractMSIMetadata(packagePath)
		if err != nil {
			return Metadata{}, err
		}
		// Prompt for metadata after extracting MSI metadata
		metadata, err = promptForMetadataWithDefaults(packagePath, metadata)
		if err != nil {
			return Metadata{}, err
		}
		return metadata, nil
	case ".exe":
		metadata, err := extractExeMetadata(packagePath)
		if err != nil {
			// Fallback to prompting if version extraction fails
			return promptForMetadata(packagePath)
		}
		// Prompt for other metadata fields with extracted version
		metadata, err = promptForMetadataWithDefaults(packagePath, metadata)
		if err != nil {
			return Metadata{}, err
		}
		return metadata, nil
	case ".bat", ".ps1":
		return promptForMetadata(packagePath)
	default:
		return Metadata{}, fmt.Errorf("unsupported installer type: %s", ext)
	}
}

// extractNuGetMetadata extracts metadata from a .nupkg file.
func extractNuGetMetadata(nupkgPath string) (Metadata, error) {
	tempDir, err := os.MkdirTemp("", "nuget-extract-")
	if err != nil {
		return Metadata{}, fmt.Errorf("failed to create temp directory: %v", err)
	}
	defer os.RemoveAll(tempDir)

	cmd := exec.Command("nuget", "install", nupkgPath, "-OutputDirectory", tempDir, "-NoCache")
	if err := cmd.Run(); err != nil {
		return Metadata{}, fmt.Errorf("failed to extract .nupkg: %v", err)
	}

	nuspecFiles, err := filepath.Glob(filepath.Join(tempDir, "*", "*.nuspec"))
	if err != nil || len(nuspecFiles) == 0 {
		return Metadata{}, fmt.Errorf(".nuspec file not found")
	}

	content, err := os.ReadFile(nuspecFiles[0])
	if err != nil {
		return Metadata{}, fmt.Errorf("failed to read .nuspec: %v", err)
	}

	var metadata Metadata
	if err := xml.Unmarshal(content, &metadata); err != nil {
		return Metadata{}, fmt.Errorf("failed to parse .nuspec: %v", err)
	}

	return metadata, nil
}

// extractMSIMetadata extracts metadata from a .msi file using PowerShell.
func extractMSIMetadata(msiFilePath string) (Metadata, error) {
	// Ensure we're on Windows
	if runtime.GOOS != "windows" {
		return Metadata{}, fmt.Errorf("MSI metadata extraction is only supported on Windows")
	}

	// Escape backslashes in the file path
	msiFilePathEscaped := strings.ReplaceAll(msiFilePath, `\`, `\\`)

	// PowerShell script to extract MSI properties
	psScript := fmt.Sprintf(`$WindowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$Database = $WindowsInstaller.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $WindowsInstaller, @("%s", 0))
$View = $Database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $Database, @('SELECT * FROM Property'))
$View.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $View, $null)
$Record = $View.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $View, $null)

$properties = @{}
while ($Record -ne $null) {
    $property = $Record.StringData(1)
    $value = $Record.StringData(2)
    $properties[$property] = $value
    $Record = $View.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $View, $null)
}

$properties | ConvertTo-Json -Compress`, msiFilePathEscaped)

	// Prepare the command to execute the PowerShell script
	cmd := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", psScript)

	// Execute the command and capture the output
	output, err := cmd.Output()
	if err != nil {
		return Metadata{}, fmt.Errorf("failed to execute PowerShell script: %v", err)
	}

	// Parse the JSON output
	var properties map[string]string
	if err := json.Unmarshal(output, &properties); err != nil {
		return Metadata{}, fmt.Errorf("failed to parse JSON output: %v", err)
	}

	// Extract the desired properties
	metadata := Metadata{
		Title:       properties["ProductName"],
		ID:          properties["ProductCode"], // Use ProductCode as ID
		Version:     properties["ProductVersion"],
		Developer:   properties["Manufacturer"], // Mapped from MSI's Manufacturer property
		Description: properties["Comments"],     // If available
		ProductCode: properties["ProductCode"],
		UpgradeCode: properties["UpgradeCode"],
	}

	return metadata, nil
}

// VSFixedFileInfo holds version info from the resource.
type VSFixedFileInfo struct {
	Signature        uint32
	StrucVersion     uint32
	FileVersionMS    uint32
	FileVersionLS    uint32
	ProductVersionMS uint32
	ProductVersionLS uint32
	FileFlagsMask    uint32
	FileFlags        uint32
	FileOS           uint32
	FileType         uint32
	FileSubtype      uint32
	FileDateMS       uint32
	FileDateLS       uint32
}

// Load version.dll and get the procedures
var (
	versionDLL                  = syscall.MustLoadDLL("version.dll")
	procGetFileVersionInfoSizeW = versionDLL.MustFindProc("GetFileVersionInfoSizeW")
	procGetFileVersionInfoW     = versionDLL.MustFindProc("GetFileVersionInfoW")
	procVerQueryValueW          = versionDLL.MustFindProc("VerQueryValueW")
)

// getFileVersionInfoSize calls GetFileVersionInfoSizeW to get the size of version info.
func getFileVersionInfoSize(filename string) (uint32, error) {
	p, err := syscall.UTF16PtrFromString(filename)
	if err != nil {
		return 0, err
	}
	r0, _, e1 := syscall.Syscall(procGetFileVersionInfoSizeW.Addr(), 2,
		uintptr(unsafe.Pointer(p)), 0, 0)
	size := uint32(r0)
	if size == 0 {
		if e1 != 0 {
			return 0, error(e1)
		}
		return 0, fmt.Errorf("GetFileVersionInfoSizeW failed for '%s'", filename)
	}
	return size, nil
}

// getFileVersionInfo calls GetFileVersionInfoW to get the actual version info data block.
func getFileVersionInfo(filename string, size uint32) ([]byte, error) {
	info := make([]byte, size)
	p, err := syscall.UTF16PtrFromString(filename)
	if err != nil {
		return nil, err
	}
	r0, _, e1 := syscall.Syscall6(procGetFileVersionInfoW.Addr(), 4,
		uintptr(unsafe.Pointer(p)),
		0,
		uintptr(size),
		uintptr(unsafe.Pointer(&info[0])),
		0, 0)
	if r0 == 0 {
		if e1 != 0 {
			return nil, error(e1)
		}
		return nil, fmt.Errorf("GetFileVersionInfoW failed for '%s'", filename)
	}
	return info, nil
}

// verQueryValue calls VerQueryValueW to query a value from the version info block.
func verQueryValue(block []byte, subBlock string) (unsafe.Pointer, uint32, error) {
	pSubBlock, err := syscall.UTF16PtrFromString(subBlock)
	if err != nil {
		return nil, 0, err
	}
	var buf unsafe.Pointer
	var size uint32
	r0, _, e1 := syscall.Syscall6(procVerQueryValueW.Addr(), 4,
		uintptr(unsafe.Pointer(&block[0])),
		uintptr(unsafe.Pointer(pSubBlock)),
		uintptr(unsafe.Pointer(&buf)),
		uintptr(unsafe.Pointer(&size)),
		0, 0)
	if r0 == 0 {
		if e1 != 0 {
			return nil, 0, error(e1)
		}
		return nil, 0, fmt.Errorf("VerQueryValueW failed for subBlock '%s'", subBlock)
	}
	return buf, size, nil
}

// extractExeMetadata uses Win32 API calls to retrieve version information from a Windows .exe file.
func extractExeMetadata(exePath string) (Metadata, error) {
	if runtime.GOOS != "windows" {
		return Metadata{}, fmt.Errorf(".exe metadata extraction is only supported on Windows")
	}

	size, err := getFileVersionInfoSize(exePath)
	if err != nil {
		return Metadata{}, err
	}

	verInfo, err := getFileVersionInfo(exePath, size)
	if err != nil {
		return Metadata{}, err
	}

	// Get the fixed file info
	fixedInfoPtr, fixedInfoLen, err := verQueryValue(verInfo, `\`)
	if err != nil {
		return Metadata{}, err
	}
	if fixedInfoLen == 0 {
		return Metadata{}, fmt.Errorf("no VS_FIXEDFILEINFO found")
	}
	fixedInfo := (*VSFixedFileInfo)(fixedInfoPtr)

	major := fixedInfo.FileVersionMS >> 16
	minor := fixedInfo.FileVersionMS & 0xFFFF
	build := fixedInfo.FileVersionLS >> 16
	revision := fixedInfo.FileVersionLS & 0xFFFF
	versionStr := fmt.Sprintf("%d.%d.%d.%d", major, minor, build, revision)

	// Query the translation info
	langPtr, langLen, err := verQueryValue(verInfo, `\VarFileInfo\Translation`)
	if err != nil {
		// If no translation found, just return version
		return Metadata{Version: versionStr}, nil
	}

	if langLen == 0 {
		// No language info found, return version only
		return Metadata{Version: versionStr}, nil
	}

	// LangAndCodePage is two uint16s: Language and CodePage
	type LangAndCodePage struct {
		Language uint16
		CodePage uint16
	}

	langData := (*LangAndCodePage)(langPtr)
	language := fmt.Sprintf("%04x", langData.Language)
	codepage := fmt.Sprintf("%04x", langData.CodePage)

	queryString := func(name string) string {
		subBlock := fmt.Sprintf(`\StringFileInfo\%s%s\%s`, language, codepage, name)
		valPtr, valLen, err := verQueryValue(verInfo, subBlock)
		if err != nil || valLen == 0 {
			return ""
		}
		return syscall.UTF16ToString((*[1 << 20]uint16)(valPtr)[:valLen])
	}

	companyName := queryString("CompanyName")
	productName := queryString("ProductName")
	fileDescription := queryString("FileDescription")

	metadata := Metadata{
		Title:       fileDescription,
		ID:          productName,
		Developer:   companyName,
		Version:     versionStr,
		Description: fileDescription,
		Category:    "", // Not available in .exe metadata, will be prompted later if needed
	}

	return metadata, nil
}

// calculateSHA256 calculates the SHA256 hash of the given file.
func calculateSHA256(packagePath string) (string, error) {
	file, err := os.Open(packagePath)
	if err != nil {
		return "", err
	}
	defer file.Close()

	hash := sha256.New()
	if _, err := io.Copy(hash, file); err != nil {
		return "", err
	}

	return fmt.Sprintf("%x", hash.Sum(nil)), nil
}

// copyFile copies a file from src to dst and returns the number of bytes copied.
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

// getInstallerPath determines the installer path from flags or user input.
func getInstallerPath(installerFlag string) string {
	if installerFlag != "" {
		return installerFlag
	}

	if flag.NArg() > 0 {
		return flag.Arg(0)
	}

	fmt.Print("Enter the path to the installer file: ")
	var path string
	fmt.Scanln(&path)
	return path
}

// processScript reads and processes the install/uninstall scripts.
// It wraps .bat scripts appropriately and returns the script content.
func processScript(scriptPath, wrapperType string) (string, error) {
	if scriptPath == "" {
		return "", nil
	}

	content, err := os.ReadFile(scriptPath)
	if err != nil {
		return "", fmt.Errorf("error reading script file: %v", err)
	}

	scriptContent := strings.ReplaceAll(string(content), "\r\n", "\n")

	if wrapperType == ".bat" {
		return generateWrapperScript(scriptContent, "bat"), nil
	} else if wrapperType == ".ps1" {
		return scriptContent, nil
	}
	return scriptContent, nil
}

// processUninstaller handles the uninstaller file by copying it to the repo and returning its Installer struct.
func processUninstaller(uninstallerPath, pkgsFolderPath, installerSubPath string) (*Installer, error) {
	if uninstallerPath == "" {
		return nil, nil
	}

	if _, err := os.Stat(uninstallerPath); os.IsNotExist(err) {
		return nil, fmt.Errorf("uninstaller '%s' does not exist", uninstallerPath)
	}

	uninstallerHash, err := calculateSHA256(uninstallerPath)
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

// promptInstallerItemPath interactively prompts the user to enter the installer item path.
func promptInstallerItemPath() (string, error) {
	var path string
	prompt := &survey.Input{
		Message: "Enter the Installer Item Path in the repo (default: /apps)",
		Default: "/apps",
	}
	err := survey.AskOne(prompt, &path)
	if err != nil {
		return "", err
	}
	// Ensure the path starts with a "/"
	if !strings.HasPrefix(path, "/") {
		path = "/" + path
	}
	// Remove any trailing slashes
	path = strings.TrimRight(path, "/")
	return path, nil
}

// findMatchingItemInAllCatalog checks if a package with the same ProductCode and UpgradeCode already exists in All.yaml.
func findMatchingItemInAllCatalog(repoPath, productCode, upgradeCode, currentFileHash string) (*PkgsInfo, bool, error) {
	allCatalogPath := filepath.Join(repoPath, "catalogs", "All.yaml")
	fileContent, err := os.ReadFile(allCatalogPath)
	if err != nil {
		return nil, false, fmt.Errorf("failed to read All.yaml: %v", err)
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

// gorillaImport handles the import process of the installer into the repo.
func gorillaImport(
	packagePath string,
	conf *config.Configuration,
	installScriptPath, preuninstallScriptPath, postuninstallScriptPath string,
	postinstallScriptPath, uninstallerPath, installCheckScriptPath, uninstallCheckScriptPath string,
) (bool, error) {
	// Check if the package exists.
	if _, err := os.Stat(packagePath); os.IsNotExist(err) {
		return false, fmt.Errorf("package '%s' does not exist", packagePath)
	}

	fmt.Printf("Processing package: %s\n", packagePath)

	// Extract metadata
	metadata, err := extractInstallerMetadata(packagePath)
	if err != nil {
		// If metadata extraction fails, return an error
		return false, fmt.Errorf("metadata extraction failed: %v", err)
	}

	// Parse package name from filename if not provided
	if metadata.ID == "" {
		metadata.ID = parsePackageName(filepath.Base(packagePath))
	}

	// Process scripts
	preinstallScript, err := processScript(installScriptPath, filepath.Ext(installScriptPath))
	if err != nil {
		return false, fmt.Errorf("preinstall script processing failed: %v", err)
	}
	postinstallScript, err := processScript(postinstallScriptPath, filepath.Ext(postinstallScriptPath))
	if err != nil {
		return false, fmt.Errorf("postinstall script processing failed: %v", err)
	}
	preuninstallScript, err := processScript(preuninstallScriptPath, filepath.Ext(preuninstallScriptPath))
	if err != nil {
		return false, fmt.Errorf("preuninstall script processing failed: %v", err)
	}
	postuninstallScript, err := processScript(postuninstallScriptPath, filepath.Ext(postuninstallScriptPath))
	if err != nil {
		return false, fmt.Errorf("postuninstall script processing failed: %v", err)
	}
	installCheckScript, err := processScript(installCheckScriptPath, filepath.Ext(installCheckScriptPath))
	if err != nil {
		return false, fmt.Errorf("install check script processing failed: %v", err)
	}
	uninstallCheckScript, err := processScript(uninstallCheckScriptPath, filepath.Ext(uninstallCheckScriptPath))
	if err != nil {
		return false, fmt.Errorf("uninstall check script processing failed: %v", err)
	}

	// Process uninstaller
	uninstaller, err := processUninstaller(uninstallerPath, filepath.Join(conf.RepoPath, "pkgs"), "apps")
	if err != nil {
		return false, fmt.Errorf("uninstaller processing failed: %v", err)
	}

	// Determine installer type
	installerType := strings.TrimPrefix(strings.ToLower(filepath.Ext(packagePath)), ".")

	// Calculate installer hash
	fileHash, err := calculateSHA256(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to calculate file hash: %v", err)
	}

	// Prompt for Installer Item Path
	installerItemPath, err := promptInstallerItemPath()
	if err != nil {
		return false, fmt.Errorf("error processing installer item path: %v", err)
	}

	// Define installer and pkginfo folder paths
	installerFolderPath := filepath.Join(conf.RepoPath, "pkgs", installerItemPath)
	pkginfoFolderPath := filepath.Join(conf.RepoPath, "pkgsinfo", installerItemPath)

	// Create directories if they don't exist
	if err := os.MkdirAll(installerFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create installer directory: %v", err)
	}
	if err := os.MkdirAll(pkginfoFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create pkginfo directory: %v", err)
	}

	// Copy installer to installerFolderPath
	installerFilename := filepath.Base(packagePath)
	installerDest := filepath.Join(installerFolderPath, installerFilename)
	if _, err := copyFile(packagePath, installerDest); err != nil {
		return false, fmt.Errorf("failed to copy installer: %v", err)
	}

	// Set Installer.Location relative to repo
	installerLocation := "/" + installerItemPath + "/" + installerFilename

	// Create PkgsInfo struct with extracted metadata
	pkgsInfo := PkgsInfo{
		Name:                 metadata.ID,
		DisplayName:          metadata.Title,
		Version:              metadata.Version,
		Developer:            metadata.Developer,
		Category:             metadata.Category,
		Description:          metadata.Description,
		Catalogs:             []string{conf.DefaultCatalog},
		SupportedArch:        []string{conf.DefaultArch},
		Installer:            &Installer{Location: installerLocation, Hash: fileHash, Type: installerType, Arguments: []string{}},
		Uninstaller:          uninstaller,
		PreinstallScript:     preinstallScript,
		PostinstallScript:    postinstallScript,
		PreuninstallScript:   preuninstallScript,
		PostuninstallScript:  postuninstallScript,
		InstallCheckScript:   installCheckScript,
		UninstallCheckScript: uninstallCheckScript,
		UnattendedInstall:    true,
		UnattendedUninstall:  true,
		ProductCode:          strings.TrimSpace(metadata.ProductCode),
		UpgradeCode:          strings.TrimSpace(metadata.UpgradeCode),
	}

	// Check for existing package in catalog
	existingPkg, exists, err := findMatchingItemInAllCatalog(conf.RepoPath, pkgsInfo.ProductCode, pkgsInfo.UpgradeCode, pkgsInfo.Installer.Hash)
	if err != nil {
		return false, fmt.Errorf("error checking existing packages: %v", err)
	}

	if exists && existingPkg != nil {
		fmt.Println("This item is similar to an existing item in the repo:")
		fmt.Printf("    Name: %s\n    Version: %s\n    Description: %s\n", existingPkg.Name, existingPkg.Version, existingPkg.Description)
		answer := getInputWithDefault("Use existing item as a template? [y/N] ", "N")
		if strings.ToLower(answer) == "y" {
			// Copy relevant fields from existingPkg to pkgsInfo
			pkgsInfo.Name = existingPkg.Name
			pkgsInfo.DisplayName = existingPkg.DisplayName
			pkgsInfo.Category = existingPkg.Category
			pkgsInfo.Developer = existingPkg.Developer
			pkgsInfo.SupportedArch = existingPkg.SupportedArch
			pkgsInfo.Catalogs = existingPkg.Catalogs
		}
	}

	// Prompt user to confirm import
	fmt.Printf("\nPkginfo details:\n")
	fmt.Printf("    Name: %s\n", capitalizeFirst(pkgsInfo.Name))
	fmt.Printf("    Display Name: %s\n", capitalizeFirst(pkgsInfo.DisplayName))
	fmt.Printf("    Version: %s\n", capitalizeFirst(pkgsInfo.Version))
	fmt.Printf("    Description: %s\n", pkgsInfo.Description)
	fmt.Printf("    Category: %s\n", capitalizeFirst(pkgsInfo.Category))
	fmt.Printf("    Developer: %s\n", capitalizeFirst(pkgsInfo.Developer))
	fmt.Printf("    Architectures: %s\n", strings.Join(pkgsInfo.SupportedArch, ", "))
	fmt.Printf("    Catalogs: %s\n", strings.Join(pkgsInfo.Catalogs, ", "))
	fmt.Println()

	if !confirmAction("Import this item?") {
		fmt.Println("Import canceled.")
		return false, nil
	}

	// Generate pkginfo using positional arguments
	if err := createPkgsInfo(
		packagePath,                   // filePath
		pkginfoFolderPath,             // outputDir
		pkgsInfo.Name,                 // name
		pkgsInfo.Version,              // version
		pkgsInfo.Catalogs,             // catalogs
		pkgsInfo.Category,             // category
		pkgsInfo.Developer,            // developer
		pkgsInfo.SupportedArch,        // supportedArch
		installerItemPath,             // installerSubPath
		pkgsInfo.ProductCode,          // productCode
		pkgsInfo.UpgradeCode,          // upgradeCode
		pkgsInfo.Installer.Hash,       // fileHash
		pkgsInfo.UnattendedInstall,    // unattendedInstall
		pkgsInfo.UnattendedUninstall,  // unattendedUninstall
		pkgsInfo.PreinstallScript,     // preinstallScript
		pkgsInfo.PostinstallScript,    // postinstallScript
		pkgsInfo.PreuninstallScript,   // preuninstallScript
		pkgsInfo.PostuninstallScript,  // postuninstallScript
		pkgsInfo.InstallCheckScript,   // installCheckScript
		pkgsInfo.UninstallCheckScript, // uninstallCheckScript
		pkgsInfo.Uninstaller,          // uninstaller
	); err != nil {
		return false, fmt.Errorf("failed to generate pkginfo: %v", err)
	}

	// Get absolute path for pkginfo
	outputPath := filepath.Join(pkginfoFolderPath, fmt.Sprintf("%s-%s.yaml", pkgsInfo.Name, pkgsInfo.Version))
	absOutputPath, err := filepath.Abs(outputPath)
	if err != nil {
		return true, fmt.Errorf("failed to get absolute path for pkginfo: %v", err)
	}
	fmt.Printf("Pkginfo created at: %s\n", absOutputPath)

	return true, nil
}

// createPkgsInfo generates the pkginfo YAML file based on the provided information.
func createPkgsInfo(
	filePath string,
	outputDir string,
	name string,
	version string,
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
) error {
	// Calculate installerType directly here instead of using undefined installerType
	installerType := strings.TrimPrefix(filepath.Ext(filePath), ".")

	installerLocation := "/" + installerSubPath + "/" + fmt.Sprintf("%s-%s%s", name, version, filepath.Ext(filePath))

	pkgsInfo := PkgsInfo{
		Name:                 name,
		Version:              version,
		Installer:            &Installer{Location: installerLocation, Hash: fileHash, Type: installerType},
		Uninstaller:          uninstaller,
		Catalogs:             catalogs,
		Category:             category,
		Developer:            developer,
		SupportedArch:        supportedArch,
		ProductCode:          strings.TrimSpace(productCode),
		UpgradeCode:          strings.TrimSpace(upgradeCode),
		UnattendedInstall:    unattendedInstall,
		UnattendedUninstall:  unattendedUninstall,
		PreinstallScript:     preinstallScript,
		PostinstallScript:    postinstallScript,
		PreuninstallScript:   preuninstallScript,
		PostuninstallScript:  postuninstallScript,
		InstallCheckScript:   installCheckScript,
		UninstallCheckScript: uninstallCheckScript,
	}

	outputPath := filepath.Join(outputDir, fmt.Sprintf("%s-%s.yaml", name, version))
	pkgsInfoContent, err := encodeWithSelectiveBlockScalars(pkgsInfo)
	if err != nil {
		return fmt.Errorf("failed to encode pkginfo: %v", err)
	}

	if err := os.WriteFile(outputPath, pkgsInfoContent, 0644); err != nil {
		return fmt.Errorf("failed to write pkginfo to file: %v", err)
	}

	return nil
}

// capitalizeFirst capitalizes the first letter of the given string.
func capitalizeFirst(s string) string {
	if len(s) == 0 {
		return s
	}
	return strings.ToUpper(string(s[0])) + s[1:]
}

// encodeWithSelectiveBlockScalars encodes the PkgsInfo struct into YAML with selective block scalars for scripts.
func encodeWithSelectiveBlockScalars(pkgsInfo PkgsInfo) ([]byte, error) {
	var buf bytes.Buffer
	encoder := yaml.NewEncoder(&buf)
	encoder.SetIndent(2)

	// Encode the PkgsInfo into YAML
	if err := encoder.Encode(&pkgsInfo); err != nil {
		return nil, fmt.Errorf("failed to encode pkginfo: %v", err)
	}
	encoder.Close()

	// Decode it back to manipulate script fields
	node := &yaml.Node{}
	decoder := yaml.NewDecoder(&buf)
	if err := decoder.Decode(node); err != nil {
		return nil, fmt.Errorf("failed to decode encoded pkginfo: %v", err)
	}

	// Handle script fields with block scalars
	scriptFields := map[string]bool{
		"preinstall_script":     true,
		"postinstall_script":    true,
		"preuninstall_script":   true,
		"postuninstall_script":  true,
		"installcheck_script":   true,
		"uninstallcheck_script": true,
	}

	for i := 0; i < len(node.Content); i += 2 {
		key := node.Content[i].Value
		if scriptFields[key] {
			scriptContent := node.Content[i+1].Value
			if scriptContent != "" {
				node.Content[i+1].Style = yaml.LiteralStyle
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

// generateWrapperScript wraps .bat scripts into a PowerShell executable script.
func generateWrapperScript(batchContent, scriptType string) string {
	if scriptType == "bat" {
		return fmt.Sprintf(`
$batchScriptContent = @'
%s
'@

$batchFile = "$env:TEMP\\temp_script.bat"
Set-Content -Path $batchFile -Value $batchScriptContent -Encoding ASCII
& cmd.exe /c $batchFile
Remove-Item $batchFile
`, strings.TrimSpace(batchContent))
	} else if scriptType == "ps1" {
		return batchContent
	}
	return ""
}

// promptForMetadata interactively prompts the user for metadata fields.
func promptForMetadata(packagePath string) (Metadata, error) {
	var metadata Metadata

	// Start with Package Name
	promptSurvey(&metadata.ID, "Enter the Package Name (unique identifier)", parsePackageName(filepath.Base(packagePath)))

	// Pre-populate Display Name with Package Name
	promptSurvey(&metadata.Title, "Enter the Display Name", metadata.ID)

	promptSurvey(&metadata.Version, "Enter the Version", "1.0.0")
	promptSurvey(&metadata.Developer, "Enter the Developer", "")
	promptSurvey(&metadata.Description, "Enter the Description", "")
	promptSurvey(&metadata.Category, "Enter the Category", "")

	return metadata, nil
}

// promptForMetadataWithDefaults prompts the user for metadata fields, using existing values as defaults.
func promptForMetadataWithDefaults(packagePath string, existing Metadata) (Metadata, error) {
	var metadata Metadata = existing

	// Start with Package Name
	promptSurvey(&metadata.ID, "Enter the Package Name (unique identifier)", parsePackageName(filepath.Base(packagePath)))

	// Pre-populate Display Name with Package Name
	promptSurvey(&metadata.Title, "Enter the Display Name", metadata.ID)

	promptSurvey(&metadata.Version, "Enter the Version", metadata.Version)
	promptSurvey(&metadata.Developer, "Enter the Developer", metadata.Developer)
	promptSurvey(&metadata.Description, "Enter the Description", metadata.Description)
	promptSurvey(&metadata.Category, "Enter the Category", metadata.Category)

	return metadata, nil
}

// promptSurvey uses the survey package to ask the user for input.
func promptSurvey(value *string, prompt string, defaultValue string) {
	survey.AskOne(&survey.Input{
		Message: prompt,
		Default: cleanTextForPrompt(defaultValue),
	}, value)
}

// getInputWithDefault prompts the user and returns the input or default value.
func getInputWithDefault(prompt, defaultValue string) string {
	fmt.Printf("%s [%s]: ", prompt, cleanTextForPrompt(defaultValue))
	var input string
	fmt.Scanln(&input)
	if input == "" {
		return defaultValue
	}
	return input
}

// cleanTextForPrompt trims spaces from the input string.
func cleanTextForPrompt(input string) string {
	return strings.TrimSpace(input)
}

// confirmAction prompts the user for a yes/no confirmation.
func confirmAction(prompt string) bool {
	fmt.Printf("%s (y/n): ", prompt)
	var response string
	fmt.Scanln(&response)
	return strings.ToLower(response) == "y"
}

// uploadToCloud syncs the specified directories to the configured cloud provider.
func uploadToCloud(conf *config.Configuration) error {
	localPkgsPath := filepath.Join(conf.RepoPath, "pkgs")
	localIconsPath := filepath.Join(conf.RepoPath, "icons")

	if conf.CloudProvider == "aws" {
		// Sync pkgs
		pkgsDestination := fmt.Sprintf("s3://%s/pkgs/", conf.CloudBucket)
		cmdPkgs := exec.Command("aws", "s3", "sync", localPkgsPath, pkgsDestination,
			"--exclude", "*.git/*", "--exclude", "**/.DS_Store")

		cmdPkgs.Stdout = os.Stdout
		cmdPkgs.Stderr = os.Stderr
		if err := cmdPkgs.Run(); err != nil {
			return fmt.Errorf("error syncing pkgs to S3: %v", err)
		}

		// Sync icons
		iconsDestination := fmt.Sprintf("s3://%s/icons/", conf.CloudBucket)
		cmdIcons := exec.Command("aws", "s3", "sync", localIconsPath, iconsDestination,
			"--exclude", "*.git/*", "--exclude", "**/.DS_Store")

		cmdIcons.Stdout = os.Stdout
		cmdIcons.Stderr = os.Stderr
		if err := cmdIcons.Run(); err != nil {
			return fmt.Errorf("error syncing icons to S3: %v", err)
		}
	} else if conf.CloudProvider == "azure" {
		// Sync pkgs
		pkgsDestination := fmt.Sprintf("https://%s/pkgs/", conf.CloudBucket)
		cmdPkgs := exec.Command("azcopy", "sync", localPkgsPath, pkgsDestination,
			"--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")

		cmdPkgs.Stdout = os.Stdout
		cmdPkgs.Stderr = os.Stderr
		if err := cmdPkgs.Run(); err != nil {
			return fmt.Errorf("error syncing pkgs to Azure: %v", err)
		}

		// Sync icons
		iconsDestination := fmt.Sprintf("https://%s/icons/", conf.CloudBucket)
		cmdIcons := exec.Command("azcopy", "sync", localIconsPath, iconsDestination,
			"--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")

		cmdIcons.Stdout = os.Stdout
		cmdIcons.Stderr = os.Stderr
		if err := cmdIcons.Run(); err != nil {
			return fmt.Errorf("error syncing icons to Azure: %v", err)
		}
	} else {
		return fmt.Errorf("unsupported cloud provider: %s", conf.CloudProvider)
	}

	return nil
}

// runMakeCatalogs executes the makecatalogs binary to rebuild catalogs.
func runMakeCatalogs() error {
	makeCatalogsBinary := `C:\Program Files\Gorilla\makecatalogs.exe`

	if _, err := os.Stat(makeCatalogsBinary); os.IsNotExist(err) {
		return fmt.Errorf("makecatalogs binary not found at %s", makeCatalogsBinary)
	}

	cmd := exec.Command(makeCatalogsBinary)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	fmt.Printf("Running makecatalogs from: %s\n", makeCatalogsBinary)
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("makecatalogs execution failed: %v", err)
	}

	fmt.Println("makecatalogs completed successfully.")
	return nil
}

// syncToCloud is a helper function to sync specific subdirectories to the cloud.
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
		cmd := exec.Command("aws", "s3", "sync", source, destination,
			"--exclude", "*.git/*", "--exclude", "**/.DS_Store")

		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("error syncing %s to S3: %v", destinationSubPath, err)
		}
	} else if conf.CloudProvider == "azure" {
		cmd := exec.Command("azcopy", "sync", source, destination,
			"--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")

		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("error syncing %s to Azure: %v", destinationSubPath, err)
		}
	}

	fmt.Printf("Successfully synced %s to %s\n", source, destination)
	return nil
}

// parsePackageName extracts the package name from the filename before the version number.
func parsePackageName(filename string) string {
	base := strings.TrimSuffix(filename, filepath.Ext(filename))
	parts := strings.Split(base, "-")
	if len(parts) > 1 {
		return strings.Join(parts[:len(parts)-1], "-")
	}
	return base
}
