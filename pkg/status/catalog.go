// pkg/status/catalog.go - functions for retrieving catalog items.

package status

import (
	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/logging"
)

// DeduplicateCatalogItems iterates all catalog items and returns the one with the highest version.
func DeduplicateCatalogItems(items []catalog.Item) catalog.Item {
	best := items[0]
	logging.Debug("DeduplicateCatalogItems starting", "package", best.Name, "initial_version", best.Version)
	
	for _, candidate := range items[1:] {
		logging.Debug("DeduplicateCatalogItems comparing versions", 
			"package", best.Name, 
			"current_best", best.Version, 
			"candidate", candidate.Version)
		
		if IsOlderVersion(best.Version, candidate.Version) {
			logging.Debug("DeduplicateCatalogItems selecting newer version", 
				"package", best.Name, 
				"old_version", best.Version, 
				"new_version", candidate.Version)
			best = candidate
		} else {
			logging.Debug("DeduplicateCatalogItems keeping current version", 
				"package", best.Name, 
				"version", best.Version)
		}
	}
	
	logging.Debug("DeduplicateCatalogItems completed", "package", best.Name, "final_version", best.Version)
	return best
}
