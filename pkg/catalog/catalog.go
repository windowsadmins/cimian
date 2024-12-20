// pkg/catalog/catalog.go

package catalog

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/windowsadmins/gorilla/pkg/config"
	"github.com/windowsadmins/gorilla/pkg/download"
	"github.com/windowsadmins/gorilla/pkg/logging"
	"github.com/windowsadmins/gorilla/pkg/report"
	"gopkg.in/yaml.v3"
)

// Item contains an individual entry from the catalog
type Item struct {
	Name          string        `yaml:"name"`
	Dependencies  []string      `yaml:"dependencies"`
	DisplayName   string        `yaml:"display_name"`
	Check         InstallCheck  `yaml:"check"`
	Installer     InstallerItem `yaml:"installer"`
	Uninstaller   InstallerItem `yaml:"uninstaller"`
	Version       string        `yaml:"version"`
	BlockingApps  []string      `yaml:"blocking_apps"`
	PreScript     string        `yaml:"preinstall_script"`
	PostScript    string        `yaml:"postinstall_script"`
	SupportedArch []string      `yaml:"supported_architectures"`
}

// InstallerItem holds information about how to install a catalog item
type InstallerItem struct {
	Type      string   `yaml:"type"`
	Location  string   `yaml:"location"`
	Hash      string   `yaml:"hash"`
	Arguments []string `yaml:"arguments"`
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

		// Parse the catalog YAML content
		var catalogItems map[string]Item
		if err := yaml.Unmarshal(yamlFile, &catalogItems); err != nil {
			logging.Error("Unable to parse YAML catalog", "path", catalogFilePath, "error", err)
			continue
		}

		// Add parsed items to the catalogMap
		catalogMap[catalogCount] = catalogItems
		logging.Info("Successfully processed catalog", "name", catalog, "items", len(catalogItems))
	}

	return catalogMap
}
