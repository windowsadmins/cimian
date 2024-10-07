package main

import (
	"crypto/sha256"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"gopkg.in/yaml.v3"
	"github.com/AlecAivazis/survey/v2"
)

// PkgsInfo structure holds package metadata
type Installer struct {
	Location  string   `yaml:"location"`
	Hash      string   `yaml:"hash"`
	Arguments []string `yaml:"arguments,omitempty"`
	Type      string   `yaml:"type"`
}

type PkgsInfo struct {
	Name                string     `yaml:"name"`
	DisplayName         string     `yaml:"display_name"`
	Version             string     `yaml:"version"`
	Description         string     `yaml:"description"`
	Catalogs            []string   `yaml:"catalogs"`
	Category            string     `yaml:"category"`
	Developer           string     `yaml:"developer"`
	UnattendedInstall   bool       `yaml:"unattended_install"`
	UnattendedUninstall bool       `yaml:"unattended_uninstall"`
	Installer           *Installer `yaml:"installer"`
	Uninstaller         *Installer `yaml:"uninstaller,omitempty"`
	SupportedArch       []string   `yaml:"supported_architectures"`
	ProductCode         string     `yaml:"product_code,omitempty"`
	UpgradeCode         string     `yaml:"upgrade_code,omitempty"`
	PreinstallScript    string     `yaml:"preinstall_script,omitempty"`
	PostinstallScript   string     `yaml:"postinstall_script,omitempty"`
	UninstallScript     string     `yaml:"uninstall_script,omitempty"`
}

// Config structure holds the configuration settings
type Config struct {
	RepoPath       string `yaml:"repo_path"`
	CloudProvider  string `yaml:"cloud_provider"`
	CloudBucket    string `yaml:"cloud_bucket"`
	DefaultCatalog string `yaml:"default_catalog"`
	DefaultArch    string `yaml:"default_arch"`
}

// Default configuration values
var defaultConfig = Config{
	RepoPath:       "./repo",
	CloudBucket:    "",
	DefaultCatalog: "testing",
	DefaultArch:    "x86_64",
}

// checkTools verifies the required tools are installed based on the OS
func checkTools() error {
	switch runtime.GOOS {
	case "windows":
		_, err := exec.LookPath("msiexec")
		if err != nil {
			return fmt.Errorf("msiexec is missing. It is needed to extract MSI metadata on Windows.")
		}
	case "darwin":
		_, err := exec.LookPath("msidump")
		if err != nil {
			return fmt.Errorf("msidump is missing. You can install it using Homebrew.")
		}
	default:
		return fmt.Errorf("Only supported on Windows and macOS.")
	}
	return nil
}

// findMatchingItem checks for existing packages with the same name and version
func findMatchingItem(pkgsInfos []PkgsInfo, name string, version string) *PkgsInfo {
	for _, item := range pkgsInfos {
		if item.Name == name && item.Version == version {
			return &item
		}
	}
	return nil
}

// scanRepo scans the repo path for existing pkgsinfo YAML files
func scanRepo(repoPath string) ([]PkgsInfo, error) {
	var pkgsInfos []PkgsInfo

	err := filepath.Walk(repoPath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if filepath.Ext(path) == ".yaml" {
			fileContent, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			var pkgsInfo PkgsInfo
			if err := yaml.Unmarshal(fileContent, &pkgsInfo); err != nil {
				return err
			}
			pkgsInfos = append(pkgsInfos, pkgsInfo)
		}
		return nil
	})

	return pkgsInfos, err
}

// getConfigPath returns the appropriate configuration file path based on the OS
func getConfigPath() string {
	if runtime.GOOS == "darwin" {
		return filepath.Join(os.Getenv("HOME"), "Library/Preferences/com.github.gorilla.import.yaml")
	} else if runtime.GOOS == "windows" {
		return filepath.Join(os.Getenv("APPDATA"), "Gorilla", "import.yaml")
	}
	return "config.yaml" // Default path for other OSes
}

// loadConfig loads the configuration from a YAML file
func loadConfig(configPath string) (Config, error) {
	var config Config
	file, err := os.Open(configPath)
	if err != nil {
		return config, err
	}
	defer file.Close()

	yamlDecoder := yaml.NewDecoder(file)
	if err := yamlDecoder.Decode(&config); err != nil {
		return config, err
	}

	return config, nil
}

// saveConfig saves the configuration to YAML file when running `--config`
func saveConfig(configPath string, config Config) error {
	file, err := os.Create(configPath)
	if err != nil {
		return err
	}
	defer file.Close()

	encoder := yaml.NewEncoder(file)
	if err := encoder.Encode(config); err != nil {
		return err
	}

	return nil
}

// configureGorillaImport interactively configures gorillaimport settings with sanity checks
func configureGorillaImport() Config {
	config := defaultConfig
	fmt.Println("Configuring gorillaimport...")

	// Sanity check for repo path
	for {
		fmt.Printf("Repo URL (must be an absolute path, e.g., /Users/username/DevOps/Gorilla/deployment): ")
		fmt.Scanln(&config.RepoPath)

		// Check if the path starts with "/"
		if filepath.IsAbs(config.RepoPath) {
			break
		}
		fmt.Println("Invalid repo path. Please ensure it's an absolute path starting with '/'.")
	}

	// Validate the cloud provider
	for {
		fmt.Printf("Cloud Provider (aws/azure or leave blank for none): ")
		fmt.Scanln(&config.CloudProvider)

		config.CloudProvider = strings.ToLower(config.CloudProvider) // Normalize case
		if config.CloudProvider == "" || config.CloudProvider == "aws" || config.CloudProvider == "azure" {
			break
		}
		fmt.Println("Invalid cloud provider. Please enter 'aws', 'azure', or leave blank for none.")
	}

	// Validate the cloud bucket if cloud provider is set
	if config.CloudProvider != "" {
		for {
			fmt.Printf("Cloud Bucket (e.g., your-bucket-name/path/to/repo, no s3:// or https://): ")
			fmt.Scanln(&config.CloudBucket)

			// Check if the cloud bucket doesn't start with a protocol
			if !strings.HasPrefix(config.CloudBucket, "s3://") && !strings.HasPrefix(config.CloudBucket, "https://") {
				break
			}
			fmt.Println("Invalid cloud bucket. Please remove any 's3://' or 'https://' prefix and enter only the bucket path.")
		}
	}

	// Default catalog and architecture prompts
	fmt.Printf("Default catalog (default: %s): ", config.DefaultCatalog)
	fmt.Scanln(&config.DefaultCatalog)
	if config.DefaultCatalog == "" {
		config.DefaultCatalog = defaultConfig.DefaultCatalog
	}

	fmt.Printf("Default architecture (default: %s): ", config.DefaultArch)
	fmt.Scanln(&config.DefaultArch)
	if config.DefaultArch == "" {
		config.DefaultArch = defaultConfig.DefaultArch
	}

	// Save the configuration
	err := saveConfig(getConfigPath(), config)
	if err != nil {
		fmt.Printf("Error saving config: %s\n", err)
	}

	return config
}

// extractMSIMetadata extracts MSI metadata depending on the platform (macOS or Windows)
func extractMSIMetadata(msiFilePath string) (string, string, string, string, string, error) {
	var productName, developer, version, productCode, upgradeCode string
	tempDir, err := os.MkdirTemp("", "msi-extract-")
	if err != nil {
		return "", "", "", "", "", fmt.Errorf("failed to create temporary directory: %v", err)
	}
	defer os.RemoveAll(tempDir)

	switch runtime.GOOS {
	case "windows":
		// Run msiexec with the correct working directory
		msiexecCmd := exec.Command("msiexec", "/a", msiFilePath, "/qn", "TARGETDIR="+tempDir)
		msiexecCmd.Dir = tempDir // Set the working directory
		err = msiexecCmd.Run()
		if err != nil {
			return "", "", "", "", "", fmt.Errorf("failed to extract MSI on Windows: %v", err)
		}

	case "darwin":
		// On macOS, we use msidump
		msidumpCmd := exec.Command("msidump", msiFilePath, "-d", tempDir)
		msidumpCmd.Dir = tempDir // Set the working directory
		err = msidumpCmd.Run()
		if err != nil {
			return "", "", "", "", "", fmt.Errorf("failed to extract MSI on macOS: %v", err)
		}

	default:
		return "", "", "", "", "", fmt.Errorf("unsupported platform")
	}

	// Validate that the expected files were extracted
	summaryInfoFile := filepath.Join(tempDir, "_SummaryInformation.idt")
	if _, err := os.Stat(summaryInfoFile); os.IsNotExist(err) {
		return "", "", "", "", "", fmt.Errorf("failed to read _SummaryInformation.idt: file does not exist in %s", tempDir)
	}

	// Parse _SummaryInformation.idt for productName, developer, version
	summaryData, err := os.ReadFile(summaryInfoFile)
	if err != nil {
		return "", "", "", "", "", fmt.Errorf("failed to read _SummaryInformation.idt: %v", err)
	}
	lines := strings.Split(string(summaryData), "\n")
	for _, line := range lines {
		cols := strings.Split(line, "\t")
		if len(cols) < 2 {
			continue
		}
		switch cols[0] {
		case "3":
			productName = cols[1]
		case "4":
			developer = cols[1]
		case "6":
			version = strings.Fields(cols[1])[0]
		}
	}

	// Parse Property.idt for productCode and upgradeCode
	propertyFile := filepath.Join(tempDir, "Property.idt")
	propertyData, err := os.ReadFile(propertyFile)
	if err != nil {
		return "", "", "", "", "", fmt.Errorf("failed to read Property.idt: %v", err)
	}
	lines = strings.Split(string(propertyData), "\n")
	for _, line := range lines {
		cols := strings.Split(line, "\t")
		if len(cols) < 2 {
			continue
		}
		switch cols[0] {
		case "ProductCode":
			productCode = cols[1]
		case "UpgradeCode":
			upgradeCode = cols[1]
		}
	}

	return productName, developer, version, productCode, upgradeCode, nil
}

// calculateSHA256 calculates the SHA-256 hash of the given file.
func calculateSHA256(filePath string) (string, error) {
	file, err := os.Open(filePath)
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

// copyFile copies a file from src to dst, creating directories as needed.
func copyFile(src, dst string) (int64, error) {
	destDir := filepath.Dir(dst)
	if _, err := os.Stat(destDir); os.IsNotExist(err) {
		if err := os.MkdirAll(destDir, 0755); err != nil {
			return 0, fmt.Errorf("failed to create directory '%s': %v", destDir, err)
		}
	}

	sourceFile, err := os.Open(src)
	if err != nil {
		return 0, err
	}
	defer sourceFile.Close()

	destFile, err := os.Create(dst)
	if err != nil {
		return 0, err
	}
	defer destFile.Close()

	nBytes, err := io.Copy(destFile, sourceFile)
	if err != nil {
		return 0, err
	}

	err = destFile.Sync()
	return nBytes, err
}

// createPkgsInfo generates a pkgsinfo YAML file for the provided package
func createPkgsInfo(
	filePath string,
	outputDir string,
	name string,
	version string,
	catalogs []string,
	category string,
	developer string,
	supportedArch []string,
	repoPath string,
	installerSubPath string,
	productCode string,
	upgradeCode string,
	fileHash string,
	unattendedInstall bool,
	unattendedUninstall bool,
	preinstallScript string,
	postinstallScript string,
	uninstallScript string,
	uninstaller *Installer,
) error {

	// Ensure that productCode and upgradeCode don't contain artifacts
	cleanProductCode := strings.Trim(productCode, "{}\r\n ")
	cleanUpgradeCode := strings.Trim(upgradeCode, "{}\r\n ")

	pkgsInfo := PkgsInfo{
		Name:                name,
		Version:             version,
		Installer: &Installer{
			Location: installerLocation,
			Hash:     fileHash,
			Type:     filepath.Ext(filePath)[1:],
		},
		Uninstaller:         uninstaller,
		Catalogs:            catalogs,
		Category:            category,
		Developer:           developer,
		Description:         "",
		SupportedArch:       supportedArch,
		ProductCode:         cleanProductCode,
		UpgradeCode:         cleanUpgradeCode,
		UnattendedInstall:   unattendedInstall,
		UnattendedUninstall: unattendedUninstall,
		PreinstallScript:    preinstallScript,
		PostinstallScript:   postinstallScript,
		UninstallScript:     uninstallScript,
	}

	// Ensure that the subfolder path in pkgsinfo exists
	outputFilePath := filepath.Join(outputDir, installerSubPath)
	if _, err := os.Stat(outputFilePath); os.IsNotExist(err) {
		// Create the directories if they don't exist
		err = os.MkdirAll(outputFilePath, 0755)
		if err != nil {
			return fmt.Errorf("failed to create directory structure: %v", err)
		}
	}

	outputFile := filepath.Join(outputFilePath, fmt.Sprintf("%s-%s.yaml", name, version))

	file, err := os.Create(outputFile)
	if err != nil {
		return fmt.Errorf("failed to create pkgsinfo file: %v", err)
	}
	defer file.Close()

	encoder := yaml.NewEncoder(file)
	encoder.SetIndent(2)
	if err := encoder.Encode(pkgsInfo); err != nil {
		return fmt.Errorf("failed to encode pkgsinfo YAML: %v", err)
	}

	return nil
}

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

    // Clean the input productCode and upgradeCode
    cleanedProductCode := strings.Trim(strings.ToLower(productCode), "{}\r\n ")
    cleanedUpgradeCode := strings.Trim(strings.ToLower(upgradeCode), "{}\r\n ")

    for _, item := range allPackages {
        // Skip items where product codes are empty
        if item.ProductCode == "" || item.UpgradeCode == "" {
            continue
        }

        // Clean the item product codes
        itemProductCode := strings.Trim(strings.ToLower(item.ProductCode), "{}\r\n ")
        itemUpgradeCode := strings.Trim(strings.ToLower(item.UpgradeCode), "{}\r\n ")

        // Compare product codes and upgrade codes
        if itemProductCode == cleanedProductCode && itemUpgradeCode == cleanedUpgradeCode {
            // Check if the hashes match
            if item.Installer != nil && item.Installer.Hash == currentFileHash {
                return &item, true, nil
            } else {
                return &item, false, nil
            }
        }
    }
    return nil, false, nil
}

func findMatchingItemInAllCatalogWithDifferentVersion(repoPath, name, version string) (*PkgsInfo, error) {
    allCatalogPath := filepath.Join(repoPath, "catalogs", "All.yaml")
    fileContent, err := os.ReadFile(allCatalogPath)
    if err != nil {
        return nil, fmt.Errorf("failed to read All.yaml: %v", err)
    }

    var allPackages []PkgsInfo
    if err := yaml.Unmarshal(fileContent, &allPackages); err != nil {
        return nil, fmt.Errorf("failed to unmarshal All.yaml: %v", err)
    }

    // Normalize input name and version
    cleanName := strings.TrimSpace(strings.ToLower(name))
    cleanVersion := strings.TrimSpace(strings.ToLower(version))

    for _, item := range allPackages {
        // Skip items with empty name or version
        if item.Name == "" || item.Version == "" {
            continue
        }

        // Normalize item name and version
        itemName := strings.TrimSpace(strings.ToLower(item.Name))
        itemVersion := strings.TrimSpace(strings.ToLower(item.Version))

        // Compare names and versions
        if itemName == cleanName && itemVersion != cleanVersion {
            return &item, nil // Return if the name matches but the version is different
        }
    }

    return nil, nil
}

// generateWrapperScript wraps a batch script inside a PowerShell script
func generateWrapperScript(batchContent string) string {
	// Escape single quotes in batchContent
	batchContentEscaped := strings.ReplaceAll(batchContent, "'", "''")
	psScript := fmt.Sprintf(`
$batchScriptContent = @'
%s
'@

$batchFile = "$env:TEMP\\temp_script.bat"
Set-Content -Path $batchFile -Value $batchScriptContent -Encoding ASCII
& cmd.exe /c $batchFile
Remove-Item $batchFile
`, batchContentEscaped)
	return psScript
}

// Custom prompt template to remove `?`
var customPromptTemplate = survey.IconSet{
	Question: survey.Icon{
		Text: "",
	},
}

// promptSurvey prompts the user with a prepopulated value using survey and allows them to modify it
func promptSurvey(value *string, prompt string, defaultValue string) {
	// Clean default value
	cleanDefault := cleanTextForPrompt(defaultValue)

	survey.AskOne(&survey.Input{
		Message: prompt,
		Default: cleanDefault,
	}, value, survey.WithIcons(func(icons *survey.IconSet) {
		*icons = customPromptTemplate
	}))
}

// getInputWithDefault prompts the user with a prepopulated value and allows them to confirm or modify it
func getInputWithDefault(prompt, defaultValue string) string {
	cleanDefault := cleanTextForPrompt(defaultValue)

	if cleanDefault != "" {
		fmt.Printf("%s [%s]: ", prompt, cleanDefault)
	} else {
		fmt.Printf("%s: ", prompt)
	}
	var input string
	fmt.Scanln(&input)

	if input == "" {
		return cleanDefault
	}
	return input
}

// cleanTextForPrompt ensures text is clean and doesn't cause issues in terminal input
func cleanTextForPrompt(input string) string {
	return strings.TrimSpace(input)
}

// confirmAction prompts the user to confirm an action.
func confirmAction(prompt string) bool {
	fmt.Printf("%s (y/n): ", prompt)
	var response string
	_, err := fmt.Scanln(&response)
	if err != nil {
		fmt.Println("Error reading input, assuming 'no'")
		return false
	}
	response = strings.ToLower(strings.TrimSpace(response))
	return response == "y" || response == "yes"
}

// gorillaImport handles the import process and metadata extraction
func gorillaImport(
	installerPath string,
	installScriptPath string,
	uninstallScriptPath string,
	postinstallScriptPath string,
	uninstallerPath string,
	config Config,
) (bool, error) {

	// Check if installer exists
	if _, err := os.Stat(installerPath); os.IsNotExist(err) {
		return false, fmt.Errorf("installer '%s' does not exist", installerPath)
	}

	// Get installer extension
	installerExtension := strings.ToLower(filepath.Ext(installerPath))

	var productName, developer, version, productCode, upgradeCode string
	var err error

	if installerExtension == ".msi" {
		// Extract metadata
		productName, developer, version, productCode, upgradeCode, err = extractMSIMetadata(installerPath)
		if err != nil {
			fmt.Printf("Error extracting metadata from MSI: %v\n", err)
			fmt.Println("Fallback to manual input.")
		}
	} else {
		// Prompt user for metadata
		fmt.Println("Installer is not an MSI. Please provide the following information.")
		promptSurvey(&productName, "Product Name", "")
		promptSurvey(&developer, "Developer", "")
		promptSurvey(&version, "Version", "")
		// ProductCode and UpgradeCode may not be available for EXE installers
		productCode = ""
		upgradeCode = ""
	}

	// Calculate hash of the installer
	installerHash, err := calculateSHA256(installerPath)
	if err != nil {
		return false, fmt.Errorf("error calculating installer file hash: %v", err)
	}

	// Use 'apps' as default installerSubPath
	installerSubPath := "apps"

	// Copy installer to repo
	installerFilename := fmt.Sprintf("%s-%s%s", productName, version, filepath.Ext(installerPath))
	pkgsFolderPath := filepath.Join(config.RepoPath, "pkgs", installerSubPath)
	if _, err := os.Stat(pkgsFolderPath); os.IsNotExist(err) {
		err = os.MkdirAll(pkgsFolderPath, 0755)
		if err != nil {
			return false, fmt.Errorf("failed to create directory structure: %v", err)
		}
	}
	installerDestinationPath := filepath.Join(pkgsFolderPath, installerFilename)
	_, err = copyFile(installerPath, installerDestinationPath)
	if err != nil {
		return false, fmt.Errorf("failed to copy installer to destination: %v", err)
	}

	// Installer location in pkgs
	installerLocation := filepath.Join("/", installerSubPath, installerFilename)

	// Process scripts
	var preinstallScriptContent string
	var postinstallScriptContent string
	var uninstallScriptContent string

	// Process install script
	if installScriptPath != "" {
		content, err := os.ReadFile(installScriptPath)
		if err != nil {
			return false, fmt.Errorf("error reading install script file: %v", err)
		}
		extension := strings.ToLower(filepath.Ext(installScriptPath))
		if extension == ".bat" {
			preinstallScriptContent = generateWrapperScript(string(content))
		} else if extension == ".ps1" {
			preinstallScriptContent = string(content)
		} else {
			return false, fmt.Errorf("unsupported install script file type: %s", extension)
		}
	}

	// Process uninstall script
	if uninstallScriptPath != "" {
		content, err := os.ReadFile(uninstallScriptPath)
		if err != nil {
			return false, fmt.Errorf("error reading uninstall script file: %v", err)
		}
		extension := strings.ToLower(filepath.Ext(uninstallScriptPath))
		if extension == ".bat" {
			uninstallScriptContent = generateWrapperScript(string(content))
		} else if extension == ".ps1" {
			uninstallScriptContent = string(content)
		} else {
			return false, fmt.Errorf("unsupported uninstall script file type: %s", extension)
		}
	}

	// Process post-install script
	if postinstallScriptPath != "" {
		content, err := os.ReadFile(postinstallScriptPath)
		if err != nil {
			return false, fmt.Errorf("error reading post-install script file: %v", err)
		}
		extension := strings.ToLower(filepath.Ext(postinstallScriptPath))
		if extension == ".ps1" {
			postinstallScriptContent = string(content)
		} else {
			return false, fmt.Errorf("unsupported post-install script file type: %s", extension)
		}
	}

	// Process uninstaller
	var uninstaller *Installer
	if uninstallerPath != "" {
		if _, err := os.Stat(uninstallerPath); os.IsNotExist(err) {
			return false, fmt.Errorf("uninstaller '%s' does not exist", uninstallerPath)
		}
		uninstallerHash, err := calculateSHA256(uninstallerPath)
		if err != nil {
			return false, fmt.Errorf("error calculating uninstaller file hash: %v", err)
		}
		uninstallerExtension := strings.TrimPrefix(strings.ToLower(filepath.Ext(uninstallerPath)), ".")

		// Copy uninstaller to repo
		uninstallerFilename := filepath.Base(uninstallerPath)
		uninstallerDestinationPath := filepath.Join(pkgsFolderPath, uninstallerFilename)
		_, err = copyFile(uninstallerPath, uninstallerDestinationPath)
		if err != nil {
			return false, fmt.Errorf("failed to copy uninstaller to destination: %v", err)
		}

		uninstallerLocation := filepath.Join("/", installerSubPath, uninstallerFilename)

		uninstaller = &Installer{
			Location:  uninstallerLocation,
			Hash:      uninstallerHash,
			Arguments: []string{}, // You can add logic to handle uninstaller arguments if needed
			Type:      uninstallerExtension,
		}
	}

	// Prompt for additional metadata if not already set
	category := "Apps"
	supportedArch := config.DefaultArch
	catalogs := config.DefaultCatalog

	promptSurvey(&category, "Category", category)
	promptSurvey(&supportedArch, "Architecture(s)", supportedArch)
	promptSurvey(&catalogs, "Catalogs", catalogs)

	catalogList := strings.Split(catalogs, ",")
	for i := range catalogList {
		catalogList[i] = strings.TrimSpace(catalogList[i])
	}

	// Proceed with the creation of pkgsinfo YAML file using the confirmed/extracted metadata
	err = createPkgsInfo(
		installerPath,
		filepath.Join(config.RepoPath, "pkgsinfo"),
		productName,
		version,
		catalogList,
		category,
		developer,
		[]string{supportedArch},
		config.RepoPath,
		installerSubPath,
		productCode,
		upgradeCode,
		installerHash,
		true, // Unattended install default
		true, // Unattended uninstall default
		preinstallScriptContent,
		postinstallScriptContent,
		uninstallScriptContent,
		uninstaller,
	)

	if err != nil {
		return false, fmt.Errorf("failed to create pkgsinfo: %v", err)
	}

	fmt.Printf("Imported %s version %s successfully.\n", productName, version)
	return true, nil
}

// uploadToCloud handles uploading files to AWS or AZURE if a valid bucket is provided
func uploadToCloud(config Config) error {
	// Skip if cloud provider is none
	if config.CloudProvider == "none" {
		return nil
	}

	// Construct the local pkgs path based on the RepoPath
	localPkgsPath := filepath.Join(config.RepoPath, "pkgs")

	// Check cloud provider and run the appropriate sync logic
	if config.CloudProvider == "aws" {
		// Check if AWS CLI exists
		awsPath := "/usr/local/bin/aws"
		if _, err := os.Stat(awsPath); os.IsNotExist(err) {
			fmt.Println("AWS CLI not found at /usr/local/bin/aws. Please install AWS CLI.")
			return err
		}

		// Check if AWS credentials are properly set up
		awsCheckCmd := exec.Command(awsPath, "sts", "get-caller-identity")
		if err := awsCheckCmd.Run(); err != nil {
			fmt.Println("AWS CLI not properly configured or logged in. Please run `aws configure`.")
			return err
		}

		fmt.Println("Starting upload for pkgs to AWS S3")
		cmd := exec.Command(awsPath, "s3", "sync",
			localPkgsPath,
			fmt.Sprintf("s3://%s/pkgs/", config.CloudBucket),
			"--exclude", "*.git/*", "--exclude", "**/.DS_Store")

		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr

		if err := cmd.Run(); err != nil {
			return fmt.Errorf("error syncing pkgs directory to S3: %v", err)
		}
		fmt.Println("Upload to S3 completed successfully")

	} else if config.CloudProvider == "azure" {
		// Check if AzCopy exists
		azcopyPath := "/opt/homebrew/bin/azcopy"
		if _, err := os.Stat(azcopyPath); os.IsNotExist(err) {
			fmt.Println("AzCopy not found at /opt/homebrew/bin/azcopy. Please install AzCopy.")
			return err
		}

		// Check if Azure is properly logged in
		azureCheckCmd := exec.Command("/opt/homebrew/bin/az", "account", "show")
		if err := azureCheckCmd.Run(); err != nil {
			fmt.Println("AzCopy not properly configured or logged in. Please run `az login`.")
			return err
		}

		fmt.Println("Starting upload for pkgs to Azure Blob Storage")
		cmd := exec.Command(azcopyPath, "sync",
			localPkgsPath,
			fmt.Sprintf("https://%s/pkgs/", config.CloudBucket),
			"--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")

		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr

		if err := cmd.Run(); err != nil {
			return fmt.Errorf("error syncing pkgs directory to Azure Blob Storage: %v", err)
		}
		fmt.Println("Upload to Azure completed successfully")
	}

	return nil
}

// Stub function to avoid the error
func rebuildCatalogs() {
	fmt.Println("Rebuild catalogs not implemented yet.")
}

// main handles the configuration and running gorillaImport
func main() {
	configFlag := flag.Bool("config", false, "Run interactive configuration setup.")
	archFlag := flag.String("arch", "", "Specify the architecture (e.g., x86_64, arm64)")
	installerFlag := flag.String("installer", "", "Path to the installer .exe or .msi file.")
	uninstallerFlag := flag.String("uninstaller", "", "Path to the uninstaller .exe or .msi file.")
	installScriptFlag := flag.String("installscript", "", "Path to the install script (.bat or .ps1).")
	uninstallScriptFlag := flag.String("uninstallscript", "", "Path to the uninstall script (.bat or .ps1).")
	postinstallScriptFlag := flag.String("postinstallscript", "", "Path to the post-install script (.ps1).")
	flag.Parse()

	if *configFlag {
		configureGorillaImport()
		return
	}

	// Check for necessary tools
	if err := checkTools(); err != nil {
		fmt.Printf("Error: %s\n", err)
		os.Exit(1)
	}

	configData := defaultConfig
	configPath := getConfigPath()

	// Load config if available
	if _, err := os.Stat(configPath); err == nil {
		loadedConfig, err := loadConfig(configPath)
		if err == nil {
			if loadedConfig.RepoPath != "" {
				configData.RepoPath = loadedConfig.RepoPath
			}
			if loadedConfig.DefaultCatalog != "" {
				configData.DefaultCatalog = loadedConfig.DefaultCatalog
			}
			if loadedConfig.DefaultArch != "" {
				configData.DefaultArch = loadedConfig.DefaultArch
			}
			if loadedConfig.CloudBucket != "" {
				configData.CloudBucket = loadedConfig.CloudBucket
			}
			if loadedConfig.CloudProvider != "" {
				configData.CloudProvider = loadedConfig.CloudProvider
			}
		}
	}

	// Validate that if --installscript is provided and is a .bat, then --installer is mandatory
	if *installScriptFlag != "" {
		scriptExtension := strings.ToLower(filepath.Ext(*installScriptFlag))
		if scriptExtension == ".bat" && *installerFlag == "" {
			fmt.Println("Error: --installer is mandatory when using --installscript with a .bat file.")
			os.Exit(1)
		}
	}

	// Determine the installer path
	packagePath := *installerFlag
	if packagePath == "" && flag.NArg() > 0 {
		packagePath = flag.Arg(0)
	}

	if packagePath == "" {
		fmt.Println("Error: No installer specified. Use --installer or provide the installer path as an argument.")
		os.Exit(1)
	}

	// If --arch flag is provided, override the default architecture
	if *archFlag != "" {
		configData.DefaultArch = *archFlag
	}

	// Perform the import and check if it was successful
	importSuccess, err := gorillaImport(
		packagePath,
		*installScriptFlag,
		*uninstallScriptFlag,
		*postinstallScriptFlag,
		*uninstallerFlag,
		configData,
	)
	if err != nil {
		fmt.Printf("Error: %s\n", err)
		os.Exit(1)
	}

	// Only upload if the import was successful
	if importSuccess && configData.CloudProvider != "none" {
		if err := uploadToCloud(configData); err != nil {
			fmt.Printf("Error uploading to cloud: %s\n", err)
		}
	}

	// After successful import, ask the user if they want to run makecatalogs
	if importSuccess {
		confirm := getInputWithDefault("Would you like to run makecatalogs? [y/n]", "n")
		if strings.ToLower(confirm) == "y" {
			fmt.Println("Running makecatalogs to update catalogs...")

			makeCatalogsBinary := filepath.Join(filepath.Dir(os.Args[0]), "makecatalogs")
			cmd := exec.Command(makeCatalogsBinary)
			cmd.Stdout = os.Stdout
			cmd.Stderr = os.Stderr

			err := cmd.Run()
			if err != nil {
				fmt.Printf("Error running makecatalogs: %v\n", err)
				os.Exit(1)
			}

			fmt.Println("makecatalogs completed successfully.")
		} else {
			fmt.Println("Skipped running makecatalogs.")
		}
	}
}

