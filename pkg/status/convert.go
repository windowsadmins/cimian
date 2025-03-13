// pkg/status/convert.go - functions for converting between package status types.

package status

import (
	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/manifest"
)

// ToCatalogItem converts a manifest.Item into a minimal catalog.Item
func ToCatalogItem(m manifest.Item) catalog.Item {
	// If you need more logic, expand it. This is a minimal example:
	return catalog.Item{
		Name:        m.Name,
		Version:     m.Version,
		DisplayName: m.Name,
		Installer: catalog.InstallerItem{
			Location: m.InstallerLocation,
			// If you need a hash or type, fill them in, e.g. "exe" or "msi"
			Type: "exe",
		},
		SupportedArch: m.SupportedArch,
	}
}
