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
	"time"

	"gopkg.in/yaml.v3"
)

// Configuration structure to hold settings
type Config struct {
	RepoPath       string `yaml:"repo_path"`
	DefaultVersion string `yaml:"default_version"`
	OutputDir      string `yaml:"output_dir"`
	PkginfoEditor  string `yaml:"pkginfo_editor"`
	DefaultCatalog string `yaml:"default_catalog"`
}

// Default configuration values
var defaultConfig = Config{
	RepoPath:       "./repo",
	DefaultVersion: "1.0.0",
	OutputDir:      "./pkginfo",
	PkginfoEditor:  "",
	DefaultCatalog: "testing",
}

// getConfigPath returns the appropriate configuration file path based on the OS
func getConfigPath() string {
	if runtime.GOOS == "darwin" {
		return filepath.Join(os.Getenv("HOME"), "Library/Preferences/com.github.gorilla.import.plist")
	} else if runtime.GOOS == "windows" {
		return filepath.Join(os.Getenv("APPDATA"), "Gorilla", "import.yaml")
	}
	return "config.yaml"
}

// configureGorillaImport interactively configures gorillaimport settings
func configureGorillaImport() Config {
	config := defaultConfig

	fmt.Println("Configuring gorillaimport...")

	fmt.Printf("Repo URL (default: %s): ", config.RepoPath)
	fmt.Scanln(&config.RepoPath)
	if config.RepoPath == "" {
		config.RepoPath = defaultConfig.RepoPath
	}

	fmt.Printf("Pkginfo extension (default: .yaml): ")
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

	// Save the configuration to the appropriate path
	err := saveConfig(getConfigPath(), config)
	if err != nil {
		fmt.Printf("Error saving config: %s\n", err)
	}

	return config
}

// saveConfig saves the configuration to a plist or YAML file
func saveConfig(configPath string, config Config) error {
	if runtime.GOOS == "darwin" {
		// Save as plist using native macOS command
		cmd := exec.Command("defaults", "write", configPath[:len(configPath)-6], "repo_path", config.RepoPath)
		if err := cmd.Run(); err != nil {
			return err
		}
		cmd = exec.Command("defaults", "write", configPath[:len(configPath)-6], "default_version", config.DefaultVersion)
		if err := cmd.Run(); err != nil {
			return err
		}
		cmd = exec.Command("defaults", "write", configPath[:len(configPath)-6], "output_dir", config.OutputDir)
		if err := cmd.Run(); err != nil {
			return err
		}
		cmd = exec.Command("defaults", "write", configPath[:len(configPath)-6], "pkginfo_editor", config.PkginfoEditor)
		if err := cmd.Run(); err != nil {
			return err
		}
		cmd = exec.Command("defaults", "write", configPath[:len(configPath)-6], "default_catalog", config.DefaultCatalog)
		return cmd.Run()
	} else {
		// Save as YAML
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

// createPkgInfo generates a pkginfo YAML file for the provided package.
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
		"name":                    filepath.Base(filePath),              // Use the file name as the package name
		"version":                 version,                              // Use the provided version
		"installer_item_hash":     hash,                                 // The calculated hash of the package file
		"installer_item_size":     fileInfo.Size(),                      // Size of the package file in bytes
		"description":             "Package imported via GorillaImport", // Description of the package
		"import_date":             time.Now().Format(time.RFC3339),      // Timestamp of the import
		"catalogs":                []string{catalog},
		"category":                "Apps",
		"developer":               "Unknown",
		"supported_architectures": []string{"x86_64", "arm64"},
		"unattended_install":      true,
		"unattended_uninstall":    false,
	}

	// Handle special case for PowerShell scripts (.ps1)
	if filepath.Ext(filePath) == ".ps1" {
		pkgInfo["post_install_script"] = filepath.Base(filePath)
		pkgInfo["nopkg"] = true
	}

	// Define the output path for the pkginfo file
	outputPath := filepath.Join(outputDir, filepath.Base(filePath)+".yaml")

	// Create the pkginfo file for writing
	outputFile, err := os.Create(outputPath)
	if err != nil {
		return err
	}
	defer outputFile.Close() // Ensure the file is closed when the function exits

	// Encode the pkginfo map as YAML and write it to the file
	encoder := yaml.NewEncoder(outputFile)
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
			pkginfoPath := filepath.Join(config.OutputDir, filepath.Base(packagePath)+".yaml")
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
	// Define command-line flag for config
	config := flag.Bool("config", false, "Run interactive configuration setup.")
	flag.Parse() // Parse the command-line flags

	// Run configuration if requested
	if *config {
		configureGorillaImport()
		return
	}

	// Load configuration from default path
	configData := defaultConfig

	// Ensure package argument is provided by prompting the user
	fmt.Printf("Enter the path to the package to import: ")
	var packagePath string
	fmt.Scanln(&packagePath)

	if packagePath == "" {
		fmt.Println("Error: Package argument is required.")
		flag.Usage()
		os.Exit(1)
	}

	// Call gorillaImport to handle the import process
	if err := gorillaImport(packagePath, configData); err != nil {
		fmt.Printf("Error: %s\n", err)
		os.Exit(1)
	}
}
