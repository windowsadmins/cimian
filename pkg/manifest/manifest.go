// pkg/manifest/manifest.go - Functions for downloading and parsing manifests and catalogs.

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

// InstallDetail represents an individual file check in the “installs” array (if used).
type InstallDetail struct {
	Type        string `yaml:"type,omitempty"`
	Path        string `yaml:"path,omitempty"`
	MD5Checksum string `yaml:"md5checksum,omitempty"`
	Version     string `yaml:"version,omitempty"`
}

// Item represents the final data your client needs to decide installation/uninstallation.
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

	// Add a field to record if the item is for install/update/uninstall
	Action string `yaml:"-"` // internal use
}

// CatalogEntry matches how each record in your catalogs is shaped.
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
	// Add fields like category, developer, dependencies, etc. if needed
}

// ManifestFile is how each main “manifest” looks on disk.
type ManifestFile struct {
	Name              string   `yaml:"name"`
	Catalogs          []string `yaml:"catalogs"`
	ManagedInstalls   []string `yaml:"managed_installs"`
	ManagedUninstalls []string `yaml:"managed_uninstalls"`
	ManagedUpdates    []string `yaml:"managed_updates"`
	OptionalInstalls  []string `yaml:"optional_installs"`
	IncludedManifests []string `yaml:"included_manifests"`
}

// -----------------------------------------------------------------------------
// HELPER: ensureYamlExtension
// -----------------------------------------------------------------------------
func ensureYamlExtension(name string) string {
	if !strings.HasSuffix(strings.ToLower(name), ".yaml") {
		name += ".yaml"
	}
	return name
}

// -----------------------------------------------------------------------------
// AuthenticatedGet is the main entry point:
//  1. Downloads the main manifest plus any included manifests
//  2. Reads each manifest’s "Catalogs", downloads those catalog files, and merges them
//  3. For each package in ManagedInstalls/Updates/Uninstalls, merges any catalog data
//  4. Returns a single unique slice of Items that need installing/updating/uninstalling
//
// -----------------------------------------------------------------------------
func AuthenticatedGet(cfg *config.Configuration) ([]Item, error) {
	var allManifests []ManifestFile
	visitedManifests := make(map[string]bool)

	// Start from just the main “client_identifier”
	manifestsToProcess := []string{cfg.ClientIdentifier}

	// We’ll keep a global map of packageName => CatalogEntry
	catalogMap := make(map[string]CatalogEntry)

	// BFS: process each named manifest
	for len(manifestsToProcess) > 0 {
		currentName := manifestsToProcess[0]
		manifestsToProcess = manifestsToProcess[1:] // pop front
		currentName = ensureYamlExtension(strings.ReplaceAll(currentName, `\`, `/`))

		if visitedManifests[currentName] {
			continue
		}
		visitedManifests[currentName] = true

		// Construct the URL for this manifest
		manifestURL := fmt.Sprintf("%s/manifests/%s",
			strings.TrimRight(cfg.SoftwareRepoURL, "/"),
			currentName)
		localPath := filepath.Join(`C:\ProgramData\ManagedInstalls\manifests`, currentName)

		// Download the manifest
		if err := download.DownloadFile(manifestURL, localPath, cfg); err != nil {
			logging.Warn("Failed to download manifest", "manifestURL", manifestURL, "error", err)
			continue
		}

		// Read the .yaml
		data, err := os.ReadFile(localPath)
		if err != nil {
			logging.Warn("Failed to read manifest file", "file", localPath, "error", err)
			continue
		}

		// Parse it
		var mf ManifestFile
		if err := yaml.Unmarshal(data, &mf); err != nil {
			logging.Warn("Failed to parse manifest YAML", "file", localPath, "error", err)
			continue
		}
		logging.Info(fmt.Sprintf("Processed manifest: %s", mf.Name))

		allManifests = append(allManifests, mf)

		// Enqueue its "included_manifests"
		for _, inc := range mf.IncludedManifests {
			inc = ensureYamlExtension(strings.ReplaceAll(inc, `\`, `/`))
			if !visitedManifests[inc] {
				manifestsToProcess = append(manifestsToProcess, inc)
			}
		}

		// For each Catalog in this manifest, we always download & parse => add to catalogMap
		for _, catName := range mf.Catalogs {
			if catName == "" {
				continue
			}
			catURL := fmt.Sprintf("%s/catalogs/%s.yaml",
				strings.TrimRight(cfg.SoftwareRepoURL, "/"),
				catName)
			catLocal := filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`, catName+".yaml")

			// Download the catalog
			if err := download.DownloadFile(catURL, catLocal, cfg); err != nil {
				logging.Warn("Failed to download catalog", "catalogURL", catURL, "error", err)
				continue
			}
			logging.Info(fmt.Sprintf("Downloaded catalog: %s", catName))

			// Parse it
			cEntries, err := parseCatalogFile(catLocal)
			if err != nil {
				logging.Error("Failed to parse catalog", "catalog", catName, "error", err)
				continue
			}
			// Merge into our global map
			for _, ce := range cEntries {
				key := strings.ToLower(ce.Name)
				catalogMap[key] = ce
			}
		}
	}

	// Now we have all manifests in `allManifests`, and a global catalogMap
	// Merge them into final items
	var finalItems []Item
	deduplicateCheck := make(map[string]bool) // key = action + pkgName (case-insensitive)

	for _, mf := range allManifests {
		// For each array, we create an item for each pkg, merging with the catalog
		// (Below we do “install” or “update” items in the same array—just set Action if you want.)
		for _, pkgName := range mf.ManagedInstalls {
			if pkgName == "" {
				continue
			}
			actionKey := "install|" + strings.ToLower(pkgName)
			if deduplicateCheck[actionKey] {
				continue
			}
			deduplicateCheck[actionKey] = true

			catKey := strings.ToLower(pkgName)
			catEntry, found := catalogMap[catKey]

			if !found {
				// No data in catalogs
				logging.Warn("No catalog entry found for package", "package", pkgName)
				finalItems = append(finalItems, Item{
					Name:     pkgName,
					Version:  "", // unknown
					Catalogs: mf.Catalogs,
					Action:   "install", // or "install"
				})
			} else {
				finalItems = append(finalItems, Item{
					Name:              catEntry.Name,
					Version:           catEntry.Version,
					InstallerLocation: catEntry.Installer.Location,
					Catalogs:          mf.Catalogs,
					SupportedArch:     catEntry.SupportedArch,
					Action:            "install", // or "install"
				})
			}
		}
		for _, pkgName := range mf.ManagedUpdates {
			if pkgName == "" {
				continue
			}
			actionKey := "update|" + strings.ToLower(pkgName)
			if deduplicateCheck[actionKey] {
				continue
			}
			deduplicateCheck[actionKey] = true

			catKey := strings.ToLower(pkgName)
			catEntry, found := catalogMap[catKey]
			if !found {
				logging.Warn("No catalog entry for update package", "package", pkgName)
				finalItems = append(finalItems, Item{
					Name:     pkgName,
					Version:  "",
					Catalogs: mf.Catalogs,
					Action:   "update",
				})
			} else {
				finalItems = append(finalItems, Item{
					Name:              catEntry.Name,
					Version:           catEntry.Version,
					InstallerLocation: catEntry.Installer.Location,
					Catalogs:          mf.Catalogs,
					SupportedArch:     catEntry.SupportedArch,
					Action:            "update",
				})
			}
		}
		for _, pkgName := range mf.OptionalInstalls {
			if pkgName == "" {
				continue
			}
			actionKey := "optional|" + strings.ToLower(pkgName)
			if deduplicateCheck[actionKey] {
				continue
			}
			deduplicateCheck[actionKey] = true

			catKey := strings.ToLower(pkgName)
			catEntry, found := catalogMap[catKey]
			if !found {
				logging.Warn("No catalog entry for optional package", "package", pkgName)
				finalItems = append(finalItems, Item{
					Name:     pkgName,
					Version:  "",
					Catalogs: mf.Catalogs,
					Action:   "optional",
				})
			} else {
				finalItems = append(finalItems, Item{
					Name:              catEntry.Name,
					Version:           catEntry.Version,
					InstallerLocation: catEntry.Installer.Location,
					Catalogs:          mf.Catalogs,
					SupportedArch:     catEntry.SupportedArch,
					Action:            "optional",
				})
			}
		}
		for _, pkgName := range mf.ManagedUninstalls {
			if pkgName == "" {
				continue
			}
			actionKey := "uninstall|" + strings.ToLower(pkgName)
			if deduplicateCheck[actionKey] {
				continue
			}
			deduplicateCheck[actionKey] = true

			catKey := strings.ToLower(pkgName)
			catEntry, found := catalogMap[catKey]
			if !found {
				logging.Warn("No catalog entry for uninstall package", "package", pkgName)
				// But we can still do an uninstall if the local system had it.
				finalItems = append(finalItems, Item{
					Name:     pkgName,
					Version:  "",
					Catalogs: mf.Catalogs,
					Action:   "uninstall",
				})
			} else {
				// Possibly we only need name + version for uninstall, or the uninstaller data?
				finalItems = append(finalItems, Item{
					Name:              catEntry.Name,
					Version:           catEntry.Version,
					InstallerLocation: catEntry.Installer.Location, // or catEntry.UninstallerLocation if you store that
					Catalogs:          mf.Catalogs,
					SupportedArch:     catEntry.SupportedArch,
					Action:            "uninstall",
				})
			}
		}
	}

	return finalItems, nil
}

type catalogWrapper struct {
	Items []CatalogEntry `yaml:"items"`
}

func parseCatalogFile(path string) ([]CatalogEntry, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("failed to read catalog file: %w", err)
	}

	var wrapper catalogWrapper
	if err := yaml.Unmarshal(data, &wrapper); err != nil {
		return nil, fmt.Errorf("failed to unmarshal catalog: %w", err)
	}

	return wrapper.Items, nil
}
