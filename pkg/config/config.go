package config

import (
	"log"
	"os"
	"path/filepath"

	"gopkg.in/yaml.v3"
)

const ConfigPath = `C:\ProgramData\ManagedInstalls\Config.yaml`

// Configuration holds the configurable options for Gorilla in YAML format
type Configuration struct {
	Catalogs         []string `yaml:"catalogs"`
	CatalogsPath     string   `yaml:"catalogs_path"`
	CachePath        string   `yaml:"cache_path"`
	CheckOnly        bool     `yaml:"check_only"`
	CloudBucket      string   `yaml:"cloud_bucket"`
	CloudProvider    string   `yaml:"cloud_provider"`
	Debug            bool     `yaml:"debug"`
	DefaultArch      string   `yaml:"default_arch"`
	DefaultCatalog   string   `yaml:"default_catalog"`
	InstallPath      string   `yaml:"install_path"`
	LocalManifests   []string `yaml:"local_manifests"`
	LogLevel         string   `yaml:"log_level"`
	ClientIdentifier string   `yaml:"ClientIdentifier"`
	SoftwareRepoURL  string   `yaml:"SoftwareRepoURL"`
	RepoPath         string   `yaml:"repo_path"`
	URLPkgsInfo      string   `yaml:"url_pkgsinfo"`
	Verbose          bool     `yaml:"verbose"`
	ForceBasicAuth   bool     `yaml:"force_basic_auth"`
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
		InstallPath:      `C:\Program Files\Gorilla`,
		RepoPath:         `C:\ProgramData\ManagedInstalls\repo`,
		CatalogsPath:     `C:\ProgramData\ManagedInstalls\catalogs`,
		CachePath:        `C:\ProgramData\ManagedInstalls\Cache`,
		Debug:            false,
		Verbose:          false,
		CheckOnly:        false,
		ClientIdentifier: "",
		SoftwareRepoURL:  "http://gorilla/repo",
		DefaultArch:      "x86_64",
		DefaultCatalog:   "testing",
		CloudProvider:    "none",
		CloudBucket:      "",
		ForceBasicAuth:   false,
	}
}
