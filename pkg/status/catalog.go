// pkg/status/catalog.go - functions for retrieving catalog items.

package status

import (
	"github.com/windowsadmins/cimian/pkg/catalog"
)

// DeduplicateCatalogItems iterates all catalog items and returns the one with the highest version.
func DeduplicateCatalogItems(items []catalog.Item) catalog.Item {
	best := items[0]
	for _, candidate := range items[1:] {
		if IsOlderVersion(best.Version, candidate.Version) {
			best = candidate
		}
	}
	return best
}
