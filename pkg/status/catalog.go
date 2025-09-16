// pkg/status/catalog.go - functions for retrieving catalog items.

package status

import (
	"fmt"
	"github.com/windowsadmins/cimian/pkg/catalog"
)

// DeduplicateCatalogItems iterates all catalog items and returns the one with the highest version.
func DeduplicateCatalogItems(items []catalog.Item) catalog.Item {
	best := items[0]
	fmt.Printf("[DEBUG] DeduplicateCatalogItems: Starting with %s version %s\n", best.Name, best.Version)
	
	for _, candidate := range items[1:] {
		fmt.Printf("[DEBUG] DeduplicateCatalogItems: Comparing %s (%s) vs %s (%s)\n", 
			best.Name, best.Version, candidate.Name, candidate.Version)
		
		if IsOlderVersion(best.Version, candidate.Version) {
			fmt.Printf("[DEBUG] DeduplicateCatalogItems: %s (%s) is older than %s (%s), selecting newer\n", 
				best.Name, best.Version, candidate.Name, candidate.Version)
			best = candidate
		} else {
			fmt.Printf("[DEBUG] DeduplicateCatalogItems: Keeping %s (%s) as best\n", best.Name, best.Version)
		}
	}
	
	fmt.Printf("[DEBUG] DeduplicateCatalogItems: Final selection: %s version %s\n", best.Name, best.Version)
	return best
}
