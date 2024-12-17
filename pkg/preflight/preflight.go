// pkg/preflight/preflight.go

package preflight

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

// RunPreflight runs the preflight script if it exists.
func RunPreflight(verbosity int, logInfo func(string, ...interface{}), logError func(string, ...interface{})) error {
	scriptPath := `C:\Program Files\Gorilla\preflight.ps1`
	displayName := "preflight"

	// Log the script path
	logInfo("Preflight script path", "path", scriptPath)

	// Check if script exists
	if _, err := os.Stat(scriptPath); os.IsNotExist(err) {
		logInfo("Preflight script not found", "path", scriptPath)
		return nil
	}

	// Build the command
	cmd := exec.Command("powershell.exe", "-ExecutionPolicy", "Bypass", "-File", scriptPath)
	cmd.Dir = filepath.Dir(scriptPath)

	// Log the full command and working directory
	logInfo("Preflight command", "command", strings.Join(cmd.Args, " "))
	logInfo("Preflight working directory", "directory", cmd.Dir)

	// Execute the command
	output, err := cmd.CombinedOutput()
	outputStr := string(output)

	// Log the raw output
	logInfo("Preflight raw output", "output", outputStr)

	if err != nil {
		logError("Preflight script error", "error", err)
		return fmt.Errorf("preflight script error: %w", err)
	}

	logInfo("Preflight script completed successfully", "script", displayName)
	return nil
}
