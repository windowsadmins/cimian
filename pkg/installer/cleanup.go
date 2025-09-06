package installer

import (
	"context"
	"fmt"
	"os/exec"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
)

// ProcessCleanup provides simple process cleanup functionality for installers
type ProcessCleanup struct {
	cfg   *config.Configuration
	mutex sync.RWMutex
}

// NewProcessCleanup creates a new process cleanup instance
func NewProcessCleanup(cfg *config.Configuration) *ProcessCleanup {
	return &ProcessCleanup{
		cfg: cfg,
	}
}

// CleanupOrphanedMSIProcesses finds and terminates orphaned msiexec.exe processes
func (pc *ProcessCleanup) CleanupOrphanedMSIProcesses() error {
	logging.Debug("Checking for orphaned msiexec.exe processes")

	// Get all msiexec.exe processes with creation time
	cmd := exec.Command("wmic", "process", "where", "name='msiexec.exe'", "get", "ProcessId,CreationDate,CommandLine", "/format:csv")
	output, err := cmd.Output()
	if err != nil {
		logging.Debug("Failed to query msiexec processes (may not exist)", "error", err)
		return nil
	}

	lines := strings.Split(string(output), "\n")
	var oldProcesses []string

	for _, line := range lines {
		if strings.Contains(line, "msiexec.exe") {
			fields := strings.Split(line, ",")
			if len(fields) >= 4 {
				processID := strings.TrimSpace(fields[3])
				creationDate := strings.TrimSpace(fields[2])
				
				// Parse creation date and check if process is older than 30 minutes
				if pc.isProcessOld(creationDate, 30*time.Minute) {
					oldProcesses = append(oldProcesses, processID)
				}
			}
		}
	}

	if len(oldProcesses) > 0 {
		logging.Warn("Found potentially orphaned msiexec.exe processes", "count", len(oldProcesses))
		
		for _, pid := range oldProcesses {
			logging.Info("Terminating orphaned msiexec.exe process", "pid", pid)
			killCmd := exec.Command("taskkill", "/F", "/PID", pid)
			if err := killCmd.Run(); err != nil {
				logging.Warn("Failed to kill msiexec process", "pid", pid, "error", err)
			}
		}
	}

	return nil
}

// isProcessOld checks if a WMI creation date indicates the process is older than the specified duration
func (pc *ProcessCleanup) isProcessOld(wmiDate string, maxAge time.Duration) bool {
	// WMI date format: 20240905162345.123456-480
	if len(wmiDate) < 14 {
		return false
	}

	// Parse the date portion (YYYYMMDDHHMMSS)
	dateStr := wmiDate[:14]
	t, err := time.Parse("20060102150405", dateStr)
	if err != nil {
		logging.Debug("Failed to parse WMI date", "date", wmiDate, "error", err)
		return false
	}

	return time.Since(t) > maxAge
}

// WaitForMSIAvailable waits for the Windows Installer service to become available
func (pc *ProcessCleanup) WaitForMSIAvailable(maxWaitMinutes int) error {
	logging.Debug("Waiting for MSI service to become available", "maxWaitMinutes", maxWaitMinutes)
	
	timeout := time.Now().Add(time.Duration(maxWaitMinutes) * time.Minute)
	
	for time.Now().Before(timeout) {
		busy, err := pc.checkMSIMutex()
		if err != nil {
			logging.Debug("Error checking MSI mutex", "error", err)
		}
		
		if !busy {
			logging.Debug("MSI service is available")
			return nil
		}
		
		logging.Debug("MSI service is busy, waiting...")
		time.Sleep(10 * time.Second)
	}
	
	return fmt.Errorf("MSI service did not become available within %d minutes", maxWaitMinutes)
}

// checkMSIMutex checks if the Windows Installer service mutex is locked
func (pc *ProcessCleanup) checkMSIMutex() (bool, error) {
	// Try to run a quick MSI operation to check if installer is busy
	cmd := exec.Command(commandMsi, "/help")
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	
	done := make(chan error, 1)
	go func() {
		done <- cmd.Run()
	}()

	select {
	case err := <-done:
		return err != nil, err
	case <-time.After(5 * time.Second):
		// If it takes more than 5 seconds to show help, installer is probably locked
		if cmd.Process != nil {
			cmd.Process.Kill()
		}
		return true, fmt.Errorf("MSI service appears to be locked")
	}
}

// RunMSIWithCleanup runs an MSI installer with proper cleanup and timeout handling
func (pc *ProcessCleanup) RunMSIWithCleanup(msiPath string, arguments []string, timeoutMinutes int) (string, error) {
	logging.Info("Running MSI with cleanup and timeout", 
		"msi", msiPath, "args", arguments, "timeoutMinutes", timeoutMinutes)

	// Pre-install cleanup
	if err := pc.CleanupOrphanedMSIProcesses(); err != nil {
		logging.Warn("Pre-install cleanup had issues", "error", err)
	}

	// Wait for MSI service to be available
	if err := pc.WaitForMSIAvailable(2); err != nil {
		logging.Warn("MSI service availability check failed", "error", err)
	}

	// Create context with timeout
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(timeoutMinutes)*time.Minute)
	defer cancel()

	// Build full command arguments
	fullArgs := append([]string{"/i", msiPath}, arguments...)

	cmd := exec.CommandContext(ctx, commandMsi, fullArgs...)
	
	// Set up process attributes for proper cleanup
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: syscall.CREATE_NEW_PROCESS_GROUP,
	}

	// Capture output
	output, err := cmd.CombinedOutput()
	outputStr := string(output)

	if err != nil {
		// Check if it was a timeout
		if ctx.Err() == context.DeadlineExceeded {
			logging.Error("MSI installation timed out", "msi", msiPath, "timeoutMinutes", timeoutMinutes)
			
			// Try to forcefully terminate the process and any child processes
			if cmd.Process != nil {
				logging.Debug("Terminating timed-out MSI process", "pid", cmd.Process.Pid)
				pc.terminateProcessTree(cmd.Process.Pid)
			}
			
			return outputStr, fmt.Errorf("MSI installer timed out after %d minutes", timeoutMinutes)
		}

		if exitErr, ok := err.(*exec.ExitError); ok {
			exitCode := exitErr.ExitCode()
			logging.Error("MSI installation failed", "msi", msiPath, "exitCode", exitCode, "output", outputStr)
			return outputStr, fmt.Errorf("MSI installation failed with exit code %d: %s", exitCode, outputStr)
		}

		logging.Error("Failed to run MSI installation", "msi", msiPath, "error", err, "output", outputStr)
		return outputStr, err
	}

	logging.Debug("MSI installation completed successfully", "msi", msiPath, "output", outputStr)
	
	// Post-install cleanup
	if err := pc.CleanupOrphanedMSIProcesses(); err != nil {
		logging.Warn("Post-install cleanup had issues", "error", err)
	}

	return outputStr, nil
}

// terminateProcessTree terminates a process and all its child processes
func (pc *ProcessCleanup) terminateProcessTree(pid int) error {
	logging.Debug("Terminating process tree", "rootPid", pid)

	// Use taskkill to terminate the process tree
	cmd := exec.Command("taskkill", "/F", "/T", "/PID", fmt.Sprintf("%d", pid))
	if err := cmd.Run(); err != nil {
		logging.Debug("Failed to terminate process tree", "pid", pid, "error", err)
		return err
	}

	logging.Debug("Process tree terminated successfully", "rootPid", pid)
	return nil
}

// CleanupOrphanedProcesses finds and terminates orphaned processes by name
func (pc *ProcessCleanup) CleanupOrphanedProcesses(processName string) error {
	logging.Debug("Checking for orphaned processes", "processName", processName)

	// Get all processes with the specified name
	cmd := exec.Command("wmic", "process", "where", fmt.Sprintf("name='%s'", processName), "get", "ProcessId,CreationDate", "/format:csv")
	output, err := cmd.Output()
	if err != nil {
		logging.Debug("Failed to query processes (may not exist)", "processName", processName, "error", err)
		return nil
	}

	lines := strings.Split(string(output), "\n")
	var oldProcesses []string

	for _, line := range lines {
		if strings.Contains(line, processName) {
			fields := strings.Split(line, ",")
			if len(fields) >= 3 {
				processID := strings.TrimSpace(fields[2])
				creationDate := strings.TrimSpace(fields[1])
				
				// Check if process is older than 30 minutes
				if pc.isProcessOld(creationDate, 30*time.Minute) {
					oldProcesses = append(oldProcesses, processID)
				}
			}
		}
	}

	if len(oldProcesses) > 0 {
		logging.Warn("Found potentially orphaned processes", "processName", processName, "count", len(oldProcesses))
		
		for _, pid := range oldProcesses {
			logging.Info("Terminating orphaned process", "processName", processName, "pid", pid)
			killCmd := exec.Command("taskkill", "/F", "/PID", pid)
			if err := killCmd.Run(); err != nil {
				logging.Warn("Failed to kill process", "processName", processName, "pid", pid, "error", err)
			}
		}
	}

	return nil
}

// RunCommandWithCleanup runs any command with timeout and cleanup
func (pc *ProcessCleanup) RunCommandWithCleanup(command string, args []string, timeoutMinutes int) (string, error) {
	logging.Debug("Running command with cleanup", "command", command, "args", args, "timeoutMinutes", timeoutMinutes)

	// Create context with timeout
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(timeoutMinutes)*time.Minute)
	defer cancel()

	cmd := exec.CommandContext(ctx, command, args...)
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: syscall.CREATE_NEW_PROCESS_GROUP,
	}

	output, err := cmd.CombinedOutput()
	outputStr := string(output)

	if err != nil {
		if ctx.Err() == context.DeadlineExceeded {
			logging.Error("Command timed out", "command", command, "timeoutMinutes", timeoutMinutes)
			
			if cmd.Process != nil {
				pc.terminateProcessTree(cmd.Process.Pid)
			}
			
			return outputStr, fmt.Errorf("command timed out after %d minutes", timeoutMinutes)
		}

		if exitErr, ok := err.(*exec.ExitError); ok {
			exitCode := exitErr.ExitCode()
			return outputStr, fmt.Errorf("command failed with exit code %d: %s", exitCode, outputStr)
		}

		return outputStr, err
	}

	return outputStr, nil
}

// EmergencyCleanup performs an emergency cleanup of all installer-related processes
func (pc *ProcessCleanup) EmergencyCleanup() error {
	logging.Warn("Performing emergency installer cleanup")

	var errors []error

	// Kill all msiexec processes
	if err := pc.CleanupOrphanedProcesses("msiexec.exe"); err != nil {
		errors = append(errors, fmt.Errorf("failed to cleanup msiexec: %w", err))
	}

	// Kill any hanging PowerShell processes that might be running installers
	if err := pc.CleanupOrphanedProcesses("powershell.exe"); err != nil {
		errors = append(errors, fmt.Errorf("failed to cleanup powershell: %w", err))
	}

	if len(errors) > 0 {
		return fmt.Errorf("emergency cleanup completed with errors: %v", errors)
	}

	logging.Info("Emergency cleanup completed successfully")
	return nil
}

// Global cleanup instance
var globalCleanup *ProcessCleanup
var cleanupOnce sync.Once

// GetGlobalCleanup returns the global cleanup instance
func GetGlobalCleanup(cfg *config.Configuration) *ProcessCleanup {
	cleanupOnce.Do(func() {
		globalCleanup = NewProcessCleanup(cfg)
	})
	return globalCleanup
}

// Convenience functions for backward compatibility

// PreInstallMSICleanupV2 performs pre-install MSI cleanup
func PreInstallMSICleanupV2(cfg *config.Configuration) error {
	cleanup := GetGlobalCleanup(cfg)
	return cleanup.CleanupOrphanedMSIProcesses()
}

// WaitForMSIAvailableV2 waits for MSI service to become available
func WaitForMSIAvailableV2(maxWaitMinutes int, cfg *config.Configuration) error {
	cleanup := GetGlobalCleanup(cfg)
	return cleanup.WaitForMSIAvailable(maxWaitMinutes)
}

// runMSIDirectlyV2 runs MSI with cleanup and timeout handling
func runMSIDirectlyV2(msiPath string, arguments []string, timeoutMinutes int, cfg *config.Configuration) (string, error) {
	cleanup := GetGlobalCleanup(cfg)
	return cleanup.RunMSIWithCleanup(msiPath, arguments, timeoutMinutes)
}
