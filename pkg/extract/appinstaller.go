// pkg/extract/appinstaller.go - functions for extracting metadata from AppInstaller files.

package extract

import (
	"encoding/xml"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

// AppInstallerManifest represents the structure of an .appinstaller file
type AppInstallerManifest struct {
	XMLName     xml.Name `xml:"AppInstaller"`
	URI         string   `xml:"Uri,attr"`
	Version     string   `xml:"Version,attr"`
	MainBundle  MainBundle `xml:"MainBundle"`
	MainPackage MainPackage `xml:"MainPackage"`
	Dependencies Dependencies `xml:"Dependencies"`
	UpdateSettings UpdateSettings `xml:"UpdateSettings"`
}

type MainBundle struct {
	Name      string `xml:"Name,attr"`
	Version   string `xml:"Version,attr"`
	Publisher string `xml:"Publisher,attr"`
	URI       string `xml:"Uri,attr"`
}

type MainPackage struct {
	Name      string `xml:"Name,attr"`
	Version   string `xml:"Version,attr"`
	Publisher string `xml:"Publisher,attr"`
	URI       string `xml:"Uri,attr"`
}

type Dependencies struct {
	Packages []DependencyPackage `xml:"Package"`
}

type DependencyPackage struct {
	Name                  string `xml:"Name,attr"`
	Publisher             string `xml:"Publisher,attr"`
	ProcessorArchitecture string `xml:"ProcessorArchitecture,attr"`
	URI                   string `xml:"Uri,attr"`
	Version               string `xml:"Version,attr"`
}

type UpdateSettings struct {
	OnLaunch OnLaunch `xml:"OnLaunch"`
}

type OnLaunch struct {
	HoursBetweenUpdateChecks string `xml:"HoursBetweenUpdateChecks,attr"`
}

// AppInstallerMetadata extracts metadata from an .appinstaller file
func AppInstallerMetadata(appInstallerPath string) (name, version, developer, description, packageFamilyName, uri string) {
	file, err := os.Open(appInstallerPath)
	if err != nil {
		return "UnknownAppInstaller", "1.0.0", "", "", "", ""
	}
	defer file.Close()

	data, err := io.ReadAll(file)
	if err != nil {
		return "UnknownAppInstaller", "1.0.0", "", "", "", ""
	}

	var manifest AppInstallerManifest
	if err := xml.Unmarshal(data, &manifest); err != nil {
		return "UnknownAppInstaller", "1.0.0", "", "", "", ""
	}

	// Extract metadata from MainBundle (preferred) or MainPackage
	var appName, appVersion, publisher string
	
	if manifest.MainBundle.Name != "" {
		appName = manifest.MainBundle.Name
		appVersion = manifest.MainBundle.Version
		publisher = manifest.MainBundle.Publisher
	} else if manifest.MainPackage.Name != "" {
		appName = manifest.MainPackage.Name
		appVersion = manifest.MainPackage.Version
		publisher = manifest.MainPackage.Publisher
	} else {
		// Fallback to filename if no package info found
		appName = strings.TrimSuffix(filepath.Base(appInstallerPath), ".appinstaller")
		appVersion = manifest.Version
		if appVersion == "" {
			appVersion = "1.0.0"
		}
	}

	// Clean up the publisher name - extract just the company name from CN=
	developerName := extractDeveloperFromPublisher(publisher)
	
	// Generate a basic description
	description = fmt.Sprintf("Application package for %s", appName)
	if len(manifest.Dependencies.Packages) > 0 {
		description += fmt.Sprintf(" (includes %d dependencies)", len(manifest.Dependencies.Packages))
	}

	// Generate package family name (simplified version)
	packageFamilyName = generatePackageFamilyName(appName, publisher)

	return appName, appVersion, developerName, description, packageFamilyName, manifest.URI
}

// extractDeveloperFromPublisher extracts the company name from a publisher string like "CN=Files, O=Files, S=Washington, C=US"
func extractDeveloperFromPublisher(publisher string) string {
	if publisher == "" {
		return ""
	}

	// Look for CN= (Common Name) in the publisher string
	parts := strings.Split(publisher, ",")
	for _, part := range parts {
		part = strings.TrimSpace(part)
		if strings.HasPrefix(part, "CN=") {
			return strings.TrimPrefix(part, "CN=")
		}
	}

	// If no CN= found, return the whole publisher string
	return publisher
}

// generatePackageFamilyName creates a simplified package family name
// In reality, this would be more complex and involve hash generation
func generatePackageFamilyName(appName, publisher string) string {
	if appName == "" {
		return ""
	}
	
	// Simplified family name generation
	cleanName := strings.ReplaceAll(appName, " ", "")
	if publisher != "" {
		cleanPublisher := extractDeveloperFromPublisher(publisher)
		return fmt.Sprintf("%s_%s", cleanName, strings.ReplaceAll(cleanPublisher, " ", ""))
	}
	
	return cleanName + "_8wekyb3d8bbwe" // Generic suffix for unknown publishers
}

// GetAppInstallerDependencies returns information about package dependencies
func GetAppInstallerDependencies(appInstallerPath string) ([]DependencyPackage, error) {
	file, err := os.Open(appInstallerPath)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	data, err := io.ReadAll(file)
	if err != nil {
		return nil, err
	}

	var manifest AppInstallerManifest
	if err := xml.Unmarshal(data, &manifest); err != nil {
		return nil, err
	}

	return manifest.Dependencies.Packages, nil
}

// GetAppInstallerUpdateSettings returns the update configuration
func GetAppInstallerUpdateSettings(appInstallerPath string) (UpdateSettings, error) {
	file, err := os.Open(appInstallerPath)
	if err != nil {
		return UpdateSettings{}, err
	}
	defer file.Close()

	data, err := io.ReadAll(file)
	if err != nil {
		return UpdateSettings{}, err
	}

	var manifest AppInstallerManifest
	if err := xml.Unmarshal(data, &manifest); err != nil {
		return UpdateSettings{}, err
	}

	return manifest.UpdateSettings, nil
}
