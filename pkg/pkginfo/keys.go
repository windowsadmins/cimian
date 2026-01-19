// pkg/pkginfo/keys.go - shared types for package metadata YAML keys.

package pkginfo

import (
	"fmt"

	"gopkg.in/yaml.v3"
)

// SingleQuotedString ensures single quotes in YAML output.
type SingleQuotedString string

func (s SingleQuotedString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Style: yaml.SingleQuotedStyle,
		Value: string(s),
	}
	return node, nil
}

// NoQuoteEmptyString ensures empty strings appear without quotes.
type NoQuoteEmptyString string

func (s NoQuoteEmptyString) MarshalYAML() (interface{}, error) {
	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Value: string(s),
	}
	return node, nil
}

// InstallItem represents a single file entry in Cimian's "installs" array.
type InstallItem struct {
	Type        SingleQuotedString `yaml:"type"`
	Path        SingleQuotedString `yaml:"path,omitempty"`
	MD5Checksum SingleQuotedString `yaml:"md5checksum,omitempty"`
	Version     SingleQuotedString `yaml:"version,omitempty"`
	ProductCode SingleQuotedString `yaml:"product_code,omitempty"`
	UpgradeCode SingleQuotedString `yaml:"upgrade_code,omitempty"`
}

// Installer represents the installer/uninstaller details.
type Installer struct {
	Location    string   `yaml:"location"`
	Hash        string   `yaml:"hash"`
	Type        string   `yaml:"type"`
	Size        int64    `yaml:"size,omitempty"`
	Arguments   []string `yaml:"arguments,omitempty"`
	ProductCode string   `yaml:"product_code,omitempty"`
	UpgradeCode string   `yaml:"upgrade_code,omitempty"`
}

// MarshalYAML forces the output order for Installer as follows:
// type, size, location, hash, then (if type=="msi") product_code and upgrade_code,
// then arguments (only if non-empty).
func (i *Installer) MarshalYAML() (interface{}, error) {
	var content []*yaml.Node

	// Always include "type"
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "type"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: i.Type},
	)
	// Always include "size"
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "size"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!int", Value: fmt.Sprintf("%d", i.Size)},
	)
	// Always include "location"
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "location"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: i.Location},
	)
	// Always include "hash"
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "hash"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: i.Hash},
	)
	// Only include arguments if there are any
	if len(i.Arguments) > 0 {
		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "arguments"},
			buildArgumentsNode(i.Arguments),
		)
	}

	node := &yaml.Node{
		Kind:    yaml.MappingNode,
		Tag:     "!!map",
		Content: content,
	}
	return node, nil
}

func buildArgumentsNode(args []string) *yaml.Node {
	seq := &yaml.Node{
		Kind: yaml.SequenceNode,
		Tag:  "!!seq",
	}
	for _, a := range args {
		seq.Content = append(seq.Content, &yaml.Node{
			Kind:  yaml.ScalarNode,
			Tag:   "!!str",
			Value: a,
		})
	}
	return seq
}

// PkgsInfo represents the structure of the pkginfo YAML file.
type PkgsInfo struct {
	Name                 string             `yaml:"name"`
	DisplayName          string             `yaml:"display_name,omitempty"`
	Identifier           string             `yaml:"identifier,omitempty"`
	Version              string             `yaml:"version"`
	Description          NoQuoteEmptyString `yaml:"description"`
	Category             NoQuoteEmptyString `yaml:"category"`
	Developer            NoQuoteEmptyString `yaml:"developer"`
	Catalogs             []string           `yaml:"catalogs"`
	Installs             []InstallItem      `yaml:"installs,omitempty"`
	SupportedArch        []string           `yaml:"supported_architectures"`
	UnattendedInstall    bool               `yaml:"unattended_install"`
	UnattendedUninstall  bool               `yaml:"unattended_uninstall"`
	Requires             []string           `yaml:"requires,omitempty"`
	UpdateFor            []string           `yaml:"update_for,omitempty"`
	BlockingApps         []string           `yaml:"blocking_applications,omitempty"`
	MinOSVersion         string             `yaml:"minimum_os_version,omitempty"`
	MaxOSVersion         string             `yaml:"maximum_os_version,omitempty"`
	InstallerType        string             `yaml:"installer_type,omitempty"`
	Installer            *Installer         `yaml:"installer,omitempty"`
	Uninstaller          *Installer         `yaml:"uninstaller,omitempty"`
	PreinstallScript     string             `yaml:"preinstall_script,omitempty"`
	PostinstallScript    string             `yaml:"postinstall_script,omitempty"`
	PreuninstallScript   string             `yaml:"preuninstall_script,omitempty"`
	PostuninstallScript  string             `yaml:"postuninstall_script,omitempty"`
	InstallCheckScript   string             `yaml:"installcheck_script,omitempty"`
	UninstallCheckScript string             `yaml:"uninstallcheck_script,omitempty"`
	IconName             string             `yaml:"icon_name,omitempty"`
	OnDemand             bool               `yaml:"OnDemand,omitempty"`
	ManagedProfiles      []string           `yaml:"managed_profiles,omitempty"`
	ManagedApps          []string           `yaml:"managed_apps,omitempty"`
}

// MarshalYAML forces the output order for PkgsInfo:
// name, display_name, version first, then the rest alphabetically.
func (p *PkgsInfo) MarshalYAML() (interface{}, error) {
	var content []*yaml.Node

	// Helper function to add a string field
	addStringField := func(key, value string) {
		if value != "" {
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: key},
				&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: value},
			)
		}
	}

	// Helper function to add a NoQuoteEmptyString field (allows empty values)
	addNoQuoteField := func(key string, value NoQuoteEmptyString) {
		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: key},
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: string(value)},
		)
	}

	// Helper function to add a bool field
	addBoolField := func(key string, value bool) {
		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: key},
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!bool", Value: fmt.Sprintf("%t", value)},
		)
	}

	// Helper function to add a string slice field
	addStringSlice := func(key string, values []string) {
		if len(values) > 0 {
			seq := &yaml.Node{Kind: yaml.SequenceNode, Tag: "!!seq"}
			for _, v := range values {
				seq.Content = append(seq.Content, &yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: v})
			}
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: key},
				seq,
			)
		}
	}

	// === PRIORITY FIELDS (always first, in this order) ===
	// 1. name (always required)
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "name"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: p.Name},
	)

	// 2. display_name (if present)
	addStringField("display_name", p.DisplayName)

	// 3. version (always required)
	content = append(content,
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "version"},
		&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: p.Version},
	)

	// === REMAINING FIELDS (alphabetically) ===

	// blocking_applications
	addStringSlice("blocking_applications", p.BlockingApps)

	// catalogs
	addStringSlice("catalogs", p.Catalogs)

	// category
	addNoQuoteField("category", p.Category)

	// description
	addNoQuoteField("description", p.Description)

	// developer
	addNoQuoteField("developer", p.Developer)

	// icon_name
	addStringField("icon_name", p.IconName)

	// identifier
	addStringField("identifier", p.Identifier)

	// installcheck_script
	addStringField("installcheck_script", p.InstallCheckScript)

	// installer
	if p.Installer != nil {
		installerNode, err := p.Installer.MarshalYAML()
		if err != nil {
			return nil, err
		}
		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "installer"},
			installerNode.(*yaml.Node),
		)
	}

	// installer_type
	addStringField("installer_type", p.InstallerType)

	// installs
	if len(p.Installs) > 0 {
		seq := &yaml.Node{Kind: yaml.SequenceNode, Tag: "!!seq"}
		for _, inst := range p.Installs {
			itemNode := &yaml.Node{Kind: yaml.MappingNode, Tag: "!!map"}
			// type
			itemNode.Content = append(itemNode.Content,
				&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "type"},
				&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: string(inst.Type), Style: yaml.SingleQuotedStyle},
			)
			// path
			if inst.Path != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "path"},
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: string(inst.Path), Style: yaml.SingleQuotedStyle},
				)
			}
			// md5checksum
			if inst.MD5Checksum != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "md5checksum"},
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: string(inst.MD5Checksum), Style: yaml.SingleQuotedStyle},
				)
			}
			// product_code
			if inst.ProductCode != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "product_code"},
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: string(inst.ProductCode), Style: yaml.SingleQuotedStyle},
				)
			}
			// upgrade_code
			if inst.UpgradeCode != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "upgrade_code"},
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: string(inst.UpgradeCode), Style: yaml.SingleQuotedStyle},
				)
			}
			// version
			if inst.Version != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "version"},
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: string(inst.Version), Style: yaml.SingleQuotedStyle},
				)
			}
			seq.Content = append(seq.Content, itemNode)
		}
		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "installs"},
			seq,
		)
	}

	// managed_apps
	addStringSlice("managed_apps", p.ManagedApps)

	// managed_profiles
	addStringSlice("managed_profiles", p.ManagedProfiles)

	// maximum_os_version
	addStringField("maximum_os_version", p.MaxOSVersion)

	// minimum_os_version
	addStringField("minimum_os_version", p.MinOSVersion)

	// OnDemand
	if p.OnDemand {
		addBoolField("OnDemand", p.OnDemand)
	}

	// postinstall_script
	addStringField("postinstall_script", p.PostinstallScript)

	// postuninstall_script
	addStringField("postuninstall_script", p.PostuninstallScript)

	// preinstall_script
	addStringField("preinstall_script", p.PreinstallScript)

	// preuninstall_script
	addStringField("preuninstall_script", p.PreuninstallScript)

	// requires
	addStringSlice("requires", p.Requires)

	// supported_architectures
	addStringSlice("supported_architectures", p.SupportedArch)

	// unattended_install
	addBoolField("unattended_install", p.UnattendedInstall)

	// unattended_uninstall
	addBoolField("unattended_uninstall", p.UnattendedUninstall)

	// uninstallcheck_script
	addStringField("uninstallcheck_script", p.UninstallCheckScript)

	// uninstaller
	if p.Uninstaller != nil {
		uninstallerNode, err := p.Uninstaller.MarshalYAML()
		if err != nil {
			return nil, err
		}
		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: "uninstaller"},
			uninstallerNode.(*yaml.Node),
		)
	}

	// update_for
	addStringSlice("update_for", p.UpdateFor)

	node := &yaml.Node{
		Kind:    yaml.MappingNode,
		Tag:     "!!map",
		Content: content,
	}
	return node, nil
}
