package scripts

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
)

// runScript executes the PowerShell script at the provided path,
// passing -Verbosity <int> and setting TERM=xterm-256color so that ANSI
// escape codes from the script output are preserved.
// Debug messages about the PowerShell executable and its version are printed
// only if the provided verbosity is 3 or greater.
//
// IMPORTANT: Always uses Windows PowerShell (powershell.exe) instead of PowerShell Core (pwsh.exe)
// because preflight.ps1 depends on legacy PackageManagement and PowerShellGet modules that don't
// work properly in PowerShell Core.
//
// FUNCTION IS TEMPORARILY set to NEVER return an error, even if the script fails. Preflight/postflight -- does not match Munki's behavior, which fails the entire run if pre/postflight fails. Future versions will change this.
// failures must never block the main managedsoftwareupdate run. Script failures are logged but
// the function always returns nil to ensure the main workflow continues.
func runScript(
	scriptPath string,
	displayName string,
	verbosity int,
	logInfo func(string, ...interface{}),
	logError func(string, ...interface{}),
) error {
	// 1. Use Windows PowerShell 5.1 executable directly (not pwsh.exe).
	// This prevents issues with the preflight script's re-invocation logic and legacy module dependencies.
	// Always use the full path to avoid any PATH issues with PowerShell Core.
	psExe := filepath.Join(os.Getenv("SystemRoot"), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
	
	// Fallback to PATH lookup if the direct path doesn't exist (edge case)
	if _, err := os.Stat(psExe); os.IsNotExist(err) {
		var lookupErr error
		psExe, lookupErr = exec.LookPath("powershell.exe")
		if lookupErr != nil {
			return fmt.Errorf("powershell.exe not found (Windows PowerShell 5.1 required): %v", lookupErr)
		}
	}
	
	if verbosity >= 3 {
		logInfo("Using Windows PowerShell 5.1 (%s) for %s", psExe, displayName)
	}

	// 2. Build the command to run the script.
	cmd := exec.Command(
		psExe,
		"-NoLogo",
		"-NoProfile",
		"-NonInteractive",
		"-ExecutionPolicy", "Bypass",
		"-File", scriptPath,
		"-Verbosity", strconv.Itoa(verbosity),
	)
	cmd.Dir = filepath.Dir(scriptPath)
	// Set TERM so that ANSI colors are preserved.
	cmd.Env = append(os.Environ(), "TERM=xterm-256color")
	
	// Connect stdin, stdout, and stderr for real-time output
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	cmd.Stdin = nil

	// 3. Run the command and wait for completion.
	// IMPORTANT: Never fail the main run if preflight/postflight fails
	execErr := cmd.Run()
	if execErr != nil {
		logError("%s script error: %v (continuing anyway)", displayName, execErr)
		// Log the error but DO NOT return it - must continue with main run
		if verbosity >= 3 {
			logInfo("%s script failed but continuing with managed software update", displayName)
		}
		return nil  // Always return nil to continue the main run
	}

	if verbosity >= 3 {
		logInfo("%s script completed successfully", displayName)
	}
	return nil
}

// RunPreflight calls runScript for preflight.
func RunPreflight(verbosity int, logInfo func(string, ...interface{}), logError func(string, ...interface{})) error {
	// Use ProgramW6432 environment variable to force 64-bit Program Files path
	programFiles := os.Getenv("ProgramW6432")
	if programFiles == "" {
		programFiles = `C:\Program Files`
	}
	scriptPath := filepath.Join(programFiles, "Cimian", "preflight.ps1")

	// Check if the script exists before trying to run it
	if _, err := os.Stat(scriptPath); os.IsNotExist(err) {
		if verbosity >= 3 {
			logInfo("Preflight script not found at %s, skipping", scriptPath)
		}
		return nil // Not an error - script is optional
	}

	return runScript(scriptPath, "Preflight", verbosity, logInfo, logError)
}

// RunPostflight calls runScript for postflight.
func RunPostflight(verbosity int, logInfo func(string, ...interface{}), logError func(string, ...interface{})) error {
	// Use ProgramW6432 environment variable to force 64-bit Program Files path
	programFiles := os.Getenv("ProgramW6432")
	if programFiles == "" {
		programFiles = `C:\Program Files`
	}
	scriptPath := filepath.Join(programFiles, "Cimian", "postflight.ps1")

	// Check if the script exists before trying to run it
	if _, err := os.Stat(scriptPath); os.IsNotExist(err) {
		if verbosity >= 3 {
			logInfo("Postflight script not found at %s, skipping", scriptPath)
		}
		return nil // Not an error - script is optional
	}

	return runScript(scriptPath, "Postflight", verbosity, logInfo, logError)
}
