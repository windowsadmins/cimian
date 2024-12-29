// pkg/extract/nupkg.go

package extract

import (
	"archive/zip"
	"bufio"
	"crypto/md5"
	"encoding/hex"
	"encoding/xml"
	"fmt"
	"io"
	"path/filepath"
	"regexp"
	"strings"

	"gopkg.in/yaml.v3"
)

// SingleQuotedString ensures single quotes in YAML output.
type SingleQuotedString string

func (s SingleQuotedString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Style: yaml.SingleQuotedStyle,
		Value: string(s),
	}
	return node, nil
}

// InstallItem represents a single file entry in Gorilla's "installs" array.
type InstallItem struct {
	Type        SingleQuotedString `yaml:"type"`
	Path        SingleQuotedString `yaml:"path"`
	MD5Checksum SingleQuotedString `yaml:"md5checksum,omitempty"`
	Version     SingleQuotedString `yaml:"version,omitempty"`
}

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

// parsePackageName extracts the base name from a file.
func parsePackageName(fileName string) string {
	ext := filepath.Ext(fileName)
	return strings.TrimSuffix(fileName, ext)
}

// NupkgMetadata parses a .nupkg, extracts the .nuspec, and returns (name, version, developer, description).
func NupkgMetadata(nupkgPath string) (string, string, string, string) {
	r, err := zip.OpenReader(nupkgPath)
	if err != nil {
		// fallback: return file-based name if we can't open
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

	var doc Nuspec
	if err := xml.NewDecoder(rc).Decode(&doc); err != nil {
		return parsePackageName(filepath.Base(nupkgPath)), "", "", ""
	}

	name := doc.Metadata.Title
	if name == "" {
		name = doc.Metadata.ID
	}
	version := doc.Metadata.Version
	dev := doc.Metadata.Authors
	desc := doc.Metadata.Description

	return strings.TrimSpace(name),
		strings.TrimSpace(version),
		strings.TrimSpace(dev),
		strings.TrimSpace(desc)
}

// BuildNupkgInstalls enumerates the contents of a .nupkg, computes MD5 checksums,
// and returns an array of InstallItem for Gorilla's "installs" array.
// We guess final install paths like:
//
//	C:\ProgramData\chocolatey\lib\<pkgId>\tools\<filename>
//
// Only the file extensions in extsWeCare get added.
func BuildNupkgInstalls(nupkgPath, pkgID, pkgVersion string) []InstallItem {
	var results []InstallItem

	zr, err := zip.OpenReader(nupkgPath)
	if err != nil {
		return results // return empty if we can't open
	}
	defer zr.Close()

	// We'll look for .exe, .dll, .msi, .sys inside the package
	extsWeCare := map[string]bool{
		".exe": true, ".dll": true, ".msi": true, ".sys": true,
		".ps1": true, ".txt": true, ".json": true, ".config": true,
	}

	for _, f := range zr.File {
		// skip directories
		if f.FileInfo().IsDir() {
			continue
		}

		extLower := strings.ToLower(filepath.Ext(f.Name))
		if !extsWeCare[extLower] {
			continue
		}

		// guess final path:
		//   C:\ProgramData\chocolatey\lib\<pkgID>\tools\filename
		filenameOnly := filepath.Base(f.Name)
		finalPath := filepath.Join(`C:\ProgramData\chocolatey\lib`, pkgID, `tools`, filenameOnly)

		// compute MD5 from inside the zip
		md5Val, _ := computeMD5FromZipFile(f)

		item := InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(finalPath),
			MD5Checksum: SingleQuotedString(md5Val),
			Version:     SingleQuotedString(pkgVersion),
		}
		results = append(results, item)
	}

	return results
}

var reInstallLocation = regexp.MustCompile(`(?i)\$installLocation\s*=\s*["']([^"']+)["']`)

// BuildGorillaPkgInstalls analyses the .nupkg built by gorillapkg and returns a well-formed "installs" array.
//
// Steps:
//  1. Identify the $installLocation in tools/chocolateyInstall.ps1 if it exists
//  2. If not found, fallback to "C:\Program Files\<TruncatedName>\"
//  3. Enumerate only "payload/" subfolders, compute MD5, preserve subfolder structure
//  4. Keep "version" in each item = rawVersion
//  5. Skip .nuspec, [Content_Types].xml, _rels, "tools/" files
func BuildGorillaPkgInstalls(nupkgPath, pkgID, rawVersion string) ([]InstallItem, error) {
	var results []InstallItem

	// 1) Attempt to open .nupkg
	zr, err := zip.OpenReader(nupkgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open .nupkg: %v", err)
	}
	defer zr.Close()

	// 2) Attempt to read $installLocation from tools/chocolateyInstall.ps1
	installLocation := ""
	var chocoInstall *zip.File
	for _, f := range zr.File {
		lf := strings.ToLower(f.Name)
		if strings.Contains(lf, `tools/chocolateyinstall.ps1`) {
			chocoInstall = f
			break
		}
	}
	if chocoInstall != nil {
		loc, _ := parseInstallLocation(chocoInstall)
		installLocation = loc // might be empty if not found
	}

	// If no $installLocation found, fallback to "C:\Program Files\<TruncatedName>\"
	if installLocation == "" {
		truncatedName := TruncateDomain(pkgID)
		installLocation = filepath.Join(`C:\Program Files`, truncatedName)
	}

	// 3) Enumerate only "payload/" subfolders for real files
	for _, f := range zr.File {
		if f.FileInfo().IsDir() {
			continue
		}
		lowerName := strings.ToLower(f.Name)

		// skip meta files: .nuspec, [Content_Types].xml, _rels, and anything in "tools/"
		if strings.HasSuffix(lowerName, ".nuspec") ||
			strings.Contains(lowerName, "[content_types].xml") ||
			strings.Contains(lowerName, "_rels") ||
			strings.HasPrefix(lowerName, "tools/") {
			continue
		}

		// We only care about "payload/" files (including subfolders)
		if !strings.HasPrefix(lowerName, "payload/") {
			continue
		}

		// subPath = everything after "payload/", preserving subfolders
		subPath := strings.TrimPrefix(f.Name, "payload/")
		subPath = strings.TrimPrefix(subPath, `/`)
		// final path => installLocation + subfolders
		finalPath := filepath.Join(installLocation, subPath)

		// compute MD5
		md5Val, _ := computeMD5FromZipFile(f)

		// build an InstallItem
		item := InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(finalPath),
			MD5Checksum: SingleQuotedString(md5Val),
			Version:     SingleQuotedString(rawVersion), // keep the .nupkg version
		}
		results = append(results, item)
	}

	return results, nil
}

// parseInstallLocation tries to read the contents of chocolateyInstall.ps1 and match $installLocation
func parseInstallLocation(zf *zip.File) (string, error) {
	rc, err := zf.Open()
	if err != nil {
		return "", err
	}
	defer rc.Close()

	var location string
	scanner := bufio.NewScanner(rc)
	for scanner.Scan() {
		line := scanner.Text()
		if matches := reInstallLocation.FindStringSubmatch(line); len(matches) == 2 {
			location = matches[1]
			break
		}
	}
	return location, nil
}

// computeMD5FromZipFile streams a single file entry from the zip, returning the MD5 as hex
func computeMD5FromZipFile(zf *zip.File) (string, error) {
	rc, err := zf.Open()
	if err != nil {
		return "", err
	}
	defer rc.Close()

	h := md5.New()
	if _, err := io.Copy(h, rc); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// truncateDomain splits on '.' and returns the final segment, e.g.
//
//	"com.someorg.app" => "app"
func TruncateDomain(pkgID string) string {
	parts := strings.Split(pkgID, ".")
	if len(parts) == 0 {
		return pkgID
	}
	return parts[len(parts)-1]
}
