// pkg/preflight/preflight.go

package preflight

import (
    "fmt"
    "os"
    "os/exec"
    "path/filepath"
)

// RunPreflight runs the preflight script if it exists.
func RunPreflight(verbosity int, logInfo func(string, ...interface{}), logError func(string, ...interface{})) error {
    scriptPath := `C:\Program Files\Gorilla\preflight.ps1`

    // Check if the script exists
    if _, err := os.Stat(scriptPath); os.IsNotExist(err) {
        // Script does not exist; nothing to do
        return nil
    }

    displayName := "preflight"
    runType := "checkandinstall"

    logInfo("Performing %s tasks...", displayName)

    // Optionally, verify script permissions here

    // Prepare the command to run the script
    cmd := exec.Command("powershell.exe", "-ExecutionPolicy", "Bypass", "-File", scriptPath, runType)
    cmd.Dir = filepath.Dir(scriptPath)

    // Capture the output
    output, err := cmd.CombinedOutput()
    if err != nil {
        logError("%s returned error: %v", displayName, err)
        logError("%s output: %s", displayName, string(output))
        return err
    }

    // Log the output
    if verbosity >= 1 {
        logInfo("%s output: %s", displayName, string(output))
    }

    return nil
}
