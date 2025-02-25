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
	scriptPath := `C:\Program Files\Cimian\preflight.ps1`
	displayName := "preflight"

	// Check if script exists
	if _, err := os.Stat(scriptPath); os.IsNotExist(err) {
		logInfo("Preflight script not found", "path", scriptPath)
		return nil
	}

	// Use pwsh.exe for PowerShell 7.5.
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

	// Split and log each output line directly
	lines := strings.Split(outputStr, "\n")
	for _, line := range lines {
		txt := strings.TrimSpace(line)
		if txt == "" {
			continue
		}
		// Optionally remove any BOM or ANSI sequences
		txt = strings.TrimPrefix(txt, "\ufeff")
		txt = strings.ReplaceAll(txt, "\u001b[0m", "")
		txt = strings.ReplaceAll(txt, "\u001b[", "")
		logInfo(txt)
	}

	if err != nil {
		logError("Preflight script error", "error", err)
		return fmt.Errorf("preflight script error: %w", err)
	}

	logInfo("Preflight script completed successfully", "script", displayName)
	return nil
}
