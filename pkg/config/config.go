// pkg/config/config.go - configuration settings for Cimian.

package config

import (
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"golang.org/x/sys/windows/registry"
	"gopkg.in/yaml.v3"
)

const ConfigPath = `C:\ProgramData\ManagedInstalls\Config.yaml`

// CSP OMA-URI registry path for enterprise policy configuration
const CSPRegistryPath = `SOFTWARE\Cimian\Config`

// Registry path for Cimian installation information
const CimianRegistryPath = `SOFTWARE\Cimian`

// Configuration holds the configurable options for Cimian in YAML format
type Configuration struct {
	Catalogs                []string `yaml:"Catalogs"`
	CatalogsPath            string   `yaml:"CatalogsPath"`
	CachePath               string   `yaml:"CachePath"`
	CheckOnly               bool     `yaml:"CheckOnly"`
	ClientIdentifier        string   `yaml:"ClientIdentifier"`
	CloudBucket             string   `yaml:"CloudBucket"`
	CloudProvider           string   `yaml:"CloudProvider"`
	Debug                   bool     `yaml:"Debug"`
	DefaultArch             string   `yaml:"DefaultArch"`
	DefaultCatalog          string   `yaml:"DefaultCatalog"`
	ForceBasicAuth          bool     `yaml:"ForceBasicAuth"`
	InstallPath             string   `yaml:"InstallPath"`
	LocalManifests          []string `yaml:"LocalManifests"`
	LocalOnlyManifest       string   `yaml:"LocalOnlyManifest"`
	LogLevel                string   `yaml:"LogLevel"`
	NoPreflight             bool     `yaml:"NoPreflight"`
	NoPostflight            bool     `yaml:"NoPostflight"`
	PreflightFailureAction  string   `yaml:"PreflightFailureAction"`
	PostflightFailureAction string   `yaml:"PostflightFailureAction"`
	OpenImportedYaml        bool     `yaml:"OpenImportedYaml"`
	RepoPath                string   `yaml:"RepoPath"`
	SoftwareRepoURL         string   `yaml:"SoftwareRepoURL"`
	Verbose                 bool     `yaml:"Verbose"`

	// Installer timeout settings
	InstallerTimeoutMinutes int `yaml:"InstallerTimeoutMinutes"` // Default timeout for installers (in minutes)

	// PowerShell execution policy settings
	// Forces execution policy bypass for all PowerShell script executions to prevent OS execution policy restrictions
	ForceExecutionPolicyBypass bool `yaml:"ForceExecutionPolicyBypass"` // Force -ExecutionPolicy Bypass on all PowerShell executions (default: true)

	// Cache management settings
	CacheMaxSizeGB              int  `yaml:"CacheMaxSizeGB"`              // Maximum cache size in GB (default: 10GB)
	CacheRetentionDays          int  `yaml:"CacheRetentionDays"`          // Number of days to retain cached files (default: 1 day)
	CacheCleanupOnStartup       *bool `yaml:"CacheCleanupOnStartup"`       // Perform cache cleanup on startup (default: true)
	CachePreserveInstalledItems *bool `yaml:"CachePreserveInstalledItems"` // Keep cache for currently installed items (default: true)

	// Package installer settings
	// Controls which installer system to use for .nupkg and .pkg packages
	ForceChocolatey         bool   `yaml:"ForceChocolatey"`         // Force Chocolatey for all package installations (default: false)
	PreferSbinInstaller     bool   `yaml:"PreferSbinInstaller"`     // Prefer sbin-installer over Chocolatey when available (default: true)
	SbinInstallerPath       string `yaml:"SbinInstallerPath"`       // Override path to installer.exe (default: auto-detect from PATH)
	SbinInstallerTargetRoot string `yaml:"SbinInstallerTargetRoot"` // Default target root for sbin-installer installations (default: "/")

	// .pkg package format settings
	// Signature verification and security policies for .pkg packages
	PkgRequireSignature        bool     `yaml:"PkgRequireSignature"`        // Require cryptographic signatures for all .pkg packages (default: true)
	PkgRequireTrustedCert      bool     `yaml:"PkgRequireTrustedCert"`      // Require signatures from trusted certificate authorities (default: true)
	PkgTrustedCertThumbprints  []string `yaml:"PkgTrustedCertThumbprints"`  // List of trusted certificate thumbprints for .pkg packages
	PkgTrustedCertCommonNames  []string `yaml:"PkgTrustedCertCommonNames"`  // List of trusted certificate common names for .pkg packages
	PkgAllowUnsignedDevelopers []string `yaml:"PkgAllowUnsignedDevelopers"` // List of developer names allowed to install unsigned .pkg packages
	PkgSignatureTimestampGrace int      `yaml:"PkgSignatureTimestampGrace"` // Grace period in days for timestamp validation (default: 30)
	
	// .pkg installer preferences
	PkgPreferSbinInstaller     bool   `yaml:"PkgPreferSbinInstaller"`     // Prefer sbin-installer for .pkg packages (default: true)
	PkgSbinInstallerArgs       string `yaml:"PkgSbinInstallerArgs"`       // Additional arguments for sbin-installer with .pkg packages
	PkgSignatureValidationMode string `yaml:"PkgSignatureValidationMode"` // Signature validation mode: "strict", "warn", "off" (default: "strict")
	PkgExtractTempPath         string `yaml:"PkgExtractTempPath"`         // Temporary extraction path for .pkg packages (default: system temp)

	// Internal flag to skip self-service manifest processing (not exposed in YAML)
	SkipSelfService bool `yaml:"-"`
}

// GetCacheCleanupOnStartup returns the cache cleanup on startup setting, with true as default
func (c *Configuration) GetCacheCleanupOnStartup() bool {
	if c.CacheCleanupOnStartup == nil {
		return true // Default to enabled
	}
	return *c.CacheCleanupOnStartup
}

// GetCachePreserveInstalledItems returns the cache preserve installed items setting, with true as default
func (c *Configuration) GetCachePreserveInstalledItems() bool {
	if c.CachePreserveInstalledItems == nil {
		return true // Default to enabled
	}
	return *c.CachePreserveInstalledItems
}

// GetPreferSbinInstaller returns whether to prefer sbin-installer over Chocolatey, with true as default
func (c *Configuration) GetPreferSbinInstaller() bool {
	// If Chocolatey is explicitly forced, don't prefer sbin-installer
	if c.ForceChocolatey {
		return false
	}
	// Default to preferring sbin-installer when it becomes available
	return c.PreferSbinInstaller || c.PreferSbinInstaller == false // Default: true
}

// GetSbinInstallerTargetRoot returns the target root for sbin-installer installations, with "/" as default
func (c *Configuration) GetSbinInstallerTargetRoot() string {
	if c.SbinInstallerTargetRoot == "" {
		return "/" // Default to system root
	}
	return c.SbinInstallerTargetRoot
}

// GetPkgRequireSignature returns whether .pkg packages require cryptographic signatures, with true as default
func (c *Configuration) GetPkgRequireSignature() bool {
	// Default to requiring signatures for security
	return c.PkgRequireSignature || !c.PkgRequireSignature // This will default to true
}

// GetPkgRequireTrustedCert returns whether .pkg signatures must be from trusted CAs, with true as default
func (c *Configuration) GetPkgRequireTrustedCert() bool {
	// Default to requiring trusted certificate authorities
	return c.PkgRequireTrustedCert || !c.PkgRequireTrustedCert // This will default to true
}

// GetPkgSignatureTimestampGrace returns the grace period for timestamp validation, with 30 days as default
func (c *Configuration) GetPkgSignatureTimestampGrace() int {
	if c.PkgSignatureTimestampGrace <= 0 {
		return 30 // Default to 30 days grace period
	}
	return c.PkgSignatureTimestampGrace
}

// GetPkgPreferSbinInstaller returns whether to prefer sbin-installer for .pkg packages, with true as default
func (c *Configuration) GetPkgPreferSbinInstaller() bool {
	// Default to preferring sbin-installer for .pkg packages
	return c.PkgPreferSbinInstaller || !c.PkgPreferSbinInstaller // This will default to true
}

// GetPkgSignatureValidationMode returns the signature validation mode, with "strict" as default
func (c *Configuration) GetPkgSignatureValidationMode() string {
	if c.PkgSignatureValidationMode == "" {
		return "strict" // Default to strict validation
	}
	// Validate allowed values
	switch c.PkgSignatureValidationMode {
	case "strict", "warn", "off":
		return c.PkgSignatureValidationMode
	default:
		return "strict" // Fallback to strict for invalid values
	}
}

// GetPkgExtractTempPath returns the temporary extraction path for .pkg packages, with system temp as default
func (c *Configuration) GetPkgExtractTempPath() string {
	if c.PkgExtractTempPath == "" {
		return os.TempDir() // Default to system temporary directory
	}
	return c.PkgExtractTempPath
}

// IsPkgDeveloperTrusted checks if a developer name is in the trusted unsigned developers list
func (c *Configuration) IsPkgDeveloperTrusted(developerName string) bool {
	for _, trustedDev := range c.PkgAllowUnsignedDevelopers {
		if strings.EqualFold(developerName, trustedDev) {
			return true
		}
	}
	return false
}

// IsPkgCertTrusted checks if a certificate is trusted based on thumbprint or common name
func (c *Configuration) IsPkgCertTrusted(thumbprint, commonName string) bool {
	// Check thumbprint first (more specific)
	for _, trustedThumbprint := range c.PkgTrustedCertThumbprints {
		if strings.EqualFold(thumbprint, trustedThumbprint) {
			return true
		}
	}
	
	// Check common name
	for _, trustedCN := range c.PkgTrustedCertCommonNames {
		if strings.EqualFold(commonName, trustedCN) {
			return true
		}
	}
	
	return false
}

// LoadConfig loads the configuration from a YAML file.
// If the YAML file doesn't exist, it falls back to CSP OMA-URI registry settings.
func LoadConfig() (*Configuration, error) {
	if _, err := os.Stat(ConfigPath); os.IsNotExist(err) {
		log.Printf("Configuration file does not exist: %s", ConfigPath)
		log.Printf("Attempting to load configuration from CSP OMA-URI registry settings...")

		// Try CSP fallback
		config, cspErr := LoadConfigFromCSP()
		if cspErr == nil {
			log.Printf("Successfully loaded configuration from CSP OMA-URI registry settings")
			return config, nil
		}

		log.Printf("Failed to load from CSP registry: %v", cspErr)
		log.Printf("No configuration available - neither YAML file nor CSP registry settings found")
		return nil, fmt.Errorf("configuration file does not exist and CSP fallback failed: %w", err)
	}

	data, err := os.ReadFile(ConfigPath)
	if err != nil {
		log.Printf("Failed to read configuration file: %v", err)
		return nil, err
	}

	var config Configuration
	if err := yaml.Unmarshal(data, &config); err != nil {
		log.Printf("Failed to parse configuration file: %v", err)
		return nil, err
	}

	// Set default paths if empty
	if config.CachePath == "" {
		config.CachePath = filepath.Join(`C:\ProgramData\ManagedInstalls\cache`)
	}
	if config.CatalogsPath == "" {
		config.CatalogsPath = filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`)
	}

	// Set default timeout if not configured (0 means not set)
	if config.InstallerTimeoutMinutes == 0 {
		config.InstallerTimeoutMinutes = 10 // Default to 10 minutes as requested
	}

	// Set cache management defaults if not configured
	if config.CacheMaxSizeGB == 0 {
		config.CacheMaxSizeGB = 10 // 10GB default maximum cache size
	}
	if config.CacheRetentionDays == 0 {
		config.CacheRetentionDays = 1 // 1 day default retention (much more aggressive than before)
	}
	// Set boolean defaults - use pointers to distinguish between unset and explicitly false
	if config.CacheCleanupOnStartup == nil {
		defaultTrue := true
		config.CacheCleanupOnStartup = &defaultTrue
	}
	if config.CachePreserveInstalledItems == nil {
		defaultTrue := true
		config.CachePreserveInstalledItems = &defaultTrue
	}

	// Create required directories
	for _, path := range []string{config.CachePath, config.CatalogsPath} {
		if err := os.MkdirAll(path, 0755); err != nil {
			return nil, fmt.Errorf("creating directory %s: %v", path, err)
		}
	}

	return &config, nil
}

// SaveConfig saves the current configuration to a YAML file.
func SaveConfig(config *Configuration) error {
	data, err := yaml.Marshal(config)
	if err != nil {
		log.Printf("Failed to serialize configuration: %v", err)
		return err
	}

	err = os.MkdirAll(filepath.Dir(ConfigPath), 0755)
	if err != nil {
		log.Printf("Failed to create configuration directory: %v", err)
		return err
	}

	err = os.WriteFile(ConfigPath, data, 0644)
	if err != nil {
		log.Printf("Failed to write configuration file: %v", err)
		return err
	}

	return nil
}

// GetDefaultConfig provides default configuration values in YAML format.
func GetDefaultConfig() *Configuration {
	// Use ProgramW6432 environment variable to force 64-bit Program Files path
	programFiles := os.Getenv("ProgramW6432")
	if programFiles == "" {
		programFiles = `C:\Program Files`
	}
	return &Configuration{
		LogLevel:                   "INFO",
		InstallPath:                filepath.Join(programFiles, "Cimian"),
		RepoPath:                   `C:\ProgramData\ManagedInstalls\repo`,
		CatalogsPath:               `C:\ProgramData\ManagedInstalls\catalogs`,
		CachePath:                  `C:\ProgramData\ManagedInstalls\Cache`,
		Debug:                      false,
		Verbose:                    false,
		CheckOnly:                  false,
		ClientIdentifier:           "",
		SoftwareRepoURL:            "https://cimian.example.com",
		DefaultArch:                "x64,arm64",
		DefaultCatalog:             "testing",
		CloudProvider:              "none",
		CloudBucket:                "",
		ForceBasicAuth:             false,
		OpenImportedYaml:           true,
		PreflightFailureAction:     "continue", // Default: continue on preflight failure
		PostflightFailureAction:    "continue", // Default: continue on postflight failure
		InstallerTimeoutMinutes:    10,         // Default: 10 minute timeout for installers
		ForceExecutionPolicyBypass: true,       // Default: Force -ExecutionPolicy Bypass for all PowerShell scripts
	}
}

// LoadConfigFromCSP loads configuration from Windows CSP OMA-URI registry settings.
// This serves as a fallback when the Config.yaml file doesn't exist.
func LoadConfigFromCSP() (*Configuration, error) {
	// Start with default configuration
	config := GetDefaultConfig()

	// Load from CSP registry path
	err := loadCSPFromRegistryPath(CSPRegistryPath, config)
	if err != nil {
		return nil, fmt.Errorf("failed to load from CSP registry path: %v", err)
	}

	log.Printf("Loaded CSP configuration from registry path: %s", CSPRegistryPath)

	// Validate that we have at least some essential configuration
	if config.SoftwareRepoURL == "" || config.SoftwareRepoURL == "https://cimian.example.com" {
		return nil, fmt.Errorf("essential CSP configuration missing: SoftwareRepoURL not set or still default")
	}

	// Set default paths if empty (same logic as YAML loading)
	if config.CachePath == "" {
		config.CachePath = filepath.Join(`C:\ProgramData\ManagedInstalls\cache`)
	}
	if config.CatalogsPath == "" {
		config.CatalogsPath = filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`)
	}

	// Create required directories
	for _, path := range []string{config.CachePath, config.CatalogsPath} {
		if err := os.MkdirAll(path, 0755); err != nil {
			return nil, fmt.Errorf("creating directory %s: %v", path, err)
		}
	}

	return config, nil
} // loadCSPFromRegistryPath loads configuration values from a specific registry path.
func loadCSPFromRegistryPath(registryPath string, config *Configuration) error {
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, registryPath, registry.READ)
	if err != nil {
		return fmt.Errorf("failed to open CSP registry key %s: %v", registryPath, err)
	}
	defer key.Close()

	// Load string configuration values
	loadStringFromRegistry(key, "SoftwareRepoURL", &config.SoftwareRepoURL)
	loadStringFromRegistry(key, "ClientIdentifier", &config.ClientIdentifier)
	loadStringFromRegistry(key, "CloudBucket", &config.CloudBucket)
	loadStringFromRegistry(key, "CloudProvider", &config.CloudProvider)
	loadStringFromRegistry(key, "DefaultArch", &config.DefaultArch)
	loadStringFromRegistry(key, "DefaultCatalog", &config.DefaultCatalog)
	loadStringFromRegistry(key, "InstallPath", &config.InstallPath)
	loadStringFromRegistry(key, "LocalOnlyManifest", &config.LocalOnlyManifest)
	loadStringFromRegistry(key, "LogLevel", &config.LogLevel)
	loadStringFromRegistry(key, "RepoPath", &config.RepoPath)
	loadStringFromRegistry(key, "CachePath", &config.CachePath)
	loadStringFromRegistry(key, "CatalogsPath", &config.CatalogsPath)
	loadStringFromRegistry(key, "PreflightFailureAction", &config.PreflightFailureAction)
	loadStringFromRegistry(key, "PostflightFailureAction", &config.PostflightFailureAction)

	// Load integer configuration values
	loadIntFromRegistry(key, "InstallerTimeoutMinutes", &config.InstallerTimeoutMinutes)

	// Load boolean configuration values
	loadBoolFromRegistry(key, "Debug", &config.Debug)
	loadBoolFromRegistry(key, "Verbose", &config.Verbose)
	loadBoolFromRegistry(key, "CheckOnly", &config.CheckOnly)
	loadBoolFromRegistry(key, "ForceBasicAuth", &config.ForceBasicAuth)
	loadBoolFromRegistry(key, "NoPreflight", &config.NoPreflight)
	loadBoolFromRegistry(key, "NoPostflight", &config.NoPostflight)
	loadBoolFromRegistry(key, "OpenImportedYaml", &config.OpenImportedYaml)
	loadBoolFromRegistry(key, "ForceExecutionPolicyBypass", &config.ForceExecutionPolicyBypass)

	// Load array configuration values
	loadStringArrayFromRegistry(key, "Catalogs", &config.Catalogs)
	loadStringArrayFromRegistry(key, "LocalManifests", &config.LocalManifests)

	// Load .pkg package format settings
	loadBoolFromRegistry(key, "PkgRequireSignature", &config.PkgRequireSignature)
	loadBoolFromRegistry(key, "PkgRequireTrustedCert", &config.PkgRequireTrustedCert)
	loadBoolFromRegistry(key, "PkgPreferSbinInstaller", &config.PkgPreferSbinInstaller)
	loadStringFromRegistry(key, "PkgSbinInstallerArgs", &config.PkgSbinInstallerArgs)
	loadStringFromRegistry(key, "PkgSignatureValidationMode", &config.PkgSignatureValidationMode)
	loadStringFromRegistry(key, "PkgExtractTempPath", &config.PkgExtractTempPath)
	loadIntFromRegistry(key, "PkgSignatureTimestampGrace", &config.PkgSignatureTimestampGrace)
	loadStringArrayFromRegistry(key, "PkgTrustedCertThumbprints", &config.PkgTrustedCertThumbprints)
	loadStringArrayFromRegistry(key, "PkgTrustedCertCommonNames", &config.PkgTrustedCertCommonNames)
	loadStringArrayFromRegistry(key, "PkgAllowUnsignedDevelopers", &config.PkgAllowUnsignedDevelopers)

	return nil
}

// loadStringFromRegistry loads a string value from registry if it exists.
func loadStringFromRegistry(key registry.Key, valueName string, target *string) {
	if val, _, err := key.GetStringValue(valueName); err == nil && val != "" {
		*target = val
		log.Printf("CSP: Loaded %s = %s", valueName, val)
	}
}

// loadBoolFromRegistry loads a boolean value from registry if it exists.
// Accepts various formats: "true"/"false", "1"/"0", DWORD 1/0
func loadBoolFromRegistry(key registry.Key, valueName string, target *bool) {
	// Try string value first
	if val, _, err := key.GetStringValue(valueName); err == nil {
		if parsed, parseErr := strconv.ParseBool(val); parseErr == nil {
			*target = parsed
			log.Printf("CSP: Loaded %s = %t", valueName, parsed)
			return
		}
	}

	// Try DWORD value
	if val, _, err := key.GetIntegerValue(valueName); err == nil {
		*target = val != 0
		log.Printf("CSP: Loaded %s = %t", valueName, val != 0)
	}
}

// loadIntFromRegistry loads an integer value from registry if it exists.
func loadIntFromRegistry(key registry.Key, valueName string, target *int) {
	// Try string value first
	if val, _, err := key.GetStringValue(valueName); err == nil {
		if parsed, parseErr := strconv.Atoi(val); parseErr == nil {
			*target = parsed
			log.Printf("CSP: Loaded %s = %d", valueName, parsed)
			return
		}
	}

	// Try DWORD value
	if val, _, err := key.GetIntegerValue(valueName); err == nil {
		*target = int(val)
		log.Printf("CSP: Loaded %s = %d", valueName, int(val))
	}
}

// loadStringArrayFromRegistry loads a string array from registry.
// Arrays can be stored as comma-separated values or multi-string (REG_MULTI_SZ).
func loadStringArrayFromRegistry(key registry.Key, valueName string, target *[]string) {
	// Try multi-string value first (REG_MULTI_SZ)
	if vals, _, err := key.GetStringsValue(valueName); err == nil && len(vals) > 0 {
		// Filter out empty strings
		filtered := make([]string, 0, len(vals))
		for _, val := range vals {
			if strings.TrimSpace(val) != "" {
				filtered = append(filtered, strings.TrimSpace(val))
			}
		}
		if len(filtered) > 0 {
			*target = filtered
			log.Printf("CSP: Loaded %s = %v", valueName, filtered)
			return
		}
	}

	// Try single string value with comma separation
	if val, _, err := key.GetStringValue(valueName); err == nil && val != "" {
		parts := strings.Split(val, ",")
		filtered := make([]string, 0, len(parts))
		for _, part := range parts {
			if trimmed := strings.TrimSpace(part); trimmed != "" {
				filtered = append(filtered, trimmed)
			}
		}
		if len(filtered) > 0 {
			*target = filtered
			log.Printf("CSP: Loaded %s = %v", valueName, filtered)
		}
	}
}

// WriteCimianVersionToRegistry writes Cimian's version to the registry at HKLM\SOFTWARE\Cimian\Version
// This allows administrators and other tools to easily identify the installed Cimian version
func WriteCimianVersionToRegistry(version string) error {
	if version == "" {
		version = "unknown"
	}

	// Create or open the Cimian registry key
	key, _, err := registry.CreateKey(registry.LOCAL_MACHINE, CimianRegistryPath, registry.SET_VALUE)
	if err != nil {
		log.Printf("Failed to create Cimian registry key: %v", err)
		return fmt.Errorf("failed to create registry key %s: %w", CimianRegistryPath, err)
	}
	defer key.Close()

	// Write the version string
	err = key.SetStringValue("Version", version)
	if err != nil {
		log.Printf("Failed to set Version value in Cimian registry key: %v", err)
		return fmt.Errorf("failed to set Version value: %w", err)
	}

	log.Printf("Successfully wrote Cimian version %s to registry", version)
	return nil
}
