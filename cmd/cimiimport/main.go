// cmd/cimianimport/main.go

//go:build windows
// +build windows

package main

import (
	"bufio"
	"bytes"
	"fmt"
	"io"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"regexp"
	"runtime"
	"strconv"
	"strings"

	"gopkg.in/yaml.v3"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/extract"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/utils"
	"github.com/windowsadmins/cimian/pkg/version"
)

var (
	identifierFlag string
	configAuto     bool
	logger         *logging.Logger
)

// parseVersion handles version normalization for date-based versions while preserving other formats.
// Date formats like YYYY.MM.DD or YYYY.MM.DD.HHmm are normalized to full format (YYYY.MM.DD).
// Handles Chocolatey truncation where 2025.09.01 becomes 25.9.1 and restores to full date format.
// All other version formats are passed through unchanged.
func parseVersion(versionStr string) string {
	if versionStr == "" {
		return versionStr
	}

	parts := strings.Split(versionStr, ".")

	// Handle date-based versions: YYYY.MM.DD or YYYY.MM.DD.HHmm
	if len(parts) == 3 || len(parts) == 4 {
		// Check if this looks like a date format by validating the first part as a year
		if yearNum, err := strconv.Atoi(parts[0]); err == nil {
			// Handle both full years (2000-2100) and Chocolatey-truncated years (00-99)
			isDateFormat := false
			
			// Full 4-digit years
			if yearNum >= 2000 && yearNum <= 2100 {
				isDateFormat = true
			}
			// Chocolatey-truncated 2-digit years (handle common range 00-99)
			if yearNum >= 0 && yearNum <= 99 {
				// Additional validation: check if month and day parts look like date components
				if len(parts) >= 3 {
					if monthNum, err := strconv.Atoi(parts[1]); err == nil && monthNum >= 1 && monthNum <= 12 {
						if dayNum, err := strconv.Atoi(parts[2]); err == nil && dayNum >= 1 && dayNum <= 31 {
							isDateFormat = true
						}
					}
				}
			}
			
			if isDateFormat {
				// Validate all parts are numeric for date format
				var numericParts []int
				allNumeric := true
				for _, part := range parts {
					if num, err := strconv.Atoi(part); err == nil {
						numericParts = append(numericParts, num)
					} else {
						allNumeric = false
						break
					}
				}

				if allNumeric && len(numericParts) >= 3 {
					year := numericParts[0]
					month := numericParts[1]
					day := numericParts[2]

					// Convert 2-digit year to 4-digit year (handle Chocolatey truncation)
					if year < 100 {
						if year <= 50 {
							year = 2000 + year
						} else {
							year = 1900 + year
						}
					}

					// Ensure month and day are zero-padded for date-based versions
					if len(numericParts) == 4 {
						// 4-part date version: YYYY.MM.DD.HHmm
						return fmt.Sprintf("%d.%02d.%02d.%d", year, month, day, numericParts[3])
					} else {
						// 3-part date version: YYYY.MM.DD
						return fmt.Sprintf("%d.%02d.%02d", year, month, day)
					}
				}
			}
		}
	}

	// For all other version formats (like 1.1.2, 1.2.3.4, etc.), pass through unchanged
	return versionStr
}

// PkgsInfo represents the structure of the pkginfo YAML file.
type PkgsInfo struct {
	Name                 string             `yaml:"name"`
	DisplayName          string             `yaml:"display_name,omitempty"`
	Identifier           string             `yaml:"identifier,omitempty"`
	Version              string             `yaml:"version"`
	Description          NoQuoteEmptyString `yaml:"description"`
	Category             NoQuoteEmptyString `yaml:"category"`
	Developer            NoQuoteEmptyString `yaml:"developer"`
	Catalogs             []string           `yaml:"catalogs"`
	Installs             []InstallItem      `yaml:"installs,omitempty"`
	SupportedArch        []string           `yaml:"supported_architectures"`
	UnattendedInstall    bool               `yaml:"unattended_install"`
	UnattendedUninstall  bool               `yaml:"unattended_uninstall"`
	Requires             []string           `yaml:"requires,omitempty"`
	UpdateFor            []string           `yaml:"update_for,omitempty"`
	MinOSVersion         string             `yaml:"minimum_os_version,omitempty"` // Minimum Windows version required
	MaxOSVersion         string             `yaml:"maximum_os_version,omitempty"` // Maximum Windows version supported
	Installer            *Installer         `yaml:"installer"`
	Uninstaller          *Installer         `yaml:"uninstaller,omitempty"`
	PreinstallScript     string             `yaml:"preinstall_script,omitempty"`
	PostinstallScript    string             `yaml:"postinstall_script,omitempty"`
	PreuninstallScript   string             `yaml:"preuninstall_script,omitempty"`
	PostuninstallScript  string             `yaml:"postuninstall_script,omitempty"`
	InstallCheckScript   string             `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string             `yaml:"uninstallcheck_script,omitempty"`
}

// Installer represents the installer/uninstaller details.
type Installer struct {
	Location    string   `yaml:"location"`
	Hash        string   `yaml:"hash"`
	Type        string   `yaml:"type"`
	Size        int64    `yaml:"size,omitempty"`
	Arguments   []string `yaml:"arguments,omitempty"`
	ProductCode string   `yaml:"product_code,omitempty"`
	UpgradeCode string   `yaml:"upgrade_code,omitempty"`
}

// MarshalYAML forces the output order as follows:
// type, size, location, hash, then (if type=="msi") product_code and upgrade_code,
// then arguments (only if non-empty).
func (i *Installer) MarshalYAML() (interface{}, error) {
	var content []*yaml.Node

	// Always include "type"
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "type"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: i.Type},
	)
	// Always include "size"
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "size"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!int", Value: fmt.Sprintf("%d", i.Size)},
	)
	// Always include "location"
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "location"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: i.Location},
	)
	// Always include "hash"
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "hash"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: i.Hash},
	)
	// Include product_code and upgrade_code only if installer type is "msi"
	if strings.ToLower(i.Type) == "msi" {
		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "product_code"},
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: i.ProductCode},
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "upgrade_code"},
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: i.UpgradeCode},
		)
	}
	// Only include arguments if there are any
	if len(i.Arguments) > 0 {
		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "arguments"},
			buildArgumentsNode(i.Arguments),
		)
	}

	node := &yaml.Node{
		Kind:    yaml.MappingNode,
		Tag:     "!!map",
		Content: content,
	}
	return node, nil
}

func buildArgumentsNode(args []string) *yaml.Node {
	seq := &yaml.Node{
		Kind: yaml.SequenceNode,
		Tag:  "!!seq",
	}
	for _, a := range args {
		seq.Content = append(seq.Content, &yaml.Node{
			Kind:  yaml.ScalarNode,
			Tag:   "!!str",
			Value: a,
		})
	}
	return seq
}

// InstallItem for the "installs" array.
type InstallItem struct {
	Type        SingleQuotedString `yaml:"type"`
	Path        SingleQuotedString `yaml:"path"`
	MD5Checksum SingleQuotedString `yaml:"md5checksum"`
	Version     SingleQuotedString `yaml:"version,omitempty"`
}

// SingleQuotedString ensures single quotes in YAML output.
type SingleQuotedString string

func (s SingleQuotedString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Style: yaml.SingleQuotedStyle,
		Value: string(s),
	}
	return node, nil
}

// ScriptPaths captures paths for custom scripts.
type ScriptPaths struct {
	Preinstall     string
	Postinstall    string
	Preuninstall   string
	Postuninstall  string
	InstallCheck   string
	UninstallCheck string
}

// Metadata holds extracted metadata.
type Metadata struct {
	Title               string
	ID                  string
	Version             string
	Developer           string
	Category            string
	Description         string
	ProductCode         string
	UpgradeCode         string
	Architecture        string
	SupportedArch       []string
	InstallerType       string
	Installs            []InstallItem
	Catalogs            []string
	RepoPath            string
	UnattendedInstall   bool
	UnattendedUninstall bool
	Requires            []string
	UpdateFor           []string
}

// NoQuoteEmptyString ensures empty strings appear without quotes.
type NoQuoteEmptyString string

func (s NoQuoteEmptyString) MarshalYAML() (interface{}, error) {
	if len(s) == 0 {
		return &yaml.Node{
			Kind:  yaml.ScalarNode,
			Tag:   "!!str",
			Value: "",
			Style: 0,
		}, nil
	}
	return &yaml.Node{
		Kind:  yaml.ScalarNode,
		Tag:   "!!str",
		Value: string(s),
		Style: 0,
	}, nil
}

// parseCustomArgs manually parses os.Args for:
//  1. The first non-flag => packagePath
//  2. -i or --installs-array => appended to filePaths
//  3. key=value flags => stored in otherFlags["key"] = "value"
//  4. single flags => stored in otherFlags["flag"] = "true" (boolean style)
//  5. a special boolean configRequested if user typed --config
//
// Returns (packagePath, filePaths, otherFlags, configRequested, helpRequested, configAuto, versionRequested).
func parseCustomArgs(args []string) (string, []string, map[string]string, bool, bool, bool, bool) {
	var packagePath string
	filePaths := []string{}
	otherFlags := make(map[string]string)
	configRequested := false
	helpRequested := false
	versionRequested := false
	configAuto = false

	skipNext := false
	for i := 1; i < len(args); i++ {
		if skipNext {
			skipNext = false
			continue
		}
		arg := args[i]

		// Check --help or -h
		if arg == "--help" || arg == "-h" {
			helpRequested = true
			continue
		}
		// Check --version
		if arg == "--version" {
			versionRequested = true
			continue
		}
		// Check --config-auto
		if arg == "--config-auto" {
			// This signals we want non-interactive config
			configRequested = true
			configAuto = true
			continue
		}
		// Check --config
		if arg == "--config" {
			configRequested = true
			continue
		}

		switch {
		// -i or --installs-array => next token is file path
		case arg == "-i" || arg == "--installs-array":
			if i+1 < len(args) {
				filePaths = append(filePaths, args[i+1])
				skipNext = true
			}

		// If this is something like -arch=x64 or --arch=x64
		case strings.HasPrefix(arg, "-") && strings.Contains(arg, "="):
			// Split on the first '='
			parts := strings.SplitN(arg, "=", 2)
			// The first part might be "-arch" or "--arch"; strip leading '-'
			key := strings.TrimLeft(parts[0], "-")
			val := parts[1]
			otherFlags[key] = val

		// Otherwise, check if the next arg belongs to this flag
		case strings.HasPrefix(arg, "-"):
			// Maybe user typed: -arch x64 (no '=')
			// If the next arg doesn't start with '-', treat it as this flag's value
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") {
				key := strings.TrimLeft(arg, "-")
				otherFlags[key] = args[i+1]
				skipNext = true
			} else {
				// It's a standalone boolean flag
				key := strings.TrimLeft(arg, "-")
				otherFlags[key] = "true"
			}

		// First non-flag => packagePath (main installer path)
		default:
			if packagePath == "" {
				packagePath = arg
			}
		}
	}

	return packagePath, filePaths, otherFlags, configRequested, helpRequested, configAuto, versionRequested
}

func main() {
	// Initialize logger
	logger = logging.New(false) // Set to true for verbose mode

	// 1) Patch Windows args so spaces are preserved:
	utils.PatchWindowsArgs()

	// 2) Manually parse arguments for:
	//   - The first non-flag => main installer path
	//   - -i or --installs-array => user filePaths
	//   - everything else => otherFlags
	//   - and check if user typed --config / --help / --config-auto
	packagePath, filePaths, otherFlags, configRequested, helpRequested, configAuto, versionRequested := parseCustomArgs(os.Args)

	// If user typed --version
	if versionRequested {
		version.PrintVersion()
		os.Exit(0)
	}

	// If user typed --help or -h
	if helpRequested {
		showUsageAndExit()
	}

	// If user typed --config (with optional --config-auto)
	if configRequested {
		conf, err := loadOrCreateConfig()
		if err != nil {
			logger.Error("Error loading config: %v", err)
			os.Exit(1)
		}

		// If user typed --config or --config-auto
		if configRequested {
			// If user also typed --config-auto, we *still* do interactive first
			// (i.e., `--config` overrides `--config-auto` if both are set).
			if !configAuto {
				// interactive config
				if err := configureCimianImport(conf); err != nil {
					logger.Error("Failed to save config (interactive): %v", err)
					os.Exit(1)
				}
				logger.Success("Configuration saved successfully.")
			} else {
				// non-interactive
				if err := configureCimianImportNonInteractive(conf); err != nil {
					logger.Error("Failed to save config (auto): %v", err)
					os.Exit(1)
				}
			}
			// Either path => exit
			return
		}

		// In either case, we exit after config
		return
	}

	// If packagePath was empty, prompt user:
	if packagePath == "" {
		packagePath = getInstallerPathInteractive()
		if packagePath == "" {
			logger.Error("No installer path provided; exiting.")
			os.Exit(1)
		}
	}

	// 3) Load config
	conf, err := loadOrCreateConfig()
	if err != nil {
		logger.Error("Error loading config: %v", err)
		os.Exit(1)
	}

	// 4) If user typed -arch=..., override conf.DefaultArch
	if val, ok := otherFlags["arch"]; ok && val != "" {
		conf.DefaultArch = val
	}
	// If user typed -repo_path=..., override conf.RepoPath
	if val, ok := otherFlags["repo_path"]; ok && val != "" {
		conf.RepoPath = val
	}

	// 5) Check if we're in a git repository and run git pull BEFORE import to ensure we're up to date
	if isGitRepository(conf.RepoPath) {
		logger.Printf("Git repository detected, pulling latest changes before import...")
		if err := runGitPull(conf.RepoPath); err != nil {
			logger.Warning("Git pull failed (continuing anyway): %v", err)
		}
	} else {
		logger.Printf("No git repository detected at %s, skipping git pull", conf.RepoPath)
	}

	// 6) Build script paths from e.g. -preinstall-script=..., etc.
	scripts := ScriptPaths{
		Preinstall:     otherFlags["preinstall-script"],
		Postinstall:    otherFlags["postinstall-script"],
		Preuninstall:   otherFlags["preuninstall-script"],
		Postuninstall:  otherFlags["postuninstall-script"],
		InstallCheck:   otherFlags["install-check-script"],
		UninstallCheck: otherFlags["uninstall-check-script"],
	}
	// If user typed e.g. -uninstaller="C:\some\path"
	uninstallerPath := otherFlags["uninstaller"]

	// Extract OS version flags if provided
	minOSVersion := otherFlags["minimum_os_version"]
	maxOSVersion := otherFlags["maximum_os_version"]

	// 7) Do the cimianImport
	success, err := cimianImport(
		packagePath, // the main installer
		conf,        // loaded config
		scripts,     // your script paths
		uninstallerPath,
		filePaths, // the array from -i flags
		minOSVersion,
		maxOSVersion,
	)
	if err != nil {
		logger.Error("Error in cimianImport: %v", err)
		os.Exit(1)
	}
	if !success {
		os.Exit(0) // user canceled
	}

	// 8) upload to cloud if set
	if conf.CloudProvider != "none" {
		if err := uploadToCloud(conf); err != nil {
			logger.Error("Error uploading to cloud: %v", err)
		}
	}

	// 9) Always run makecatalogs
	if err := runMakeCatalogs(true); err != nil {
		logger.Error("makecatalogs error: %v", err)
	}

	// 10) If AWS/Azure => optionally sync pkgs or icons
	if conf.CloudProvider == "aws" || conf.CloudProvider == "azure" {
		logger.Printf("Starting upload for pkgs")
		err = syncToCloud(conf, filepath.Join(conf.RepoPath, "pkgs"), "pkgs")
		if err != nil {
			logger.Error("Error syncing pkgs to %s: %v", conf.CloudProvider, err)
		}
	}

	logger.Success("Cimian import completed successfully.")
}

// getConfigPath returns the path for the config file.
func getConfigPath() string {
	return `C:\ProgramData\ManagedInstalls\Config.yaml`
}

// loadOrCreateConfig loads or prompts. Similar to your existing code.
func loadOrCreateConfig() (*config.Configuration, error) {
	conf, err := config.LoadConfig()
	if err != nil {
		configPath := getConfigPath()
		configDir := filepath.Dir(configPath)
		if _, statErr := os.Stat(configDir); os.IsNotExist(statErr) {
			if err2 := os.MkdirAll(configDir, 0755); err2 != nil {
				return nil, fmt.Errorf("failed to create config directory: %v", err2)
			}
			conf, err = config.LoadConfig()
			if err != nil {
				return nil, fmt.Errorf("failed to load config after creating dirs: %v", err)
			}
		} else {
			return nil, err
		}
	}
	return conf, nil
}

// configureCimianImport is the interactive wizard for config
func configureCimianImport(conf *config.Configuration) error {
	// conf is already loaded.
	// Instead of creating a new conf or calling config.GetDefaultConfig(),
	// just use the one passed in.

	usr, err := user.Current()
	if err != nil {
		return fmt.Errorf("failed to get current user: %v", err)
	}

	defaultRepoPath := filepath.Join(usr.HomeDir, "DevOps", "Cimian", "deployment")
	defaultCloudProvider := "none"
	defaultCatalog := "Development"
	defaultArch := "x64,arm64"

	fmt.Printf("Enter Repo Path [%s]: ", defaultRepoPath)
	var repoPathInput string
	fmt.Scanln(&repoPathInput)
	if strings.TrimSpace(repoPathInput) == "" {
		conf.RepoPath = defaultRepoPath
	} else {
		conf.RepoPath = repoPathInput
	}

	fmt.Printf("Enter Cloud Provider (aws/azure/none) [%s]: ", defaultCloudProvider)
	var cloudProviderInput string
	fmt.Scanln(&cloudProviderInput)
	if strings.TrimSpace(cloudProviderInput) == "" {
		conf.CloudProvider = defaultCloudProvider
	} else {
		conf.CloudProvider = cloudProviderInput
	}

	if conf.CloudProvider != "none" {
		fmt.Print("Enter Cloud Bucket: ")
		fmt.Scanln(&conf.CloudBucket)
	}

	fmt.Printf("Enter Default Catalog [%s]: ", defaultCatalog)
	var defaultCatalogInput string
	fmt.Scanln(&defaultCatalogInput)
	if strings.TrimSpace(defaultCatalogInput) == "" {
		conf.DefaultCatalog = defaultCatalog
	} else {
		conf.DefaultCatalog = defaultCatalogInput
	}

	fmt.Printf("Enter Default Architecture [%s]: ", defaultArch)
	var defaultArchInput string
	fmt.Scanln(&defaultArchInput)
	if strings.TrimSpace(defaultArchInput) == "" {
		conf.DefaultArch = defaultArch
	} else {
		conf.DefaultArch = defaultArchInput
	}

	fmt.Printf("Open imported YAML after creation? [true/false] (%v): ", conf.OpenImportedYaml)
	var openYamlInput string
	fmt.Scanln(&openYamlInput)
	if strings.TrimSpace(openYamlInput) != "" {
		val := strings.TrimSpace(strings.ToLower(openYamlInput))
		conf.OpenImportedYaml = (val == "true")
	}

	configPath := getConfigPath()
	configDir := filepath.Dir(configPath)
	if _, err := os.Stat(configDir); os.IsNotExist(err) {
		if mkErr := os.MkdirAll(configDir, 0755); mkErr != nil {
			return fmt.Errorf("failed to create config directory: %v", mkErr)
		}
	}
	if err := config.SaveConfig(conf); err != nil {
		return err
	}

	// Write Cimian version to registry during installation
	versionInfo := version.Version()
	if err := config.WriteCimianVersionToRegistry(versionInfo.Version); err != nil {
		// Log warning but don't fail installation
		logger.Warning("Failed to write Cimian version to registry: %v", err)
	}

	return nil
}

func configureCimianImportNonInteractive(conf *config.Configuration) error {
	// Only fill in defaults if they're currently empty/zero:

	// If no RepoPath yet, fill from the user’s profile
	if conf.RepoPath == "" {
		userProfile := os.Getenv("USERPROFILE")
		if userProfile == "" {
			usr, err := user.Current()
			if err != nil {
				return fmt.Errorf("cannot determine user profile: %v", err)
			}
			userProfile = usr.HomeDir
		}
		conf.RepoPath = filepath.Join(userProfile, "DevOps", "Cimian", "deployment")
	}
	if conf.CloudProvider == "" {
		conf.CloudProvider = "none"
	}
	if conf.DefaultCatalog == "" {
		conf.DefaultCatalog = "Development"
	}
	if conf.DefaultArch == "" {
		conf.DefaultArch = "x64,arm64"
	}
	if !conf.OpenImportedYaml {
		conf.OpenImportedYaml = true
	}

	configPath := getConfigPath()
	configDir := filepath.Dir(configPath)
	if _, err := os.Stat(configDir); os.IsNotExist(err) {
		if mkErr := os.MkdirAll(configDir, 0755); mkErr != nil {
			return fmt.Errorf("failed to create config directory: %v", mkErr)
		}
	}
	if err := config.SaveConfig(conf); err != nil {
		return fmt.Errorf("failed to save non-interactive config: %v", err)
	}

	// Write Cimian version to registry during installation
	versionInfo := version.Version()
	if err := config.WriteCimianVersionToRegistry(versionInfo.Version); err != nil {
		// Log warning but don't fail installation
		logger.Warning("Failed to write Cimian version to registry: %v", err)
	}

	return nil
}

func findMatchingItemInAllCatalog(repoPath string, newItemName string) (*PkgsInfo, bool, error) {
	// run makecatalogs silently
	if err := runMakeCatalogs(true); err != nil {
		logger.Warning("makecatalogs failed: %v", err)
	}

	allCatalogPath := filepath.Join(repoPath, "catalogs", "All.yaml")
	fileContent, err := os.ReadFile(allCatalogPath)
	if err != nil {
		return nil, false, fmt.Errorf("failed to read All.yaml: %v", err)
	}

	// Use a wrapper with `items:` for the top-level
	var wrap struct {
		Items []PkgsInfo `yaml:"items"`
	}
	if err := yaml.Unmarshal(fileContent, &wrap); err != nil {
		return nil, false, fmt.Errorf("unmarshal of All.yaml failed: %v", err)
	}

	newNameLower := strings.ToLower(strings.TrimSpace(newItemName))
	for i := range wrap.Items {
		existingNameLower := strings.ToLower(strings.TrimSpace(wrap.Items[i].Name))
		if existingNameLower == newNameLower {
			return &wrap.Items[i], true, nil
		}
	}
	return nil, false, nil
}

// getInstallerPathInteractive prompts the user for an installer path if none
// was passed on the command line. This ensures the function is used.
func getInstallerPathInteractive() string {
	fmt.Print("path to the installer file: ")
	var path string
	fmt.Scanln(&path)
	return strings.TrimSpace(path)
}

// A more robust pattern for capturing lines like:
//
//	--INSTALLDIR=somePath
//	--INSTALLDIR="C:\Some Path"
//	--INSTALLDIR='C:\Some Path'
//
// or the same with INSTALLLOCATION
var reInstallDir = regexp.MustCompile(`(?i)--INSTALL(?:DIR|LOCATION)\s*=\s*(?:"([^"]+)"|'([^']+)'|(\S+))`)

func GuessInstallDirFromBat(batPath string) string {
	data, err := os.ReadFile(batPath)
	if err != nil {
		return ""
	}
	lines := strings.Split(string(data), "\n")

	for _, line := range lines {
		line = strings.TrimSpace(line)
		if strings.Contains(strings.ToLower(line), "--installdir=") ||
			strings.Contains(strings.ToLower(line), "--installlocation=") {
			matches := reInstallDir.FindStringSubmatch(line)
			if len(matches) == 4 {
				// matches[1] => double-quoted path
				// matches[2] => single-quoted path
				// matches[3] => unquoted path
				if matches[1] != "" {
					return matches[1]
				}
				if matches[2] != "" {
					return matches[2]
				}
				if matches[3] != "" {
					return matches[3]
				}
			}
		}
	}
	return ""
}

func convertExtractItems(ei []extract.InstallItem) []InstallItem {
	var results []InstallItem
	for _, item := range ei {
		lowerPath := strings.ToLower(string(item.Path))

		// Skip the standard Chocolatey script-only paths we never want listed in installs:
		if strings.HasSuffix(lowerPath, `\tools\chocolateyinstall.ps1`) ||
			strings.HasSuffix(lowerPath, `\tools\chocolateybeforemodify.ps1`) {
			continue
		}

		results = append(results, InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(item.Path),
			MD5Checksum: SingleQuotedString(item.MD5Checksum),
			Version:     SingleQuotedString(item.Version),
		})
	}
	return results
}

func promptInstallerItemPath(defaultPath string) (string, error) {
	if defaultPath == "" {
		defaultPath = "\\mgmt"
	}
	fmt.Printf("Location in repo [%s]: ", defaultPath)
	var path string
	fmt.Scanln(&path)
	path = strings.TrimSpace(path)
	if path == "" {
		path = defaultPath
	}

	// Ensure path starts with backslash
	if !strings.HasPrefix(path, "\\") {
		path = "\\" + path
	}
	path = strings.TrimRight(path, "\\")

	// Do NOT sanitize path components here - these are directory names
	// that should remain as-is
	return path, nil
}

// cimianImport ingests an installer, writes pkgsinfo, etc.
func cimianImport(
	packagePath string,
	conf *config.Configuration,
	scripts ScriptPaths,
	uninstallerPath string,
	filePaths []string,
	minOSVersion string,
	maxOSVersion string,
) (bool, error) {

	// Step 1: check file
	if _, err := os.Stat(packagePath); os.IsNotExist(err) {
		return false, fmt.Errorf("package '%s' does not exist", packagePath)
	}

	// Step 2: extract metadata
	metadata, err := extractInstallerMetadata(packagePath, conf)
	if err != nil {
		return false, fmt.Errorf("metadata extraction failed: %v", err)
	}
	if strings.TrimSpace(metadata.ID) == "" {
		metadata.ID = parsePackageName(filepath.Base(packagePath))
	}

	// Step 3: see if item already in All.yaml
	existingPkg, found, err := findMatchingItemInAllCatalog(conf.RepoPath, metadata.ID)
	if err != nil {
		logger.Warning("Could not check existing items: %v", err)
	} else if found && existingPkg != nil {
		logger.Printf("This item has the same Name as an existing item in the repo:")
		logger.Printf("    Name: %s\n    Version: %s\n    Description: %s",
			existingPkg.Name, existingPkg.Version, existingPkg.Description)
		ans := getInput("Use existing item as a template? [Y/n]: ", "Y")
		if strings.EqualFold(ans, "y") || ans == "" {
			extractedVersion := metadata.Version

			metadata.ID = existingPkg.Name
			metadata.Title = existingPkg.DisplayName
			metadata.Version = extractedVersion
			metadata.Developer = string(existingPkg.Developer)

			// Take the template’s description but replace the old version with the newly extracted one
			desc := string(existingPkg.Description)
			desc = strings.ReplaceAll(desc, existingPkg.Version, extractedVersion)
			metadata.Description = desc

			metadata.Category = string(existingPkg.Category)
			metadata.SupportedArch = existingPkg.SupportedArch
			metadata.Catalogs = existingPkg.Catalogs

			// Bring forward scripts from the existing template if they exist and no command-line scripts were provided
			if scripts.Preinstall == "" && existingPkg.PreinstallScript != "" {
				scripts.Preinstall = "template" // Mark as coming from template
			}
			if scripts.Postinstall == "" && existingPkg.PostinstallScript != "" {
				scripts.Postinstall = "template"
			}
			if scripts.Preuninstall == "" && existingPkg.PreuninstallScript != "" {
				scripts.Preuninstall = "template"
			}
			if scripts.Postuninstall == "" && existingPkg.PostuninstallScript != "" {
				scripts.Postuninstall = "template"
			}
			if scripts.InstallCheck == "" && existingPkg.InstallCheckScript != "" {
				scripts.InstallCheck = "template"
			}
			if scripts.UninstallCheck == "" && existingPkg.UninstallCheckScript != "" {
				scripts.UninstallCheck = "template"
			}

			// Bring forward additional fields from template
			metadata.UnattendedInstall = existingPkg.UnattendedInstall
			metadata.UnattendedUninstall = existingPkg.UnattendedUninstall
			metadata.Requires = existingPkg.Requires
			metadata.UpdateFor = existingPkg.UpdateFor

			// Extract repo path from installer location
			if existingPkg.Installer != nil && existingPkg.Installer.Location != "" {
				metadata.RepoPath = filepath.Dir(existingPkg.Installer.Location)
			}
			if len(existingPkg.Catalogs) > 0 {
				conf.DefaultCatalog = existingPkg.Catalogs[0]
			}
		}
	}

	// Step 4: let user override some fields
	metadata = promptForAllMetadata(packagePath, metadata, conf)

	// Step 5: gather script contents
	preinstallScriptContent := loadScriptContentOrTemplate(scripts.Preinstall, existingPkg, "preinstall")
	postinstallScriptContent := loadScriptContentOrTemplate(scripts.Postinstall, existingPkg, "postinstall")
	preuninstallScriptContent := loadScriptContentOrTemplate(scripts.Preuninstall, existingPkg, "preuninstall")
	postuninstallScriptContent := loadScriptContentOrTemplate(scripts.Postuninstall, existingPkg, "postuninstall")
	installCheckScriptContent := loadScriptContentOrTemplate(scripts.InstallCheck, existingPkg, "installcheck")
	uninstallCheckScriptContent := loadScriptContentOrTemplate(scripts.UninstallCheck, existingPkg, "uninstallcheck")

	// Step 6: handle uninstaller if any
	uninstaller, err := processUninstaller(uninstallerPath,
		filepath.Join(conf.RepoPath, "pkgs"), "apps")
	if err != nil {
		return false, fmt.Errorf("uninstaller processing failed: %v", err)
	}

	// Step 7: file hash + size
	fileHash, err := utils.FileSHA256(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to calculate file hash: %v", err)
	}
	stat, err := os.Stat(packagePath)
	if err != nil {
		return false, fmt.Errorf("failed to stat installer: %v", err)
	}
	fileSizeKB := stat.Size() / 1024

	// Step 8: build PkgsInfo
	displayName := metadata.ID                 // Keep spaces in display name
	sanitizedName := sanitizeName(metadata.ID) // Remove spaces for file paths

	pkgsInfo := PkgsInfo{
		Name:          sanitizedName, // Use sanitized version for Name
		DisplayName:   displayName,   // Keep spaces in DisplayName
		Identifier:    identifierFlag,
		Version:       metadata.Version,
		Description:   NoQuoteEmptyString(metadata.Description),
		Category:      NoQuoteEmptyString(metadata.Category),
		Developer:     NoQuoteEmptyString(metadata.Developer),
		SupportedArch: metadata.SupportedArch,
		Catalogs:      metadata.Catalogs,
		Installs:      []InstallItem{},
		MinOSVersion:  minOSVersion,
		MaxOSVersion:  maxOSVersion,

		Installer: &Installer{
			Hash:        fileHash,
			Type:        metadata.InstallerType,
			Size:        fileSizeKB,
			ProductCode: strings.TrimSpace(metadata.ProductCode),
			UpgradeCode: strings.TrimSpace(metadata.UpgradeCode),
		},
		Uninstaller:         uninstaller,
		UnattendedInstall:   metadata.UnattendedInstall,
		UnattendedUninstall: metadata.UnattendedUninstall,
		Requires:            metadata.Requires,
		UpdateFor:           metadata.UpdateFor,

		PreinstallScript:     preinstallScriptContent,
		PostinstallScript:    postinstallScriptContent,
		PreuninstallScript:   preuninstallScriptContent,
		PostuninstallScript:  postuninstallScriptContent,
		InstallCheckScript:   installCheckScriptContent,
		UninstallCheckScript: uninstallCheckScriptContent,
	}

	// ─── decide architecture tag ────────────────────────────────────────────────
	archTag := ""
	if len(pkgsInfo.SupportedArch) == 1 {
		// Include arch tag when there's exactly one architecture
		primaryArch := strings.ToLower(pkgsInfo.SupportedArch[0])
		archTag = "-" + primaryArch + "-"
	} else {
		// For multiple architectures, just use a separator dash
		archTag = "-"
	}

	// Step 9: prompt user for subdir
	repoSubPath, err := promptInstallerItemPath(metadata.RepoPath)
	if err != nil {
		return false, err
	}

	// Step 10: decide final installs
	var finalInstalls []InstallItem
	if len(filePaths) > 0 {
		// user-provided -i => skip fallback
		finalInstalls = buildInstallsArray(filePaths)
	} else if metadata.InstallerType == "exe" {
		fallbackExe := fmt.Sprintf(`C:\Program Files\%s\%s.exe`,
			pkgsInfo.Name, pkgsInfo.Name)
		fmt.Println("Using fallback .exe =>", fallbackExe)
		finalInstalls = []InstallItem{{
			Type:    SingleQuotedString("file"),
			Path:    SingleQuotedString(fallbackExe),
			Version: SingleQuotedString(pkgsInfo.Version),
		}}
	}

	// If there's e.g. nupkg-based installs
	if len(metadata.Installs) > 0 {
		finalInstalls = append(finalInstalls, metadata.Installs...)
	}
	pkgsInfo.Installs = finalInstalls

	// Step 11: show final details
	fmt.Printf("\nPkginfo details:\n") // Added newline after the colon
	// Use fmt.Printf instead of logger.Printf to avoid timestamps
	fmt.Printf("     Name: %s\n", pkgsInfo.Name)
	fmt.Printf("     Display Name: %s\n", pkgsInfo.DisplayName)
	fmt.Printf("     Version: %s\n", pkgsInfo.Version)
	fmt.Printf("     Description: %s\n", pkgsInfo.Description)
	fmt.Printf("     Category: %s\n", pkgsInfo.Category)
	fmt.Printf("     Developer: %s\n", pkgsInfo.Developer)
	fmt.Printf("     Architectures: %s\n", strings.Join(pkgsInfo.SupportedArch, ", "))
	fmt.Printf("     Catalogs: %s\n", strings.Join(pkgsInfo.Catalogs, ", "))
	fmt.Printf("     Installer Type: %s\n\n", pkgsInfo.Installer.Type)

	confirm := getInput("Import this item? (y/n): ", "n")
	if !strings.EqualFold(confirm, "y") {
		logger.Printf("Import canceled.")
		return false, nil
	}

	// Step 12: copy installer to pkgs subdir
	logger.Debug("Copying installer to repo...")
	repoSubPath = strings.TrimPrefix(repoSubPath, "\\") // Remove leading backslash for joining
	installerFolderPath := filepath.Join(conf.RepoPath, "pkgs", repoSubPath)
	if err := os.MkdirAll(installerFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create installer directory: %v", err)
	}
	installerFilename := sanitizedName + archTag + pkgsInfo.Version + filepath.Ext(packagePath)
	installerDest := filepath.Join(installerFolderPath, installerFilename)

	if _, err := copyFile(packagePath, installerDest); err != nil {
		return false, fmt.Errorf("failed to copy installer: %v", err)
	}
	// Use utils.NormalizeWindowsPath instead of local normalizeInstallerLocation
	subpathAndFile := filepath.Join(repoSubPath, installerFilename)
	pkgsInfo.Installer.Location = utils.NormalizeWindowsPath(subpathAndFile)

	// Step 13: write pkginfo to pkgsinfo subdir
	logger.Debug("Writing pkginfo file...")
	pkginfoFolderPath := filepath.Join(conf.RepoPath, "pkgsinfo", repoSubPath)
	if err := os.MkdirAll(pkginfoFolderPath, 0755); err != nil {
		return false, fmt.Errorf("failed to create pkginfo directory: %v", err)
	}

	err = writePkgInfoFile(pkginfoFolderPath, pkgsInfo,
		pkgsInfo.Name, pkgsInfo.Version, archTag)
	if err != nil {
		return false, fmt.Errorf("failed to write final pkginfo: %v", err)
	}

	logger.Success("Installer imported successfully!")
	return true, nil
}

// extractInstallerMetadata for the main installer
func extractInstallerMetadata(packagePath string, conf *config.Configuration) (Metadata, error) {
	ext := strings.ToLower(filepath.Ext(packagePath))
	var metadata Metadata

	switch ext {
	case ".nupkg":
		ident, name, ver, dev, desc := extract.NupkgMetadata(packagePath)

		// For reverse domain identifiers, only keep the last part after the dot
		if strings.Contains(ident, ".") {
			parts := strings.Split(ident, ".")
			metadata.ID = parts[len(parts)-1]
		} else {
			metadata.ID = ident
		}

		metadata.Title = name
		metadata.Version = parseVersion(ver) // Normalize version to YYYY.MM.DD format
		metadata.Developer = dev
		metadata.Description = desc
		metadata.InstallerType = "nupkg"

		// Attempt to build Cimian-based paths
		cimianItems, err := extract.BuildCimianPkgInstalls(packagePath, metadata.ID, metadata.Version)
		if err != nil {
			fmt.Printf("Warning: BuildCimianPkgInstalls failed: %v\n", err)
		}

		if len(cimianItems) > 0 {
			// Cimian-based .nupkg
			metadata.Installs = convertExtractItems(cimianItems)
		} else {
			// Fallback: standard Chocolatey style
			chocoItems := extract.BuildNupkgInstalls(packagePath, metadata.ID, metadata.Version)
			metadata.Installs = convertExtractItems(chocoItems)
			fmt.Printf("No cimianpkg items found; using %d chocolatey items.\n", len(chocoItems))
		}

	case ".msi":
		name, ver, dev, desc, prodCode, upgCode := extract.MsiMetadata(packagePath)
		metadata.Title = name
		metadata.ID = name
		metadata.Version = parseVersion(ver) // Normalize version to YYYY.MM.DD format
		metadata.Developer = dev
		metadata.Description = desc
		metadata.InstallerType = "msi"
		metadata.ProductCode = prodCode
		metadata.UpgradeCode = upgCode

	case ".msix":
		// For now, use a simple fallback since MSIX metadata extraction might require a separate tool/API.
		metadata.Title = parsePackageName(filepath.Base(packagePath))
		metadata.ID = metadata.Title
		metadata.Version = "1.0.0" // or try to extract version via another method
		metadata.Developer = ""
		metadata.Description = ""
		metadata.InstallerType = "msix"

	case ".exe":
		versionString, err := extract.ExeMetadata(packagePath)
		devName, devErr := extract.ExtractExeDeveloper(packagePath)

		metadata.Title = parsePackageName(filepath.Base(packagePath))
		metadata.ID = metadata.Title
		if err != nil {
			metadata.Version = ""
		} else {
			metadata.Version = parseVersion(versionString) // Normalize version to YYYY.MM.DD format
		}

		if devErr == nil && devName != "" {
			metadata.Developer = devName
		} else {
			metadata.Developer = ""
		}

		metadata.Description = ""
		metadata.InstallerType = "exe"

	default:
		metadata.InstallerType = "unknown"
		metadata.Title = parsePackageName(filepath.Base(packagePath))
		metadata.ID = metadata.Title
		metadata.Version = "1.0.0"
	}

	// Ensure architecture is set
	if metadata.Architecture == "" {
		metadata.Architecture = conf.DefaultArch
	}

	// Parse architectures from conf.DefaultArch (could be comma-separated like "x64,arm64")
	if strings.Contains(metadata.Architecture, ",") {
		// Split comma-separated architectures
		parts := strings.Split(metadata.Architecture, ",")
		var archList []string
		for _, part := range parts {
			arch := strings.ToLower(strings.TrimSpace(part))
			if arch != "" {
				archList = append(archList, arch)
			}
		}
		metadata.SupportedArch = archList
		if len(archList) > 0 {
			metadata.Architecture = archList[0] // Set primary arch to first one
		}
	} else {
		metadata.SupportedArch = []string{metadata.Architecture}
	}
	return metadata, nil
}

// readLineWithDefault prints e.g. `prompt [defaultVal]: `, reads a full line,
// and if the user typed nothing, returns defaultVal.
func readLineWithDefault(r *bufio.Reader, prompt, defaultVal string) string {
	fmt.Printf("%s [%s]: ", prompt, defaultVal)
	line, err := r.ReadString('\n')
	if err != nil {
		// any read error, fallback
		return defaultVal
	}
	line = strings.TrimSpace(line)
	if line == "" {
		return defaultVal
	}
	return line
}

func promptForAllMetadata(packagePath string, m Metadata, conf *config.Configuration) Metadata {
	reader := bufio.NewReader(os.Stdin)

	// Pre-define fallback strings
	defaultID := m.ID
	if defaultID == "" {
		defaultID = parsePackageName(filepath.Base(packagePath))
	}
	defaultVersion := m.Version
	if defaultVersion == "" {
		defaultVersion = "1.0.0"
	}
	defaultDeveloper := m.Developer
	defaultDescription := m.Description
	defaultCategory := m.Category

	// read each field
	m.ID = readLineWithDefault(reader, "Name", defaultID)
	m.Version = parseVersion(readLineWithDefault(reader, "Version", defaultVersion)) // Normalize version input
	m.Developer = readLineWithDefault(reader, "Developer", defaultDeveloper)
	m.Description = readLineWithDefault(reader, "Description", defaultDescription)
	m.Category = readLineWithDefault(reader, "Category", defaultCategory)

	// ─── Architecture(s) ────────────────────────────────────────────────────────
	archLine := readLineWithDefault(reader, "Architecture(s)", strings.Join(m.SupportedArch, ","))
	archLine = strings.TrimSpace(archLine) // allow blank to keep default

	if archLine != "" {
		// accept comma/space/semicolon separators – e.g. "arm64", "x64,arm64", "x64 arm64"
		parts := strings.FieldsFunc(archLine, func(r rune) bool {
			return r == ',' || r == ';' || r == ' ' || r == '\t'
		})

		for i := range parts {
			parts[i] = strings.ToLower(strings.TrimSpace(parts[i]))
		}

		m.SupportedArch = parts   // drives filename/tag creation
		m.Architecture = parts[0] // primary arch for metadata
	}

	// For catalogs: use template catalogs if available, otherwise use defaults
	var fallbackCatalogs []string
	if len(m.Catalogs) > 0 {
		// Use catalogs from template
		fallbackCatalogs = m.Catalogs
	} else {
		// Use default catalogs
		fallbackCatalogs = []string{conf.DefaultCatalog}
		if len(fallbackCatalogs) == 0 {
			fallbackCatalogs = []string{"Development"}
		}
	}

	// Join with commas for display
	fallbackCatalogsStr := strings.Join(fallbackCatalogs, ", ")
	typedCatalogs := readLineWithDefault(reader, "Catalogs", fallbackCatalogsStr)
	if typedCatalogs == fallbackCatalogsStr {
		// user pressed Enter
		m.Catalogs = fallbackCatalogs
	} else {
		// user typed something => parse
		parts := strings.Split(typedCatalogs, ",")
		for i := range parts {
			parts[i] = strings.TrimSpace(parts[i])
		}
		m.Catalogs = parts
	}

	return m
}

func parsePackageName(filename string) string {
	return strings.TrimSuffix(filename, filepath.Ext(filename))
}

// loadScriptContent reads script or returns empty
func loadScriptContent(path string) (string, error) {
	if path == "" {
		return "", nil
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return string(b), nil
}

// loadScriptContentOrTemplate loads script content from file path or from template
func loadScriptContentOrTemplate(path string, existingPkg *PkgsInfo, scriptType string) string {
	if path == "" {
		return ""
	}

	// If marked as template, get content from existing package
	if path == "template" && existingPkg != nil {
		switch scriptType {
		case "preinstall":
			return existingPkg.PreinstallScript
		case "postinstall":
			return existingPkg.PostinstallScript
		case "preuninstall":
			return existingPkg.PreuninstallScript
		case "postuninstall":
			return existingPkg.PostuninstallScript
		case "installcheck":
			return existingPkg.InstallCheckScript
		case "uninstallcheck":
			return existingPkg.UninstallCheckScript
		}
		return ""
	}

	// Otherwise load from file path
	content, _ := loadScriptContent(path)
	return content
}

// processUninstaller copies the uninstaller if provided
func processUninstaller(uninstallerPath, pkgsFolderPath, installerSubPath string) (*Installer, error) {
	if uninstallerPath == "" {
		return nil, nil
	}
	if _, err := os.Stat(uninstallerPath); os.IsNotExist(err) {
		return nil, fmt.Errorf("uninstaller '%s' does not exist", uninstallerPath)
	}
	uninstallerHash, err := utils.FileSHA256(uninstallerPath)
	if err != nil {
		return nil, fmt.Errorf("error calculating uninstaller hash: %v", err)
	}
	uninstallerFilename := filepath.Base(uninstallerPath)
	uninstallerDest := filepath.Join(pkgsFolderPath, uninstallerFilename)
	if _, err := copyFile(uninstallerPath, uninstallerDest); err != nil {
		return nil, fmt.Errorf("failed to copy uninstaller: %v", err)
	}
	// Use utils.NormalizeWindowsPath here too
	return &Installer{
		Location: utils.NormalizeWindowsPath(filepath.Join("/", installerSubPath, uninstallerFilename)),
		Hash:     uninstallerHash,
		Type:     strings.TrimPrefix(filepath.Ext(uninstallerPath), "."),
	}, nil
}

// copyFile from src to dst
func copyFile(src, dst string) (int64, error) {
	in, err := os.Open(src)
	if err != nil {
		return 0, err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return 0, err
	}
	defer out.Close()
	n, err := io.Copy(out, in)
	if err != nil {
		return 0, err
	}
	return n, out.Sync()
}

// buildInstallsArray processes -i items
func buildInstallsArray(paths []string) []InstallItem {
	var arr []InstallItem
	for _, p := range paths {
		abs, _ := filepath.Abs(p)

		// 1) Check file existence on the real path
		fi, err := os.Stat(abs)
		if err != nil || fi.IsDir() {
			fmt.Fprintf(os.Stderr, "Skipping -i path: '%s'\n", p)
			continue
		}

		// 2) Compute MD5 on the real path
		md5v, _ := utils.FileMD5(abs)

		// 3) Optionally get file version from the real path
		var ver string
		if runtime.GOOS == "windows" && strings.EqualFold(filepath.Ext(abs), ".exe") {
			if exev, err := extract.ExeMetadata(abs); err == nil {
				ver = exev
			}
		}

		// 4) Rewrite to %USERPROFILE% AFTER checks
		finalPath := replacePathUserProfile(abs)

		// 5) Build the final InstallItem
		arr = append(arr, InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(finalPath),
			MD5Checksum: SingleQuotedString(md5v),
			Version:     SingleQuotedString(ver),
		})
	}
	return arr
}

// getInput reads input with a default
func getInput(prompt, defaultVal string) string {
	fmt.Printf("%s [%s]: ", prompt, defaultVal)
	var input string
	fmt.Scanln(&input)
	input = strings.TrimSpace(input)
	if input == "" {
		return defaultVal
	}
	return input
}

// isGitRepository checks if the given path is inside a git repository
func isGitRepository(path string) bool {
	// Check for .git directory starting from the given path and walking up
	currentPath := path
	for {
		gitPath := filepath.Join(currentPath, ".git")
		if info, err := os.Stat(gitPath); err == nil && info.IsDir() {
			return true
		}

		// Move up one directory
		parentPath := filepath.Dir(currentPath)
		if parentPath == currentPath {
			// Reached the root, no .git found
			break
		}
		currentPath = parentPath
	}
	return false
}

// runGitPull runs git pull in the specified directory
func runGitPull(repoPath string) error {
	cmd := exec.Command("git", "pull")
	cmd.Dir = repoPath
	cmd.Stdout = os.Stdout

	// Capture stderr to filter out credential manager warnings
	var stderrBuf bytes.Buffer
	cmd.Stderr = &stderrBuf

	logger.Printf("Running git pull in: %s", repoPath)
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("git pull failed: %v", err)
	}

	// Filter stderr output to suppress credential manager warnings
	stderrOutput := stderrBuf.String()
	if stderrOutput != "" {
		lines := strings.Split(stderrOutput, "\n")
		for _, line := range lines {
			line = strings.TrimSpace(line)
			// Skip credential manager warnings but show other stderr messages
			if line != "" && !strings.Contains(line, "credential-manager-core") {
				fmt.Fprintf(os.Stderr, "%s\n", line)
			}
		}
	}

	logger.Success("Git pull completed successfully")
	return nil
}

// runMakeCatalogs runs makecatalogs.exe
func runMakeCatalogs(silent bool) error {
	makeCatalogsBinary := `C:\Program Files\Cimian\makecatalogs.exe`
	if _, err := os.Stat(makeCatalogsBinary); os.IsNotExist(err) {
		return fmt.Errorf("makecatalogs binary not found at %s", makeCatalogsBinary)
	}
	args := []string{}
	if silent {
		args = append(args, "-silent")
	}
	cmd := exec.Command(makeCatalogsBinary, args...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	fmt.Printf("Running makecatalogs from: %s\n", makeCatalogsBinary)
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("makecatalogs execution failed: %v", err)
	}
	return nil
}

// uploadToCloud handles AWS/Azure sync
func uploadToCloud(conf *config.Configuration) error {
	localPkgsPath := filepath.Join(conf.RepoPath, "pkgs")
	localIconsPath := filepath.Join(conf.RepoPath, "icons")

	if conf.CloudProvider == "aws" {
		// Use the variable in this branch so it's no longer unused
		pkgsDestination := fmt.Sprintf("s3://%s/pkgs/", conf.CloudBucket)
		cmdPkgs := exec.Command("aws", "s3", "sync", localPkgsPath, pkgsDestination, "--exclude", "*.git/*", "--exclude", "**/.DS_Store")
		cmdPkgs.Stdout = os.Stdout
		cmdPkgs.Stderr = os.Stderr
		if err := cmdPkgs.Run(); err != nil {
			return fmt.Errorf("failed to sync pkgs to AWS S3: %v", err)
		}

		iconsDestination := fmt.Sprintf("s3://%s/icons/", conf.CloudBucket)
		cmdIcons := exec.Command("aws", "s3", "sync", localIconsPath, iconsDestination, "--exclude", "*.git/*", "--exclude", "**/.DS_Store")
		cmdIcons.Stdout = os.Stdout
		cmdIcons.Stderr = os.Stderr
		if err := cmdIcons.Run(); err != nil {
			return fmt.Errorf("failed to sync icons to AWS S3: %v", err)
		}
	} else if conf.CloudProvider == "azure" {
		pkgsDestination := fmt.Sprintf("https://%s/pkgs/", conf.CloudBucket)
		cmdPkgs := exec.Command("azcopy", "sync", localPkgsPath, pkgsDestination, "--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")
		cmdPkgs.Stdout = os.Stdout
		cmdPkgs.Stderr = os.Stderr
		if err := cmdPkgs.Run(); err != nil {
			return fmt.Errorf("failed to sync pkgs to Azure: %v", err)
		}

		iconsDestination := fmt.Sprintf("https://%s/icons/", conf.CloudBucket)
		cmdIcons := exec.Command("azcopy", "sync", localIconsPath, iconsDestination, "--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")
		cmdIcons.Stdout = os.Stdout
		cmdIcons.Stderr = os.Stderr
		if err := cmdIcons.Run(); err != nil {
			return fmt.Errorf("failed to sync icons to Azure: %v", err)
		}
	} else {
		return fmt.Errorf("unsupported cloud provider: %s", conf.CloudProvider)
	}
	return nil
}

// syncToCloud can handle a single subfolder
func syncToCloud(conf *config.Configuration, source, destinationSubPath string) error {
	var destination string
	if conf.CloudProvider == "aws" {
		destination = fmt.Sprintf("s3://%s/%s/", conf.CloudBucket, destinationSubPath)
	} else if conf.CloudProvider == "azure" {
		destination = fmt.Sprintf("https://%s/%s/", conf.CloudBucket, destinationSubPath)
	} else {
		return fmt.Errorf("unsupported cloud provider: %s", conf.CloudProvider)
	}

	if conf.CloudProvider == "aws" {
		cmd := exec.Command("aws", "s3", "sync", source, destination, "--exclude", "*.git/*", "--exclude", "**/.DS_Store")
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("error syncing %s to S3: %v", destinationSubPath, err)
		}
	} else if conf.CloudProvider == "azure" {
		cmd := exec.Command("azcopy", "sync", source, destination, "--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("error syncing %s to Azure: %v", destinationSubPath, err)
		}
	}

	fmt.Printf("Successfully synced %s to %s\n", source, destination)
	return nil
}

func encodeWithSelectiveBlockScalars(pkgsInfo PkgsInfo) ([]byte, error) {
	// Encode pkgsInfo -> YAML (in memory)
	var buf bytes.Buffer
	encoder := yaml.NewEncoder(&buf)
	encoder.SetIndent(2)
	if err := encoder.Encode(&pkgsInfo); err != nil {
		return nil, fmt.Errorf("failed to encode pkginfo: %v", err)
	}
	encoder.Close()

	// Decode into a yaml.Node so we can tweak styles
	node := &yaml.Node{}
	decoder := yaml.NewDecoder(&buf)
	if err := decoder.Decode(node); err != nil {
		return nil, fmt.Errorf("failed to decode re-encoded pkginfo: %v", err)
	}

	// Usually node.Content[0] is the root mapping
	if node.Kind == yaml.DocumentNode && len(node.Content) > 0 {
		node = node.Content[0]
	}

	// We’ll keep your existing scriptFields
	scriptFields := map[string]bool{
		"preinstall_script":     true,
		"postinstall_script":    true,
		"preuninstall_script":   true,
		"postuninstall_script":  true,
		"installcheck_script":   true,
		"uninstallcheck_script": true,
	}

	// Fields to always force plain/unquoted
	forcePlainUnquoted := map[string]bool{
		"description": true,
		"developer":   true,
		"category":    true,
	}

	// Iterate top-level mappings (key/value pairs)
	for i := 0; i < len(node.Content); i += 2 {
		keyNode := node.Content[i]
		valNode := node.Content[i+1]

		// (A) For script fields, literal block style if multiline
		if scriptFields[keyNode.Value] && valNode.Kind == yaml.ScalarNode && valNode.Value != "" {
			valNode.Value = strings.ReplaceAll(valNode.Value, "\r\n", "\n")
			valNode.Value = strings.ReplaceAll(valNode.Value, "\r", "\n")
			valNode.Value = strings.TrimRight(valNode.Value, "\n")
			valNode.Style = yaml.LiteralStyle
			continue
		}

		// (B) For fields we want unquoted:
		if forcePlainUnquoted[keyNode.Value] && valNode.Kind == yaml.ScalarNode {
			// Force plain style => no quotes
			valNode.Style = 0

			// If it's literally `""`, strip them => set Value = ""
			trimmed := strings.TrimSpace(valNode.Value)
			if trimmed == `""` {
				valNode.Value = ""
			}
		}
	}

	// Re-encode the modified node
	finalBuf := &bytes.Buffer{}
	finalEncoder := yaml.NewEncoder(finalBuf)
	finalEncoder.SetIndent(2)
	if err := finalEncoder.Encode(node); err != nil {
		return nil, fmt.Errorf("failed to re-encode pkginfo with block scalars: %v", err)
	}
	finalEncoder.Close()

	// 1) Convert to string
	yamlStr := finalBuf.String()

	// 2) Forcefully remove quotes for empty description/developer/category
	//    "description: \"\"" => "description:"
	replacer := strings.NewReplacer(
		`description: ""`, `description:`,
		`developer: ""`, `developer:`,
		`category: ""`, `category:`,
	)
	yamlStr = replacer.Replace(yamlStr)

	// 3) Return the final bytes
	return []byte(yamlStr), nil

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

// writePkgInfoFile writes the final YAML
func writePkgInfoFile(outputDir string, pkgsInfo PkgsInfo, sanitizedName, sanitizedVersion, archTag string) error {
	// First ensure outputDir is an absolute path and normalize it
	absOutputDir, err := filepath.Abs(outputDir)
	if err != nil {
		return fmt.Errorf("failed to get absolute path: %v", err)
	}

	// Ensure we have a properly formatted Windows path
	absOutputDir = strings.ReplaceAll(absOutputDir, "/", "\\")

	// Create pkginfo filename with sanitized components
	outputPath := filepath.Join(absOutputDir,
		sanitizeName(sanitizedName)+
			archTag+
			sanitizeName(sanitizedVersion)+
			".yaml")

	// Ensure directory exists
	if err := os.MkdirAll(filepath.Dir(outputPath), 0755); err != nil {
		return fmt.Errorf("failed to create directory: %v", err)
	}

	yamlData, err := encodeWithSelectiveBlockScalars(pkgsInfo)
	if err != nil {
		return fmt.Errorf("failed to encode pkginfo with block scalars: %v", err)
	}

	if err := os.WriteFile(outputPath, yamlData, 0644); err != nil {
		return fmt.Errorf("failed to write pkginfo to file: %v", err)
	}

	// Get clean path for display
	displayPath := strings.ReplaceAll(outputPath, "/", "\\")
	fmt.Printf("Pkginfo created at: %s\n", displayPath)

	if err := maybeOpenFile(outputPath); err != nil {
		fmt.Printf("Warning: could not open pkginfo in an editor: %v\n", err)
	}

	return nil
}

// maybeOpenFile tries code.cmd or notepad
func maybeOpenFile(filePath string) error {
	codeCmd, err := exec.LookPath("code.cmd")
	if err != nil {
		return exec.Command("notepad.exe", filePath).Start()
	}
	return exec.Command(codeCmd, filePath).Start()
}

func showUsageAndExit() {
	fmt.Println(`Usage: cimiimport.exe [options] <installerPath>

Manual argument parsing is used, so the first non-flag argument is assumed 
to be the main installer path. Example:

  cimiimport.exe "C:\Path With Spaces\SomeInstaller.exe" -i "C:\Also With Spaces\someFile.txt"

Options:
  -i, --installs-array <path>   Add a path to final 'installs' array (multiple OK)
  --repo_path=<path>            Override the Cimian repo path
  --arch=<arch>                 Override architecture (e.g. x64,arm64)
  --uninstaller=<path>          Specify an optional uninstaller
  --minimum_os_version=<version> Minimum Windows version required (e.g. 10.0.19041)
  --maximum_os_version=<version> Maximum Windows version supported (e.g. 11.0.22000)
  --install-check-script=<path> ...
  --uninstall-check-script=<path> ...
  --preinstall-script=<path>    ...
  --postinstall-script=<path>   ...
  --preuninstall-script=<path>  ...
  --postuninstall-script=<path> ...
  --config                      Run interactive configuration setup and exit
  -h, --help                    Show this usage and exit

If you specify both an installer path and one or more -i/--installs-array 
flags, the final PkgsInfo will incorporate your user-provided filePaths 
(and skip the .exe fallback).`)
	os.Exit(0)
}

// sanitizeName replaces spaces with dashes and removes any invalid chars
func sanitizeName(name string) string {
	// Replace spaces with dashes
	name = strings.ReplaceAll(name, " ", "-")
	// Remove any other potentially problematic characters
	name = strings.Map(func(r rune) rune {
		switch {
		case r >= 'a' && r <= 'z',
			r >= 'A' && r <= 'Z',
			r >= '0' && r <= '9',
			r == '-', r == '_', r == '.':
			return r
		}
		return '-'
	}, name)
	return name
}
