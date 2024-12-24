//go:build windows
// +build windows

package status

import (
	"github.com/windowsadmins/gorilla/pkg/logging"
	registry "golang.org/x/sys/windows/registry"
)

// checkValues returns true if the registry subkey contains the critical values
// needed (DisplayName, DisplayVersion, UninstallString) for identifying software.
func checkValues(values []string) (valuesExist bool) {
	var nameExists bool
	var versionExists bool
	var uninstallExists bool

	for _, value := range values {
		if value == "DisplayName" {
			nameExists = true
		}
		if value == "DisplayVersion" {
			versionExists = true
		}
		if value == "UninstallString" {
			uninstallExists = true
		}
	}

	return nameExists && versionExists && uninstallExists
}

// getUninstallKeys iterates through known registry paths to gather installed applications.
// This function returns a map of application display names to their RegistryApplication info.
func getUninstallKeys() (map[string]RegistryApplication, error) {
	installedApps := make(map[string]RegistryApplication)
	regPaths := []string{
		`Software\Microsoft\Windows\CurrentVersion\Uninstall`,
		`Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall`,
	}

	for _, regPath := range regPaths {
		key, err := registry.OpenKey(registry.LOCAL_MACHINE, regPath, registry.READ)
		if err != nil {
			logging.Warn("Unable to read registry key:", err)
			continue
		}
		defer key.Close()

		subKeys, err := key.ReadSubKeyNames(0)
		if err != nil {
			logging.Warn("Unable to read registry sub keys:", err)
			continue
		}

		for _, subKey := range subKeys {
			itemKeyName := regPath + `\` + subKey
			itemKey, err := registry.OpenKey(registry.LOCAL_MACHINE, itemKeyName, registry.READ)
			if err != nil {
				logging.Warn("Unable to read registry key:", err)
				continue
			}
			defer itemKey.Close()

			itemValues, err := itemKey.ReadValueNames(0)
			if err != nil {
				logging.Warn("Unable to read registry values:", err)
				continue
			}

			if checkValues(itemValues) {
				var app RegistryApplication
				app.Key = itemKeyName

				app.Name, _, err = itemKey.GetStringValue("DisplayName")
				if err != nil {
					continue
				}
				app.Version, _, err = itemKey.GetStringValue("DisplayVersion")
				if err != nil {
					continue
				}
				app.Uninstall, _, err = itemKey.GetStringValue("UninstallString")
				if err != nil {
					continue
				}

				installedApps[app.Name] = app
			}
		}
	}

	return installedApps, nil
}

