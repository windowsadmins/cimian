
package pkginfo

import (
    "encoding/json"
    "os"
    "log"
    "fmt"
    "golang.org/x/sys/windows/registry"
	"github.com/windowsadmins/gorilla/pkg/rollback"
)

const (
    InstallInfoPath = `C:\ProgramData\ManagedInstalls\InstallInfo.yaml`
)

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
