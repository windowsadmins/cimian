package main

import (
	"crypto/sha256"
	"gopkg.in/yaml.v3"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
	"os/exec"
	"runtime"
    "gopkg.in/yaml.v3"
    "io/ioutil"
    "path/filepath"
    "os"
    "fmt"
)

type PkgInfo struct {
    Name               string   `yaml:"name"`
    Version            string   `yaml:"version"`
    InstallerItemHash  string   `yaml:"installer_item_hash"`
    InstallerItemPath  string   `yaml:"installer_item_location"`
    Catalogs           []string `yaml:"catalogs"`
    // Add other fields as necessary
}

// scanRepo scans the pkginfo directory recursively to find existing pkginfo files.
func scanRepo(repoPath string) ([]PkgInfo, error) {
    var pkgInfos []PkgInfo

    // Walk through all files in the repo directory
    err := filepath.Walk(repoPath, func(path string, info os.FileInfo, err error) error {
        if err != nil {
            return err
        }

        // Only process YAML files
        if filepath.Ext(path) == ".yaml" {
            fileContent, err := ioutil.ReadFile(path)
            if err != nil {
                return err
            }

            // Parse the YAML file into a PkgInfo struct
            var pkgInfo PkgInfo
            if err := yaml.Unmarshal(fileContent, &pkgInfo); err != nil {
                return err
            }

            // Append the parsed PkgInfo to the list
            pkgInfos = append(pkgInfos, pkgInfo)
        }
        return nil
    })

    if err != nil {
        return nil, err
    }
    return pkgInfos, nil
}

// findMatchingItem looks for an item with the same name and/or version.
func findMatchingItem(pkgInfos []PkgInfo, name string, version string) *PkgInfo {
    for _, item := range pkgInfos {
        if item.Name == name && item.Version == version {
            return &item
        }
    }
    return nil
}

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

// loadConfig loads the configuration from a plist or YAML file
func loadConfig(configPath string) (Config, error) {
	var config Config

	if runtime.GOOS == "darwin" {
		// Load plist using macOS native command
		config = defaultConfig
		cmd := exec.Command("defaults", "read", configPath[:len(configPath)-6], "repo_path")
		output, err := cmd.Output()
		if err == nil {
			config.RepoPath = strings.TrimSpace(string(output))
		}

		cmd = exec.Command("defaults", "read", configPath[:len(configPath)-6], "default_version")
		output, err = cmd.Output()
		if err == nil {
			config.DefaultVersion = strings.TrimSpace(string(output))
		}

		cmd = exec.Command("defaults", "read", configPath[:len(configPath)-6], "output_dir")
		output, err = cmd.Output()
		if err == nil {
			config.OutputDir = strings.TrimSpace(string(output))
		}

		cmd = exec.Command("defaults", "read", configPath[:len(configPath)-6], "pkginfo_editor")
		output, err = cmd.Output()
		if err == nil {
			config.PkginfoEditor = strings.TrimSpace(string(output))
		}

		cmd = exec.Command("defaults", "read", configPath[:len(configPath)-6], "default_catalog")
		output, err = cmd.Output()
		if err == nil {
			config.DefaultCatalog = strings.TrimSpace(string(output))
		}
	} else {
		// Load YAML config
		file, err := os.Open(configPath)
		if err != nil {
			return config, err
		}
		defer file.Close()

		yamlDecoder := yaml.NewDecoder(file)
		if err := yamlDecoder.Decode(&config); err != nil {
			return config, err
		}
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

// copyFile copies a file from src to dst, creating directories as needed.
// If dst already exists, it will be overwritten.
func copyFile(src, dst string) (int64, error) {
    // Create the directory if it doesn't exist
    destDir := filepath.Dir(dst)
    if _, err := os.Stat(destDir); os.IsNotExist(err) {
        if err := os.MkdirAll(destDir, 0755); err != nil {
            return 0, fmt.Errorf("failed to create directory '%s': %v", destDir, err)
        }
    }

    // Open the source file
    sourceFile, err := os.Open(src)
    if err != nil {
        return 0, err
    }
    defer sourceFile.Close()

    // Create the destination file
    destFile, err := os.Create(dst)
    if err != nil {
        return 0, err
    }
    defer destFile.Close()

    // Copy the contents from the source file to the destination file
    nBytes, err := io.Copy(destFile, sourceFile)
    if err != nil {
        return 0, err
    }

    // Sync the destination file to ensure all changes are flushed to disk
    err = destFile.Sync()
    return nBytes, err
}

// gorillaImport handles the overall process of importing a package and generating a pkginfo file.
// gorillaImport handles the overall process of importing a package and generating a pkginfo file.
func gorillaImport(packagePath string, config Config) error {
    packageName := filepath.Base(packagePath)
    
    // Check if the package path exists
    if _, err := os.Stat(packagePath); os.IsNotExist(err) {
        fmt.Printf("Package '%s' does not exist.\n", packagePath)
        return fmt.Errorf("package '%s' does not exist", packagePath)
    }
    
    fmt.Printf("Package path '%s' exists.\n", packagePath)
    
    // Scan the repo for existing pkginfo items
    pkgInfos, err := scanRepo(config.RepoPath)
    if err != nil {
        fmt.Printf("Failed to scan repo: %v\n", err)
        return err
    }
    
    // Check for a matching item in the repo
    matchingItem := findMatchingItem(pkgInfos, packageName, config.DefaultVersion)
    if matchingItem != nil {
        fmt.Printf("This item is similar to an existing item in the repo:\n")
        fmt.Printf("    Item name: %s\n", matchingItem.Name)
        fmt.Printf("    Version: %s\n", matchingItem.Version)
        fmt.Printf("    Installer item path: %s\n", matchingItem.InstallerItemPath)
        
        // Check if the new package is identical to the existing one
        newItemHash, hashErr := calculateSHA256(packagePath)
        if hashErr != nil {
            fmt.Printf("Error calculating hash: %v\n", hashErr)
            return hashErr
        }
        
        if matchingItem.InstallerItemHash == newItemHash {
            fmt.Println("*** This item is identical to an existing item in the repo ***")
            if !confirmAction("Import this item anyway?") {
                fmt.Println("Import canceled.")
                return nil
            }
        }
        
        if confirmAction("Use existing item as a template?") {
            // Copy relevant fields from the existing item
            fmt.Println("Copying attributes from the existing item...")
            config.DefaultCatalog = matchingItem.Catalogs[0]
        }
    }
    
    // Collect metadata from the user
    fmt.Printf("Item name [default: %s]: ", packageName)
    var itemName string
    fmt.Scanln(&itemName)
    if itemName == "" {
        itemName = packageName
    }
    fmt.Printf("Item name set to: %s\n", itemName)

    fmt.Printf("Version [default: %s]: ", config.DefaultVersion)
    var version string
    fmt.Scanln(&version)
    if version == "" {
        version = config.DefaultVersion
    }
    fmt.Printf("Version set to: %s\n", version)

    fmt.Printf("Category [default: %s]: ", "Apps")
    var category string
    fmt.Scanln(&category)
    if category == "" {
        category = "Apps"
    }
    fmt.Printf("Category set to: %s\n", category)

    fmt.Printf("Developer [default: %s]: ", "Unknown")
    var developer string
    fmt.Scanln(&developer)
    if developer == "" {
        developer = "Unknown"
    }
    fmt.Printf("Developer set to: %s\n", developer)

    // Define paths for pkginfo and pkgs
    pkgsPath := filepath.Join("pkgs", "apps", strings.ToLower(developer), fmt.Sprintf("%s-%s", itemName, version))
    pkginfoPath := filepath.Join("pkginfo", "apps", strings.ToLower(developer), fmt.Sprintf("%s-%s.yaml", itemName, version))

    // Create directories as needed
    if err := os.MkdirAll(filepath.Dir(pkgsPath), 0755); err != nil {
        fmt.Printf("Failed to create directory: %v\n", err)
        return err
    }
    if err := os.MkdirAll(filepath.Dir(pkginfoPath), 0755); err != nil {
        fmt.Printf("Failed to create directory: %v\n", err)
        return err
    }

    // Copy package to the repository
    fmt.Printf("Copying package to %s...\n", pkgsPath)
    if _, err := copyFile(packagePath, pkgsPath); err != nil {
        fmt.Printf("Failed to copy package to repo: %v\n", err)
        return fmt.Errorf("failed to copy package to repo: %v", err)
    }
    fmt.Printf("Package copied to repository: %s\n", pkgsPath)

    // Create pkginfo for the item
    fmt.Printf("Creating pkginfo at %s...\n", pkginfoPath)
    if err := createPkgInfo(packagePath, pkginfoPath, version, config.DefaultCatalog); err != nil {
        fmt.Printf("Failed to create pkginfo: %v\n", err)
        return fmt.Errorf("failed to create pkginfo: %v", err)
    }
    fmt.Printf("Pkginfo created at: %s\n", pkginfoPath)

    // Prompt for catalog rebuild
    if confirmAction("Rebuild catalogs?") {
        fmt.Println("Rebuilding catalogs...")
        rebuildCatalogs()
    }

    return nil
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
	configPath := getConfigPath()

	if _, err := os.Stat(configPath); err == nil {
		if runtime.GOOS == "darwin" || runtime.GOOS == "windows" {
			loadedConfig, err := loadConfig(configPath)
			if err == nil {
				// Merge loadedConfig with defaultConfig
				if loadedConfig.RepoPath != "" {
					configData.RepoPath = loadedConfig.RepoPath
				}
				if loadedConfig.DefaultVersion != "" {
					configData.DefaultVersion = loadedConfig.DefaultVersion
				}
				if loadedConfig.OutputDir != "" {
					configData.OutputDir = loadedConfig.OutputDir
				}
				if loadedConfig.PkginfoEditor != "" {
					configData.PkginfoEditor = loadedConfig.PkginfoEditor
				}
				if loadedConfig.DefaultCatalog != "" {
					configData.DefaultCatalog = loadedConfig.DefaultCatalog
				}
			}
		}
	}
	
	// Check if a package path was provided via command-line argument
	var packagePath string
	if flag.NArg() > 0 {
	    packagePath = flag.Arg(0)
	} else {
	    // If no argument, prompt the user
	    fmt.Printf("Enter the path to the package file to import: ")
	    fmt.Scanln(&packagePath)
	}
	
	// Ensure the provided path is a file
	fileInfo, err := os.Stat(packagePath)
	if err != nil {
	    fmt.Printf("Error: %s\n", err)
	    os.Exit(1)
	}
	if fileInfo.IsDir() {
	    fmt.Println("Error: The provided path is a directory. Please provide a valid package file, not a directory.")
	    os.Exit(1)
	}

	// Call gorillaImport to handle the import process
	if err := gorillaImport(packagePath, configData); err != nil {
		fmt.Printf("Error: %s\n", err)
		os.Exit(1)
	}
}
