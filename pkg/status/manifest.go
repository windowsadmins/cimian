// pkg/status/manifest.go - functions for manipulating manifest items.

package status

import (
	"strings"

	"github.com/windowsadmins/cimian/pkg/manifest"
)

// DeduplicateManifestItems filters the slice of manifest items so that for each package name
// only the one with the highest version remains.
func DeduplicateManifestItems(manifestItems []manifest.Item) []manifest.Item {
	dedup := make(map[string]manifest.Item)
	for _, m := range manifestItems {
		if m.Name == "" {
			continue
		}
		key := strings.ToLower(m.Name)
		if existing, ok := dedup[key]; ok {
			if IsOlderVersion(existing.Version, m.Version) {
				dedup[key] = m
			}
		} else {
			dedup[key] = m
		}
	}
	var result []manifest.Item
	for _, m := range dedup {
		result = append(result, m)
	}
	return result
}
