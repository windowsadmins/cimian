// pkg/pkginfo/pkginfo.go - functions for managing package metadata.

package pkginfo

import (
	"encoding/json"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strings"

	"github.com/windowsadmins/cimian/pkg/extract"
	"github.com/windowsadmins/cimian/pkg/rollback"
	"golang.org/x/sys/windows/registry"
	"gopkg.in/yaml.v3"
)

const (
	InstallInfoPath = `C:\ProgramData\ManagedInstalls\InstallInfo.yaml`
)

// SingleQuotedString ensures single quotes in YAML output.
type SingleQuotedString string

func (s SingleQuotedString) MarshalYAML() (interface{}, error) {
	return &yaml.Node{
		Kind:  yaml.ScalarNode,
		Style: yaml.SingleQuotedStyle,
		Value: string(s),
	}, nil
}

// NoQuoteEmptyString ensures empty strings appear without quotes.
type NoQuoteEmptyString string

func (s NoQuoteEmptyString) MarshalYAML() (interface{}, error) {
	return &yaml.Node{
		Kind:  yaml.ScalarNode,
		Tag:   "!!str",
		Value: string(s),
		Style: 0,
	}, nil
}

// PkgsInfo is the canonical struct for pkginfo YAML files.
// Used by cimiimport, makepkginfo, and other tools.
type PkgsInfo struct {
	Name                   string             `yaml:"name"`
	DisplayName            string             `yaml:"display_name,omitempty"`
	Version                string             `yaml:"version"`
	Catalogs               []string           `yaml:"catalogs,omitempty"`
	Category               NoQuoteEmptyString `yaml:"category,omitempty"`
	Description            NoQuoteEmptyString `yaml:"description,omitempty"`
	Developer              NoQuoteEmptyString `yaml:"developer,omitempty"`
	IconName               string             `yaml:"icon_name,omitempty"`
	Identifier             string             `yaml:"identifier,omitempty"`
	InstallCheckScript     string             `yaml:"installcheck_script,omitempty"`
	Installer              *Installer         `yaml:"installer,omitempty"`
	InstallerType          string             `yaml:"installer_type,omitempty"`
	Installs               []InstallItem      `yaml:"installs,omitempty"`
	ManagedApps            []string           `yaml:"managed_apps,omitempty"`
	ManagedProfiles        []string           `yaml:"managed_profiles,omitempty"`
	MaxOSVersion           string             `yaml:"maximum_os_version,omitempty"`
	MinOSVersion           string             `yaml:"minimum_os_version,omitempty"`
	OnDemand               bool               `yaml:"ondemand,omitempty"`
	PostinstallScript      string             `yaml:"postinstall_script,omitempty"`
	PostuninstallScript    string             `yaml:"postuninstall_script,omitempty"`
	PreinstallScript       string             `yaml:"preinstall_script,omitempty"`
	PreuninstallScript     string             `yaml:"preuninstall_script,omitempty"`
	Requires               []string           `yaml:"requires,omitempty"`
	SupportedArchitectures []string           `yaml:"supported_architectures,omitempty"`
	UnattendedInstall      bool               `yaml:"unattended_install,omitempty"`
	UnattendedUninstall    bool               `yaml:"unattended_uninstall,omitempty"`
	UninstallCheckScript   string             `yaml:"uninstallcheck_script,omitempty"`
	Uninstaller            *Installer         `yaml:"uninstaller,omitempty"`
	UpdateFor              []string           `yaml:"update_for,omitempty"`
}

// Installer represents installer/uninstaller metadata
type Installer struct {
	Type        string   `yaml:"type,omitempty"`
	Location    string   `yaml:"location,omitempty"`
	Hash        string   `yaml:"hash,omitempty"`
	Size        int64    `yaml:"size,omitempty"`
	ProductCode string   `yaml:"product_code,omitempty"`
	UpgradeCode string   `yaml:"upgrade_code,omitempty"`
	Arguments   []string `yaml:"arguments,omitempty"`
	Switches    []string `yaml:"switches,omitempty"`
	Flags       []string `yaml:"flags,omitempty"`
}

// MarshalYAML for Installer enforces field order:
// type, size, location, hash, product_code, upgrade_code, arguments, switches, flags
func (i *Installer) MarshalYAML() (interface{}, error) {
	var content []*yaml.Node

	addField := func(key, value string) {
		if value != "" {
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Value: key},
				&yaml.Node{Kind: yaml.ScalarNode, Value: value},
			)
		}
	}
	addInt := func(key string, value int64) {
		if value > 0 {
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Value: key},
				&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!int", Value: fmt.Sprintf("%d", value)},
			)
		}
	}
	addSlice := func(key string, values []string) {
		if len(values) > 0 {
			seq := &yaml.Node{Kind: yaml.SequenceNode}
			for _, v := range values {
				seq.Content = append(seq.Content, &yaml.Node{Kind: yaml.ScalarNode, Value: v})
			}
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Value: key},
				seq,
			)
		}
	}

	addField("type", i.Type)
	addInt("size", i.Size)
	addField("location", i.Location)
	addField("hash", i.Hash)
	addField("product_code", i.ProductCode)
	addField("upgrade_code", i.UpgradeCode)
	addSlice("arguments", i.Arguments)
	addSlice("switches", i.Switches)
	addSlice("flags", i.Flags)

	return &yaml.Node{
		Kind:    yaml.MappingNode,
		Content: content,
	}, nil
}

// InstallItem represents an item in the installs array
type InstallItem struct {
	Type        SingleQuotedString `yaml:"type,omitempty"`
	Path        SingleQuotedString `yaml:"path,omitempty"`
	MD5Checksum SingleQuotedString `yaml:"md5checksum,omitempty"`
	Version     SingleQuotedString `yaml:"version,omitempty"`
	ProductCode SingleQuotedString `yaml:"product_code,omitempty"`
	UpgradeCode SingleQuotedString `yaml:"upgrade_code,omitempty"`
}

// MarshalYAML outputs PkgsInfo keys with name, display_name, version first,
// then remaining keys in alphabetical order.
func (p *PkgsInfo) MarshalYAML() (interface{}, error) {
	var content []*yaml.Node

	// Helper to add a string field if non-empty
	addStringField := func(key, value string) {
		if value != "" {
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Value: key},
				&yaml.Node{Kind: yaml.ScalarNode, Value: value},
			)
		}
	}

	// Helper to add a NoQuoteEmptyString field (empty string renders without quotes)
	addNoQuoteField := func(key string, value NoQuoteEmptyString) {
		if value != "" {
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Value: key},
				&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!str", Value: string(value)},
			)
		}
	}

	// Helper to add a string slice if non-empty
	addStringSlice := func(key string, values []string) {
		if len(values) > 0 {
			seq := &yaml.Node{Kind: yaml.SequenceNode}
			for _, v := range values {
				seq.Content = append(seq.Content, &yaml.Node{Kind: yaml.ScalarNode, Value: v})
			}
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Value: key},
				seq,
			)
		}
	}

	// Helper to add a bool field (only if true)
	addBoolField := func(key string, value bool) {
		if value {
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Value: key},
				&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!bool", Value: "true"},
			)
		}
	}

	// Helper to add a script field with literal block style
	addScriptField := func(key, value string) {
		if value != "" {
			content = append(content,
				&yaml.Node{Kind: yaml.ScalarNode, Value: key},
				&yaml.Node{Kind: yaml.ScalarNode, Style: yaml.LiteralStyle, Value: value},
			)
		}
	}

	// Helper to add an Installer field
	addInstallerField := func(key string, inst *Installer) {
		if inst == nil {
			return
		}
		instNode := &yaml.Node{Kind: yaml.MappingNode}

		addInstField := func(k, v string) {
			if v != "" {
				instNode.Content = append(instNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Value: k},
					&yaml.Node{Kind: yaml.ScalarNode, Value: v},
				)
			}
		}
		addInstInt := func(k string, v int64) {
			if v > 0 {
				instNode.Content = append(instNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Value: k},
					&yaml.Node{Kind: yaml.ScalarNode, Tag: "!!int", Value: fmt.Sprintf("%d", v)},
				)
			}
		}
		addInstSlice := func(k string, values []string) {
			if len(values) > 0 {
				seq := &yaml.Node{Kind: yaml.SequenceNode}
				for _, v := range values {
					seq.Content = append(seq.Content, &yaml.Node{Kind: yaml.ScalarNode, Value: v})
				}
				instNode.Content = append(instNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Value: k},
					seq,
				)
			}
		}

		// Installer fields in logical order
		addInstField("type", inst.Type)
		addInstInt("size", inst.Size)
		addInstField("location", inst.Location)
		addInstField("hash", inst.Hash)
		addInstField("product_code", inst.ProductCode)
		addInstField("upgrade_code", inst.UpgradeCode)
		addInstSlice("arguments", inst.Arguments)
		addInstSlice("switches", inst.Switches)
		addInstSlice("flags", inst.Flags)

		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Value: key},
			instNode,
		)
	}

	// Helper to add InstallItem slice
	addInstallsField := func(key string, items []InstallItem) {
		if len(items) == 0 {
			return
		}
		seq := &yaml.Node{Kind: yaml.SequenceNode}
		for _, item := range items {
			itemNode := &yaml.Node{Kind: yaml.MappingNode}
			if item.Type != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Value: "type"},
					&yaml.Node{Kind: yaml.ScalarNode, Style: yaml.SingleQuotedStyle, Value: string(item.Type)},
				)
			}
			if item.Path != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Value: "path"},
					&yaml.Node{Kind: yaml.ScalarNode, Style: yaml.SingleQuotedStyle, Value: string(item.Path)},
				)
			}
			if item.MD5Checksum != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Value: "md5checksum"},
					&yaml.Node{Kind: yaml.ScalarNode, Style: yaml.SingleQuotedStyle, Value: string(item.MD5Checksum)},
				)
			}
			if item.Version != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Value: "version"},
					&yaml.Node{Kind: yaml.ScalarNode, Style: yaml.SingleQuotedStyle, Value: string(item.Version)},
				)
			}
			if item.ProductCode != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Value: "product_code"},
					&yaml.Node{Kind: yaml.ScalarNode, Style: yaml.SingleQuotedStyle, Value: string(item.ProductCode)},
				)
			}
			if item.UpgradeCode != "" {
				itemNode.Content = append(itemNode.Content,
					&yaml.Node{Kind: yaml.ScalarNode, Value: "upgrade_code"},
					&yaml.Node{Kind: yaml.ScalarNode, Style: yaml.SingleQuotedStyle, Value: string(item.UpgradeCode)},
				)
			}
			seq.Content = append(seq.Content, itemNode)
		}
		content = append(content,
			&yaml.Node{Kind: yaml.ScalarNode, Value: key},
			seq,
		)
	}

	// PRIORITY FIELDS: name, display_name, version (always first, in this order)
	addStringField("name", p.Name)
	addStringField("display_name", p.DisplayName)
	addStringField("version", p.Version)

	// Remaining fields in alphabetical order
	addStringSlice("catalogs", p.Catalogs)
	addNoQuoteField("category", p.Category)
	addNoQuoteField("description", p.Description)
	addNoQuoteField("developer", p.Developer)
	addStringField("icon_name", p.IconName)
	addStringField("identifier", p.Identifier)
	addScriptField("installcheck_script", p.InstallCheckScript)
	addInstallerField("installer", p.Installer)
	addStringField("installer_type", p.InstallerType)
	addInstallsField("installs", p.Installs)
	addStringSlice("managed_apps", p.ManagedApps)
	addStringSlice("managed_profiles", p.ManagedProfiles)
	addStringField("maximum_os_version", p.MaxOSVersion)
	addStringField("minimum_os_version", p.MinOSVersion)
	addBoolField("ondemand", p.OnDemand)
	addScriptField("postinstall_script", p.PostinstallScript)
	addScriptField("postuninstall_script", p.PostuninstallScript)
	addScriptField("preinstall_script", p.PreinstallScript)
	addScriptField("preuninstall_script", p.PreuninstallScript)
	addStringSlice("requires", p.Requires)
	addStringSlice("supported_architectures", p.SupportedArchitectures)
	addBoolField("unattended_install", p.UnattendedInstall)
	addBoolField("unattended_uninstall", p.UnattendedUninstall)
	addScriptField("uninstallcheck_script", p.UninstallCheckScript)
	addInstallerField("uninstaller", p.Uninstaller)
	addStringSlice("update_for", p.UpdateFor)

	return &yaml.Node{
		Kind:    yaml.MappingNode,
		Content: content,
	}, nil
}

// GetInstalledVersion retrieves the installed version of the specified software.
func GetInstalledVersion(softwareName string) (string, error) {
	// Define the registry keys to search
	uninstallPaths := []string{
		`SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`,
		`SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall`,
	}

	// Search both HKEY_LOCAL_MACHINE and HKEY_CURRENT_USER
	hives := []registry.Key{registry.LOCAL_MACHINE, registry.CURRENT_USER}

	for _, hive := range hives {
		for _, path := range uninstallPaths {
			key, err := registry.OpenKey(hive, path, registry.READ)
			if err != nil {
				continue
			}
			defer key.Close()

			subkeyNames, err := key.ReadSubKeyNames(-1)
			if err != nil {
				continue
			}

			for _, subkeyName := range subkeyNames {
				subkey, err := registry.OpenKey(key, subkeyName, registry.READ)
				if err != nil {
					continue
				}

				displayName, _, err := subkey.GetStringValue("DisplayName")
				if err != nil {
					subkey.Close()
					continue
				}

				if displayName == softwareName {
					displayVersion, _, err := subkey.GetStringValue("DisplayVersion")
					subkey.Close()
					if err != nil {
						return "", fmt.Errorf("failed to get version for %s: %v", softwareName, err)
					}
					log.Printf("Found installed version for %s: %s", softwareName, displayVersion)
					return displayVersion, nil
				}
				subkey.Close()
			}
		}
	}

	// Software not found
	return "", fmt.Errorf("software %s not found", softwareName)
}

// PkgInfo represents the metadata for a package, including dependencies
type PkgInfo struct {
	Name              string   `json:"name"`
	Version           string   `json:"version"`
	Dependencies      []string `json:"dependencies"`
	InstallerLocation string   `json:"installer_location"`
}

// ReadPkgInfo reads and parses the pkgsinfo metadata from the given path.
func ReadPkgInfo(filePath string) (map[string]interface{}, error) {
	file, err := os.Open(filePath)
	if err != nil {
		return nil, fmt.Errorf("failed to open pkgsinfo file: %v", err)
	}
	defer file.Close()

	var pkgInfo map[string]interface{}
	if err := json.NewDecoder(file).Decode(&pkgInfo); err != nil {
		return nil, fmt.Errorf("failed to decode pkgsinfo: %v", err)
	}

	return pkgInfo, nil
}

// InstallDependencies installs all dependencies for the given package
func InstallDependencies(pkg *PkgInfo) error {
	if len(pkg.Dependencies) == 0 {
		log.Printf("No dependencies for package: %s", pkg.Name)
		return nil
	}

	for _, dependency := range pkg.Dependencies {
		log.Printf("Installing dependency: %s for package: %s", dependency, pkg.Name)
		depPkg, err := LoadPackageInfo(dependency)
		if err != nil {
			return fmt.Errorf("failed to load dependency %s: %v", dependency, err)
		}

		err = InstallPackage(depPkg)
		if err != nil {
			return fmt.Errorf("failed to install dependency %s: %v", dependency, err)
		}
	}
	return nil
}

// LoadPackageInfo loads the package metadata from InstallInfo.yaml
func LoadPackageInfo(packageName string) (*PkgInfo, error) {
	file, err := os.Open(InstallInfoPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open install info file: %v", err)
	}
	defer file.Close()

	var installedPackages []PkgInfo
	if err := json.NewDecoder(file).Decode(&installedPackages); err != nil {
		return nil, fmt.Errorf("failed to decode install info: %v", err)
	}

	for _, pkg := range installedPackages {
		if pkg.Name == packageName {
			return &pkg, nil
		}
	}
	return nil, fmt.Errorf("package %s not found", packageName)
}

// InstallPackage installs the given package, including handling dependencies
func InstallPackage(pkg *PkgInfo) error {
	rollbackManager := &rollback.RollbackManager{}
	log.Printf("Starting installation for package: %s Version: %s", pkg.Name, pkg.Version)

	// Install dependencies first
	if err := InstallDependencies(pkg); err != nil {
		rollbackManager.ExecuteRollback()
		return fmt.Errorf("failed to install dependencies for package %s: %v", pkg.Name, err)
	}

	// Simulate the installation process
	log.Printf("Installing package: %s Version: %s", pkg.Name, pkg.Version)
	rollbackManager.AddRollbackAction(rollback.RollbackAction{Description: "Uninstalling package", Execute: func() error {
		// Placeholder for uninstall logic
		log.Printf("Rolling back installation for package: %s", pkg.Name)
		return nil
	}})

	// Log the completion of the installation
	log.Printf("Successfully installed package: %s Version: %s", pkg.Name, pkg.Version)
	return nil
}

// Metadata can be the common struct that describes the extracted info
type Metadata struct {
	Title         string
	ID            string
	Version       string
	Developer     string
	Description   string
	Category      string
	ProductCode   string
	UpgradeCode   string
	Architecture  string
	SupportedArch []string
	InstallerType string
}

// ExtractPackageInfo merges the “gatherInstallerInfo” and
// “extractInstallerMetadata” logic into one function.
// You can call this from makepkginfo or cimianimport.
func ExtractPackageInfo(path string, defaultArch string) (Metadata, error) {
	ext := strings.ToLower(filepath.Ext(path))
	var meta Metadata

	switch ext {
	case ".nupkg":
		ident, nm, ver, dev, extra := extract.NupkgMetadata(path)
		// Use 'extra' as needed or assign it to a relevant field
		meta.Title = ident
		meta.ID = nm
		meta.Version = ver
		meta.Developer = dev
		meta.Description = extra
		meta.InstallerType = "nupkg"

	case ".msi":
		name, ver, dev, desc, _, _ := extract.MsiMetadata(path)
		meta.Title = name
		meta.ID = name
		meta.Version = ver
		meta.Developer = dev
		meta.Description = desc
		meta.InstallerType = "msi"

	case ".exe":
		versionString, err := extract.ExeMetadata(path)
		// Fallback: just use the filename for title/ID
		meta.Title = parsePackageName(filepath.Base(path))
		meta.ID = meta.Title
		meta.Version = versionString
		if err != nil {
			// If ExeMetadata fails, set to empty or “unknown”
			meta.Version = ""
		}
		meta.InstallerType = "exe"

	default:
		// .bat, .ps1, .sh, etc. => "unknown"
		meta.InstallerType = "unknown"
		meta.Title = parsePackageName(filepath.Base(path))
		meta.ID = meta.Title
		meta.Version = "1.0.0"
	}

	// If no arch was determined, use the default (e.g. "x64,arm64")
	if defaultArch == "" {
		defaultArch = "x64,arm64"
	}
	if meta.Architecture == "" {
		meta.Architecture = defaultArch
	}

	// Parse architectures from defaultArch (could be comma-separated like "x64,arm64")
	if strings.Contains(meta.Architecture, ",") {
		// Split comma-separated architectures
		parts := strings.Split(meta.Architecture, ",")
		var archList []string
		for _, part := range parts {
			arch := strings.ToLower(strings.TrimSpace(part))
			if arch != "" {
				archList = append(archList, arch)
			}
		}
		meta.SupportedArch = archList
		if len(archList) > 0 {
			meta.Architecture = archList[0] // Set primary arch to first one
		}
	} else {
		meta.SupportedArch = []string{meta.Architecture}
	}

	return meta, nil
}

func parsePackageName(filename string) string {
	return strings.TrimSuffix(filename, filepath.Ext(filename))
}

func NormalizeInstallerLocation(location string) string {
	// Keep consistent with cimianimport's normalizeInstallerLocation
	normalized := strings.ReplaceAll(location, `/`, `\`)
	normalized = strings.TrimPrefix(normalized, `\`)
	return `\` + normalized
}
