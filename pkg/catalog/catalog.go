// pkg/catalog/catalog.go - functions for managing software catalogs.

package catalog

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/report"
	"github.com/windowsadmins/cimian/pkg/utils"
	"gopkg.in/yaml.v3"
)

// Item contains an individual entry from the catalog
type Item struct {
	Name          string              `yaml:"name"`
	Dependencies  []string            `yaml:"dependencies"`
	DisplayName   string              `yaml:"display_name"`
	Identifier    string              `yaml:"identifier,omitempty"`
	Installer     InstallerItem       `yaml:"installer"`
	Check         InstallCheck        `yaml:"check"`
	Installs      []InstallItem       `yaml:"installs"`
	Uninstaller   InstallerItem       `yaml:"uninstaller"`
	Version       string              `yaml:"version"`
	BlockingApps  []string            `yaml:"blocking_apps"`
	PreScript     utils.LiteralString `yaml:"preinstall_script"`
	PostScript    utils.LiteralString `yaml:"postinstall_script"`
	SupportedArch []string            `yaml:"supported_architectures"`
}

type InstallItem struct {
	Type        string `yaml:"type"`
	Path        string `yaml:"path"`
	Version     string `yaml:"version"`
	MD5Checksum string `yaml:"md5checksum"`
	ProductCode string `yaml:"product_code"`
	UpgradeCode string `yaml:"upgrade_code"`
}

// InstallerItem holds information about how to install a catalog item
type InstallerItem struct {
	Type        string   `yaml:"type"`
	Location    string   `yaml:"location"`
	Hash        string   `yaml:"hash"`
	Verb        string   `yaml:"verb,omitempty" json:"verb,omitempty"`
	Switches    []string `yaml:"switches,omitempty" json:"switches,omitempty"`
	Flags       []string `yaml:"flags,omitempty" json:"flags,omitempty"`
	Arguments   []string `yaml:"arguments"`
	ProductCode string   `yaml:"product_code,omitempty"`
}

// InstallCheck holds information about how to check the status of a catalog item
type InstallCheck struct {
	File     []FileCheck `yaml:"file"`
	Script   string      `yaml:"script"`
	Registry RegCheck    `yaml:"registry"`
}

// FileCheck holds information about checking via a file
type FileCheck struct {
	Path        string `yaml:"path"`
	Version     string `yaml:"version"`
	ProductName string `yaml:"product_name"`
	Hash        string `yaml:"hash"`
}

// RegCheck holds information about checking via registry
type RegCheck struct {
	Name    string `yaml:"name"`
	Version string `yaml:"version"`
}

// AuthenticatedGet retrieves and parses catalogs into a map
func AuthenticatedGet(cfg config.Configuration) map[int]map[string]Item {
	// catalogMap holds parsed catalog data
	var catalogMap = make(map[int]map[string]Item)
	catalogCount := 0

	// Catch unexpected failures
	defer func() {
		if r := recover(); r != nil {
			fmt.Println(r)
			report.End()
			os.Exit(1)
		}
	}()

	// Ensure at least one catalog is defined
	if len(cfg.Catalogs) < 1 {
		logging.Error("Unable to continue, no catalogs assigned", "catalogs", cfg.Catalogs)
		return catalogMap
	}

	// Loop through and process each catalog
	for _, catalog := range cfg.Catalogs {
		catalogCount++

		// Build catalog URL and destination path (preserve structure)
		catalogURL := fmt.Sprintf("%s/catalogs/%s.yaml", strings.TrimRight(cfg.SoftwareRepoURL, "/"), catalog)
		catalogFilePath := filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`, catalog+".yaml")

		logging.Info("Downloading catalog", "url", catalogURL, "path", catalogFilePath)

		// Download the catalog file
		if err := download.DownloadFile(catalogURL, catalogFilePath, &cfg); err != nil {
			logging.Error("Failed to download catalog", "url", catalogURL, "error", err)
			continue
		}

		// Read the downloaded YAML file
		yamlFile, err := os.ReadFile(catalogFilePath)
		if err != nil {
			logging.Error("Failed to read downloaded catalog file", "path", catalogFilePath, "error", err)
			continue
		}

		// Parse the catalog YAML content into a slice of Items
		var itemSlice []Item
		if err := yaml.Unmarshal(yamlFile, &itemSlice); err != nil {
			logging.Error("unable to parse YAML", "path", catalogFilePath, "error", err)
			continue
		}

		// Build a map, keyed by item name
		indexedItems := make(map[string]Item)
		for _, it := range itemSlice {
			if it.Name != "" {
				indexedItems[it.Name] = it
			}
		}
		// Store the map in catalogMap
		catalogMap[catalogCount] = indexedItems

		logging.Info("Successfully processed catalog", catalog, "items", len(indexedItems))
	}

	return catalogMap
}
