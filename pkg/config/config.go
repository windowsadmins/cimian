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
	LocalOnlyManifest       string   `yaml:"LocalOnlyManifest"` // Munki-compatible: path to local-only manifest
	LogLevel                string   `yaml:"LogLevel"`
	NoPreflight             bool     `yaml:"NoPreflight"`             // Munki-compatible: skip preflight script
	PreflightFailureAction  string   `yaml:"PreflightFailureAction"`  // "continue", "abort", or "warn" (default: continue)
	PostflightFailureAction string   `yaml:"PostflightFailureAction"` // "continue", "abort", or "warn" (default: continue)
	OpenImportedYaml        bool     `yaml:"OpenImportedYaml"`
	RepoPath                string   `yaml:"RepoPath"`
	SoftwareRepoURL         string   `yaml:"SoftwareRepoURL"`
	Verbose                 bool     `yaml:"Verbose"`

	// Installer timeout settings
	InstallerTimeoutMinutes int `yaml:"InstallerTimeoutMinutes"` // Default timeout for installers (in minutes)

	// Internal flag to skip self-service manifest processing (not exposed in YAML)
	SkipSelfService bool `yaml:"-"`
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
		LogLevel:                "INFO",
		InstallPath:             filepath.Join(programFiles, "Cimian"),
		RepoPath:                `C:\ProgramData\ManagedInstalls\repo`,
		CatalogsPath:            `C:\ProgramData\ManagedInstalls\catalogs`,
		CachePath:               `C:\ProgramData\ManagedInstalls\Cache`,
		Debug:                   false,
		Verbose:                 false,
		CheckOnly:               false,
		ClientIdentifier:        "",
		SoftwareRepoURL:         "https://cimian.example.com",
		DefaultArch:             "x64,arm64",
		DefaultCatalog:          "testing",
		CloudProvider:           "none",
		CloudBucket:             "",
		ForceBasicAuth:          false,
		OpenImportedYaml:        true,
		PreflightFailureAction:  "continue", // Default: continue on preflight failure
		PostflightFailureAction: "continue", // Default: continue on postflight failure
		InstallerTimeoutMinutes: 15,         // Default: 15 minute timeout for installers
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
	loadBoolFromRegistry(key, "OpenImportedYaml", &config.OpenImportedYaml)

	// Load array configuration values
	loadStringArrayFromRegistry(key, "Catalogs", &config.Catalogs)
	loadStringArrayFromRegistry(key, "LocalManifests", &config.LocalManifests)

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
