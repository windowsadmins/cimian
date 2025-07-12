// pkg/filter/filter.go - Package for filtering manifest items based on various criteria

package filter

import (
	"os"
	"strings"

	"github.com/spf13/pflag"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/manifest"
)

// ItemFilter holds the filtering configuration and state
type ItemFilter struct {
	items  []string
	logger *logging.Logger
}

// NewItemFilter creates a new ItemFilter instance
func NewItemFilter(logger *logging.Logger) *ItemFilter {
	return &ItemFilter{
		logger: logger,
	}
}

// RegisterFlags registers the --item flag with pflag
func (f *ItemFilter) RegisterFlags() {
	pflag.StringSliceVar(
		&f.items,
		"item",
		nil,
		"Install only the specified package name(s). "+
			"Can be repeated or given as a comma-separated list.",
	)
}

// SetItems allows setting the items filter programmatically
func (f *ItemFilter) SetItems(items []string) {
	f.items = items
}

// GetItems returns the current items filter
func (f *ItemFilter) GetItems() []string {
	return f.items
}

// FilterManifestItems returns only manifest entries whose Name appears in the filter.
// If no filter is set, returns all items unchanged.
func (f *ItemFilter) FilterManifestItems(all []manifest.Item) []manifest.Item {
	if len(f.items) == 0 {
		return all
	}

	// Create a map for case-insensitive lookup
	wantMap := make(map[string]struct{}, len(f.items))
	for _, item := range f.items {
		wantMap[strings.ToLower(strings.TrimSpace(item))] = struct{}{}
	}

	var filtered []manifest.Item
	for _, manifestItem := range all {
		if _, ok := wantMap[strings.ToLower(manifestItem.Name)]; ok {
			filtered = append(filtered, manifestItem)
		}
	}

	return filtered
}

// Apply applies the item filter to a slice of manifest items.
// If no items match the filter, it logs a message and exits with code 0.
// If no filter is set, it returns the items unchanged.
func (f *ItemFilter) Apply(manifestItems []manifest.Item) []manifest.Item {
	if len(f.items) == 0 {
		return manifestItems // no filter requested
	}

	filtered := f.FilterManifestItems(manifestItems)
	if len(filtered) == 0 {
		f.logger.Info("No manifest items match --item filter; exiting.")
		os.Exit(0)
	}

	f.logger.Info("Filtered manifest list to %d item(s) via --item.", len(filtered))
	return filtered
}

// HasFilter returns true if any items are set in the filter
func (f *ItemFilter) HasFilter() bool {
	return len(f.items) > 0
}

// ShouldOverrideCheckOnly returns true if the filter is active and should override checkonly behavior
// When using --item flag, you typically want to test actual installation, not just check
func (f *ItemFilter) ShouldOverrideCheckOnly() bool {
	return f.HasFilter()
}

// SetLogger allows updating the logger after initialization
func (f *ItemFilter) SetLogger(logger *logging.Logger) {
	f.logger = logger
}
