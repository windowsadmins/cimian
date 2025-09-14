// pkg/status/multisource.go - Multi-source version detection for package management
// Inspired by ReportMate's comprehensive application discovery approach
package status

import (
	"encoding/json"
	"fmt"
	"os/exec"
	"regexp"
	"sort"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/logging"
)

// VersionSource represents where a version was detected from
type VersionSource string

const (
	SourceFileHash       VersionSource = "file_hash"        // File-based with hash verification (highest priority)
	SourceChocolatey     VersionSource = "chocolatey"       // Chocolatey package manager
	SourceWinGet         VersionSource = "winget"           // Windows Package Manager
	SourceMSIProductCode VersionSource = "msi_product"      // MSI Product Code registry
	SourceWindowsStore   VersionSource = "windows_store"    // Microsoft Store / MSIX
	SourceUninstallReg   VersionSource = "uninstall_reg"    // Standard Windows uninstall registry
	SourceCimianReg      VersionSource = "cimian_reg"       // Cimian's own tracking (lowest priority)
	SourceUnknown        VersionSource = "unknown"          // Could not determine source
)

// DetectedVersion represents a version found by a specific source
type DetectedVersion struct {
	Version    string        `json:"version"`
	Source     VersionSource `json:"source"`
	Confidence int           `json:"confidence"` // 1-100, higher = more reliable
	DetectedAt time.Time     `json:"detected_at"`
	Metadata   map[string]string `json:"metadata,omitempty"` // Additional source-specific data
}

// MultiSourceVersionInfo contains all detected versions from all sources
type MultiSourceVersionInfo struct {
	PackageName       string            `json:"package_name"`
	AuthoritativeVersion string         `json:"authoritative_version"` // The version Cimian should trust
	AuthoritativeSource  VersionSource  `json:"authoritative_source"`
	AllDetections     []DetectedVersion `json:"all_detections"`
	ConflictDetected  bool             `json:"conflict_detected"`
	LastScanned       time.Time        `json:"last_scanned"`
}

// ChocolateyPackage represents a package from chocolatey list
type ChocolateyPackage struct {
	Name    string `json:"name"`
	Version string `json:"version"`
}

// WinGetPackage represents a package from winget list
type WinGetPackage struct {
	Name      string `json:"name"`
	ID        string `json:"id"`
	Version   string `json:"version"`
	Available string `json:"available,omitempty"`
	Source    string `json:"source"`
}

// GetMultiSourceVersionInfo performs comprehensive version detection across all sources
func GetMultiSourceVersionInfo(packageName string) (*MultiSourceVersionInfo, error) {
	info := &MultiSourceVersionInfo{
		PackageName:   packageName,
		AllDetections: make([]DetectedVersion, 0),
		LastScanned:   time.Now(),
	}

	// Detect versions from all sources in parallel for performance
	detectionChannels := []chan DetectedVersion{
		make(chan DetectedVersion, 1), // File hash
		make(chan DetectedVersion, 1), // Chocolatey
		make(chan DetectedVersion, 1), // WinGet
		make(chan DetectedVersion, 1), // MSI Product Code
		make(chan DetectedVersion, 1), // Windows Store
		make(chan DetectedVersion, 1), // Uninstall Registry
		make(chan DetectedVersion, 1), // Cimian Registry
	}

	// Launch detection goroutines
	go detectFromFileHash(packageName, detectionChannels[0])
	go detectFromChocolatey(packageName, detectionChannels[1])
	go detectFromWinGet(packageName, detectionChannels[2])
	go detectFromMSIProductCode(packageName, detectionChannels[3])
	go detectFromWindowsStore(packageName, detectionChannels[4])
	go detectFromUninstallRegistry(packageName, detectionChannels[5])
	go detectFromCimianRegistry(packageName, detectionChannels[6])

	// Collect results with timeout
	timeout := time.After(30 * time.Second) // timeout for 10k+ machines
	for i := 0; i < len(detectionChannels); i++ {
		select {
		case detection := <-detectionChannels[i]:
			if detection.Version != "" {
				info.AllDetections = append(info.AllDetections, detection)
			}
		case <-timeout:
			logging.Warn("Multi-source version detection timeout", "package", packageName)
			break
		}
	}

	// Determine authoritative version using priority ranking
	info.determineAuthoritativeVersion()
	
	return info, nil
}

// determineAuthoritativeVersion selects the most reliable version
func (info *MultiSourceVersionInfo) determineAuthoritativeVersion() {
	if len(info.AllDetections) == 0 {
		return
	}

	// Sort by confidence (highest first)
	sort.Slice(info.AllDetections, func(i, j int) bool {
		return info.AllDetections[i].Confidence > info.AllDetections[j].Confidence
	})

	// Use highest confidence detection
	best := info.AllDetections[0]
	info.AuthoritativeVersion = best.Version
	info.AuthoritativeSource = best.Source

	// Check for conflicts (different versions from high-confidence sources)
	for _, detection := range info.AllDetections {
		if detection.Confidence >= 80 && detection.Version != info.AuthoritativeVersion {
			info.ConflictDetected = true
			logging.Warn("Version conflict detected",
				"package", info.PackageName,
				"authoritative", info.AuthoritativeVersion,
				"conflicting", detection.Version,
				"source", detection.Source)
			break
		}
	}
}

// detectFromFileHash checks file-based verification with hash (highest priority)
func detectFromFileHash(packageName string, result chan<- DetectedVersion) {
	defer close(result)
	
	// This would integrate with the existing installs array logic
	// For now, return empty - this can be enhanced to call existing checkInstalls logic
	
	// TODO: Integrate with pkg/status checkInstalls() function
	// This should have highest confidence (95-100) when hash matches
	
	result <- DetectedVersion{} // Empty for now
}

// detectFromChocolatey queries chocolatey for installed packages
func detectFromChocolatey(packageName string, result chan<- DetectedVersion) {
	defer close(result)
	
	// Query chocolatey for all installed packages
	cmd := exec.Command("choco", "list", "--local-only", "--limit-output")
	output, err := cmd.Output()
	if err != nil {
		logging.Debug("Chocolatey detection failed", "package", packageName, "error", err)
		result <- DetectedVersion{}
		return
	}

	// Parse chocolatey output: "packagename|version"
	lines := strings.Split(string(output), "\n")
	for _, line := range lines {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}

		parts := strings.Split(line, "|")
		if len(parts) != 2 {
			continue
		}

		chocoName := strings.TrimSpace(parts[0])
		version := strings.TrimSpace(parts[1])

		// Match against package name (fuzzy matching for different naming conventions)
		if matchesPackageName(chocoName, packageName) {
			result <- DetectedVersion{
				Version:    version,
				Source:     SourceChocolatey,
				Confidence: 90, // High confidence for package manager
				DetectedAt: time.Now(),
				Metadata: map[string]string{
					"chocolatey_name": chocoName,
				},
			}
			return
		}
	}

	result <- DetectedVersion{} // Not found
}

// detectFromWinGet queries Windows Package Manager
func detectFromWinGet(packageName string, result chan<- DetectedVersion) {
	defer close(result)
	
	// Query winget for installed packages
	cmd := exec.Command("winget", "list", "--accept-source-agreements")
	output, err := cmd.Output()
	if err != nil {
		logging.Debug("WinGet detection failed", "package", packageName, "error", err)
		result <- DetectedVersion{}
		return
	}

	// Parse winget output (more complex format)
	lines := strings.Split(string(output), "\n")
	for _, line := range lines {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "Name") || strings.HasPrefix(line, "----") {
			continue
		}

		// WinGet output is typically: Name    Id    Version   Available   Source
		fields := regexp.MustCompile(`\s{2,}`).Split(line, -1)
		if len(fields) >= 3 {
			wingetName := strings.TrimSpace(fields[0])
			wingetID := strings.TrimSpace(fields[1])
			version := strings.TrimSpace(fields[2])

			if matchesPackageName(wingetName, packageName) || matchesPackageName(wingetID, packageName) {
				result <- DetectedVersion{
					Version:    version,
					Source:     SourceWinGet,
					Confidence: 85, // High confidence for package manager
					DetectedAt: time.Now(),
					Metadata: map[string]string{
						"winget_name": wingetName,
						"winget_id":   wingetID,
					},
				}
				return
			}
		}
	}

	result <- DetectedVersion{} // Not found
}

// detectFromMSIProductCode checks MSI product codes (existing logic)
func detectFromMSIProductCode(packageName string, result chan<- DetectedVersion) {
	defer close(result)
	
	// TODO: Integrate with existing MSI product code logic from checkStatus
	// This should have high confidence (85-90) when product code matches
	
	result <- DetectedVersion{} // Empty for now - to be implemented
}

// detectFromWindowsStore checks Microsoft Store / MSIX packages
func detectFromWindowsStore(packageName string, result chan<- DetectedVersion) {
	defer close(result)
	
	// Query PowerShell for MSIX/AppX packages
	psScript := `Get-AppxPackage | Where-Object { $_.Name -like "*` + packageName + `*" } | Select-Object Name, Version | ConvertTo-Json`
	cmd := exec.Command("powershell", "-Command", psScript)
	output, err := cmd.Output()
	if err != nil {
		logging.Debug("Windows Store detection failed", "package", packageName, "error", err)
		result <- DetectedVersion{}
		return
	}

	// Parse JSON output
	var packages []map[string]interface{}
	if err := json.Unmarshal(output, &packages); err != nil {
		// Try single object
		var singlePackage map[string]interface{}
		if err := json.Unmarshal(output, &singlePackage); err == nil {
			packages = []map[string]interface{}{singlePackage}
		} else {
			result <- DetectedVersion{}
			return
		}
	}

	for _, pkg := range packages {
		if name, ok := pkg["Name"].(string); ok {
			if version, ok := pkg["Version"].(string); ok {
				if matchesPackageName(name, packageName) {
					result <- DetectedVersion{
						Version:    version,
						Source:     SourceWindowsStore,
						Confidence: 80, // Good confidence for store apps
						DetectedAt: time.Now(),
						Metadata: map[string]string{
							"store_name": name,
						},
					}
					return
				}
			}
		}
	}

	result <- DetectedVersion{} // Not found
}

// detectFromUninstallRegistry uses existing registry logic
func detectFromUninstallRegistry(packageName string, result chan<- DetectedVersion) {
	defer close(result)
	
	// Use existing getUninstallKeys() function
	installedApps, err := getUninstallKeys()
	if err != nil {
		logging.Debug("Uninstall registry detection failed", "package", packageName, "error", err)
		result <- DetectedVersion{}
		return
	}

	// Find matching package
	for name, app := range installedApps {
		if matchesPackageName(name, packageName) {
			result <- DetectedVersion{
				Version:    app.Version,
				Source:     SourceUninstallReg,
				Confidence: 75, // Moderate confidence - registry can be inconsistent
				DetectedAt: time.Now(),
				Metadata: map[string]string{
					"registry_name": name,
					"registry_key":  app.Key,
					"uninstall_string": app.Uninstall,
				},
			}
			return
		}
	}

	result <- DetectedVersion{} // Not found
}

// detectFromCimianRegistry checks Cimian's own tracking (lowest priority)
func detectFromCimianRegistry(packageName string, result chan<- DetectedVersion) {
	defer close(result)
	
	// Use existing readInstalledVersionFromRegistry() function
	version, err := readInstalledVersionFromRegistry(packageName)
	if err != nil || version == "" {
		result <- DetectedVersion{}
		return
	}

	result <- DetectedVersion{
		Version:    version,
		Source:     SourceCimianReg,
		Confidence: 50, // Lowest confidence - only fallback
		DetectedAt: time.Now(),
		Metadata: map[string]string{
			"cimian_registry_key": fmt.Sprintf("HKLM\\Software\\ManagedInstalls\\%s", packageName),
		},
	}
}

// matchesPackageName performs fuzzy matching for package names across different sources
func matchesPackageName(detected, target string) bool {
	detected = strings.ToLower(strings.TrimSpace(detected))
	target = strings.ToLower(strings.TrimSpace(target))
	
	// Exact match
	if detected == target {
		return true
	}
	
	// Contains match
	if strings.Contains(detected, target) || strings.Contains(target, detected) {
		return true
	}
	
	// Handle common name variations
	variations := []string{
		strings.ReplaceAll(target, " ", ""),           // Remove spaces
		strings.ReplaceAll(target, "-", ""),           // Remove hyphens  
		strings.ReplaceAll(target, ".", ""),           // Remove dots
		strings.ReplaceAll(target, " ", "-"),          // Space to hyphen
		strings.ReplaceAll(target, "-", " "),          // Hyphen to space
	}
	
	for _, variation := range variations {
		if strings.Contains(detected, variation) || strings.Contains(variation, detected) {
			return true
		}
	}
	
	return false
}

// GetAuthoritativeInstalledVersion returns the most reliable version for reporting
// This replaces the current getLocalInstalledVersion() for multi-source awareness
func GetAuthoritativeInstalledVersion(packageName string) (string, VersionSource, error) {
	info, err := GetMultiSourceVersionInfo(packageName)
	if err != nil {
		return "", SourceUnknown, err
	}
	
	if info.AuthoritativeVersion == "" {
		return "", SourceUnknown, fmt.Errorf("no version detected from any source")
	}
	
	return info.AuthoritativeVersion, info.AuthoritativeSource, nil
}

// GetVersionConflicts returns packages with conflicting versions across sources
// Useful for reporting and troubleshooting
func GetVersionConflicts(packageNames []string) ([]MultiSourceVersionInfo, error) {
	var conflicts []MultiSourceVersionInfo
	
	for _, packageName := range packageNames {
		info, err := GetMultiSourceVersionInfo(packageName)
		if err != nil {
			continue
		}
		
		if info.ConflictDetected {
			conflicts = append(conflicts, *info)
		}
	}
	
	return conflicts, nil
}
