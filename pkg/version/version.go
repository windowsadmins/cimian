// pkg/version/version.go - functions for displaying version information about a Go application.

package version

import (
	"fmt"
	"strings"
)

// These values are private which ensures they can only be set with the build flags.
var (
	version   = "unknown"
	branch    = "unknown"
	revision  = "unknown"
	goVersion = "unknown"
	buildDate = "unknown"
	appName   = "unknown"
)

// Info is a structure with version build information about the current application.
type Info struct {
	Version   string `json:"version"`
	Branch    string `json:"branch"`
	Revision  string `json:"revision"`
	GoVersion string `json:"go_version"`
	BuildDate string `json:"build_date"`
}

// Version returns a structure with the current version information.
func Version() Info {
	return Info{
		Version:   version,
		Branch:    branch,
		Revision:  revision,
		GoVersion: goVersion,
		BuildDate: buildDate,
	}
}

// Print outputs the application name and version string.
func Print() {
	v := Version()
	fmt.Printf("%s %s\n", appName, v.Version)
}

// PrintVersion outputs only the version string.
func PrintVersion() {
	v := Version()
	fmt.Printf("%s\n", v.Version)
}

// PrintFull prints the application name and detailed version information.
func PrintFull() {
	v := Version()
	fmt.Printf("%s %s\n", appName, v.Version)
	fmt.Printf("  branch: \t%s\n", v.Branch)
	fmt.Printf("  revision: \t%s\n", v.Revision)
	fmt.Printf("  build date: \t%s\n", v.BuildDate)
	fmt.Printf("  go version: \t%s\n", v.GoVersion)
}

// Normalize trims trailing ".0" segments from version strings.
func Normalize(version string) string {
	parts := strings.Split(version, ".")
	for len(parts) > 1 && parts[len(parts)-1] == "0" {
		parts = parts[:len(parts)-1]
	}
	return strings.Join(parts, ".")
}
