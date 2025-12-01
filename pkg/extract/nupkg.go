// pkg/extract/nupkg.go - functions for extracting metadata from NuGet packages.

package extract

import (
	"archive/zip"
	"encoding/xml"
	"path/filepath"
	"strings"
)

// Nuspec defines the minimal .nuspec struct for .nupkg files
type Nuspec struct {
	XMLName  xml.Name `xml:"package"`
	Metadata struct {
		ID          string `xml:"id"`
		Version     string `xml:"version"`
		Title       string `xml:"title"`
		Description string `xml:"description"`
		Authors     string `xml:"authors"`
		Owners      string `xml:"owners"`
		Tags        string `xml:"tags"`
	} `xml:"metadata"`
}

// parsePackageName extracts the base name from a file (no .nupkg extension).
func parsePackageName(fileName string) string {
	ext := filepath.Ext(fileName)
	return strings.TrimSuffix(fileName, ext)
}

// NupkgMetadata parses a .nupkg, extracts the .nuspec, and returns 5 values:
// (identifier, name, version, developer, description).
func NupkgMetadata(nupkgPath string) (string, string, string, string, string) {
	// If we fail to open or read the .nuspec, fallback to the file-based name,
	// with empty version/author/desc. But always set "identifier" to something.

	r, err := zip.OpenReader(nupkgPath)
	if err != nil {
		fallback := parsePackageName(filepath.Base(nupkgPath))
		return fallback, fallback, "", "", ""
	}
	defer r.Close()

	var nuspecFile *zip.File
	for _, f := range r.File {
		if strings.EqualFold(filepath.Ext(f.Name), ".nuspec") {
			nuspecFile = f
			break
		}
	}
	if nuspecFile == nil {
		fallback := parsePackageName(filepath.Base(nupkgPath))
		return fallback, fallback, "", "", ""
	}

	rc, err := nuspecFile.Open()
	if err != nil {
		fallback := parsePackageName(filepath.Base(nupkgPath))
		return fallback, fallback, "", "", ""
	}
	defer rc.Close()

	var doc Nuspec
	if err := xml.NewDecoder(rc).Decode(&doc); err != nil {
		fallback := parsePackageName(filepath.Base(nupkgPath))
		return fallback, fallback, "", "", ""
	}

	// identifier is always Nuspec.Metadata.ID
	identifier := strings.TrimSpace(doc.Metadata.ID)

	// name can prefer Title, fallback to ID
	name := doc.Metadata.Title
	if name == "" {
		name = doc.Metadata.ID
	}
	name = strings.TrimSpace(name)

	version := strings.TrimSpace(doc.Metadata.Version)
	dev := strings.TrimSpace(doc.Metadata.Authors)
	desc := strings.TrimSpace(doc.Metadata.Description)

	// Return 5 separate strings.
	return identifier, name, version, dev, desc
}
