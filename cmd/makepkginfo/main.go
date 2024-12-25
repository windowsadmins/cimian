// cmd/makepkginfo/main.go

package main

import (
	"crypto/md5"
	"crypto/sha256"
	"encoding/hex"
	"encoding/xml"
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
	"github.com/windowsadmins/gorilla/pkg/extract"
	"github.com/windowsadmins/gorilla/pkg/utils"

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

// MultiFlag for repeated `-f` usage
type multiFlag []string

func (m *multiFlag) String() string {
	return strings.Join(*m, ", ")
}
func (m *multiFlag) Set(value string) error {
	*m = append(*m, value)
	return nil
}

// nuspec struct for .nupkg
type nuspec struct {
	XMLName  xml.Name `xml:"package"`
	Metadata struct {
		ID          string `xml:"id"`
		Version     string `xml:"version"`
		Title       string `xml:"title"`
		Description string `xml:"description"`
		Authors     string `xml:"authors"`
		Owners      string `xml:"owners"`
		Tags        string `xml:"tags"`
	} `xml:"metadata"`
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
		// script flags
		installCheckScript   string
		uninstallCheckScript string
		preinstallScript     string
		postinstallScript    string

		// basic fields
		catalogs          string
		category          string
		developer         string
		name              string
		displayName       string
		description       string
		unattendedInstall bool
		newPkg            bool
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
	flag.BoolVar(&unattended, "unattended_install", false, "Set 'unattended_install: true'")
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
        Name:                 finalName,
        DisplayName:          displayName,
        Version:              finalVersion,
        Catalogs:             strings.Split(catalogs, ","),
        Category:             category,
        Developer:            metaDeveloper,
        Description:          metaDesc,
        InstallerType:        installerType,
        UnattendedInstall:    unattended,
        Installs:             autoInstalls, // from auto-detect
    }

    // gather size + hash for the main installer
    fSize, fHash, _ := getFileInfo(installerPath)
    pkginfo.InstallerItemSize = fSize / 1024
    pkginfo.InstallerItemHash = fHash
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

		md5sum, err := utils.FileMD5(fullpath)
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
        fi, _ := getFileInfo(path)
        installs = []InstallItem{{
            Type:        SingleQuotedString("file"),
            Path:        SingleQuotedString(path),
            MD5Checksum: SingleQuotedString(fi.Hash), // using SHA-256 as a stand-in
            Version:     SingleQuotedString(metaVersion),
        }}

    case ".exe":
        iType = "exe"
        nm, ver, dev, desc := extract.ExeMetadata(path)
        metaName, metaVersion, metaDeveloper, metaDesc = nm, ver, dev, desc

        fi, _ := getFileInfo(path)
        installs = []InstallItem{{
            Type:        SingleQuotedString("file"),
            Path:        SingleQuotedString(path),
            MD5Checksum: SingleQuotedString(fi.Hash),
            Version:     SingleQuotedString(metaVersion),
        }}

    case ".nupkg":
        iType = "nupkg"
        nm, ver, dev, desc := extract.NupkgMetadata(path)
        metaName, metaVersion, metaDeveloper, metaDesc = nm, ver, dev, desc

        // Optionally parse .nupkg contents, or just add the .nupkg file:
        fi, _ := getFileInfo(path)
        installs = []InstallItem{{
            Type:        SingleQuotedString("file"),
            Path:        SingleQuotedString(path),
            MD5Checksum: SingleQuotedString(fi.Hash),
        }}

    default:
        // fallback for unrecognized extensions
        iType = "unknown"
        metaName = parsePackageName(filepath.Base(path))
        fi, _ := getFileInfo(path)
        installs = []InstallItem{{
            Type:        SingleQuotedString("file"),
            Path:        SingleQuotedString(path),
            MD5Checksum: SingleQuotedString(fi.Hash),
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

// fileInfoResult holds size + hash for the file. We’ll use SHA-256 for
// "installer_item_hash" or MD5 in the "installs" array if needed.
type fileInfoResult struct {
    Size int64
    Hash string
}

// getFileInfo calculates a SHA-256 hash and gets the size
// of a local file. This is used to fill "installer_item_hash" or "MD5Checksum"
// in the Installs array.
func getFileInfo(pkgPath string) (fileInfoResult, error) {
    fi, err := os.Stat(pkgPath)
    if err != nil {
        return fileInfoResult{}, err
    }

    f, err := os.Open(pkgPath)
    if err != nil {
        return fileInfoResult{}, err
    }
    defer f.Close()

    h := sha256.New()
    if _, err := io.Copy(h, f); err != nil {
        return fileInfoResult{}, err
    }
    return fileInfoResult{
        Size: fi.Size(),
        Hash: hex.EncodeToString(h.Sum(nil)), // Return as hex string
    }, nil
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
            _, ver2, _, _ := extract.ExeMetadata(abs)
            ver = ver2
        }

        installs = append(installs, InstallItem{
            Type:        SingleQuotedString("file"),
            Path:        SingleQuotedString(abs),
            MD5Checksum: SingleQuotedString(md5val),
            Version:     SingleQuotedString(ver),
        })
    }
    return installs
}