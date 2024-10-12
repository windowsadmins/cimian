package pkginfo

import (
	"encoding/json"
	"io/ioutil"
	"os"
	"path/filepath"

	"github.com/rodchristiansen/gorilla/pkg/logging"
)

// PkgInfo represents the structure of a pkginfo file
type PkgInfo struct {
	Name                  string   `json:"name"`
	Version               string   `json:"version"`
	Catalogs              []string `json:"catalogs"`
	InstallerItemLocation string   `json:"installer_item_location"`
	InstallerType         string   `json:"installer_type"`
	MinimumOSVersion      string   `json:"minimum_os_version"`
	UninstallMethod       string   `json:"uninstall_method,omitempty"`
	UninstallScript       string   `json:"uninstall_script,omitempty"`
	InstallCheckScript    string   `json:"install_check_script,omitempty"`
	PostInstallScript     string   `json:"postinstall_script,omitempty"`
	PreInstallScript      string   `json:"preinstall_script,omitempty"`
	RestartAction         string   `json:"restart_action,omitempty"`
	SuppressBundle        bool     `json:"suppress_bundle_relocation,omitempty"`
	UnattendedInstall     bool     `json:"unattended_install,omitempty"`
	UnattendedUninstall   bool     `json:"unattended_uninstall,omitempty"`
	AdditionalPackages    []string `json:"additional_packages,omitempty"`
}

// Load reads all pkginfo files from the specified directory
func Load(pkgInfoPath string) map[string]PkgInfo {
	pkgInfos := make(map[string]PkgInfo)

	err := filepath.Walk(pkgInfoPath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if filepath.Ext(path) == ".json" {
			pkgInfo, err := loadPkgInfo(path)
			if err != nil {
				logging.Warn("Error loading pkginfo file:", path, err)
				return nil
			}
			pkgInfos[pkgInfo.Name] = pkgInfo
		}
		return nil
	})

	if err != nil {
		logging.Warn("Error walking pkginfo directory:", err)
	}

	return pkgInfos
}

func loadPkgInfo(path string) (PkgInfo, error) {
	var pkgInfo PkgInfo

	data, err := ioutil.ReadFile(path)
	if err != nil {
		return pkgInfo, err
	}

	err = json.Unmarshal(data, &pkgInfo)
	if err != nil {
		return pkgInfo, err
	}

	return pkgInfo, nil
}
