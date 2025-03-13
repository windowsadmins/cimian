package scripts

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
)

// runScript executes the PowerShell script at the provided path,
// passing -Verbosity <int> and setting TERM=xterm-256color so that ANSI
// escape codes from the script output are preserved.
// Debug messages about the PowerShell executable and its version are printed
// only if the provided verbosity is 3 or greater.
func runScript(
	scriptPath string,
	displayName string,
	verbosity int,
	logInfo func(string, ...interface{}),
	logError func(string, ...interface{}),
) error {
	// 1. Find the PowerShell executable.
	psExe, err := exec.LookPath("pwsh.exe")
	if err != nil {
		if verbosity >= 3 {
			logInfo("pwsh.exe not found; falling back to Windows PowerShell (v5)")
		}
		psExe, err = exec.LookPath("powershell.exe")
		if err != nil {
			return fmt.Errorf("neither pwsh.exe nor powershell.exe were found: %v", err)
		}
	} else {
		if verbosity >= 3 {
			logInfo("Using PowerShell Core (pwsh) for %s", displayName)
		}
	}

	// 2. Print the PowerShell version only if verbosity is high.
	if verbosity >= 3 {
		if versionBytes, verErr := exec.Command(psExe, "--version").CombinedOutput(); verErr == nil {
			logInfo("%s version: %s", psExe, strings.TrimSpace(string(versionBytes)))
		}
	}

	// 3. Build the command to run the script.
	cmd := exec.Command(
		psExe,
		"-NoLogo",
		"-NoProfile",
		"-ExecutionPolicy", "Bypass",
		"-File", scriptPath,
		"-Verbosity", strconv.Itoa(verbosity),
	)
	cmd.Dir = filepath.Dir(scriptPath)
	// Set TERM so that ANSI colors are preserved.
	cmd.Env = append(cmd.Env, "TERM=xterm-256color")
	cmd.Env = append(cmd.Env, os.Environ()...)

	// 4. Run the command and capture its output.
	outputBytes, execErr := cmd.CombinedOutput()
	// Print the raw output so any ANSI codes are passed through.
	fmt.Print(string(outputBytes))
	if execErr != nil {
		logError("%s script error: %v", displayName, execErr)
		return fmt.Errorf("%s script error: %w", displayName, execErr)
	}

	if verbosity >= 3 {
		logInfo("%s script completed successfully", displayName)
	}
	return nil
}

// RunPreflight calls runScript for preflight.
func RunPreflight(verbosity int, logInfo func(string, ...interface{}), logError func(string, ...interface{})) error {
	scriptPath := `C:\Program Files\Cimian\preflight.ps1`
	return runScript(scriptPath, "Preflight", verbosity, logInfo, logError)
}

// RunPostflight calls runScript for postflight.
func RunPostflight(verbosity int, logInfo func(string, ...interface{}), logError func(string, ...interface{})) error {
	scriptPath := `C:\Program Files\Cimian\postflight.ps1`
	return runScript(scriptPath, "Postflight", verbosity, logInfo, logError)
}
