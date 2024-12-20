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
	Catalogs         []string `yaml:"Catalogs"`
	CatalogsPath     string   `yaml:"CatalogsPath"`
	CachePath        string   `yaml:"CachePath"`
	CheckOnly        bool     `yaml:"CheckOnly"`
	CloudBucket      string   `yaml:"CloudBucket"`
	CloudProvider    string   `yaml:"CloudProvider"`
	Debug            bool     `yaml:"Debug"`
	DefaultArch      string   `yaml:"DefaultArch"`
	DefaultCatalog   string   `yaml:"DefaultCatalog"`
	InstallPath      string   `yaml:"InstallPath"`
	LocalManifests   []string `yaml:"LocalManifests"`
	LogLevel         string   `yaml:"LogLevel"`
	ClientIdentifier string   `yaml:"ClientIdentifier"`
	SoftwareRepoURL  string   `yaml:"SoftwareRepoURL"`
	RepoPath         string   `yaml:"RepoPath"`
	URLPkgsInfo      string   `yaml:"URLPkgsInfo"`
	Verbose          bool     `yaml:"Verbose"`
	ForceBasicAuth   bool     `yaml:"ForceBasicAuth"`
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
		SoftwareRepoURL:  "https://gorilla.example.com",
		DefaultArch:      "x64",
		DefaultCatalog:   "testing",
		CloudProvider:    "none",
		CloudBucket:      "",
		ForceBasicAuth:   false,
	}
}
