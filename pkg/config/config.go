package config

import (
    "os"
    "log"
    "path/filepath"
    "gopkg.in/yaml.v3"
)

const ConfigPath = `C:\ProgramData\ManagedInstalls\Config.yaml`

// Configuration holds the configurable options for Gorilla in YAML format
type Configuration struct {
    InstallPath    string `yaml:"install_path"`
    LogLevel       string `yaml:"log_level"`
    RepoPath       string `yaml:"repo_path"`
    CatalogsPath   string `yaml:"catalogs_path"`
    Debug          bool   `yaml:"debug"`
    Verbose        bool   `yaml:"verbose"`
    CheckOnly      bool   `yaml:"check_only"`
    DefaultArch    string `yaml:"default_arch"`
    DefaultCatalog string `yaml:"default_catalog"`
    CloudProvider  string `yaml:"cloud_provider"`
    CloudBucket    string `yaml:"cloud_bucket"`
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
        LogLevel:       "INFO",
        InstallPath:    `C:\Program Files\Gorilla`,
        RepoPath:       `C:\ProgramData\Gorilla\repo`,
        CatalogsPath:   `C:\ProgramData\ManagedInstalls\catalogs`,
        Debug:          false,
        Verbose:        false,
        CheckOnly:      false,
        DefaultArch:    "x86_64",
        DefaultCatalog: "testing",
        CloudProvider:  "none",
        CloudBucket:    "",
    }
}
