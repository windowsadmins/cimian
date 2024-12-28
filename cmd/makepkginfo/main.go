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

// InstallItem struct for "installs" array
type InstallItem struct {
	Type           SingleQuotedString `yaml:"type"`
	Path           SingleQuotedString `yaml:"path"`
	MD5Checksum    SingleQuotedString `yaml:"md5checksum,omitempty"`
	SHA256Checksum SingleQuotedString `yaml:"sha256checksum,omitempty"`
	Version        SingleQuotedString `yaml:"version,omitempty"`
}

// Installer is our new struct to hold location, hash, size, etc.
// This parallels gorillaimport's "Installer" struct.
type Installer struct {
	Location string `yaml:"location,omitempty"`
	Hash     string `yaml:"hash,omitempty"`
	Type     string `yaml:"type,omitempty"`
	Size     int64  `yaml:"size,omitempty"`
}

// PkgsInfo represents the package information
type PkgsInfo struct {
	Name                 string        `yaml:"name"`
	DisplayName          string        `yaml:"display_name,omitempty"`
	Version              string        `yaml:"version"`
	Catalogs             []string      `yaml:"catalogs,omitempty"`
	Category             string        `yaml:"category,omitempty"`
	Description          string        `yaml:"description,omitempty"`
	Developer            string        `yaml:"developer,omitempty"`
	InstallerType        string        `yaml:"installer_type,omitempty"` // leftover if needed
	UnattendedInstall    bool          `yaml:"unattended_install,omitempty"`
	Installs             []InstallItem `yaml:"installs,omitempty"`
	InstallCheckScript   string        `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string        `yaml:"uninstallcheck_script,omitempty"`
	PreinstallScript     string        `yaml:"preinstall_script,omitempty"`
	PostinstallScript    string        `yaml:"postinstall_script,omitempty"`

	// Instead of top-level installer_item_* fields, we embed an Installer struct
	Installer *Installer `yaml:"installer,omitempty"`
}

// NoQuoteString ensures empty strings appear without quotes.
type NoQuoteString string

func (s NoQuoteString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Value: string(s),
	}
	return node, nil
}

// wrapperPkgsInfo is from your original code, might or might not need revision
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

// getFileInfo calculates file size and SHA256
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

// SavePkgsInfo for writing YAML
func SavePkgsInfo(pkgsinfoPath string, pkgsinfo PkgsInfo) error {
	data, err := yaml.Marshal(pkgsinfo)
	if err != nil {
		return err
	}
	return os.WriteFile(pkgsinfoPath, data, 0644)
}

// CreateNewPkgsInfo can remain mostly unchanged, or updated to embed new Installer struct if needed
func CreateNewPkgsInfo(pkgsinfoPath, name string) error {
	newPkgsInfo := PkgsInfo{
		Name:              name,
		Version:           time.Now().Format("2006.01.02"),
		Catalogs:          []string{"Testing"},
		UnattendedInstall: true,
	}

	// For your minimal new file
	wrapped := wrapperPkgsInfo{
		Name:              NoQuoteString(newPkgsInfo.Name),
		DisplayName:       "",
		Version:           NoQuoteString(newPkgsInfo.Version),
		Catalogs:          newPkgsInfo.Catalogs,
		Category:          "",
		Description:       "",
		Developer:         "",
		UnattendedInstall: newPkgsInfo.UnattendedInstall,
	}

	data, err := yaml.Marshal(&wrapped)
	if err != nil {
		return err
	}
	return os.WriteFile(pkgsinfoPath, data, 0644)
}

// getFileVersion for .exe
func getFileVersion(path string) (string, error) {
	if runtime.GOOS != "windows" {
		return "", nil
	}
	version, err := extract.ExeMetadata(path)
	return version, err
}

// main is our entry point
func main() {
	var (
		installCheckScript   string
		uninstallCheckScript string
		preinstallScript     string
		postinstallScript    string

		catalogs            string
		category            string
		developer           string
		pkgName             string
		displayName         string
		description         string
		versionString       string
		unattendedInstall   bool
		unattendedUninstall bool
		newPkg              bool
	)

	// Multi-flag for -f
	var filePaths multiStringSlice

	flag.StringVar(&installCheckScript, "installcheck_script", "", "Path to install check script")
	flag.StringVar(&uninstallCheckScript, "uninstallcheck_script", "", "Path to uninstall check script")
	flag.StringVar(&preinstallScript, "preinstall_script", "", "Path to preinstall script")
	flag.StringVar(&postinstallScript, "postinstall_script", "", "Path to postinstall script")
	flag.StringVar(&catalogs, "catalogs", "Development", "Comma-separated list of catalogs")
	flag.StringVar(&category, "category", "", "Category")
	flag.StringVar(&developer, "developer", "", "Developer")
	flag.StringVar(&pkgName, "name", "", "Name override for the package")
	flag.StringVar(&displayName, "displayname", "", "Display name override")
	flag.StringVar(&description, "description", "", "Description")
	flag.StringVar(&versionString, "version", "", "Version override")
	flag.BoolVar(&unattendedInstall, "unattended_install", false, "Set 'unattended_install: true'")
	flag.BoolVar(&unattendedUninstall, "unattended_uninstall", false, "Set 'unattended_uninstall: true'")
	flag.BoolVar(&newPkg, "new", false, "Create a new pkginfo stub")
	flag.Var(&filePaths, "f", "Add extra files to 'installs' array (multiple -f flags allowed)")

	showMakePkgInfoVersion := flag.Bool("makepkginfo_version", false, "Print the version and exit.")
	flag.Parse()

	// Handle --version
	if *showMakePkgInfoVersion {
		version.Print()
		return
	}

	// Load config
	config, err := LoadConfig(`C:\ProgramData\ManagedInstalls\Config.yaml`)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error loading config: %v\n", err)
		os.Exit(1)
	}

	// Handle --new stub
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

	// If not creating a new pkg, we expect at least 1 argument => the installer path
	if flag.NArg() < 1 && len(filePaths) == 0 {
		fmt.Println("Usage: makepkginfo [options] /path/to/installer.msi -f path1 -f path2 ...")
		flag.PrintDefaults()
		os.Exit(1)
	}

	installerPath := ""
	if flag.NArg() > 0 {
		installerPath = flag.Arg(0)
		installerPath = strings.TrimSuffix(installerPath, "/")
	}

	// gather metadata
	autoInstalls, metaName, metaVersion, metaDeveloper, metaDesc, installerType := gatherInstallerInfo(installerPath)

	// override from flags
	finalName := metaName
	if pkgName != "" {
		finalName = pkgName
	}
	if developer != "" {
		metaDeveloper = developer
	}
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

	// Build PkgsInfo
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
		Installs:          autoInstalls,
	}

	// If we have an installer path, gather size + hash and store in Installer
	if installerPath != "" {
		sizeBytes, hashVal, _ := getFileInfo(installerPath)
		// In KB
		sizeKB := sizeBytes / 1024

		// We'll store an `Installer` object
		pkginfo.Installer = &Installer{
			Location: filepath.Base(installerPath), // or full path, depending on your usage
			Hash:     hashVal,
			Type:     installerType,
			Size:     sizeKB,
		}
	}

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

	// also process user-specified -f items
	userInstalls := buildInstallsArray(filePaths)
	pkginfo.Installs = append(pkginfo.Installs, userInstalls...)

	// Output final YAML to stdout
	yamlData, err := yaml.Marshal(&pkginfo)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error marshaling YAML: %v\n", err)
		os.Exit(1)
	}
	fmt.Println(string(yamlData))

	// If you want to do more advanced usage, like writing the file, you can do so here.
}

// multiStringSlice is your existing custom flag type
type multiStringSlice []string

func (m *multiStringSlice) String() string {
	return strings.Join(*m, ", ")
}
func (m *multiStringSlice) Set(value string) error {
	*m = append(*m, value)
	return nil
}

// gatherInstallerInfo is the same as before, but remove references to top-level "installer_item_*"
func gatherInstallerInfo(path string) (installs []InstallItem, metaName, metaVersion, metaDeveloper, metaDesc, iType string) {
	if path == "" {
		// If no installer at all, return empty
		return nil, "NoName", "", "", "", ""
	}
	ext := strings.ToLower(filepath.Ext(path))

	switch ext {
	case ".msi":
		iType = "msi"
		metaName, metaVersion, metaDeveloper, metaDesc = extract.MsiMetadata(path)
		// Just add the MSI itself as an "installs" entry
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

// readFileOrEmpty is the same as before
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

// parsePackageName is the same
func parsePackageName(filename string) string {
	return strings.TrimSuffix(filename, filepath.Ext(filename))
}

// buildInstallsArray to handle -f items
func buildInstallsArray(paths []string) []InstallItem {
	var installs []InstallItem
	for _, f := range paths {
		abs, _ := filepath.Abs(f)
		st, err := os.Stat(abs)
		if err != nil || st.IsDir() {
			fmt.Fprintf(os.Stderr, "Skipping '%s' in -f, not found or directory.\n", f)
			continue
		}
		md5val, _ := utils.FileMD5(abs)

		var ver string
		if runtime.GOOS == "windows" && strings.EqualFold(filepath.Ext(abs), ".exe") {
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
