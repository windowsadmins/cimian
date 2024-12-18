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
	Authors     string `xml:"authors"`
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

	packagePath := getInstallerPath(*installerFlag)
	if packagePath == "" {
		fmt.Println("Error: No installer provided.")
		os.Exit(1)
	}

	importSuccess, err := gorillaImport(
		packagePath, conf, *installScriptFlag, *preuninstallScriptFlag,
		*postuninstallScriptFlag, *postinstallScriptFlag, *uninstallerFlag,
		*installCheckScriptFlag, *uninstallCheckScriptFlag,
	)
	if err != nil {
		fmt.Printf("Error: %v\n", err)
		os.Exit(1)
	}

	if importSuccess && conf.CloudProvider != "none" {
		if err := uploadToCloud(conf); err != nil {
			fmt.Printf("Error uploading to cloud: %v\n", err)
			os.Exit(1)
		}
	}

	if confirmAction("Run makecatalogs? (y/n)") {
		if err := runMakeCatalogs(); err != nil {
			fmt.Printf("makecatalogs error: %v\n", err)
			os.Exit(1)
		}
	}

	// Sync icons and pkgs directories to cloud
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
	case ".exe", ".bat", ".ps1":
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
		Authors:     properties["Manufacturer"],
		Description: properties["Comments"], // If available
		ProductCode: properties["ProductCode"],
		UpgradeCode: properties["UpgradeCode"],
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
		return false, fmt.Errorf("metadata extraction failed: %v", err)
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

	// Copy installer to pkgs directory
	installerFilename := filepath.Base(packagePath)
	pkgsFolderPath := filepath.Join(conf.RepoPath, "pkgs", "apps")
	if err := os.MkdirAll(pkgsFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create pkgs directory: %v", err)
	}
	installerDest := filepath.Join(pkgsFolderPath, installerFilename)
	if _, err := copyFile(packagePath, installerDest); err != nil {
		return false, fmt.Errorf("failed to copy installer: %v", err)
	}

	// Create PkgsInfo struct with extracted metadata
	pkgsInfo := PkgsInfo{
		Name:                 metadata.ID,
		DisplayName:          metadata.Title,
		Version:              metadata.Version,
		Developer:            metadata.Authors,
		Description:          metadata.Description,
		Catalogs:             []string{conf.DefaultCatalog},
		SupportedArch:        []string{conf.DefaultArch},
		Installer:            &Installer{Location: filepath.Join("/", "apps", installerFilename), Hash: fileHash, Type: installerType, Arguments: []string{}},
		Uninstaller:          uninstaller,
		PreinstallScript:     preinstallScript,
		PostinstallScript:    postinstallScript,
		PreuninstallScript:   preuninstallScript,
		PostuninstallScript:  postuninstallScript,
		InstallCheckScript:   installCheckScript,
		UninstallCheckScript: uninstallCheckScript,
		UnattendedInstall:    true,
		UnattendedUninstall:  true,
		ProductCode:          metadata.ProductCode,
		UpgradeCode:          metadata.UpgradeCode,
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
	fmt.Printf("    Name: %s\n", pkgsInfo.Name)
	fmt.Printf("    Display Name: %s\n", pkgsInfo.DisplayName)
	fmt.Printf("    Version: %s\n", pkgsInfo.Version)
	fmt.Printf("    Description: %s\n", pkgsInfo.Description)
	fmt.Printf("    Category: %s\n", pkgsInfo.Category)
	fmt.Printf("    Developer: %s\n", pkgsInfo.Developer)
	fmt.Printf("    Supported Architectures: %s\n", strings.Join(pkgsInfo.SupportedArch, ", "))
	fmt.Printf("    Catalogs: %s\n", strings.Join(pkgsInfo.Catalogs, ", "))
	fmt.Println()

	if !confirmAction("Import this item? (y/n)") {
		fmt.Println("Import canceled.")
		return false, nil
	}

	// Generate pkgsinfo
	if err := createPkgsInfo(
		packagePath,
		filepath.Join(conf.RepoPath, "pkgsinfo", "apps"),
		pkgsInfo.Name,
		pkgsInfo.Version,
		pkgsInfo.Catalogs,
		pkgsInfo.Category,
		pkgsInfo.Developer,
		pkgsInfo.SupportedArch,
		"apps",
		pkgsInfo.ProductCode,
		pkgsInfo.UpgradeCode,
		pkgsInfo.Installer.Hash,
		pkgsInfo.UnattendedInstall,
		pkgsInfo.UnattendedUninstall,
		pkgsInfo.PreinstallScript,
		pkgsInfo.PostinstallScript,
		pkgsInfo.PreuninstallScript,
		pkgsInfo.PostuninstallScript,
		pkgsInfo.InstallCheckScript,
		pkgsInfo.UninstallCheckScript,
		pkgsInfo.Uninstaller,
	); err != nil {
		return false, fmt.Errorf("failed to generate pkgsinfo: %v", err)
	}

	fmt.Printf("Pkgsinfo created at: /apps/%s-%s.yaml\n", pkgsInfo.Name, pkgsInfo.Version)
	return true, nil
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
	installerLocation := filepath.Join("/", installerSubPath, fmt.Sprintf("%s-%s%s", name, version, filepath.Ext(filePath)))

	pkgsInfo := PkgsInfo{
		Name:                 name,
		Version:              version,
		Installer:            &Installer{Location: installerLocation, Hash: fileHash, Type: strings.TrimPrefix(filepath.Ext(filePath), ".")},
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
		return fmt.Errorf("failed to encode pkgsinfo: %v", err)
	}

	if err := os.WriteFile(outputPath, pkgsInfoContent, 0644); err != nil {
		return fmt.Errorf("failed to write pkgsinfo to file: %v", err)
	}

	fmt.Printf("Pkgsinfo created at: /%s/%s-%s.yaml\n", installerSubPath, pkgsInfo.Name, pkgsInfo.Version)
	return nil
}

// encodeWithSelectiveBlockScalars encodes the PkgsInfo struct into YAML with selective block scalars for scripts.
func encodeWithSelectiveBlockScalars(pkgsInfo PkgsInfo) ([]byte, error) {
	var buf bytes.Buffer
	encoder := yaml.NewEncoder(&buf)
	encoder.SetIndent(2)

	// Custom encoding to handle script fields with block scalars
	node := &yaml.Node{}
	if err := encoder.Encode(&pkgsInfo); err != nil {
		return nil, fmt.Errorf("failed to encode pkgsinfo: %v", err)
	}
	decoder := yaml.NewDecoder(&buf)
	if err := decoder.Decode(node); err != nil {
		return nil, fmt.Errorf("failed to decode encoded pkgsinfo: %v", err)
	}

	// Handle script fields
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
		return nil, fmt.Errorf("failed to re-encode pkgsinfo with block scalars: %v", err)
	}

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
`, strings.TrimLeft(batchContent, " "))
	} else if scriptType == "ps1" {
		return batchContent
	}
	return ""
}

// promptForMetadata interactively prompts the user for metadata fields.
func promptForMetadata(packagePath string) (Metadata, error) {
	var metadata Metadata

	defaultName := strings.TrimSuffix(filepath.Base(packagePath), filepath.Ext(packagePath))

	promptSurvey(&metadata.Title, "Enter the display name", defaultName)
	promptSurvey(&metadata.ID, "Enter the package name (unique identifier)", defaultName)
	promptSurvey(&metadata.Version, "Enter the version", "1.0.0")
	promptSurvey(&metadata.Authors, "Enter the developer/author", "")
	promptSurvey(&metadata.Description, "Enter the description", "")

	return metadata, nil
}

// promptForMetadataWithDefaults prompts the user for metadata fields, using existing values as defaults.
func promptForMetadataWithDefaults(packagePath string, existing Metadata) (Metadata, error) {
	var metadata Metadata = existing

	defaultName := strings.TrimSuffix(filepath.Base(packagePath), filepath.Ext(packagePath))

	promptSurvey(&metadata.Title, "Enter the display name", defaultName)
	promptSurvey(&metadata.ID, "Enter the package name (unique identifier)", metadata.ID)
	promptSurvey(&metadata.Version, "Enter the version", metadata.Version)
	promptSurvey(&metadata.Authors, "Enter the developer/author", metadata.Authors)
	promptSurvey(&metadata.Description, "Enter the description", metadata.Description)

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
