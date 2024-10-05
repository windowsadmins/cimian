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
)

// Configuration structure to hold settings
type Config struct {
	RepoPath       string `json:"repo_path"`
	DefaultVersion string `json:"default_version"`
	OutputDir      string `json:"output_dir"`
}

// Default configuration values
var defaultConfig = Config{
	RepoPath:       "./repo",
	DefaultVersion: "1.0.0",
	OutputDir:      "./pkginfo",
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
func createPkgInfo(filePath string, outputDir string, version string) error {
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
		"name":    filepath.Base(filePath), // Use the file name as the package name
		"version": version,                 // Use the provided version
		"installer_item_hash": hash,        // The calculated hash of the package file
		"installer_item_size": fileInfo.Size(), // Size of the package file in bytes
		"description":         "Package imported via GorillaImport", // Description of the package
		"import_date":         time.Now().Format(time.RFC3339),        // Timestamp of the import
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
func gorillaImport(packagePath string, outputDir string, version string) error {
	// Check if the package path exists
	if _, err := os.Stat(packagePath); os.IsNotExist(err) {
		return fmt.Errorf("package path '%s' does not exist", packagePath)
	}

	// Confirm action with the user before proceeding
	if !confirmAction(fmt.Sprintf("Are you sure you want to import '%s' into the repository?", packagePath)) {
		fmt.Println("Import canceled.")
		return nil
	}

	// Check if the output directory exists, and create it if it does not
	if _, err := os.Stat(outputDir); os.IsNotExist(err) {
		if err := os.MkdirAll(outputDir, 0755); err != nil {
			return err
		}
	}

	// Create the pkginfo file for the specified package
	if err := createPkgInfo(packagePath, outputDir, version); err != nil {
		return err
	}

	// Copy package to the repository's pkgs folder
	repoPkgPath := filepath.Join(outputDir, "..", "pkgs", filepath.Base(packagePath))
	if _, err := copyFile(packagePath, repoPkgPath); err != nil {
		return fmt.Errorf("failed to copy package to repo: %v", err)
	}

	fmt.Printf("Package copied to repository: %s\n", repoPkgPath)

	// Upload pkgs to S3 bucket (example command)
	s3BucketPath := "s3://your-s3-bucket/repo/pkgs/"
	cmd := exec.Command("aws", "s3", "sync", filepath.Join(outputDir, "..", "pkgs/"), s3BucketPath, "--exclude", "*.git/*", "--exclude", "**/.DS_Store")
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
	configPath := flag.String("config", "", "Path to configuration file.")
	flag.Parse() // Parse the command-line flags

	// Load configuration if a config path is provided
	config, err := loadConfig(*configPath)
	if err != nil {
		fmt.Printf("Error loading config: %s\n", err)
		os.Exit(1)
	}

	// Use command-line arguments if provided, otherwise use config values
	finalOutputDir := config.OutputDir
	if *outputDir != "" {
		finalOutputDir = *outputDir
	}

	// Ensure both package and output arguments are provided
	if *packagePath == "" || finalOutputDir == "" {
		fmt.Println("Error: Both package and output arguments are required.")
}
