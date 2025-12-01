// pkg/pkginfo/installs.go - functions for building installs arrays from packages.

package pkginfo

import (
	"archive/zip"
	"bufio"
	"crypto/md5"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"github.com/windowsadmins/cimian/pkg/extract"
)

// MaxInstallItems is the maximum number of items to include in the installs array.
// Only the 3 most recently modified files are included to avoid generating excessive hashes.
const MaxInstallItems = 3

// fileWithModTime holds a zip file entry and its associated install item for sorting
type fileWithModTime struct {
	file *zip.File
	item InstallItem
}

var reInstallLocation = regexp.MustCompile(`(?i)\$installLocation\s*=\s*["']([^"']+)["']`)

// BuildNupkgInstalls enumerates the contents of a .nupkg, computes MD5 checksums,
// and returns an array of InstallItem for Cimian's "installs" array.
// We guess final install paths like:
//
//	C:\ProgramData\chocolatey\lib\<pkgId>\tools\<filename>
//
// Only the file extensions in extsWeCare get added.
// Returns up to MaxInstallItems (3) most recently modified files.
func BuildNupkgInstalls(nupkgPath, pkgID, pkgVersion string) []InstallItem {
	zr, err := zip.OpenReader(nupkgPath)
	if err != nil {
		return nil // return nil if we can't open
	}
	defer zr.Close()

	// We'll look for .exe, .dll, .msi, .sys, etc.
	extsWeCare := map[string]bool{
		".exe": true, ".dll": true, ".msi": true, ".sys": true,
		".ps1": true, ".txt": true, ".json": true, ".config": true,
	}

	// Collect files with their modification times
	var filesWithTimes []fileWithModTime

	for _, f := range zr.File {
		// skip directories
		if f.FileInfo().IsDir() {
			continue
		}

		extLower := strings.ToLower(filepath.Ext(f.Name))
		if !extsWeCare[extLower] {
			continue
		}

		// guess final path => C:\ProgramData\chocolatey\lib\<pkgID>\tools\<filename>
		filenameOnly := filepath.Base(f.Name)
		finalPath := filepath.Join(`C:\ProgramData\chocolatey\lib`, pkgID, `tools`, filenameOnly)

		// compute MD5
		md5Val, _ := computeMD5FromZipFile(f)

		item := InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(finalPath),
			MD5Checksum: SingleQuotedString(md5Val),
			Version:     SingleQuotedString(pkgVersion),
		}
		filesWithTimes = append(filesWithTimes, fileWithModTime{file: f, item: item})
	}

	// Sort by modification time (most recent first)
	sort.Slice(filesWithTimes, func(i, j int) bool {
		return filesWithTimes[i].file.Modified.After(filesWithTimes[j].file.Modified)
	})

	// Limit to MaxInstallItems (3 most recently modified)
	if len(filesWithTimes) > MaxInstallItems {
		filesWithTimes = filesWithTimes[:MaxInstallItems]
	}

	// Extract just the InstallItem from sorted/limited slice
	var results []InstallItem
	for _, fwt := range filesWithTimes {
		results = append(results, fwt.item)
	}

	return results
}

// BuildCimianPkgInstalls analyses the .nupkg built by cimianpkg and returns a well-formed "installs" array.
//
// Steps:
//  1. Identify the $installLocation in tools/chocolateyInstall.ps1 if it exists
//  2. If not found, fallback to "C:\Program Files\<TruncatedName>\"
//  3. Enumerate only "payload/" subfolders, compute MD5, preserve subfolder structure
//  4. Keep "version" in each item = rawVersion
//  5. Skip .nuspec, [Content_Types].xml, _rels, "tools/" files
//  6. Sort by modification time (most recent first) and limit to MaxInstallItems (3)
func BuildCimianPkgInstalls(nupkgPath, pkgID, rawVersion string) ([]InstallItem, error) {
	zr, err := zip.OpenReader(nupkgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open .nupkg: %v", err)
	}
	defer zr.Close()

	installLocation := ""
	// Look for tools/chocolateyInstall.ps1 to extract $installLocation
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
		installLocation = loc
	}
	// If not found, fallback
	if installLocation == "" {
		truncatedName := TruncateDomain(pkgID)
		// Use ProgramW6432 environment variable to force 64-bit Program Files path
		programFiles := os.Getenv("ProgramW6432")
		if programFiles == "" {
			programFiles = `C:\Program Files`
		}
		installLocation = filepath.Join(programFiles, truncatedName)
	}

	// Collect all payload files with their modification times
	var filesWithTimes []fileWithModTime

	// enumerate only payload/ subfolder
	for _, f := range zr.File {
		if f.FileInfo().IsDir() {
			continue
		}
		lowerName := strings.ToLower(f.Name)

		// skip meta files: .nuspec, [Content_Types].xml, _rels, tools/
		if strings.HasSuffix(lowerName, ".nuspec") ||
			strings.Contains(lowerName, "[content_types].xml") ||
			strings.Contains(lowerName, "_rels") ||
			strings.HasPrefix(lowerName, "tools/") {
			continue
		}

		if !strings.HasPrefix(lowerName, "payload/") {
			continue
		}

		subPath := strings.TrimPrefix(f.Name, "payload/")
		subPath = strings.TrimPrefix(subPath, `/`)
		finalPath := filepath.Join(installLocation, subPath)

		md5Val, _ := computeMD5FromZipFile(f)

		item := InstallItem{
			Type:        SingleQuotedString("file"),
			Path:        SingleQuotedString(finalPath),
			MD5Checksum: SingleQuotedString(md5Val),
			Version:     SingleQuotedString(rawVersion),
		}
		filesWithTimes = append(filesWithTimes, fileWithModTime{file: f, item: item})
	}

	// Sort by modification time (most recent first)
	sort.Slice(filesWithTimes, func(i, j int) bool {
		return filesWithTimes[i].file.Modified.After(filesWithTimes[j].file.Modified)
	})

	// Limit to MaxInstallItems (3 most recently modified)
	if len(filesWithTimes) > MaxInstallItems {
		filesWithTimes = filesWithTimes[:MaxInstallItems]
	}

	// Extract just the InstallItem from sorted/limited slice
	var results []InstallItem
	for _, fwt := range filesWithTimes {
		results = append(results, fwt.item)
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

// TruncateDomain splits on '.' and returns the final segment, e.g.
//
//	"com.someorg.app" => "app"
func TruncateDomain(pkgID string) string {
	parts := strings.Split(pkgID, ".")
	if len(parts) == 0 {
		return pkgID
	}
	return parts[len(parts)-1]
}

// BuildPkgInstalls enumerates the payload contents of a .pkg package and returns
// an array of InstallItem for Cimian's "installs" array. This maintains compatibility
// with the existing installation tracking system.
// Returns up to MaxInstallItems (3) most recently modified files.
func BuildPkgInstalls(pkgPath, pkgID, pkgVersion string) ([]InstallItem, error) {
	// Extract build-info.yaml to determine installation type
	buildInfo, err := extract.ExtractPkgBuildInfo(pkgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to extract build-info.yaml: %v", err)
	}

	zipReader, err := zip.OpenReader(pkgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open .pkg file: %v", err)
	}
	defer zipReader.Close()

	// Collect files with their modification times
	var filesWithTimes []fileWithModTime

	// Determine installation location
	installLocation := strings.TrimSpace(buildInfo.InstallLocation)
	if installLocation == "" {
		// Installer-type package - files stay in extraction location
		// For installer packages, we track the installer file itself
		for _, f := range zipReader.File {
			if f.FileInfo().IsDir() {
				continue
			}

			lowerName := strings.ToLower(f.Name)
			if !strings.HasPrefix(lowerName, "payload/") {
				continue
			}

			// Look for installer files
			ext := strings.ToLower(filepath.Ext(f.Name))
			if ext == ".msi" || ext == ".exe" || ext == ".msix" {
				subPath := strings.TrimPrefix(f.Name, "payload/")
				tempPath := filepath.Join(`C:\Temp\pkg_install`, subPath)

				md5Val, _ := computeMD5FromZipFile(f)

				item := InstallItem{
					Type:        SingleQuotedString("installer"),
					Path:        SingleQuotedString(tempPath),
					MD5Checksum: SingleQuotedString(md5Val),
					Version:     SingleQuotedString(pkgVersion),
				}
				filesWithTimes = append(filesWithTimes, fileWithModTime{file: f, item: item})
			}
		}
	} else {
		// Copy-type package - enumerate payload files with final paths
		for _, f := range zipReader.File {
			if f.FileInfo().IsDir() {
				continue
			}

			lowerName := strings.ToLower(f.Name)
			if !strings.HasPrefix(lowerName, "payload/") {
				continue
			}

			// Calculate final installation path
			subPath := strings.TrimPrefix(f.Name, "payload/")
			subPath = strings.TrimPrefix(subPath, `/`)
			finalPath := filepath.Join(installLocation, subPath)

			md5Val, _ := computeMD5FromZipFile(f)

			item := InstallItem{
				Type:        SingleQuotedString("file"),
				Path:        SingleQuotedString(finalPath),
				MD5Checksum: SingleQuotedString(md5Val),
				Version:     SingleQuotedString(pkgVersion),
			}
			filesWithTimes = append(filesWithTimes, fileWithModTime{file: f, item: item})
		}
	}

	// Sort by modification time (most recent first)
	sort.Slice(filesWithTimes, func(i, j int) bool {
		return filesWithTimes[i].file.Modified.After(filesWithTimes[j].file.Modified)
	})

	// Limit to MaxInstallItems (3 most recently modified)
	if len(filesWithTimes) > MaxInstallItems {
		filesWithTimes = filesWithTimes[:MaxInstallItems]
	}

	// Extract just the InstallItem from sorted/limited slice
	var results []InstallItem
	for _, fwt := range filesWithTimes {
		results = append(results, fwt.item)
	}

	return results, nil
}
