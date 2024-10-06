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
	RepoPath      string `yaml:"repo_path"`
	CloudBucket   string `yaml:"cloud_bucket"`
	DefaultCatalog string `yaml:"default_catalog"`
	DefaultArch   string `yaml:"default_arch"`
}

// Default configuration values
var defaultConfig = Config{
	RepoPath:       "./repo",
	CloudBucket:    "",
	DefaultCatalog: "testing",
	DefaultArch:    "x86_64",
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

// configureGorillaImport interactively configures gorillaimport settings
func configureGorillaImport() Config {
	config := defaultConfig

	fmt.Println("Configuring gorillaimport...")

	fmt.Printf("Repo URL: ")
	fmt.Scanln(&config.RepoPath)

	fmt.Printf("Default catalog: ")
	fmt.Scanln(&config.DefaultCatalog)

	fmt.Printf("Default architecture (default: %s): ", config.DefaultArch)
	fmt.Scanln(&config.DefaultArch)
	if config.DefaultArch == "" {
		config.DefaultArch = defaultConfig.DefaultArch
	}

	fmt.Printf("Cloud Bucket: ")
	fmt.Scanln(&config.CloudBucket)
	if config.CloudBucket == "" {
		config.CloudBucket = defaultConfig.CloudBucket
	}

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

    installerItemLocation := filepath.Join(installerSubPath, filepath.Base(filePath))

    pkgsInfo := PkgsInfo{
        Name:              name,
        Version:           version,
        InstallerItemHash: hash,
        InstallerItemPath: installerItemLocation,  // Adjusted to include the relative path
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

// gorillaImport handles the overall process of importing a package and generating a pkgsinfo file.
func gorillaImport(packagePath string, config Config) error {
	packageName := filepath.Base(packagePath)
	packageExt := filepath.Ext(packagePath)

	if _, err := os.Stat(packagePath); os.IsNotExist(err) {
		return fmt.Errorf("package '%s' does not exist", packagePath)
	}

	// Scan the repo for existing pkgsinfo items
	pkgsInfos, err := scanRepo(config.RepoPath)
	if err != nil {
		return fmt.Errorf("failed to scan repo: %v", err)
	}

	// Check for a matching item in the repo by both name and version
	var matchingItem *PkgsInfo
	var installerSubPath string
	for _, item := range pkgsInfos {
		if item.Name == packageName && item.Version == item.Version { // Compare both name and version
			matchingItem = &item
			installerSubPath = filepath.Dir(item.InstallerItemPath) // Capture subfolder structure
			break
		}
	}

	// If a matching item is found, check if the package is identical
	if matchingItem != nil {
		newItemHash, hashErr := calculateSHA256(packagePath)
		if hashErr != nil {
			return hashErr
		}

		if matchingItem.InstallerItemHash == newItemHash {
			fmt.Println("*** This item is identical to an existing item in the repo ***")
			if !confirmAction("Import this item anyway?") {
				fmt.Println("Import canceled.")
				return nil
			}
		}

		fmt.Println("Copying attributes from the existing item...")
		config.DefaultCatalog = matchingItem.Catalogs[0]

		// Reuse the subfolder from the existing item for pkgsinfo
		pkgsinfoPath := filepath.Join(config.RepoPath, "pkgsinfo", installerSubPath, fmt.Sprintf("%s-%s.yaml", matchingItem.Name, matchingItem.Version))

		// Create pkgsinfo directly
		err = createPkgsInfo(packagePath, filepath.Dir(pkgsinfoPath), matchingItem.Name, matchingItem.Version, config.DefaultCatalog, matchingItem.Category, matchingItem.Developer, config.DefaultArch, config.RepoPath, installerSubPath)
		if err != nil {
			return fmt.Errorf("failed to create pkgsinfo: %v", err)
		}

		return nil
	}

	// Collect metadata from the user if no match was found
	var itemName, version, category, developer, arch string
	fmt.Printf("Item name: ")
	fmt.Scanln(&itemName)

	fmt.Printf("Version: ")
	fmt.Scanln(&version)

	fmt.Printf("Category: ")
	fmt.Scanln(&category)

	fmt.Printf("Developer: ")
	fmt.Scanln(&developer)

	// Prompt for the architecture
	fmt.Printf("Architecture (default: %s): ", config.DefaultArch)
	fmt.Scanln(&arch)
	if arch == "" {
		arch = config.DefaultArch
	}

	// Prompt for the installer item path
	var installerPath string
	fmt.Printf("What is the installer item path? ")
	fmt.Scanln(&installerPath)

	if installerPath == "" {
		installerPath = "apps" // Default subpath if none provided
	}

	// Use the configured repo path for storage
	pkgsPath := filepath.Join(config.RepoPath, "pkgs", installerPath, fmt.Sprintf("%s-%s%s", itemName, version, packageExt))
	pkgsinfoPath := filepath.Join(config.RepoPath, "pkgsinfo", installerPath, fmt.Sprintf("%s-%s.yaml", itemName, version))

	// Create directories as needed
	if err := os.MkdirAll(filepath.Dir(pkgsPath), 0755); err != nil {
		return fmt.Errorf("failed to create directory: %v", err)
	}
	if err := os.MkdirAll(filepath.Dir(pkgsinfoPath), 0755); err != nil {
		return fmt.Errorf("failed to create directory: %v", err)
	}

	// Copy package to the repository
	if _, err := copyFile(packagePath, pkgsPath); err != nil {
		return fmt.Errorf("failed to copy package to repo: %v", err)
	}

	// Create pkgsinfo for the item
	err = createPkgsInfo(packagePath, filepath.Dir(pkgsinfoPath), itemName, version, config.DefaultCatalog, category, developer, arch, config.RepoPath, installerPath)
	if err != nil {
		return fmt.Errorf("failed to create pkgsinfo: %v", err)
	}

	// Prompt for catalog rebuild
	if confirmAction("Rebuild catalogs?") {
		fmt.Println("Rebuilding catalogs...")
		rebuildCatalogs()
	}

	return nil
}

// uploadToS3 handles uploading files to S3 if a valid bucket is provided
func uploadToS3(bucket string) error {
	if bucket == "" {
		fmt.Println("No S3 bucket provided, skipping upload.")
		return nil
	}

	fmt.Println("Starting upload for pkgs")
	cmd := exec.Command("/usr/local/bin/aws", "s3", "sync",
		filepath.Join(os.Getenv("HOME"), "DevOps/Munki/deployment/pkgs/"),
		fmt.Sprintf("s3://%s/repo/deployment/pkgs/", bucket),
		"--exclude", "*.git/*", "--exclude", "**/.DS_Store")

	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		return fmt.Errorf("error syncing pkgs directory to S3: %v", err)
	}
	fmt.Println("Upload to S3 completed successfully")
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
	
	// After creating pkgs and pkgsinfo, upload to S3 if bucket is set
	if err := uploadToS3(configData.CloudBucket); err != nil {
	    fmt.Printf("Error uploading to S3: %s\n", err)
	}

}
