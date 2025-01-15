// cmd/makepkginfo/main.go

package main

import (
	"crypto/md5"
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
	Type        SingleQuotedString `yaml:"type"`
	Path        SingleQuotedString `yaml:"path"`
	MD5Checksum SingleQuotedString `yaml:"md5checksum,omitempty"`
	Version     SingleQuotedString `yaml:"version,omitempty"`
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
	Installer            *Installer    `yaml:"installer,omitempty"`
	ProductCode          string        `yaml:"product_code,omitempty"`
	UpgradeCode          string        `yaml:"upgrade_code,omitempty"`
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

// calculateMD5 calculates the MD5 checksum of a file
func calculateMD5(filePath string) (string, error) {
	file, err := os.ReadFile(filePath)
	if err != nil {
		return "", err
	}
	hash := md5.Sum(file)
	return fmt.Sprintf("%x", hash), nil
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
	autoInstalls, metaName, metaVersion, metaDeveloper, metaDesc, installerType, prodCode, upgrCode := gatherInstallerInfo(installerPath)

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
		ProductCode:       prodCode,
		UpgradeCode:       upgrCode,
	}

	// If we have an installer path, gather size + hash and store in Installer
	if installerPath != "" {
		sizeBytes, hashVal, errFileInfo := getFileInfo(installerPath)
		if errFileInfo != nil {
			fmt.Fprintf(os.Stderr, "Warning: can't read installer info: %v\n", errFileInfo)
		}
		sizeKB := sizeBytes / 1024

		// We'll store an `Installer` object
		pkginfo.Installer = &Installer{
			Location: normalizeInstallerLocation(filepath.Base(installerPath)), // or full path, depending on your usage
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
	alreadyHasValidVersion := (pkginfo.Version != "" && pkginfo.Version != "1.0.0")

	for i := range userInstalls {
		fileVersion := string(userInstalls[i].Version)

		// Always remove the per-file version from the final YAML
		userInstalls[i].Version = ""

		// If top-level version is still default/blank, adopt the first real .exe version found
		if !alreadyHasValidVersion && fileVersion != "" && fileVersion != "1.0.0" {
			pkginfo.Version = fileVersion
			alreadyHasValidVersion = true
		}
	}

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

// gatherInstallerInfo now returns 8 items to accommodate productCode & upgradeCode
func gatherInstallerInfo(path string) (
	installs []InstallItem,
	metaName, metaVersion, metaDeveloper, metaDesc, iType string,
	productCode, upgradeCode string,
) {
	var err error
	if path == "" {
		// If no installer at all, return empty placeholders
		return nil, "NoName", "", "", "", "", "", ""
	}

	ext := strings.ToLower(filepath.Ext(path))

	switch ext {
	case ".msi":
		iType = "msi"
		// MsiMetadata should now return 6 fields: (name, ver, dev, desc, productCode, upgradeCode)
		metaName, metaVersion, metaDeveloper, metaDesc, productCode, upgradeCode =
			extract.MsiMetadata(path)

		// Same logic as before
		_, _, err = getFileInfo(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error getting file info: %v\n", err)
		}

		md5Val, _ := calculateMD5(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error calculating MD5: %v\n", err)
		}

		installs = []InstallItem{{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(path),
			MD5Checksum: SingleQuotedString(md5Val),
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
		productCode = "" // MSI fields not applicable
		upgradeCode = "" // MSI fields not applicable

		_, _, err = getFileInfo(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error getting file info: %v\n", err)
		}

		md5Val, err := calculateMD5(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error calculating MD5: %v\n", err)
		}

		installs = []InstallItem{{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(path),
			MD5Checksum: SingleQuotedString(md5Val),
			Version:     SingleQuotedString(metaVersion),
		}}

	case ".nupkg":
		iType = "nupkg"
		nm, ver, dev, desc := extract.NupkgMetadata(path)
		metaName, metaVersion, metaDeveloper, metaDesc = nm, ver, dev, desc
		productCode = ""
		upgradeCode = ""

		_, _, err = getFileInfo(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error getting file info: %v\n", err)
		}

		md5Val, err := calculateMD5(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error calculating MD5: %v\n", err)
		}

		installs = []InstallItem{{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(path),
			MD5Checksum: SingleQuotedString(md5Val),
			Version:     SingleQuotedString(metaVersion),
		}}

	default:
		iType = "unknown"
		metaName = parsePackageName(filepath.Base(path))
		metaVersion = ""
		metaDeveloper = ""
		metaDesc = ""
		productCode = ""
		upgradeCode = ""

		// Remove unneeded call entirely since we don't use its results
		md5Val, _ := calculateMD5(path)

		installs = []InstallItem{{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(path),
			MD5Checksum: SingleQuotedString(md5Val),
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

func replacePathUserProfile(p string) string {
	if runtime.GOOS != "windows" {
		return p
	}
	userProfile := os.Getenv("USERPROFILE")
	if userProfile == "" {
		return p
	}
	lowerP := strings.ToLower(p)
	lowerUserProfile := strings.ToLower(userProfile)
	if strings.HasPrefix(lowerP, lowerUserProfile) {
		return `C:\Users\%USERPROFILE%` + p[len(userProfile):]
	}
	return p
}

// buildInstallsArray to handle -f items
func buildInstallsArray(paths []string) []InstallItem {
	var installs []InstallItem
	for _, f := range paths {
		abs, _ := filepath.Abs(f)

		// 1) Stat the real path
		st, err := os.Stat(abs)
		if err != nil || st.IsDir() {
			fmt.Fprintf(os.Stderr, "Skipping '%s' in -f, not found or directory.\n", f)
			continue
		}

		// 2) Calculate MD5 on the real path
		md5val, err := calculateMD5(abs)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error calculating MD5 for %s: %v\n", abs, err)
			continue
		}

		// 3) If .exe, grab file version from the real path
		var fileVersion string
		if runtime.GOOS == "windows" && strings.EqualFold(filepath.Ext(abs), ".exe") {
			v, err := getFileVersion(abs)
			if err != nil {
				fmt.Fprintf(os.Stderr, "Error getting file version for %s: %v\n", abs, err)
			} else {
				fileVersion = v
			}
		}

		// 4) Rewrite final path AFTER checks
		finalPath := replacePathUserProfile(abs)

		// 5) Append to the final installs array
		installs = append(installs, InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(finalPath),
			MD5Checksum: SingleQuotedString(md5val),
			Version:     SingleQuotedString(fileVersion),
		})
	}
	return installs
}

func normalizeInstallerLocation(location string) string {
	return strings.ReplaceAll(location, `\`, `/`)
}
