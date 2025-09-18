// pkg/status/integration.go - Integration layer for multi-source version detection
package status

import (
	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/logging"
)

// GetInstalledVersionMultiSource is the new multi-source aware version detection
// This replaces GetInstalledVersion() for package management
func GetInstalledVersionMultiSource(item catalog.Item) (string, VersionSource, error) {
	// Use the comprehensive multi-source detection
	version, source, err := GetAuthoritativeInstalledVersion(item.Name)
	
	if err != nil {
		logging.Debug("Multi-source version detection failed, falling back to legacy method",
			"item", item.Name, "error", err)
		
		// Fallback to existing logic for backward compatibility
		legacyVersion, legacyErr := GetInstalledVersion(item) // Use the exported function
		if legacyErr == nil && legacyVersion != "" {
			return legacyVersion, SourceCimianReg, nil
		}
		
		return "", SourceUnknown, err
	}
	
	// Log version source for debugging at scale
	if source != SourceCimianReg {
		logging.Info("Multi-source version detected",
			"item", item.Name,
			"version", version,
			"source", string(source),
		)
		
		// Update Cimian's registry to sync with discovered version
		// This prevents version drift in future checks
		updateCimianRegistryFromDiscoveredVersion(item, version, source)
	}
	
	return version, source, nil
}

// updateCimianRegistryFromDiscoveredVersion syncs Cimian's registry with discovered versions
func updateCimianRegistryFromDiscoveredVersion(item catalog.Item, discoveredVersion string, source VersionSource) {
	// Only update if the discovered version is from a higher-confidence source
	highConfidenceSources := []VersionSource{
		SourceFileHash,
		SourceChocolatey,
		SourceWinGet,
		SourceMSIProductCode,
	}
	
	isHighConfidence := false
	for _, hcs := range highConfidenceSources {
		if source == hcs {
			isHighConfidence = true
			break
		}
	}
	
	if !isHighConfidence {
		return // Don't update from low-confidence sources
	}
	
	// Get current Cimian registry version
	currentCimianVersion, err := readInstalledVersionFromRegistry(item.Name)
	if err != nil || currentCimianVersion != discoveredVersion {
		// Update Cimian's registry to match discovered version
		logging.Info("Syncing Cimian registry with discovered version",
			"item", item.Name,
			"oldVersion", currentCimianVersion,
			"newVersion", discoveredVersion,
			"source", string(source),
		)
		
		// Create updated item for registry storage
		// Note: storeInstalledVersionInRegistry is in pkg/installer, 
		// so we use direct registry update here
		updateCimianRegistry(item.Name, discoveredVersion)
	}
}

// updateCimianRegistry directly updates Cimian's registry tracking
func updateCimianRegistry(packageName, version string) {
	// This function will directly update the registry
	// without depending on pkg/installer to avoid circular imports
	
	// TODO: Implement direct registry update
	// For now, log the need for update
	logging.Info("Registry sync needed",
		"package", packageName,
		"version", version,
		"note", "Direct registry update to be implemented")
}

// GetVersionConflictsForReporting returns version conflicts for external monitoring
// This helps ReportMate and other systems identify version drift issues
func GetVersionConflictsForReporting(manifestItems []catalog.Item) []MultiSourceVersionInfo {
	var packageNames []string
	for _, item := range manifestItems {
		packageNames = append(packageNames, item.Name)
	}
	
	conflicts, err := GetVersionConflicts(packageNames)
	if err != nil {
		logging.Warn("Failed to get version conflicts", "error", err)
		return []MultiSourceVersionInfo{}
	}
	
	// Log conflicts for troubleshooting
	if len(conflicts) > 0 {
		logging.Warn("Version conflicts detected - action required",
			"conflictCount", len(conflicts))
		
		for _, conflict := range conflicts {
			logging.Warn("Version conflict detail",
				"package", conflict.PackageName,
				"authoritative", conflict.AuthoritativeVersion,
				"source", string(conflict.AuthoritativeSource),
				"allDetections", len(conflict.AllDetections),
			)
		}
	}
	
	return conflicts
}

// Enhanced version of CheckStatus that uses multi-source detection
func CheckStatusMultiSource(catalogItem catalog.Item, installType, cachePath string) (bool, error) {
	logging.Debug("CheckStatusMultiSource starting",
		"item", catalogItem.Name,
		"installType", installType,
		"OnDemand", catalogItem.OnDemand)
	
	// Use multi-source version detection
	installedVersion, versionSource, err := GetInstalledVersionMultiSource(catalogItem)
	if err != nil {
		logging.Debug("Multi-source version detection failed, using legacy CheckStatus",
			"item", catalogItem.Name, "error", err)
		// Fall back to existing CheckStatus for backward compatibility
		return CheckStatus(catalogItem, installType, cachePath)
	}
	
	// Enhanced logic based on version source confidence
	switch installType {
	case "install", "update":
		if installedVersion == "" {
			logging.Debug("No installed version detected from any source",
				"item", catalogItem.Name)
			return true, nil // Installation needed
		}
		
		// Version comparison with catalog
		if IsOlderVersion(installedVersion, catalogItem.Version) {
			logging.Debug("Update needed - catalog version is newer",
				"item", catalogItem.Name,
				"installedVersion", installedVersion,
				"catalogVersion", catalogItem.Version,
				"versionSource", string(versionSource))
			return true, nil
		}
		
		// If catalog version is older than installed, log but don't downgrade
		if IsOlderVersion(catalogItem.Version, installedVersion) {
			logging.Info("Installed version is newer than catalog - no downgrade",
				"item", catalogItem.Name,
				"installedVersion", installedVersion,
				"catalogVersion", catalogItem.Version,
				"versionSource", string(versionSource))
			return false, nil
		}
		
		// For high-confidence sources, trust the version detection
		if versionSource == SourceFileHash || versionSource == SourceChocolatey || versionSource == SourceWinGet {
			logging.Debug("High-confidence version source indicates no update needed",
				"item", catalogItem.Name,
				"versionSource", string(versionSource))
			return false, nil
		}
		
		// For lower-confidence sources, fall back to existing logic
		return CheckStatus(catalogItem, installType, cachePath)
		
	default:
		// For uninstall and other operations, use existing logic
		return CheckStatus(catalogItem, installType, cachePath)
	}
}
