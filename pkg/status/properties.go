//go:build windows
// +build windows

package status

import (
	"fmt"

	"github.com/gonutz/w32"
	"github.com/windowsadmins/gorilla/pkg/logging"
)

//
//
// This is based on the StackOverflow answer here: https://stackoverflow.com/a/46222037
// Information on what strings can be passed to `VerQueryValueString` can
// be found here: https://docs.microsoft.com/en-us/windows/desktop/api/winver/nf-winver-verqueryvaluea#remarks
//
// This uses a bunch of C stuff I dont understand and seems kinda silly.
// Here is what I *think* I understand:
// - GetFileVersionInfoSize returns the size of the buffer needed to store file metadata
// - GetFileVersionInfo returns the actual metadata, but only the "fixed" portion is directly readable
// - VerQueryValueRoot gets info from he fixed part, which contains the "FileVersion"
// - VerQueryValueTranslations The other stuff is in a "non-fixed" part, and this function translates it ¯\_(ツ)_/¯
// - VerQueryValueString looks up data in the translated data
//
// Again, I dont really understand this, so maybe this is all wrong...
//
//

// GetFileMetadata retrieves Windows metadata from the file at 'path'.
// The returned WindowsMetadata struct includes the version string and product name if present.
func GetFileMetadata(path string) WindowsMetadata {
	var finalMetadata WindowsMetadata

	bufferSize := w32.GetFileVersionInfoSize(path)
	if bufferSize <= 0 {
		logging.Info("No metadata found:", path)
		return finalMetadata
	}

	rawMetadata := make([]byte, bufferSize)
	if !w32.GetFileVersionInfo(path, rawMetadata) {
		logging.Warn("Unable to get metadata:", path)
		return finalMetadata
	}

	// Additional inline explanation about the w32.VerQueryValueRoot usage:
	// VerQueryValueRoot fetches the 'fixed' portion of version info from the file.
	fixed, ok := w32.VerQueryValueRoot(rawMetadata)
	if !ok {
		logging.Warn("Unable to get file version:", path)
		return finalMetadata
	}

	rawVersion := fixed.FileVersion()
	finalMetadata.versionMajor = int(rawVersion & 0xFFFF000000000000 >> 48)
	finalMetadata.versionMinor = int(rawVersion & 0x0000FFFF00000000 >> 32)
	finalMetadata.versionPatch = int(rawVersion & 0x00000000FFFF0000 >> 16)
	finalMetadata.versionBuild = int(rawVersion & 0x000000000000FFFF)

	finalMetadata.versionString = fmt.Sprintf("%d.%d.%d.%d",
		finalMetadata.versionMajor,
		finalMetadata.versionMinor,
		finalMetadata.versionPatch,
		finalMetadata.versionBuild)

	if translations, ok := w32.VerQueryValueTranslations(rawMetadata); ok && len(translations) > 0 {
		finalMetadata.productName, _ = w32.VerQueryValueString(rawMetadata, translations[0], "ProductName")
	}

	return finalMetadata
}
