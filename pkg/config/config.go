// pkg/config/config.go - configuration settings for Cimian.

package config

import (
	"fmt"
	"log"
	"os"
	"path/filepath"

	"gopkg.in/yaml.v3"
)

const ConfigPath = `C:\ProgramData\ManagedInstalls\Config.yaml`

// Configuration holds the configurable options for Cimian in YAML format
type Configuration struct {
	Catalogs          []string `yaml:"Catalogs"`
	CatalogsPath      string   `yaml:"CatalogsPath"`
	CachePath         string   `yaml:"CachePath"`
	CheckOnly         bool     `yaml:"CheckOnly"`
	ClientIdentifier  string   `yaml:"ClientIdentifier"`
	CloudBucket       string   `yaml:"CloudBucket"`
	CloudProvider     string   `yaml:"CloudProvider"`
	Debug             bool     `yaml:"Debug"`
	DefaultArch       string   `yaml:"DefaultArch"`
	DefaultCatalog    string   `yaml:"DefaultCatalog"`
	ForceBasicAuth    bool     `yaml:"ForceBasicAuth"`
	InstallPath       string   `yaml:"InstallPath"`
	LocalManifests    []string `yaml:"LocalManifests"`
	LocalOnlyManifest string   `yaml:"LocalOnlyManifest"` // Munki-compatible: path to local-only manifest
	LogLevel          string   `yaml:"LogLevel"`
	NoPreflight       bool     `yaml:"NoPreflight"` // Munki-compatible: skip preflight script
	OpenImportedYaml  bool     `yaml:"OpenImportedYaml"`
	RepoPath          string   `yaml:"RepoPath"`
	SoftwareRepoURL   string   `yaml:"SoftwareRepoURL"`
	Verbose           bool     `yaml:"Verbose"`

	// Internal flag to skip self-service manifest processing (not exposed in YAML)
	SkipSelfService bool `yaml:"-"`
}

// LoadConfig loads the configuration from a YAML file.
func LoadConfig() (*Configuration, error) {
	if _, err := os.Stat(ConfigPath); os.IsNotExist(err) {
		log.Printf("Configuration file does not exist: %s", ConfigPath)
		return nil, err
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
	return &Configuration{
		LogLevel:         "INFO",
		InstallPath:      `C:\Program Files\Cimian`,
		RepoPath:         `C:\ProgramData\ManagedInstalls\repo`,
		CatalogsPath:     `C:\ProgramData\ManagedInstalls\catalogs`,
		CachePath:        `C:\ProgramData\ManagedInstalls\Cache`,
		Debug:            false,
		Verbose:          false,
		CheckOnly:        false,
		ClientIdentifier: "",
		SoftwareRepoURL:  "https://cimian.example.com",
		DefaultArch:      "x64",
		DefaultCatalog:   "testing",
		CloudProvider:    "none",
		CloudBucket:      "",
		ForceBasicAuth:   false,
		OpenImportedYaml: true,
	}
}
