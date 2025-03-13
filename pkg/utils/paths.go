// pkg/utils/paths.go - utility functions for working with file paths.

package utils

import "strings"

// NormalizeWindowsPath ensures Windows-style paths with single backslashes.
// It handles:
// - Converting forward slashes to backslashes
// - Ensuring single leading backslash
// - Removing any double backslashes
func NormalizeWindowsPath(path string) string {
	// Replace any forward slashes with backslashes
	normalized := strings.ReplaceAll(path, "/", `\`)

	// Remove all leading backslashes to add exactly one
	normalized = strings.TrimLeft(normalized, `\`)

	// Ensure one leading backslash
	normalized = `\` + normalized

	// Handle any accidental double-backslashes
	for strings.Contains(normalized, `\\`) {
		normalized = strings.ReplaceAll(normalized, `\\`, `\`)
	}

	return normalized
}
