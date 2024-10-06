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
)

// PkgsInfo structure
type PkgsInfo struct {
	Name              string   `yaml:"name"`
	Version           string   `yaml:"version"`
	Catalogs          []string `yaml:"catalogs"`
	Category          string   `yaml:"category"`
	Developer         string   `yaml:"developer"`
	Description       string   `yaml:"description"`
	InstallerItemPath string   `yaml:"installer_item_location"`
	InstallerItemHash string   `yaml:"installer_item_hash"`
	SupportedArch     []string `yaml:"supported_architectures"`
}

// Configuration structure to hold settings
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

// checkTools checks if necessary tools are installed on the system (msiexec, sigcheck for Windows; msiextract for macOS)
func checkTools() error {
	switch runtime.GOOS {
	case "windows":
		// Check for msiexec (Windows)
		_, err := exec.LookPath("msiexec")
		if err != nil {
			fmt.Println("msiexec is not found. This is necessary for extracting .msi metadata on Windows.")
			fmt.Println("msiexec is pre-installed on Windows. If it is not working, make sure the system PATH is configured correctly.")
		}

		// Check for sigcheck (Windows)
		_, err = exec.LookPath("sigcheck.exe")
		if err != nil {
			fmt.Println("sigcheck.exe is not found. You can download it from the Sysinternals Suite at:")
			fmt.Println("https://docs.microsoft.com/en-us/sysinternals/downloads/sysinternals-suite")
			fmt.Println("Extract the suite, and make sure sigcheck.exe is added to your PATH for easy access.")
		}

	case "darwin": // MacOS
		// Check for msiextract (from msitools) on macOS
		_, err := exec.LookPath("msiextract")
		if err != nil {
			fmt.Println("msiextract (from msitools) is not found. You can install it using Homebrew:")
			fmt.Println("Then install msitools:")
			fmt.Println("    brew install msitools")
		}
	default:
		fmt.Println("This script is only supported on Windows and macOS.")
	}
	return nil
}

// scanRepo scans the pkgsinfo directory recursively to find existing pkgsinfo files.
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

	if err != nil {
		return nil, err
	}
	return pkgsInfos, nil
}

// findMatchingItem looks for an item with the same name and/or version.
func findMatchingItem(pkgsInfos []PkgsInfo, name string, version string) *PkgsInfo {
	for _, item := range pkgsInfos {
		if item.Name == name && item.Version == version {
			return &item
		}
	}
	return nil
}

// getConfigPath returns the appropriate configuration file path based on the OS
func getConfigPath() string {
	if runtime.GOOS == "darwin" {
		return filepath.Join(os.Getenv("HOME"), "Library/Preferences/com.github.gorilla.import.yaml")
	} else if runtime.GOOS == "windows" {
		return filepath.Join(os.Getenv("APPDATA"), "Gorilla", "import.yaml")
	}
	return "config.yaml"
}

// configureGorillaImport interactively configures gorillaimport settings with sanity checks
func configureGorillaImport() Config {
    config := defaultConfig
    fmt.Println("Configuring gorillaimport...")

    // Sanity check for repo path
    for {
        fmt.Printf("Repo URL (must be an absolute path, e.g., ~/DevOps/Gorilla/deployment): ")
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

// saveConfig saves the configuration to a YAML file
func saveConfig(configPath string, config Config) error {
	if _, err := os.Stat(filepath.Dir(configPath)); os.IsNotExist(err) {
		err = os.MkdirAll(filepath.Dir(configPath), 0755)
		if err != nil {
			return err
		}
	}

	file, err := os.Create(configPath)
	if err != nil {
		return err
	}
	defer file.Close()

	yamlEncoder := yaml.NewEncoder(file)
	return yamlEncoder.Encode(config)
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

// createPkgsInfo generates a pkgsinfo YAML file for the provided package.
func createPkgsInfo(filePath, outputDir, name, version, catalog, category, developer, arch, repoPath, installerSubPath string) error {
    hash, err := calculateSHA256(filePath)
    if err != nil {
        return err
    }

    // Adjust installer item location to use the name and version instead of the original file name
    installerItemLocation := filepath.Join("/", installerSubPath, fmt.Sprintf("%s-%s%s", name, version, filepath.Ext(filePath)))

    pkgsInfo := PkgsInfo{
        Name:              name,
        Version:           version,
        InstallerItemHash: hash,
        InstallerItemPath: installerItemLocation,
        Catalogs:          []string{catalog},
        Category:          category,
        Developer:         developer,
        Description:       "",
        SupportedArch:     []string{arch},
    }

    outputFile := filepath.Join(outputDir, fmt.Sprintf("%s-%s.yaml", name, version))

    file, err := os.Create(outputFile)
    if err != nil {
        return err
    }
    defer file.Close()

    encoder := yaml.NewEncoder(file)
    if err := encoder.Encode(pkgsInfo); err != nil {
        return err
    }

    fmt.Printf("Pkgsinfo created at: %s\n", outputFile)
    return nil
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

// extractPackageMetadata extracts ProductName, ProductCode, and ProductVersion from MSI/EXE files
func extractPackageMetadata(packagePath string) (string, string, string, error) {
    var productName, productCode, productVersion string
    var err error

    if runtime.GOOS == "windows" {
        // On Windows, use sigcheck or msiexec for MSI and EXE files
        if filepath.Ext(packagePath) == ".msi" {
            // Using msiexec to extract MSI metadata on Windows
            productName, productVersion, productCode, err = extractMSIMetadataWindows(packagePath)
        } else if filepath.Ext(packagePath) == ".exe" {
            // Use sigcheck for EXE metadata on Windows
            productName, productVersion, err = extractEXEMetadataWindows(packagePath)
        }
    } else if runtime.GOOS == "darwin" {
        // On macOS, use msiextract from msitools
        if filepath.Ext(packagePath) == ".msi" {
            productName, productVersion, productCode, err = extractMSIMetadataMac(packagePath)
        } else {
            err = fmt.Errorf("unsupported package type on macOS")
        }
    }
    return productName, productVersion, productCode, err
}

// extractMSIMetadataWindows extracts MSI metadata on Windows using msiexec
func extractMSIMetadataWindows(packagePath string) (string, string, string, error) {
    cmd := exec.Command("msiexec", "/I", packagePath, "/quiet", "/qn", "PROPERTY=ProductName", "PROPERTY=ProductVersion", "PROPERTY=ProductCode")
    output, err := cmd.CombinedOutput()
    if err != nil {
        return "", "", "", fmt.Errorf("failed to extract MSI metadata on Windows: %v", err)
    }
    // Parse the output to extract productName, productVersion, and productCode
    productName, productVersion, productCode := parseMSIOutput(string(output))
    return productName, productVersion, productCode, nil
}

// extractEXEMetadataWindows uses sigcheck to extract EXE metadata on Windows
func extractEXEMetadataWindows(packagePath string) (string, string, error) {
    cmd := exec.Command("sigcheck.exe", "-n", "-v", packagePath)
    output, err := cmd.CombinedOutput()
    if err != nil {
        return "", "", fmt.Errorf("failed to extract EXE metadata: %v", err)
    }
    // Parse the sigcheck output to get productName and productVersion
    productName, productVersion := parseEXEOutput(string(output))
    return productName, productVersion, nil
}

// extractMSIMetadataMac extracts MSI metadata on macOS using msiextract from msitools
func extractMSIMetadataMac(packagePath string) (string, string, string, error) {
    cmd := exec.Command("msiextract", "--info", packagePath)
    output, err := cmd.CombinedOutput()
    if err != nil {
        return "", "", "", fmt.Errorf("failed to extract MSI metadata on macOS: %v", err)
    }
    productName, productVersion, productCode := parseMSIOutput(string(output))
    return productName, productVersion, productCode, nil
}

// parseMSIOutput parses the output of msiexec/msiextract and returns name, version, and code
func parseMSIOutput(output string) (string, string, string) {
    var productName, productVersion, productCode string
    lines := strings.Split(output, "\n")
    for _, line := range lines {
        if strings.Contains(line, "ProductName") {
            productName = strings.TrimSpace(strings.Split(line, ":")[1])
        }
        if strings.Contains(line, "ProductVersion") {
            productVersion = strings.TrimSpace(strings.Split(line, ":")[1])
        }
        if strings.Contains(line, "ProductCode") {
            productCode = strings.TrimSpace(strings.Split(line, ":")[1])
        }
    }
    return productName, productVersion, productCode
}

// parseEXEOutput parses the output of sigcheck for EXE files and returns name and version
func parseEXEOutput(output string) (string, string) {
    var productName, productVersion string
    lines := strings.Split(output, "\n")
    for _, line := range lines {
        if strings.Contains(line, "Product name") {
            productName = strings.TrimSpace(strings.Split(line, ":")[1])
        }
        if strings.Contains(line, "File version") {
            productVersion = strings.TrimSpace(strings.Split(line, ":")[1])
        }
    }
    return productName, productVersion
}

// gorillaImport handles the overall process of importing a package and generating a pkgsinfo file.
// gorillaImport handles the overall process of importing a package and generating a pkgsinfo file.
func gorillaImport(packagePath string, config Config) error {
    // Extract metadata from the package
    productName, productVersion, productCode, err := extractPackageMetadata(packagePath)
    if err != nil {
        fmt.Printf("Error extracting metadata: %v\n", err)
    } else {
        fmt.Printf("Extracted Name: %s, Version: %s, Product Code: %s\n", productName, productVersion, productCode)
    }

    // Fallback to user input if extraction fails or is incomplete
    if productName == "" || productVersion == "" {
        fmt.Println("Fallback to manual input.")
        fmt.Printf("Item name: ")
        fmt.Scanln(&productName)

        fmt.Printf("Version: ")
        fmt.Scanln(&productVersion)
    }

    packageExt := filepath.Ext(packagePath)
    pkgsInfos, err := scanRepo(config.RepoPath)
    if err != nil {
        return fmt.Errorf("failed to scan repo: %v", err)
    }

    // Check for matching item by comparing name and version
    matchingItem := findMatchingItem(pkgsInfos, productName, productVersion)
    if matchingItem != nil {
        newItemHash, err := calculateSHA256(packagePath)
        if err != nil {
            return err
        }

        // Compare hashes to determine if it's an identical package
        if matchingItem.InstallerItemHash == newItemHash {
            fmt.Println("*** This item is identical to an existing item in the repo ***")
            if !confirmAction("Import this item anyway?") {
                fmt.Println("Import canceled.")
                return nil
            }
        }

        fmt.Println("Reusing existing metadata for the import.")
        // Use the existing matching item's metadata
        config.DefaultCatalog = matchingItem.Catalogs[0]
        installerSubPath := filepath.Dir(matchingItem.InstallerItemPath)
        pkgsinfoPath := filepath.Join(config.RepoPath, "pkgsinfo", installerSubPath, fmt.Sprintf("%s-%s.yaml", matchingItem.Name, matchingItem.Version))

        // Create new pkgsinfo based on matching item
        err = createPkgsInfo(packagePath, filepath.Dir(pkgsinfoPath), matchingItem.Name, matchingItem.Version, config.DefaultCatalog, matchingItem.Category, matchingItem.Developer, config.DefaultArch, config.RepoPath, installerSubPath)
        if err != nil {
            return fmt.Errorf("failed to create pkgsinfo: %v", err)
        }
        return nil
    }

    // If no matching item, prompt for further info and proceed with the import
    fmt.Printf("Category: ")
    var category string
    fmt.Scanln(&category)

    fmt.Printf("Developer: ")
    var developer string
    fmt.Scanln(&developer)

    // Prompt for the installer item path
    var installerPath string
    fmt.Printf("What is the installer item path? ")
    fmt.Scanln(&installerPath)

    // Use the configured repo path for storage
    pkgsPath := filepath.Join(config.RepoPath, "pkgs", installerPath, fmt.Sprintf("%s-%s%s", productName, productVersion, packageExt))
    pkgsinfoPath := filepath.Join(config.RepoPath, "pkgsinfo", installerPath, fmt.Sprintf("%s-%s.yaml", productName, productVersion))

    // Copy the package to the repository and create the pkgsinfo file
    if _, err := copyFile(packagePath, pkgsPath); err != nil {
        return fmt.Errorf("failed to copy package to repo: %v", err)
    }

    err = createPkgsInfo(packagePath, filepath.Dir(pkgsinfoPath), productName, productVersion, config.DefaultCatalog, category, developer, config.DefaultArch, config.RepoPath, installerPath)
    if err != nil {
        return fmt.Errorf("failed to create pkgsinfo: %v", err)
    }

    return nil
}

// uploadToS3 handles uploading files to AWS or AZURE if a valid bucket is provided
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

func main() {
	config := flag.Bool("config", false, "Run interactive configuration setup.")
	archFlag := flag.String("arch", "", "Specify the architecture (e.g., x86_64, arm64)")
	flag.Parse()

	if *config {
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
			if loadedConfig.CloudProvider != "" {  // Changed from CloudType to CloudProvider
				configData.CloudProvider = loadedConfig.CloudProvider
			}
		}
	}

	var packagePath string
	if flag.NArg() > 0 {
		packagePath = flag.Arg(0)
	} else {
		fmt.Printf("Enter the path to the package file to import: ")
		fmt.Scanln(&packagePath)
	}

	var err error
	
	// If --arch flag is provided, override the default architecture
	if *archFlag != "" {
		configData.DefaultArch = *archFlag
	}
	
	// Now assign err from gorillaImport function
	err = gorillaImport(packagePath, configData)
	if err != nil {
		fmt.Printf("Error: %s\n", err)
		os.Exit(1)
	}
	
	// After creating pkgs and pkgsinfo, upload to the appropriate cloud provider only if not "none"
	if configData.CloudProvider != "none" {
		if err := uploadToCloud(configData); err != nil {
			fmt.Printf("Error uploading to cloud: %s\n", err)
		}
	}
}

