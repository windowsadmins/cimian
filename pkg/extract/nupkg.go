// pkg/extract/nupkg.go

package extract

import (
	"archive/zip"
	"encoding/xml"
	"path/filepath"
	"strings"
)

// Nuspec defines the minimal .nuspec struct
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

// parse .nupkg => .nuspec
func NupkgMetadata(nupkgPath string) (name, version, dev, desc string) {
	r, err := zip.OpenReader(nupkgPath)
	if err != nil {
		return parsePackageName(filepath.Base(nupkgPath)), "", "", ""
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
		return parsePackageName(filepath.Base(nupkgPath)), "", "", ""
	}
	rc, err := nuspecFile.Open()
	if err != nil {
		return parsePackageName(filepath.Base(nupkgPath)), "", "", ""
	}
	defer rc.Close()
	var doc nuspec
	if err := xml.NewDecoder(rc).Decode(&doc); err != nil {
		return parsePackageName(filepath.Base(nupkgPath)), "", "", ""
	}
	if doc.Metadata.Title != "" {
		name = doc.Metadata.Title
	} else {
		name = doc.Metadata.ID
	}
	version = doc.Metadata.Version
	dev = doc.Metadata.Authors
	desc = doc.Metadata.Description
	return strings.TrimSpace(name), strings.TrimSpace(version), strings.TrimSpace(dev), strings.TrimSpace(desc)
}
