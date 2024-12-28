// cmd/makepkginfo/main.go

package main

import (
	"flag"
	"fmt"
	"io/ioutil"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"gopkg.in/yaml.v3"

	"github.com/windowsadmins/gorilla/pkg/extract"
	"github.com/windowsadmins/gorilla/pkg/utils"
	"github.com/windowsadmins/gorilla/pkg/version"
)

// Struct for "installs" array
type InstallItem struct {
	Type           SingleQuotedString `yaml:"type"`
	Path           SingleQuotedString `yaml:"path"`
	MD5Checksum    SingleQuotedString `yaml:"md5checksum,omitempty"`
	SHA256Checksum SingleQuotedString `yaml:"sha256checksum,omitempty"`
	Version        SingleQuotedString `yaml:"version,omitempty"`
}

// SingleQuotedString forces single quotes in YAML output.
type SingleQuotedString string

func (s SingleQuotedString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Style: yaml.SingleQuotedStyle,
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

// Function to calculate file size and hash
func getFileInfo(pkgPath string) (int64, string, error) {
	fi, err := os.Stat(pkgPath)
	if err != nil {
		return 0, "", err
	}
	hash, err := utils.FileSHA256(pkgPath)
	if err != nil {
		return 0, "", err
	}
	return fi.Size(), hash, nil
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

// getFileVersion retrieves version information from executable files on Windows systems.
// Returns an empty string and nil error on non-Windows platforms.
func getFileVersion(path string) (string, error) {
	if runtime.GOOS != "windows" {
		return "", nil
	}
	version, err := extract.ExeMetadata(path)
	return version, err
}

// Main function
func main() {
	// Declare versionString
	var versionString string

	// Command-line flags
	var (
		// script flags
		installCheckScript   string
		uninstallCheckScript string
		preinstallScript     string
		postinstallScript    string

		// basic fields
		catalogs            string
		category            string
		developer           string
		name                string
		displayName         string
		description         string
		unattendedInstall   bool
		unattendedUninstall bool
		newPkg              bool
	)
	// Add a multi-flag for `-f`
	var filePaths multiStringSlice

	flag.StringVar(&installCheckScript, "installcheck_script", "", "Path to install check script")
	flag.StringVar(&uninstallCheckScript, "uninstallcheck_script", "", "Path to uninstall check script")
	flag.StringVar(&preinstallScript, "preinstall_script", "", "Path to preinstall script")
	flag.StringVar(&postinstallScript, "postinstall_script", "", "Path to postinstall script")
	flag.StringVar(&catalogs, "catalogs", "Development", "Comma-separated list of catalogs")
	flag.StringVar(&category, "category", "", "Category")
	flag.StringVar(&developer, "developer", "", "Developer")
	flag.StringVar(&name, "name", "", "Name override for the package")
	flag.StringVar(&displayName, "displayname", "", "Display name override")
	flag.StringVar(&description, "description", "", "Description")
	flag.StringVar(&versionString, "version", "", "Version override")
	flag.BoolVar(&unattendedInstall, "unattended_install", false, "Set 'unattended_install: true'")
	flag.BoolVar(&unattendedUninstall, "unattended_uninstall", false, "Set 'unattended_uninstall: true'")
	flag.Var(&filePaths, "f", "Add extra files to 'installs' array (multiple -f flags allowed)")

	showVersion := flag.Bool("version", false, "Print the version and exit.")
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

	installerPath := flag.Arg(0)
	installerPath = strings.TrimSuffix(installerPath, "/")

	// gather installer metadata
	autoInstalls, metaName, metaVersion, metaDeveloper, metaDesc, installerType := gatherInstallerInfo(installerPath)

	// if user provided `--name`, override metaName
	finalName := metaName
	if name != "" {
		finalName = name
	}

	// if user provided --developer, override
	if developer != "" {
		metaDeveloper = developer
	}

	// if user provided version, override
	finalVersion := metaVersion
	if versionString != "" {
		finalVersion = versionString
	}
	if finalVersion == "" {
		finalVersion = time.Now().Format("2006.01.02")
	}

	if description != "" {
		metaDesc = description
	}

	// build the base PkgsInfo
	pkginfo := PkgsInfo{
		Name:              finalName,
		DisplayName:       displayName,
		Version:           finalVersion,
		Catalogs:          strings.Split(catalogs, ","),
		Category:          category,
		Developer:         metaDeveloper,
		Description:       metaDesc,
		InstallerType:     installerType,
		UnattendedInstall: unattendedInstall,
		Installs:          autoInstalls, // from auto-detect
	}

	// gather size + hash for the main installer
	size, hash, _ := getFileInfo(installerPath)
	pkginfo.InstallerItemSize = size / 1024
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error getting file info for %s: %v\n", installerPath, err)
		os.Exit(1)
	}
	pkginfo.InstallerItemHash = hash
	pkginfo.InstallerItemLocation = filepath.Base(installerPath)

	// read scripts if specified
	if s, err := readFileOrEmpty(installCheckScript); err == nil {
		pkginfo.InstallCheckScript = s
	}
	if s, err := readFileOrEmpty(uninstallCheckScript); err == nil {
		pkginfo.UninstallCheckScript = s
	}
	if s, err := readFileOrEmpty(preinstallScript); err == nil {
		pkginfo.PreinstallScript = s
	}
	if s, err := readFileOrEmpty(postinstallScript); err == nil {
		pkginfo.PostinstallScript = s
	}

	// also add the user-specified -f files
	userInstalls := buildInstallsArray(filePaths)
	pkginfo.Installs = append(pkginfo.Installs, userInstalls...)

	// Output final YAML
	yamlData, err := yaml.Marshal(&pkginfo)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error marshaling YAML: %v\n", err)
		os.Exit(1)
	}
	fmt.Println(string(yamlData))

	if len(filePaths) > 0 {
		// Process the -f file array
		for _, fpath := range filePaths {
			fullpath, _ := filepath.Abs(fpath)
			fi, err := os.Stat(fullpath)
			if err != nil {
				fmt.Fprintf(os.Stderr, "Warning: skipping -f %s, error: %v\n", fpath, err)
				continue
			}

			if fi.IsDir() {
				fmt.Fprintf(os.Stderr, "Skipping directory for now: %s\n", fpath)
				continue
			}

			md5sum, err := utils.FileMD5(fullpath)
			if err != nil {
				fmt.Fprintf(os.Stderr, "Warning: cannot compute MD5 for %s: %v\n", fpath, err)
			}

			fversion, _ := getFileVersion(fullpath)

			_, hash, err := getFileInfo(fullpath)
			if err != nil {
				fmt.Fprintf(os.Stderr, "Warning: unable to get file info for %s: %v\n", fullpath, err)
				continue
			}

			pkginfo.Installs = append(pkginfo.Installs, InstallItem{
				Type:           SingleQuotedString("file"),
				Path:           SingleQuotedString(fullpath),
				MD5Checksum:    SingleQuotedString(md5sum),
				SHA256Checksum: SingleQuotedString(hash),
				Version:        SingleQuotedString(fversion),
			})
		}

		// Re-marshal with updated installs
		yamlData, err = yaml.Marshal(&pkginfo)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error marshaling YAML: %v\n", err)
			os.Exit(1)
		}
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

// gatherInstallerInfo inspects the file extension (.msi, .exe, or .nupkg)
// and returns a slice of InstallItem plus metadata from the file.
func gatherInstallerInfo(path string) (installs []InstallItem, metaName, metaVersion, metaDeveloper, metaDesc, iType string) {
	ext := strings.ToLower(filepath.Ext(path))

	switch ext {
	case ".msi":
		iType = "msi"
		// Call the extract package to parse .msi metadata:
		metaName, metaVersion, metaDeveloper, metaDesc = extract.MsiMetadata(path)

		// For "auto installs," either parse the MSI file table or just
		// add the MSI itself. For now, just add the MSI itself:
		_, hash, err := getFileInfo(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error getting file info: %v\n", err)
		}
		installs = []InstallItem{{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(path),
			MD5Checksum: SingleQuotedString(hash), // using SHA-256 as a stand-in
			Version:     SingleQuotedString(metaVersion),
		}}

	case ".exe":
		iType = "exe"
		exeVersion, err := getFileVersion(path)
		if err != nil || exeVersion == "" {
			metaName = parsePackageName(filepath.Base(path))
			metaVersion = ""
			metaDeveloper = ""
			metaDesc = ""
		} else {
			metaName = parsePackageName(filepath.Base(path))
			metaVersion = exeVersion
			metaDeveloper = ""
			metaDesc = ""
		}

		_, hash, err := getFileInfo(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error getting file info: %v\n", err)
		}
		installs = []InstallItem{{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(path),
			MD5Checksum: SingleQuotedString(hash),
			Version:     SingleQuotedString(metaVersion),
		}}

	case ".nupkg":
		iType = "nupkg"
		nm, ver, dev, desc := extract.NupkgMetadata(path)
		metaName, metaVersion, metaDeveloper, metaDesc = nm, ver, dev, desc

		// Optionally parse .nupkg contents, or just add the .nupkg file:
		_, hash, err := getFileInfo(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error getting file info: %v\n", err)
		}
		installs = []InstallItem{{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(path),
			MD5Checksum: SingleQuotedString(hash),
		}}

	default:
		// fallback for unrecognized extensions
		iType = "unknown"
		metaName = parsePackageName(filepath.Base(path))
		_, hash, _ := getFileInfo(path)
		installs = []InstallItem{{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(path),
			MD5Checksum: SingleQuotedString(hash),
		}}
	}

	return
}

// readFileOrEmpty reads the entire contents of a file path or returns
// an empty string if the path is blank.
func readFileOrEmpty(path string) (string, error) {
	if path == "" {
		return "", nil
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return string(b), nil
}

// parsePackageName is a fallback for metadata if none is available.
func parsePackageName(filename string) string {
	return strings.TrimSuffix(filename, filepath.Ext(filename))
}

// buildInstallsArray processes a list of extra file paths (e.g. from -f flags)
// and returns InstallItem objects for each file, including MD5 and optional .exe version.
func buildInstallsArray(paths []string) []InstallItem {
	var installs []InstallItem
	for _, f := range paths {
		abs, _ := filepath.Abs(f)
		st, err := os.Stat(abs)
		if err != nil || st.IsDir() {
			fmt.Fprintf(os.Stderr, "Skipping '%s' in -f, not found or directory.\n", f)
			continue
		}

		// Use MD5 for the “installs” array:
		md5val, _ := utils.FileMD5(abs)

		// If it’s an .exe on Windows, optionally parse version from extract.ExeMetadata:
		var ver string
		if runtime.GOOS == "windows" && strings.EqualFold(filepath.Ext(abs), ".exe") {
			// Remove reference to extract.ExeMetadata
			v, err := getFileVersion(abs)
			if err != nil {
				fmt.Fprintf(os.Stderr, "Error getting file version for %s: %v\n", abs, err)
			} else {
				ver = v
			}
		}

		_, hash, err := getFileInfo(abs)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Warning: unable to get file info for %s: %v\n", abs, err)
			continue
		}

		installs = append(installs, InstallItem{
			Type:           SingleQuotedString("file"),
			Path:           SingleQuotedString(abs),
			MD5Checksum:    SingleQuotedString(md5val),
			SHA256Checksum: SingleQuotedString(hash),
			Version:        SingleQuotedString(ver),
		})
	}
	return installs
}
