// cmd/makepkginfo/main.go

package main

import (
	"crypto/md5"
	"crypto/sha256"
	"encoding/hex"
	"flag"
	"fmt"
	"io"
	"io/ioutil"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"gopkg.in/yaml.v3"

	"github.com/windowsadmins/gorilla/pkg/version"
)

// Struct for "installs" array
type InstallItem struct {
	Type        SingleQuotedString `yaml:"type"`
	Path        SingleQuotedString `yaml:"path"`
	MD5Checksum SingleQuotedString `yaml:"md5checksum,omitempty"`
	Version     SingleQuotedString `yaml:"version,omitempty"`
}

// SingleQuotedString forces single quotes in YAML output.
type SingleQuotedString string

func (s SingleQuotedString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Style: yaml.SingleQuotedStyle, // Force single quotes
		Value: string(s),
	}
	return node, nil
}

// PkgsInfo represents the package information
type PkgsInfo struct {
	Name                  string        `yaml:"name"`
	DisplayName           string        `yaml:"display_name,omitempty"`
	Version               string        `yaml:"version"`
	Catalogs              []string      `yaml:"catalogs,omitempty"`
	Category              string        `yaml:"category,omitempty"`
	Description           string        `yaml:"description,omitempty"`
	Developer             string        `yaml:"developer,omitempty"`
	InstallerType         string        `yaml:"installer_type,omitempty"`
	InstallerItemHash     string        `yaml:"installer_item_hash,omitempty"`
	InstallerItemSize     int64         `yaml:"installer_item_size,omitempty"`
	InstallerItemLocation string        `yaml:"installer_item_location,omitempty"`
	UnattendedInstall     bool          `yaml:"unattended_install,omitempty"`
	Installs              []InstallItem `yaml:"installs,omitempty"`
	InstallCheckScript    string        `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript  string        `yaml:"uninstallcheck_script,omitempty"`
	PreinstallScript      string        `yaml:"preinstall_script,omitempty"`
	PostinstallScript     string        `yaml:"postinstall_script,omitempty"`
}

// NoQuoteString ensures empty strings appear without quotes.
// (Kept from your original code; used if needed.)
type NoQuoteString string

func (s NoQuoteString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Value: string(s),
	}
	return node, nil
}

// wrapperPkgsInfo preserves field order and removes quotes for empty strings
type wrapperPkgsInfo struct {
	Name                 NoQuoteString `yaml:"name"`
	DisplayName          NoQuoteString `yaml:"display_name"`
	Version              NoQuoteString `yaml:"version"`
	Catalogs             []string      `yaml:"catalogs"`
	Category             NoQuoteString `yaml:"category"`
	Description          NoQuoteString `yaml:"description"`
	Developer            NoQuoteString `yaml:"developer"`
	UnattendedInstall    bool          `yaml:"unattended_install"`
	InstallCheckScript   NoQuoteString `yaml:"installcheck_script"`
	UninstallCheckScript NoQuoteString `yaml:"uninstallcheck_script"`
	PreinstallScript     NoQuoteString `yaml:"preinstall_script"`
	PostinstallScript    NoQuoteString `yaml:"postinstall_script"`
}

// Config represents the configuration structure
type Config struct {
	RepoPath string `yaml:"repo_path"`
}

// LoadConfig loads the configuration from the given path
func LoadConfig(configPath string) (Config, error) {
	var config Config
	data, err := ioutil.ReadFile(configPath)
	if err != nil {
		return config, fmt.Errorf("failed to read config file: %v", err)
	}
	err = yaml.Unmarshal(data, &config)
	if err != nil {
		return config, fmt.Errorf("failed to unmarshal config: %v", err)
	}
	return config, nil
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
	output, err := execCommand("powershell", "-Command", fmt.Sprintf("Get-MSIInfo -FilePath '%s'", msiPath))
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
			productName = strings.TrimSpace(strings.SplitN(line, ":", 2)[1])
		}
		if strings.Contains(line, "ProductVersion") {
			version = strings.TrimSpace(strings.SplitN(line, ":", 2)[1])
		}
		if strings.Contains(line, "Manufacturer") {
			manufacturer = strings.TrimSpace(strings.SplitN(line, ":", 2)[1])
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

// SavePkgsInfo saves a pkgsinfo back to its YAML file.
func SavePkgsInfo(pkgsinfoPath string, pkgsinfo PkgsInfo) error {
	data, err := yaml.Marshal(pkgsinfo)
	if err != nil {
		return err
	}
	return os.WriteFile(pkgsinfoPath, data, 0644)
}

// CreateNewPkgsInfo creates a new pkgsinfo file.
func CreateNewPkgsInfo(pkgsinfoPath, name string) error {
	newPkgsInfo := PkgsInfo{
		Name:              name,
		Version:           time.Now().Format("2006.01.02"),
		Catalogs:          []string{"Testing"},
		UnattendedInstall: true,
	}

	// Copy fields to wrapperPkgsInfo
	wrapped := wrapperPkgsInfo{
		Name:                 NoQuoteString(newPkgsInfo.Name),
		DisplayName:          "",
		Version:              NoQuoteString(newPkgsInfo.Version),
		Catalogs:             newPkgsInfo.Catalogs,
		Category:             "",
		Description:          "",
		Developer:            "",
		UnattendedInstall:    newPkgsInfo.UnattendedInstall,
		InstallCheckScript:   "",
		UninstallCheckScript: "",
		PreinstallScript:     "",
		PostinstallScript:    "",
	}

	data, err := yaml.Marshal(&wrapped)
	if err != nil {
		return err
	}

	return os.WriteFile(pkgsinfoPath, data, 0644)
}

// Helper to compute MD5 of a file, for building `installs:` items.
func fileMD5(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()

	h := md5.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// If you want to detect EXE versions or read metadata, add a function here:
func getFileVersion(path string) (string, error) {
	// For a real implementation, you might parse the Windows version resource.
	// For now, we’ll just return "" so no version is set.
	return "", nil
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
		newPkg               bool
	)
	// Add a multi-flag for `-f`
	var filePaths multiStringSlice

	showVersion := flag.Bool("version", false, "Print the version and exit.")
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
	flag.BoolVar(&newPkg, "new", false, "Create a new pkgsinfo with all possible keys")

	// New: our -f flag
	// Example: -f "C:\path\to\somefile.exe" -f "C:\path\to\some.dll"
	flag.Var(&filePaths, "f", "Add an 'installs' item for each file/directory (multiple -f flags allowed)")

	flag.Parse()

	// Handle --version flag
	if *showVersion {
		version.Print()
		return
	}

	// Load config
	config, err := LoadConfig(`C:\ProgramData\ManagedInstalls\Config.yaml`)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error loading config: %v\n", err)
		os.Exit(1)
	}

	// Handle --new
	if newPkg {
		if flag.NArg() < 1 {
			fmt.Println("Usage: makepkginfo --new PkginfoName")
			flag.PrintDefaults()
			os.Exit(1)
		}
		pkgsinfoName := flag.Arg(0)
		pkgsinfoPath := filepath.Join(config.RepoPath, "pkgsinfo", pkgsinfoName+".yaml")
		err := CreateNewPkgsInfo(pkgsinfoPath, pkgsinfoName)
		if err != nil {
			fmt.Println("Error creating pkgsinfo:", err)
			return
		}
		fmt.Println("New pkgsinfo created:", pkgsinfoPath)
		return
	}

	// If we are not creating a new pkg, we expect at least one argument, e.g. the MSI path
	if flag.NArg() < 1 {
		// If the user only wants to do `-f` checks but no MSI, that's also valid
		// so we won't forcibly exit if there's no MSI. But let's see if they gave us one.
		if len(filePaths) == 0 {
			fmt.Println("Usage: makepkginfo [options] /path/to/installer.msi -f path1 -f path2 ...")
			flag.PrintDefaults()
			os.Exit(1)
		}
	}

	// Build a pkgsinfo struct
	pkgsinfo := PkgsInfo{
		Catalogs:          strings.Split(catalogs, ","),
		Category:          category,
		Developer:         developer,
		DisplayName:       displayName,
		Description:       description,
		UnattendedInstall: unattendedInstall,
		Installs:          []InstallItem{},
	}

	// If we have an MSI or other installer path, parse it
	var installerItem string
	if flag.NArg() >= 1 {
		installerItem = flag.Arg(0)
		installerItem = strings.TrimSuffix(installerItem, "/")

		// Attempt to extract MSI metadata. If that fails, we might just ignore it.
		productName, pkgVersion, manufacturer, msiErr := extractMSIMetadata(installerItem)
		if msiErr == nil {
			// We got MSI data
			if name == "" {
				pkgsinfo.Name = productName
			} else {
				pkgsinfo.Name = name
			}
			if pkgsinfo.DisplayName == "" {
				pkgsinfo.DisplayName = productName
			}
			if pkgsinfo.Version == "" {
				pkgsinfo.Version = pkgVersion
			}
			if pkgsinfo.Developer == "" {
				pkgsinfo.Developer = manufacturer
			}
			pkgsinfo.InstallerType = "msi"

			// file size & hash
			fileSize, fileHash, err := getFileInfo(installerItem)
			if err == nil {
				pkgsinfo.InstallerItemSize = fileSize / 1024 // size in KB
				pkgsinfo.InstallerItemHash = fileHash
				pkgsinfo.InstallerItemLocation = filepath.Base(installerItem)
			}
		} else {
			// If user provided a name with --name, use that:
			if name != "" {
				pkgsinfo.Name = name
			} else {
				pkgsinfo.Name = "UnknownPackage"
			}
			// no version or developer from MSI
			pkgsinfo.Version = time.Now().Format("2006.01.02.1504")
			pkgsinfo.InstallerType = "exe" // guess
		}
	}

	// Now handle the -f file array
	// For each file, gather MD5 + optional version, build an 'installs' item
	for _, fpath := range filePaths {
		fullpath, _ := filepath.Abs(fpath)
		fi, err := os.Stat(fullpath)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Warning: skipping -f %s, error: %v\n", fpath, err)
			continue
		}

		if fi.IsDir() {
			// If it's a dir, you can recursively gather files or skip.
			fmt.Fprintf(os.Stderr, "Skipping directory for now: %s\n", fpath)
			continue
		}

		md5sum, err := fileMD5(fullpath)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Warning: cannot compute MD5 for %s: %v\n", fpath, err)
		}

		fversion, _ := getFileVersion(fullpath)

		// Add to pkgsinfo.Installs
		pkgsinfo.Installs = append(pkgsinfo.Installs, InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(fullpath),
			MD5Checksum: SingleQuotedString(md5sum),
			Version:     SingleQuotedString(fversion),
		})
	}

	// If user typed --name on CLI, override name in pkgsinfo
	if name != "" {
		pkgsinfo.Name = name
	}

	// If they didn't pass version but we have no version from MSI, set something
	if pkgsinfo.Version == "" {
		pkgsinfo.Version = "1.0"
	}

	// If we have scripts, read them
	if installCheckScript != "" {
		content, err := os.ReadFile(installCheckScript)
		if err == nil {
			pkgsinfo.InstallCheckScript = string(content)
		}
	}
	if uninstallCheckScript != "" {
		content, err := os.ReadFile(uninstallCheckScript)
		if err == nil {
			pkgsinfo.UninstallCheckScript = string(content)
		}
	}
	if preinstallScript != "" {
		content, err := os.ReadFile(preinstallScript)
		if err == nil {
			pkgsinfo.PreinstallScript = string(content)
		}
	}
	if postinstallScript != "" {
		content, err := os.ReadFile(postinstallScript)
		if err == nil {
			pkgsinfo.PostinstallScript = string(content)
		}
	}

	// Output pkgsinfo as YAML
	yamlData, err := yaml.Marshal(&pkgsinfo)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error marshaling YAML: %v\n", err)
		os.Exit(1)
	}
	fmt.Println(string(yamlData))
}

// multiStringSlice is a custom flag.Value to allow multiple -f flags
type multiStringSlice []string

func (m *multiStringSlice) String() string {
	return strings.Join(*m, ", ")
}

func (m *multiStringSlice) Set(value string) error {
	*m = append(*m, value)
	return nil
}
