// pkg/scripts/prepost.go - Functions for running preflight and postflight scripts.

package scripts

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

// runScript is a helper that executes the PowerShell script at the provided path,
// logs each line via logInfo and logs errors via logError.
func runScript(scriptPath, displayName string, logInfo func(string, ...interface{}), logError func(string, ...interface{})) error {
	// Check if the script file exists.
	if _, err := os.Stat(scriptPath); os.IsNotExist(err) {
		logInfo("%s script not found", displayName, "path", scriptPath)
		return nil
	}

	// Use pwsh.exe for PowerShell (assuming PowerShell 7 is available).
	cmd := exec.Command(
		"pwsh.exe",
		"-NoLogo",
		"-NoProfile",
		"-NonInteractive",
		"-Command", fmt.Sprintf(`& "%s" 2>&1`, scriptPath),
	)
	cmd.Dir = filepath.Dir(scriptPath)

	outputBytes, err := cmd.CombinedOutput()
	outputStr := string(outputBytes)

	// Log each non-empty output line.
	lines := strings.Split(outputStr, "\n")
	for _, line := range lines {
		txt := strings.TrimSpace(line)
		if txt == "" {
			continue
		}
		// Optionally remove BOM or ANSI escape sequences.
		txt = strings.TrimPrefix(txt, "\ufeff")
		txt = strings.ReplaceAll(txt, "\u001b[0m", "")
		txt = strings.ReplaceAll(txt, "\u001b[", "")
		logInfo("%s", txt)
	}

	if err != nil {
		logError("%s script error: %v", displayName, err)
		return fmt.Errorf("%s script error: %w", displayName, err)
	}

	logInfo("%s script completed successfully", displayName)
	return nil
}

// RunPreflight runs the preflight script located at a predefined path.
func RunPreflight(logInfo func(string, ...interface{}), logError func(string, ...interface{})) error {
	scriptPath := `C:\Program Files\Cimian\preflight.ps1`
	return runScript(scriptPath, "Preflight", logInfo, logError)
}

// RunPostflight runs the postflight script located at a predefined path.
func RunPostflight(logInfo func(string, ...interface{}), logError func(string, ...interface{})) error {
	scriptPath := `C:\Program Files\Cimian\postflight.ps1`
	return runScript(scriptPath, "Postflight", logInfo, logError)
}
