// pkg/blocking/blocking.go - application blocking functionality similar to Munki's blocking applications

package blocking

import (
	"path/filepath"
	"strings"

	"github.com/shirou/gopsutil/v3/process"
	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/logging"
)

// IsAppRunning checks if a specific application is currently running
// This mimics Munki's is_app_running function for Windows
func IsAppRunning(appName string) bool {
	logging.Debug("Checking if application is running", "app", appName)

	processes, err := process.Processes()
	if err != nil {
		logging.Error("Failed to get process list", "error", err)
		return false
	}

	// Clean up the app name for comparison
	cleanAppName := strings.ToLower(appName)

	for _, proc := range processes {
		name, err := proc.Name()
		if err != nil {
			continue
		}

		// Convert to lowercase for case-insensitive comparison
		processName := strings.ToLower(name)

		// Handle different matching patterns like Munki does:
		if strings.HasPrefix(cleanAppName, "/") || strings.HasPrefix(cleanAppName, "c:\\") {
			// Search by exact path - get the exe path and compare
			exe, err := proc.Exe()
			if err != nil {
				continue
			}
			if strings.EqualFold(exe, appName) {
				logging.Debug("Found running app by exact path", "app", appName, "process", exe)
				return true
			}
		} else if strings.HasSuffix(cleanAppName, ".exe") {
			// Search by executable name
			if processName == cleanAppName {
				logging.Debug("Found running app by exe name", "app", appName, "process", processName)
				return true
			}
		} else {
			// Check if process name matches the appname (with or without .exe)
			if processName == cleanAppName || processName == cleanAppName+".exe" {
				logging.Debug("Found running app by name", "app", appName, "process", processName)
				return true
			}
		}
	}

	logging.Debug("Application not found running", "app", appName)
	return false
}

// BlockingApplicationsRunning checks if any blocking applications for a catalog item are running
// This is the main function that mimics Munki's blocking_applications_running function
func BlockingApplicationsRunning(item catalog.Item) bool {
	var appNames []string

	// First check if blocking_applications is explicitly specified
	if len(item.BlockingApps) > 0 {
		appNames = item.BlockingApps
		logging.Debug("Using explicit blocking_applications", "item", item.Name, "blocking_apps", appNames)
	} else {
		// If no blocking_applications specified, get app names from 'installs' list
		// This mirrors Munki's fallback behavior
		for _, installItem := range item.Installs {
			if installItem.Type == "application" && installItem.Path != "" {
				// Extract just the executable name from the path
				appName := filepath.Base(installItem.Path)
				if appName != "" {
					appNames = append(appNames, appName)
				}
			}
		}
		logging.Debug("Using installs list for blocking apps", "item", item.Name, "blocking_apps", appNames)
	}

	if len(appNames) == 0 {
		logging.Debug("No blocking applications to check", "item", item.Name)
		return false
	}

	logging.Debug("Checking blocking applications", "item", item.Name, "apps", appNames)

	// Check each potential blocking application
	var runningApps []string
	for _, appName := range appNames {
		if IsAppRunning(appName) {
			runningApps = append(runningApps, appName)
		}
	}

	if len(runningApps) > 0 {
		logging.Info("Blocking applications are running", "item", item.Name, "running_apps", runningApps)
		return true
	}

	logging.Debug("No blocking applications running", "item", item.Name)
	return false
}

// GetRunningBlockingApps returns a list of blocking applications that are currently running
// This is useful for logging and user feedback
func GetRunningBlockingApps(item catalog.Item) []string {
	var appNames []string
	var runningApps []string

	// Get the list of potential blocking applications (same logic as BlockingApplicationsRunning)
	if len(item.BlockingApps) > 0 {
		appNames = item.BlockingApps
	} else {
		for _, installItem := range item.Installs {
			if installItem.Type == "application" && installItem.Path != "" {
				appName := filepath.Base(installItem.Path)
				if appName != "" {
					appNames = append(appNames, appName)
				}
			}
		}
	}

	// Check which ones are actually running
	for _, appName := range appNames {
		if IsAppRunning(appName) {
			runningApps = append(runningApps, appName)
		}
	}

	return runningApps
}
