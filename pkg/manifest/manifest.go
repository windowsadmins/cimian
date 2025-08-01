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

	// OnDemand functionality - items that can be run multiple times and never considered "installed"
	OnDemand bool `yaml:"OnDemand,omitempty"`

	// Add a field to record if the item is for install/update/uninstall
	Action string `yaml:"-"` // internal use

	// Source tracking - not persisted to YAML, used for runtime tracking
	SourceManifest string `yaml:"-"` // Which manifest this item came from
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

	// OnDemand functionality - items that can be run multiple times and never considered "installed"
	OnDemand bool `yaml:"OnDemand,omitempty"`

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

	// Conditional Items - NSPredicate-style conditional evaluation
	ConditionalItems []*ConditionalItem `yaml:"conditional_items,omitempty"`
}

// Condition represents a single predicate condition
type Condition struct {
	Key      string      `yaml:"key" json:"key"`           // The fact key to evaluate (e.g., "hostname", "os_version")
	Operator string      `yaml:"operator" json:"operator"` // Comparison operator (==, !=, >, <, >=, <=, LIKE, IN, CONTAINS)
	Value    interface{} `yaml:"value" json:"value"`       // The value to compare against
}

// ConditionalItem represents an item with conditional evaluation
type ConditionalItem struct {
	Condition     *Condition   `yaml:"condition,omitempty" json:"condition,omitempty"`           // Single condition
	Conditions    []*Condition `yaml:"conditions,omitempty" json:"conditions,omitempty"`         // Multiple conditions (AND logic)
	ConditionType string       `yaml:"condition_type,omitempty" json:"condition_type,omitempty"` // "AND" or "OR" for multiple conditions

	// The actual items to include when conditions are met
	ManagedInstalls   []string `yaml:"managed_installs,omitempty" json:"managed_installs,omitempty"`
	ManagedUninstalls []string `yaml:"managed_uninstalls,omitempty" json:"managed_uninstalls,omitempty"`
	ManagedUpdates    []string `yaml:"managed_updates,omitempty" json:"managed_updates,omitempty"`
	OptionalInstalls  []string `yaml:"optional_installs,omitempty" json:"optional_installs,omitempty"`
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

	// Start from just the main "client_identifier"
	manifestsToProcess := []string{cfg.ClientIdentifier}

	// We'll keep a global map of packageName => CatalogEntry
	catalogMap := make(map[string]CatalogEntry)

	// Track all catalog names found in manifests to populate cfg.Catalogs
	catalogNames := make(map[string]bool)

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
		logging.Debug(fmt.Sprintf("Processed manifest: %s", mf.Name))

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

			// Track this catalog name for updating cfg.Catalogs
			catalogNames[catName] = true

			catURL := fmt.Sprintf("%s/catalogs/%s.yaml",
				strings.TrimRight(cfg.SoftwareRepoURL, "/"),
				catName)
			catLocal := filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`, catName+".yaml")

			// Download the catalog
			if err := download.DownloadFile(catURL, catLocal, cfg); err != nil {
				logging.Warn("Failed to download catalog", "catalogURL", catURL, "error", err)
				continue
			}
			logging.Debug(fmt.Sprintf("Downloaded catalog: %s", catName))

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
		// Process conditional items first
		if len(mf.ConditionalItems) > 0 {
			logging.Debug("Processing conditional items", "count", len(mf.ConditionalItems))
			conditionalInstalls, conditionalUninstalls, conditionalUpdates, conditionalOptional, err := EvaluateConditionalItems(mf.ConditionalItems)
			if err != nil {
				logging.Warn("Error evaluating conditional items", "error", err)
			} else {
				// Add conditional items to the manifest temporarily for processing
				mf.ManagedInstalls = append(mf.ManagedInstalls, conditionalInstalls...)
				mf.ManagedUninstalls = append(mf.ManagedUninstalls, conditionalUninstalls...)
				mf.ManagedUpdates = append(mf.ManagedUpdates, conditionalUpdates...)
				mf.OptionalInstalls = append(mf.OptionalInstalls, conditionalOptional...)
				logging.Debug("Added conditional items", "installs", len(conditionalInstalls), "uninstalls", len(conditionalUninstalls), "updates", len(conditionalUpdates), "optional", len(conditionalOptional))
			}
		}

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
					Name:           pkgName,
					Version:        "", // unknown
					Catalogs:       mf.Catalogs,
					Action:         "install", // or "install"
					SourceManifest: mf.Name,
				})
			} else {
				finalItems = append(finalItems, Item{
					Name:              catEntry.Name,
					Version:           catEntry.Version,
					InstallerLocation: catEntry.Installer.Location,
					Catalogs:          mf.Catalogs,
					SupportedArch:     catEntry.SupportedArch,
					OnDemand:          catEntry.OnDemand,
					Action:            "install", // or "install"
					SourceManifest:    mf.Name,
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
					Name:           pkgName,
					Version:        "",
					Catalogs:       mf.Catalogs,
					Action:         "update",
					SourceManifest: mf.Name,
				})
			} else {
				finalItems = append(finalItems, Item{
					Name:              catEntry.Name,
					Version:           catEntry.Version,
					InstallerLocation: catEntry.Installer.Location,
					Catalogs:          mf.Catalogs,
					SupportedArch:     catEntry.SupportedArch,
					OnDemand:          catEntry.OnDemand,
					Action:            "update",
					SourceManifest:    mf.Name,
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
					Name:           pkgName,
					Version:        "",
					Catalogs:       mf.Catalogs,
					Action:         "optional",
					SourceManifest: mf.Name,
				})
			} else {
				finalItems = append(finalItems, Item{
					Name:              catEntry.Name,
					Version:           catEntry.Version,
					InstallerLocation: catEntry.Installer.Location,
					Catalogs:          mf.Catalogs,
					SupportedArch:     catEntry.SupportedArch,
					OnDemand:          catEntry.OnDemand,
					Action:            "optional",
					SourceManifest:    mf.Name,
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
					Name:           pkgName,
					Version:        "",
					Catalogs:       mf.Catalogs,
					Action:         "uninstall",
					SourceManifest: mf.Name,
				})
			} else {
				// Possibly we only need name + version for uninstall, or the uninstaller data?
				finalItems = append(finalItems, Item{
					Name:              catEntry.Name,
					Version:           catEntry.Version,
					InstallerLocation: catEntry.Installer.Location, // or catEntry.UninstallerLocation if you store that
					Catalogs:          mf.Catalogs,
					SupportedArch:     catEntry.SupportedArch,
					OnDemand:          catEntry.OnDemand,
					Action:            "uninstall",
					SourceManifest:    mf.Name,
				})
			}
		}
	}

	// Process self-service manifest after all server manifests
	// This is similar to Munki's approach where SelfServeManifest is processed last
	// Skip if SkipSelfService flag is set (e.g., when using --manifest flag)
	if !cfg.SkipSelfService {
		selfServicePath := `C:\ProgramData\ManagedInstalls\SelfServeManifest.yaml`
		if _, err := os.Stat(selfServicePath); err == nil {
			logging.Info("Processing self-service manifest")

			// Read the self-service manifest
			data, err := os.ReadFile(selfServicePath)
			if err != nil {
				logging.Warn("Failed to read self-service manifest", "error", err)
			} else {
				var selfServiceManifest ManifestFile
				if err := yaml.Unmarshal(data, &selfServiceManifest); err != nil {
					logging.Warn("Failed to parse self-service manifest", "error", err)
				} else {
					logging.Info("Processed self-service manifest", "name", selfServiceManifest.Name)

					// Process self-service managed_installs
					for _, pkgName := range selfServiceManifest.ManagedInstalls {
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
							logging.Warn("No catalog entry for self-service package", "package", pkgName)
							finalItems = append(finalItems, Item{
								Name:     pkgName,
								Version:  "",
								Catalogs: cfg.Catalogs, // Use global catalogs
								Action:   "install",
							})
						} else {
							finalItems = append(finalItems, Item{
								Name:              catEntry.Name,
								Version:           catEntry.Version,
								InstallerLocation: catEntry.Installer.Location,
								Catalogs:          cfg.Catalogs, // Use global catalogs
								SupportedArch:     catEntry.SupportedArch,
								OnDemand:          catEntry.OnDemand,
								Action:            "install",
							})
						}
					}

					// Process self-service managed_uninstalls
					for _, pkgName := range selfServiceManifest.ManagedUninstalls {
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
							logging.Warn("No catalog entry for self-service uninstall", "package", pkgName)
							finalItems = append(finalItems, Item{
								Name:     pkgName,
								Version:  "",
								Catalogs: cfg.Catalogs,
								Action:   "uninstall",
							})
						} else {
							finalItems = append(finalItems, Item{
								Name:              catEntry.Name,
								Version:           catEntry.Version,
								InstallerLocation: catEntry.Installer.Location,
								Catalogs:          cfg.Catalogs,
								SupportedArch:     catEntry.SupportedArch,
								OnDemand:          catEntry.OnDemand,
								Action:            "uninstall",
							})
						}
					}
				}
			}
		} else {
			logging.Debug("No self-service manifest found", "path", selfServicePath)
		}
	} else {
		logging.Debug("Skipping self-service manifest processing (SkipSelfService flag set)")
	}

	// Populate cfg.Catalogs with all catalog names found in manifests
	var catalogList []string
	for catName := range catalogNames {
		catalogList = append(catalogList, catName)
	}

	// If no catalogs were found in manifests, use the default catalog
	if len(catalogList) == 0 && cfg.DefaultCatalog != "" {
		catalogList = append(catalogList, cfg.DefaultCatalog)
		logging.Info("No catalogs found in manifests, using default catalog", "defaultCatalog", cfg.DefaultCatalog)

		// Download and process the default catalog since it wasn't processed above
		catURL := fmt.Sprintf("%s/catalogs/%s.yaml",
			strings.TrimRight(cfg.SoftwareRepoURL, "/"),
			cfg.DefaultCatalog)
		catLocal := filepath.Join(`C:\ProgramData\ManagedInstalls\catalogs`, cfg.DefaultCatalog+".yaml")

		// Download the catalog
		if err := download.DownloadFile(catURL, catLocal, cfg); err != nil {
			logging.Error("Failed to download default catalog", "catalogURL", catURL, "error", err)
		} else {
			logging.Info(fmt.Sprintf("Downloaded default catalog: %s", cfg.DefaultCatalog))

			// Parse it
			cEntries, err := parseCatalogFile(catLocal)
			if err != nil {
				logging.Error("Failed to parse default catalog", "catalog", cfg.DefaultCatalog, "error", err)
			} else {
				// Merge into our global map
				for _, ce := range cEntries {
					key := strings.ToLower(ce.Name)
					catalogMap[key] = ce
				}
				logging.Info("Successfully processed default catalog", "catalog", cfg.DefaultCatalog, "entries", len(cEntries))
			}
		}
	}

	cfg.Catalogs = catalogList
	logging.Info("Updated config catalogs from manifests", "catalogs", strings.Join(cfg.Catalogs, ", "))

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

// EvaluateConditionalItems processes conditional items and returns items that match system facts
func EvaluateConditionalItems(conditionalItems []*ConditionalItem) ([]string, []string, []string, []string, error) {
	var managedInstalls, managedUninstalls, managedUpdates, optionalInstalls []string

	// Gather system facts
	facts := gatherSystemFacts()

	for _, item := range conditionalItems {
		matches, err := evaluateConditionalItem(item, facts)
		if err != nil {
			logging.Warn("Error evaluating conditional item", "error", err)
			continue
		}

		if matches {
			logging.Debug("Conditional item matched, including items")
			managedInstalls = append(managedInstalls, item.ManagedInstalls...)
			managedUninstalls = append(managedUninstalls, item.ManagedUninstalls...)
			managedUpdates = append(managedUpdates, item.ManagedUpdates...)
			optionalInstalls = append(optionalInstalls, item.OptionalInstalls...)
		} else {
			logging.Debug("Conditional item did not match, skipping")
		}
	}

	return managedInstalls, managedUninstalls, managedUpdates, optionalInstalls, nil
}

// gatherSystemFacts collects system information for predicate evaluation
func gatherSystemFacts() map[string]interface{} {
	facts := make(map[string]interface{})

	// Hostname
	if hostname, err := os.Hostname(); err == nil {
		facts["hostname"] = hostname
	}

	// Architecture
	arch := getSystemArchitecture()
	facts["arch"] = arch         // Primary key as requested
	facts["architecture"] = arch // Keep for backward compatibility

	// Domain and Username from environment
	if domain, exists := os.LookupEnv("USERDOMAIN"); exists {
		facts["domain"] = domain
	}
	if username, exists := os.LookupEnv("USERNAME"); exists {
		facts["username"] = username
	}

	// OS Version (basic implementation without import cycle)
	facts["os_version"] = getWindowsVersionBasic()

	return facts
}

// getSystemArchitecture returns the system architecture
func getSystemArchitecture() string {
	if arch := os.Getenv("PROCESSOR_ARCHITECTURE"); arch != "" {
		switch strings.ToLower(arch) {
		case "amd64", "x86_64":
			return "x64"
		case "x86", "386":
			return "x86"
		case "arm64":
			return "arm64"
		default:
			return arch
		}
	}
	return "unknown"
}

// getWindowsVersionBasic gets basic Windows version info without complex dependencies
func getWindowsVersionBasic() string {
	// This is a simplified version - could be enhanced with Windows API calls
	return "10.0" // Default fallback
}

// evaluateConditionalItem evaluates a single conditional item
func evaluateConditionalItem(item *ConditionalItem, facts map[string]interface{}) (bool, error) {
	if item == nil {
		return true, nil
	}

	// Handle single condition
	if item.Condition != nil {
		return evaluateCondition(item.Condition, facts)
	}

	// Handle multiple conditions
	if len(item.Conditions) == 0 {
		return true, nil // No conditions means always true
	}

	conditionType := strings.ToUpper(item.ConditionType)
	if conditionType == "" {
		conditionType = "AND" // Default to AND logic
	}

	switch conditionType {
	case "AND":
		return evaluateConditionsAnd(item.Conditions, facts)
	case "OR":
		return evaluateConditionsOr(item.Conditions, facts)
	default:
		return false, fmt.Errorf("unknown condition type: %s", conditionType)
	}
}

// evaluateCondition evaluates a single condition
func evaluateCondition(condition *Condition, facts map[string]interface{}) (bool, error) {
	if condition == nil {
		return true, nil
	}

	factValue, exists := facts[condition.Key]
	if !exists {
		return false, fmt.Errorf("fact key '%s' not found", condition.Key)
	}

	return compareValues(factValue, condition.Operator, condition.Value)
}

// evaluateConditionsAnd evaluates multiple conditions with AND logic
func evaluateConditionsAnd(conditions []*Condition, facts map[string]interface{}) (bool, error) {
	for _, condition := range conditions {
		result, err := evaluateCondition(condition, facts)
		if err != nil {
			return false, err
		}
		if !result {
			return false, nil // Short-circuit on first false
		}
	}
	return true, nil
}

// evaluateConditionsOr evaluates multiple conditions with OR logic
func evaluateConditionsOr(conditions []*Condition, facts map[string]interface{}) (bool, error) {
	for _, condition := range conditions {
		result, err := evaluateCondition(condition, facts)
		if err != nil {
			logging.Warn("Error evaluating condition in OR group", "error", err)
			continue // Continue with other conditions in OR group
		}
		if result {
			return true, nil // Short-circuit on first true
		}
	}
	return false, nil
}

// compareValues performs the actual comparison between fact value and condition value
func compareValues(factValue interface{}, operator string, conditionValue interface{}) (bool, error) {
	operator = strings.ToUpper(operator)

	switch operator {
	case "==", "EQUALS":
		return compareEquals(factValue, conditionValue), nil
	case "!=", "NOT_EQUALS":
		return !compareEquals(factValue, conditionValue), nil
	case ">", "GREATER_THAN":
		return compareGreater(factValue, conditionValue), nil
	case "<", "LESS_THAN":
		return compareLess(factValue, conditionValue), nil
	case ">=", "GREATER_THAN_OR_EQUAL":
		return compareEquals(factValue, conditionValue) || compareGreater(factValue, conditionValue), nil
	case "<=", "LESS_THAN_OR_EQUAL":
		return compareEquals(factValue, conditionValue) || compareLess(factValue, conditionValue), nil
	case "LIKE":
		return compareLike(factValue, conditionValue), nil
	case "IN":
		return compareIn(factValue, conditionValue), nil
	case "CONTAINS":
		return compareContains(factValue, conditionValue), nil
	case "BEGINSWITH":
		return compareBeginsWith(factValue, conditionValue), nil
	case "ENDSWITH":
		return compareEndsWith(factValue, conditionValue), nil
	default:
		return false, fmt.Errorf("unknown operator: %s", operator)
	}
}

// Helper comparison functions
func compareEquals(factValue, conditionValue interface{}) bool {
	return valueToString(factValue) == valueToString(conditionValue)
}

func compareGreater(factValue, conditionValue interface{}) bool {
	return valueToString(factValue) > valueToString(conditionValue)
}

func compareLess(factValue, conditionValue interface{}) bool {
	return valueToString(factValue) < valueToString(conditionValue)
}

func compareLike(factValue, conditionValue interface{}) bool {
	factStr := strings.ToLower(valueToString(factValue))
	pattern := strings.ToLower(valueToString(conditionValue))
	pattern = strings.ReplaceAll(pattern, "*", "")
	return strings.Contains(factStr, pattern)
}

func compareIn(factValue, conditionValue interface{}) bool {
	factStr := valueToString(factValue)

	switch cv := conditionValue.(type) {
	case []interface{}:
		for _, item := range cv {
			if factStr == valueToString(item) {
				return true
			}
		}
	case []string:
		for _, item := range cv {
			if factStr == item {
				return true
			}
		}
	case string:
		items := strings.Split(cv, ",")
		for _, item := range items {
			if factStr == strings.TrimSpace(item) {
				return true
			}
		}
	}
	return false
}

func compareContains(factValue, conditionValue interface{}) bool {
	factStr := strings.ToLower(valueToString(factValue))
	conditionStr := strings.ToLower(valueToString(conditionValue))
	return strings.Contains(factStr, conditionStr)
}

func compareBeginsWith(factValue, conditionValue interface{}) bool {
	factStr := strings.ToLower(valueToString(factValue))
	conditionStr := strings.ToLower(valueToString(conditionValue))
	return strings.HasPrefix(factStr, conditionStr)
}

func compareEndsWith(factValue, conditionValue interface{}) bool {
	factStr := strings.ToLower(valueToString(factValue))
	conditionStr := strings.ToLower(valueToString(conditionValue))
	return strings.HasSuffix(factStr, conditionStr)
}

func valueToString(value interface{}) string {
	switch v := value.(type) {
	case string:
		return v
	case int, int8, int16, int32, int64:
		return fmt.Sprintf("%d", v)
	case uint, uint8, uint16, uint32, uint64:
		return fmt.Sprintf("%d", v)
	case float32, float64:
		return fmt.Sprintf("%f", v)
	case bool:
		if v {
			return "true"
		}
		return "false"
	default:
		return fmt.Sprintf("%v", v)
	}
}
