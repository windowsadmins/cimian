// pkg/manifest/manifest.go

package manifest

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/download"
	"github.com/windowsadmins/cimian/pkg/logging"
	"gopkg.in/yaml.v3"
)

type InstallDetail struct {
	Type        string `yaml:"type,omitempty"`
	Path        string `yaml:"path,omitempty"`
	MD5Checksum string `yaml:"md5checksum,omitempty"`
	Version     string `yaml:"version,omitempty"`
}

// Item is your manifest-based object.
type Item struct {
	Name              string   `yaml:"name"`
	Version           string   `yaml:"version"`
	InstallerLocation string   `yaml:"installer_location,omitempty"`
	Includes          []string `yaml:"included_manifests,omitempty"`

	// Old arrays for referencing packages by name:
	ManagedInstalls   []string `yaml:"managed_installs,omitempty"`
	ManagedUninstalls []string `yaml:"managed_uninstalls,omitempty"`
	ManagedUpdates    []string `yaml:"managed_updates,omitempty"`
	OptionalInstalls  []string `yaml:"optional_installs,omitempty"`

	// The catalogs (Development, etc.)
	Catalogs      []string `yaml:"catalogs,omitempty"`
	SupportedArch []string `yaml:"supported_architectures,omitempty"`

	// New fields for your scripts:
	InstallCheckScript   string `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string `yaml:"uninstallcheck_script,omitempty"`
	PreinstallScript     string `yaml:"preinstall_script,omitempty"`
	PreuninstallScript   string `yaml:"preuninstall_script,omitempty"`

	// The new “installs” array for file checks:
	Installs []InstallDetail `yaml:"installs,omitempty"`
}

// CatalogEntry (adjust to match your real catalog fields).
// For example, each item in Development.yaml is shaped like:
//   - name: Git
//     installer:
//     location: /apps/dev/Git-x64-2.47.1.1.exe
//     ...
type CatalogEntry struct {
	Name          string   `yaml:"name"`
	Version       string   `yaml:"version"`
	SupportedArch []string `yaml:"supported_architectures"`
	Installer     struct {
		Location string `yaml:"location"`
		Hash     string `yaml:"hash"`
		Type     string `yaml:"type"`
		Size     int64  `yaml:"size"`
	} `yaml:"installer"`
	// e.g. category, developer, etc. if needed
}

// Helper function to append `.yaml` only if missing
func ensureYAMLExtension(name string) string {
	if !strings.HasSuffix(name, ".yaml") {
		return name + ".yaml"
	}
	return name
}

// AuthenticatedGet loads the main manifest (plus nested ones), downloads any catalogs
// they reference, then merges catalog data with each "managed_installs" item.
func AuthenticatedGet(cfg *config.Configuration) ([]Item, error) {
	var allManifests []Item
	var items []Item

	// Track which manifests we've processed
	visitedManifests := make(map[string]bool)

	// Start with just the client’s manifest name
	manifestsToProcess := []string{cfg.ClientIdentifier}

	// We'll keep a global map of "pkgname (lowercased)" -> CatalogEntry
	catalogEntries := make(map[string]CatalogEntry)

	for i := 0; i < len(manifestsToProcess); i++ {
		mName := manifestsToProcess[i]
		// Always use forward slashes for manifest names in URLs and includes
		mName = filepath.ToSlash(mName)
		if visitedManifests[mName] {
			continue
		}
		visitedManifests[mName] = true

		// When processing included manifests, strip the extension if it exists
		manifestName := strings.TrimSuffix(mName, ".yaml")
		manifestURL := fmt.Sprintf("%s/manifests/%s.yaml",
			strings.TrimRight(cfg.SoftwareRepoURL, "/"),
			manifestName)

		// Use system-specific separators for local file paths
		manifestFilePath := filepath.Join(`C:\ProgramData\ManagedInstalls\manifests`, ensureYAMLExtension(mName))

		if err := download.DownloadFile(manifestURL, manifestFilePath, cfg); err != nil {
			logging.Warn("Failed to download manifest", "url", manifestURL, "error", err)
			continue
		}

		manBytes, err := os.ReadFile(manifestFilePath)
		if err != nil {
			logging.Error("Failed to read manifest file", "path", manifestFilePath, "error", err)
			continue
		}

		// When unmarshaling manifest YAML, ensure included_manifests use forward slashes
		var man Item
		if err := yaml.Unmarshal(manBytes, &man); err != nil {
			logging.Error("Failed to parse manifest", "path", manifestFilePath, "error", err)
			continue
		}
		// Convert any included manifests to use forward slashes
		for j := range man.Includes {
			man.Includes[j] = filepath.ToSlash(man.Includes[j])
		}
		allManifests = append(allManifests, man)
		logging.Info("Processed manifest", "name", man.Name)

		// Enqueue any "included_manifests"
		for _, inc := range man.Includes {
			if !visitedManifests[inc] {
				logging.Info("Including nested manifest", "parent", mName, "nested", inc)
				manifestsToProcess = append(manifestsToProcess, inc)
			}
		}

		// For each "catalog" in this manifest, download (if not done before) + parse
		for _, catName := range man.Catalogs {
			catPath := filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`, catName+".yaml")
			// Always re-download the catalog on every run:
			catURL := fmt.Sprintf("%s/catalogs/%s.yaml", strings.TrimRight(cfg.SoftwareRepoURL, "/"), catName)
			if err := download.DownloadFile(catURL, catPath, cfg); err != nil {
				logging.Warn("Failed to download catalog", "url", catURL, "error", err)
				continue
			}
			logging.Info("Downloaded catalog", "catalog", catName, "path", catPath)

			// (NEW) Now parse that catalog file and store its items in our global map
			cEntries, err := parseCatalogFile(catPath)
			if err != nil {
				logging.Error("Failed to parse catalog", "catalog", catName, "error", err)
				continue
			}
			for _, ce := range cEntries {
				lowerName := strings.ToLower(ce.Name)
				catalogEntries[lowerName] = ce
			}
		}
	}

	// (NEW) Merge: for each manifest’s "managed_installs", see if there’s a matching catalog item
	for _, man := range allManifests {
		for _, pkgName := range man.ManagedInstalls {
			lowerName := strings.ToLower(pkgName)
			catEntry, found := catalogEntries[lowerName]
			if !found {
				// No data in the catalogs for this pkg
				logging.Warn("No catalog entry found for package", "package", pkgName)
				items = append(items, Item{
					Name:     pkgName,
					Catalogs: man.Catalogs,
				})
				continue
			}
			// We have a match in the catalogs
			mergedItem := Item{
				Name:              catEntry.Name,
				Version:           catEntry.Version,
				Catalogs:          man.Catalogs,
				InstallerLocation: catEntry.Installer.Location,
				SupportedArch:     catEntry.SupportedArch,
			}
			items = append(items, mergedItem)
			logging.Debug("Merged package from manifest+catalog", "pkg", mergedItem.Name, "installer_location", mergedItem.InstallerLocation)
		}
	}

	return items, nil
}

// parseCatalogFile reads a local .yaml and returns a list of CatalogEntry objects.
func parseCatalogFile(path string) ([]CatalogEntry, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("failed to read catalog file: %w", err)
	}

	var entries []CatalogEntry
	if err := yaml.Unmarshal(data, &entries); err != nil {
		return nil, fmt.Errorf("failed to unmarshal catalog: %w", err)
	}
	return entries, nil
}
