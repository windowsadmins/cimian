package main

import (
	"crypto/sha256"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
	"os/exec"
	"runtime"
)

// Configuration structure to hold settings
type Config struct {
	RepoPath       string `json:"repo_path"`
	DefaultVersion string `json:"default_version"`
	OutputDir      string `json:"output_dir"`
	PkginfoEditor  string `json:"pkginfo_editor"`
	DefaultCatalog string `json:"default_catalog"`
}

// Default configuration values
var defaultConfig = Config{
	RepoPath:       "./repo",
	DefaultVersion: "1.0.0",
	OutputDir:      "./pkginfo",
	PkginfoEditor:  "",
	DefaultCatalog: "production",
}

// getConfigPath returns the appropriate configuration file path based on the OS
func getConfigPath() string {
	if runtime.GOOS == "darwin" {
		return filepath.Join(os.Getenv("HOME"), "Library/Preferences/com.github.gorilla.import.plist")
	} else if runtime.GOOS == "windows" {
		return filepath.Join(os.Getenv("APPDATA"), "Gorilla", "import.json")
	}
	return "config.json"
}

// loadConfig loads the configuration from a JSON file or returns default settings
func loadConfig(configPath string) (Config, error) {
	config := defaultConfig

	file, err := os.Open(configPath)
	if err != nil {
		if os.IsNotExist(err) {
			// If the config file does not exist, return the default config
			return config, nil
		}
		return config, err
	}
	defer file.Close()

	decoder := json.NewDecoder(file)
	if err := decoder.Decode(&config); err != nil {
		return config, err
	}

	return config, nil
}

// saveConfig saves the configuration to a JSON file
func saveConfig(configPath string, config Config) error {
	if runtime.GOOS == "darwin" {
		configPath = filepath.Join(os.Getenv("HOME"), "Library/Preferences/com.github.gorilla.import.plist")
	} else if runtime.GOOS == "windows" {
		configPath = filepath.Join(os.Getenv("APPDATA"), "gorilla", "import.json")
	}

	file, err := os.Create(configPath)
	if err != nil {
		return err
	}
	defer file.Close()

	encoder := json.NewEncoder(file)
	encoder.SetIndent("", "    ")
	return encoder.Encode(config)
}

// configureGorillaImport interactively configures gorillaimport settings
func configureGorillaImport(configPath string) error {
	config := defaultConfig

	fmt.Println("Configuring gorillaimport...")

	fmt.Printf("Repo URL (default: %s): ", config.RepoPath)
	fmt.Scanln(&config.RepoPath)
	if config.RepoPath == "" {
		config.RepoPath = defaultConfig.RepoPath
	}

	fmt.Printf("Pkginfo extension (default: .plist): ")
	fmt.Scanln(&config.OutputDir)
	if config.OutputDir == "" {
		config.OutputDir = defaultConfig.OutputDir
	}

	fmt.Printf("Pkginfo editor (example: /usr/bin/vi or TextMate.app): ")
	fmt.Scanln(&config.PkginfoEditor)

	fmt.Printf("Default catalog to use (default: %s): ", config.DefaultCatalog)
	fmt.Scanln(&config.DefaultCatalog)
	if config.DefaultCatalog == "" {
		config.DefaultCatalog = defaultConfig.DefaultCatalog
	}

	return saveConfig(configPath, config)
}

// calculateSHA256 calculates the SHA-256 hash of the given file.
func calculateSHA256(filePath string) (string, error) {
	// Open the file for reading
	file, err := os.Open(filePath)
	if err != nil {
		return "", err
	}
	defer file.Close() // Ensure the file is closed when the function exits

	// Create a new SHA-256 hash object
	hash := sha256.New()

	// Copy the file contents into the hash object
	if _, err := io.Copy(hash, file); err != nil {
		return "", err
	}

	// Return the computed hash as a hexadecimal string
	return fmt.Sprintf("%x", hash.Sum(nil)), nil
}

// createPkgInfo generates a pkginfo JSON file for the provided package.
func createPkgInfo(filePath string, outputDir string, version string, catalog string) error {
	// Calculate the SHA-256 hash of the package file
	hash, err := calculateSHA256(filePath)
	if err != nil {
		return err
	}

	// Get file information (e.g., size)
	fileInfo, err := os.Stat(filePath)
	if err != nil {
		return err
	}

	// Define the pkginfo structure with relevant metadata
	pkgInfo := map[string]interface{}{
		"name":                filepath.Base(filePath), // Use the file name as the package name
		"version":             version,                 // Use the provided version
		"installer_item_hash": hash,                    // The calculated hash of the package file
		"installer_item_size": fileInfo.Size(),         // Size of the package file in bytes
		"description":         "Package imported via GorillaImport", // Description of the package
		"import_date":         time.Now().Format(time.RFC3339),        // Timestamp of the import
		"catalogs":            []string{catalog},
		"category":            "Apps",
		"developer":           "Unknown",
		"supported_architectures": []string{"x86_64", "arm64"},
		"unattended_install":  true,
		"unattended_uninstall": false,
	}

	// Handle special case for PowerShell scripts (.ps1)
	if filepath.Ext(filePath) == ".ps1" {
		pkgInfo["post_install_script"] = filepath.Base(filePath)
		pkgInfo["nopkg"] = true
	}

	// Define the output path for the pkginfo file
	outputPath := filepath.Join(outputDir, filepath.Base(filePath)+".pkginfo")

	// Create the pkginfo file for writing
	outputFile, err := os.Create(outputPath)
	if err != nil {
		return err
	}
	defer outputFile.Close() // Ensure the file is closed when the function exits

	// Encode the pkginfo map as JSON and write it to the file
	encoder := json.NewEncoder(outputFile)
	encoder.SetIndent("", "    ") // Pretty-print the JSON with indentation
	if err := encoder.Encode(pkgInfo); err != nil {
		return err
	}

	// Print a success message with the path of the created pkginfo file
	fmt.Printf("Pkginfo created at: %s\n", outputPath)
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

// gorillaImport handles the overall process of importing a package and generating a pkginfo file.
func gorillaImport(packagePath string, config Config) error {
	// Ensure the package file is of a supported type
	ext := strings.ToLower(filepath.Ext(packagePath))
	if ext != ".msi" && ext != ".ps1" && ext != ".exe" && ext != ".nupkg" {
		return fmt.Errorf("unsupported package type: %s", ext)
	}

	// Check if the package path exists
	if _, err := os.Stat(packagePath); os.IsNotExist(err) {
		return fmt.Errorf("package path '%s' does not exist", packagePath)
	}

	// Confirm action with the user before proceeding
	if !confirmAction(fmt.Sprintf("Are you sure you want to import '%s' into the repository?", packagePath)) {
		fmt.Println("Import canceled.")
		return nil
	}

	// Prompt user for metadata
	fmt.Printf("Item name [default: %s]: ", filepath.Base(packagePath))
	var itemName string
	fmt.Scanln(&itemName)
	if itemName == "" {
		itemName = filepath.Base(packagePath)
	}

	fmt.Printf("Version [default: %s]: ", config.DefaultVersion)
	var version string
	fmt.Scanln(&version)
	if version == "" {
		version = config.DefaultVersion
	}

	fmt.Printf("Description [default: Package imported via GorillaImport]: ")
	var description string
	fmt.Scanln(&description)
	if description == "" {
		description = "Package imported via GorillaImport"
	}

	fmt.Printf("Category [default: Apps]: ")
	var category string
	fmt.Scanln(&category)
	if category == "" {
		category = "Apps"
	}

	fmt.Printf("Developer [default: Unknown]: ")
	var developer string
	fmt.Scanln(&developer)
	if developer == "" {
		developer = "Unknown"
	}

	// Check if the output directory exists, and create it if it does not
	if _, err := os.Stat(config.OutputDir); os.IsNotExist(err) {
		if err := os.MkdirAll(config.OutputDir, 0755); err != nil {
			return err
		}
	}

	// Create the pkginfo file for the specified package
	if err := createPkgInfo(packagePath, config.OutputDir, version, config.DefaultCatalog); err != nil {
		return err
	}

	// Optionally edit pkginfo
	if config.PkginfoEditor != "" {
		if confirmAction("Edit pkginfo before upload?") {
			pkginfoPath := filepath.Join(config.OutputDir, filepath.Base(packagePath)+".pkginfo")
			if strings.HasSuffix(config.PkginfoEditor, ".app") {
				exec.Command("open", "-a", config.PkginfoEditor, pkginfoPath).Run()
			} else {
				exec.Command(config.PkginfoEditor, pkginfoPath).Run()
			}
		}
	}

	// Copy package to the repository's pkgs folder
	repoPkgPath := filepath.Join(config.OutputDir, "..", "pkgs", filepath.Base(packagePath))
	if _, err := copyFile(packagePath, repoPkgPath); err != nil {
		return fmt.Errorf("failed to copy package to repo: %v", err)
	}

	fmt.Printf("Package copied to repository: %s\n", repoPkgPath)

	// Upload pkgs to S3 bucket (example command)
	s3BucketPath := "s3://your-s3-bucket/repo/pkgs/"
	cmd := exec.Command("aws", "s3", "sync", filepath.Join(config.OutputDir, "..", "pkgs/"), s3BucketPath, "--exclude", "*.git/*", "--exclude", "**/.DS_Store")
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("failed to upload to S3: %v", err)
	}

	fmt.Println("Successfully synced pkgs directory to S3")

	return nil
}

// copyFile copies a file from src to dst
func copyFile(src, dst string) (int64, error) {
	sourceFile, err := os.Open(src)
	if err != nil {
		return 0, err
	}
	defer sourceFile.Close()
	destinationFile, err := os.Create(dst)
	if err != nil {
		return 0, err
	}
	defer destinationFile.Close()
	nBytes, err := io.Copy(destinationFile, sourceFile)
	return nBytes, err
}

func main() {
	// Define command-line flags for package path, output directory, and config file
	packagePath := flag.String("package", "", "Path to the package to import.")
	outputDir := flag.String("output", "", "Directory to output pkginfo file.")
	configPath := flag.String("config", getConfigPath(), "Path to configuration file.")
	configure := flag.Bool("configure", false, "Run interactive configuration setup.")
	flag.Parse() // Parse the command-line flags

	// Load configuration if a config path is provided
	config, err := loadConfig(*configPath)
	if err != nil {
		fmt.Printf("Error loading config: %s\n", err)
		os.Exit(1)
	}

	// Run configuration if requested
	if *configure {
		if err := configureGorillaImport(*configPath); err != nil {
			fmt.Printf("Error during configuration: %s\n", err)
			os.Exit(1)
		}
		os.Exit(0)
	}

	// Use command-line arguments if provided, otherwise use config values
	finalOutputDir := config.OutputDir
	if *outputDir != "" {
		finalOutputDir = *outputDir
	}

	// Ensure both package and output arguments are provided
	if *packagePath == "" || finalOutputDir == "" {
		fmt.Println("Error: Both package and output arguments are required.")
		flag.Usage()
		os.Exit(1)
	}

	// Call gorillaImport to handle the import process
	if err := gorillaImport(*packagePath, config); err != nil {
		fmt.Printf("Error: %s\n", err)
		os.Exit(1)
	}
}
