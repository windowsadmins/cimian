
package pkginfo

import (
	"github.com/rodchristiansen/gorilla/pkg/rollback"
    "encoding/json"
    "os"
    "log"
    "fmt"
)

const (
    InstallInfoPath = `C:\ProgramData\ManagedInstalls\InstallInfo.yaml`
)

// PkgInfo represents the metadata for a package, including dependencies
type PkgInfo struct {
    Name         string   `json:"name"`
    Version      string   `json:"version"`
    Dependencies []string `json:"dependencies"`
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
