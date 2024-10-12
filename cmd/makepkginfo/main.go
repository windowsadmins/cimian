package main

import (
	"crypto/sha256"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"gopkg.in/yaml.v3"
)

// PkgsInfo represents the package information
type PkgsInfo struct {
	Name                string   `yaml:"name"`
	DisplayName         string   `yaml:"display_name,omitempty"`
	Version             string   `yaml:"version"`
	Catalogs            []string `yaml:"catalogs,omitempty"`
	Category            string   `yaml:"category,omitempty"`
	Description         string   `yaml:"description,omitempty"`
	Developer           string   `yaml:"developer,omitempty"`
	InstallerType       string   `yaml:"installer_type,omitempty"`
	InstallerItemHash   string   `yaml:"installer_item_hash,omitempty"`
	InstallerItemSize   int64    `yaml:"installer_item_size,omitempty"`
	InstallerItemLocation string `yaml:"installer_item_location,omitempty"`
	UnattendedInstall   bool     `yaml:"unattended_install,omitempty"`
	Installs            []string `yaml:"installs,omitempty"`
	InstallCheckScript  string   `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string  `yaml:"uninstallcheck_script,omitempty"`
	PreinstallScript    string   `yaml:"preinstall_script,omitempty"`
	PostinstallScript   string   `yaml:"postinstall_script,omitempty"`
}

// Helper function to execute a command and return its output
func execCommand(name string, arg ...string) (string, error) {
	cmd := exec.Command(name, arg...)
	out, err := cmd.Output()
	if err != nil {
		return "", err
	}
	return string(out), nil
}

// Function to extract metadata from an MSI installer (Windows-only)
func extractMSIMetadata(msiPath string) (string, string, string, error) {
    return extractMSIMetadataWindows(msiPath)
}

// Windows-specific MSI metadata extraction using PowerShell
func extractMSIMetadataWindows(msiPath string) (string, string, string, error) {
	output, err := execCommand("powershell", "-Command", fmt.Sprintf("Get-MSIInfo -FilePath %s", msiPath))
	if err != nil {
		return "", "", "", fmt.Errorf("error extracting MSI metadata: %v", err)
	}
	return parseMSIInfoOutput(output)
}

// Function to parse MSI metadata output (works for both platforms)
func parseMSIInfoOutput(output string) (string, string, string, error) {
	var productName, version, manufacturer string
	lines := strings.Split(output, "\n")
	for _, line := range lines {
		if strings.Contains(line, "ProductName") {
			productName = strings.TrimSpace(strings.Split(line, ":")[1])
		}
		if strings.Contains(line, "ProductVersion") {
			version = strings.TrimSpace(strings.Split(line, ":")[1])
		}
		if strings.Contains(line, "Manufacturer") {
			manufacturer = strings.TrimSpace(strings.Split(line, ":")[1])
		}
	}

	if productName == "" || version == "" || manufacturer == "" {
		return "", "", "", fmt.Errorf("failed to extract MSI metadata")
	}

	return productName, version, manufacturer, nil
}

// Function to calculate file size and hash
func getFileInfo(pkgPath string) (int64, string, error) {
	fileInfo, err := os.Stat(pkgPath)
	if err != nil {
		return 0, "", fmt.Errorf("error stating file: %v", err)
	}
	fileSize := fileInfo.Size()
	file, err := os.Open(pkgPath)
	if err != nil {
		return 0, "", fmt.Errorf("error opening file: %v", err)
	}
	defer file.Close()
	hash := sha256.New()
	if _, err := io.Copy(hash, file); err != nil {
		return 0, "", fmt.Errorf("error calculating file hash: %v", err)
	}
	return fileSize, fmt.Sprintf("%x", hash.Sum(nil)), nil
}

// Main function
func main() {
	// Command-line flags
	var (
		installCheckScript   string
		uninstallCheckScript string
		preinstallScript     string
		postinstallScript    string
		catalogs             string
		category             string
		developer            string
		name                 string
		displayName          string
		description          string
		unattendedInstall    bool
	)
	flag.StringVar(&installCheckScript, "installcheck_script", "", "Path to install check script")
	flag.StringVar(&uninstallCheckScript, "uninstallcheck_script", "", "Path to uninstall check script")
	flag.StringVar(&preinstallScript, "preinstall_script", "", "Path to preinstall script")
	flag.StringVar(&postinstallScript, "postinstall_script", "", "Path to postinstall script")
	flag.StringVar(&catalogs, "catalogs", "", "Comma-separated list of catalogs")
	flag.StringVar(&category, "category", "", "Category")
	flag.StringVar(&developer, "developer", "", "Developer")
	flag.StringVar(&name, "name", "", "Name of the package")
	flag.StringVar(&displayName, "displayname", "", "Display name")
	flag.StringVar(&description, "description", "", "Description")
	flag.BoolVar(&unattendedInstall, "unattended_install", false, "Set unattended_install to true")
	flag.Parse()

	if flag.NArg() < 1 {
		fmt.Println("Usage: makepkginfo [options] /path/to/installer.msi")
		flag.PrintDefaults()
		os.Exit(1)
	}

	installerItem := flag.Arg(0)
	installerItem = strings.TrimSuffix(installerItem, "/")

	// Extract MSI metadata
	productName, version, manufacturer, err := extractMSIMetadata(installerItem)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error extracting MSI metadata: %v\n", err)
		os.Exit(1)
	}

	// Get file size and hash
	fileSize, fileHash, err := getFileInfo(installerItem)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error getting file info: %v\n", err)
		os.Exit(1)
	}

	// Build pkgsinfo
	pkgsinfo := PkgsInfo{
		Name:                 productName,
		DisplayName:          displayName,
		Version:              version,
		Catalogs:             strings.Split(catalogs, ","),
		Category:             category,
		Developer:            manufacturer,
		Description:          description,
		InstallerType:        "msi",
		InstallerItemLocation: filepath.Base(installerItem),
		InstallerItemSize:    fileSize / 1024, // Size in KB
		InstallerItemHash:    fileHash,
		UnattendedInstall:    unattendedInstall,
	}

	// Handle scripts
	if installCheckScript != "" {
		content, err := os.ReadFile(installCheckScript)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error reading installcheck script: %v\n", err)
			os.Exit(1)
		}
		pkgsinfo.InstallCheckScript = string(content)
	}
	if uninstallCheckScript != "" {
		content, err := os.ReadFile(uninstallCheckScript)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error reading uninstallcheck script: %v\n", err)
			os.Exit(1)
		}
		pkgsinfo.UninstallCheckScript = string(content)
	}
	if preinstallScript != "" {
		content, err := os.ReadFile(preinstallScript)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error reading preinstall script: %v\n", err)
			os.Exit(1)
		}
		pkgsinfo.PreinstallScript = string(content)
	}
	if postinstallScript != "" {
		content, err := os.ReadFile(postinstallScript)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error reading postinstall script: %v\n", err)
			os.Exit(1)
		}
		pkgsinfo.PostinstallScript = string(content)
	}

	// Output pkgsinfo as YAML
	yamlData, err := yaml.Marshal(&pkgsinfo)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error marshaling YAML: %v\n", err)
		os.Exit(1)
	}
	fmt.Println(string(yamlData))
}
