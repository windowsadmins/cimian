package installer

import (
	"context"
	"fmt"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
)

// ProcessCleanup provides comprehensive process and service cleanup functionality for installers
type ProcessCleanup struct {
	cfg         *config.Configuration
	mutex       sync.RWMutex
	retryConfig *MSIRetryConfig
}

// MSIRetryConfig holds configuration for MSI retry logic
type MSIRetryConfig struct {
	MaxRetries              int
	BaseRetryDelaySeconds   int
	MaxRetryDelaySeconds    int
	ServiceRestartEnabled   bool
	ServiceRestartTimeoutMs int
	EmergencyCleanupEnabled bool
}

// MSIServiceStatus represents the current status of the MSI service
type MSIServiceStatus struct {
	IsRunning      bool
	IsResponsive   bool
	ProcessCount   int
	ServiceState   string
	CanRestart     bool
	LastCheckTime  time.Time
	CheckDuration  time.Duration
}

// NewProcessCleanup creates a new process cleanup instance
func NewProcessCleanup(cfg *config.Configuration) *ProcessCleanup {
	retryConfig := &MSIRetryConfig{
		MaxRetries:              5,
		BaseRetryDelaySeconds:   30,
		MaxRetryDelaySeconds:    300, // 5 minutes max
		ServiceRestartEnabled:   true,
		ServiceRestartTimeoutMs: 30000, // 30 seconds
		EmergencyCleanupEnabled: true,
	}
	
	return &ProcessCleanup{
		cfg:         cfg,
		retryConfig: retryConfig,
	}
}

// CleanupOrphanedMSIProcesses finds and terminates orphaned msiexec.exe processes using PowerShell fallback
func (pc *ProcessCleanup) CleanupOrphanedMSIProcesses() error {
	logging.Debug("Checking for orphaned msiexec.exe processes")

	// Try PowerShell first (more reliable on modern Windows)
	if processes, err := pc.getMSIProcessesPS(); err == nil {
		return pc.terminateOldProcesses("msiexec.exe", processes)
	}

	// Fallback to wmic if available
	cmd := exec.Command("wmic", "process", "where", "name='msiexec.exe'", "get", "ProcessId,CreationDate,CommandLine", "/format:csv")
	output, err := cmd.Output()
	if err != nil {
		logging.Debug("Failed to query msiexec processes with wmic, trying PowerShell fallback", "error", err)
		// Try PowerShell as final fallback
		if processes, psErr := pc.getMSIProcessesPS(); psErr == nil {
			return pc.terminateOldProcesses("msiexec.exe", processes)
		} else {
			logging.Debug("Both wmic and PowerShell process queries failed - continuing without process cleanup", "wmicError", err, "psError", psErr)
			return nil
		}
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

// CleanupAllMSIProcesses terminates all msiexec.exe processes (for final cleanup)
func (pc *ProcessCleanup) CleanupAllMSIProcesses() error {
	logging.Info("Performing final cleanup - terminating all msiexec processes")

	// Simple approach: Use taskkill to kill all msiexec processes (most reliable)
	cmd := exec.Command("taskkill", "/F", "/IM", "msiexec.exe")
	output, err := cmd.Output()
	if err != nil {
		// Check if error is just "process not found" (which is fine)
		if exitErr, ok := err.(*exec.ExitError); ok && exitErr.ExitCode() == 128 {
			logging.Debug("No msiexec processes found to clean up")
			return nil
		}
		logging.Debug("Failed to kill msiexec processes with taskkill", "error", err, "output", string(output))
	} else {
		logging.Info("Final cleanup completed - terminated msiexec processes", "output", strings.TrimSpace(string(output)))
	}

	// Also try PowerShell method as backup
	psCmd := `Get-Process -Name msiexec -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue`
	psCommand := exec.Command("powershell", "-Command", psCmd)
	psCommand.Run() // Ignore errors - just best effort cleanup

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

// checkMSIServiceStatus performs a comprehensive check of the MSI service status
func (pc *ProcessCleanup) checkMSIServiceStatus() (*MSIServiceStatus, error) {
	startTime := time.Now()
	status := &MSIServiceStatus{
		LastCheckTime: startTime,
	}
	
	// Check service state using Windows Service Control Manager
	serviceState, canRestart, err := pc.checkWindowsInstallerService()
	if err != nil {
		logging.Debug("Failed to check Windows Installer service", "error", err)
		status.ServiceState = "unknown"
	} else {
		status.ServiceState = serviceState
		status.IsRunning = (serviceState == "Running")
		status.CanRestart = canRestart
	}
	
	// Count active msiexec processes
	processCount, err := pc.countMSIExecProcesses()
	if err != nil {
		logging.Debug("Failed to count msiexec processes", "error", err)
	}
	status.ProcessCount = processCount
	
	// Test service responsiveness
	isResponsive, err := pc.testMSIServiceResponsiveness()
	if err != nil {
		logging.Debug("MSI service responsiveness test failed", "error", err)
	}
	status.IsResponsive = isResponsive
	
	status.CheckDuration = time.Since(startTime)
	
	logging.Debug("MSI service status check completed", 
		"isRunning", status.IsRunning,
		"isResponsive", status.IsResponsive,
		"processCount", status.ProcessCount,
		"serviceState", status.ServiceState,
		"canRestart", status.CanRestart,
		"checkDurationMs", status.CheckDuration.Milliseconds())
	
	return status, nil
}

// checkWindowsInstallerService checks the Windows Installer service status
func (pc *ProcessCleanup) checkWindowsInstallerService() (string, bool, error) {
	// Connect to the Service Control Manager
	m, err := mgr.Connect()
	if err != nil {
		return "", false, fmt.Errorf("failed to connect to service manager: %w", err)
	}
	defer m.Disconnect()
	
	// Open the Windows Installer service
	service, err := m.OpenService("msiserver")
	if err != nil {
		return "", false, fmt.Errorf("failed to open msiserver service: %w", err)
	}
	defer service.Close()
	
	// Query service status
	status, err := service.Query()
	if err != nil {
		return "", false, fmt.Errorf("failed to query service status: %w", err)
	}
	
	var stateString string
	var canRestart bool
	
	switch status.State {
	case svc.Stopped:
		stateString = "Stopped"
		canRestart = true
	case svc.StartPending:
		stateString = "StartPending"
		canRestart = false
	case svc.StopPending:
		stateString = "StopPending"
		canRestart = false
	case svc.Running:
		stateString = "Running"
		canRestart = true
	case svc.ContinuePending:
		stateString = "ContinuePending"
		canRestart = false
	case svc.PausePending:
		stateString = "PausePending"
		canRestart = false
	case svc.Paused:
		stateString = "Paused"
		canRestart = true
	default:
		stateString = fmt.Sprintf("Unknown(%d)", int(status.State))
		canRestart = false
	}
	
	return stateString, canRestart, nil
}

// countMSIExecProcesses counts the number of active msiexec processes
func (pc *ProcessCleanup) countMSIExecProcesses() (int, error) {
	cmd := exec.Command("wmic", "process", "where", "name='msiexec.exe'", "get", "ProcessId", "/format:csv")
	output, err := cmd.Output()
	if err != nil {
		// wmic returns error when no processes found, which is normal
		return 0, nil
	}
	
	lines := strings.Split(string(output), "\n")
	count := 0
	
	for _, line := range lines {
		if strings.Contains(line, "msiexec.exe") {
			count++
		}
	}
	
	return count, nil
}

// testMSIServiceResponsiveness tests if the MSI service responds quickly
func (pc *ProcessCleanup) testMSIServiceResponsiveness() (bool, error) {
	// Use a quick help command to test responsiveness (reduced timeout)
	cmd := exec.Command(commandMsi, "/?")
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	
	done := make(chan error, 1)
	go func() {
		done <- cmd.Run()
	}()
	
	select {
	case err := <-done:
		// If command completes within timeout, service is responsive
		return err == nil, nil
	case <-time.After(2 * time.Second): // REDUCED from 5s to 2s
		// If it takes more than 2 seconds, service is unresponsive
		if cmd.Process != nil {
			cmd.Process.Kill()
		}
		return false, fmt.Errorf("MSI service unresponsive - help command timed out after 2 seconds")
	}
}

// restartWindowsInstallerService attempts to restart the Windows Installer service
func (pc *ProcessCleanup) restartWindowsInstallerService() error {
	if !pc.retryConfig.ServiceRestartEnabled {
		return fmt.Errorf("service restart is disabled in configuration")
	}
	
	logging.Warn("Attempting to restart Windows Installer service")
	
	// Connect to Service Control Manager
	m, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("failed to connect to service manager: %w", err)
	}
	defer m.Disconnect()
	
	// Open the Windows Installer service
	service, err := m.OpenService("msiserver")
	if err != nil {
		return fmt.Errorf("failed to open msiserver service: %w", err)
	}
	defer service.Close()
	
	// Stop the service first
	logging.Debug("Stopping Windows Installer service")
	_, err = service.Control(svc.Stop)
	if err != nil {
		logging.Warn("Failed to stop Windows Installer service", "error", err)
		// Continue anyway - service might not be running
	}
	
	// Wait for service to stop
	timeout := time.Now().Add(time.Duration(pc.retryConfig.ServiceRestartTimeoutMs) * time.Millisecond)
	for time.Now().Before(timeout) {
		status, err := service.Query()
		if err != nil {
			logging.Debug("Error querying service during stop", "error", err)
			break
		}
		if status.State == svc.Stopped {
			logging.Debug("Windows Installer service stopped successfully")
			break
		}
		time.Sleep(1 * time.Second)
	}
	
	// Start the service
	logging.Debug("Starting Windows Installer service")
	err = service.Start()
	if err != nil {
		return fmt.Errorf("failed to start Windows Installer service: %w", err)
	}
	
	// Wait for service to start
	timeout = time.Now().Add(time.Duration(pc.retryConfig.ServiceRestartTimeoutMs) * time.Millisecond)
	for time.Now().Before(timeout) {
		status, err := service.Query()
		if err != nil {
			logging.Debug("Error querying service during start", "error", err)
			break
		}
		if status.State == svc.Running {
			logging.Info("Windows Installer service restarted successfully")
			return nil
		}
		time.Sleep(1 * time.Second)
	}
	
	return fmt.Errorf("Windows Installer service failed to start within timeout")
}

// performAdvancedMSICleanup performs comprehensive MSI cleanup including registry
func (pc *ProcessCleanup) performAdvancedMSICleanup() error {
	logging.Warn("Performing advanced MSI cleanup")
	
	var errors []error
	
	// 1. Kill all msiexec processes (existing)
	if err := pc.CleanupOrphanedProcesses("msiexec.exe"); err != nil {
		errors = append(errors, fmt.Errorf("failed to cleanup msiexec processes: %w", err))
	}
	
	// 2. Clear MSI pending operations registry keys
	if err := pc.clearMSIPendingOperations(); err != nil {
		errors = append(errors, fmt.Errorf("failed to clear MSI pending operations: %w", err))
		logging.Warn("Failed to clear MSI pending operations", "error", err)
	} else {
		logging.Debug("MSI pending operations cleared")
	}
	
	// 3. Clear MSI InProgress registry keys
	if err := pc.clearMSIInProgressKeys(); err != nil {
		errors = append(errors, fmt.Errorf("failed to clear MSI in-progress keys: %w", err))
		logging.Warn("Failed to clear MSI in-progress keys", "error", err)
	} else {
		logging.Debug("MSI in-progress keys cleared")
	}
	
	// 4. Kill any associated installer helper processes
	helperProcesses := []string{"setup.exe", "install.exe", "update.exe", "MSIExec.exe"}
	for _, process := range helperProcesses {
		if err := pc.CleanupOrphanedProcesses(process); err != nil {
			logging.Debug("Failed to cleanup helper process", "process", process, "error", err)
		}
	}
	
	if len(errors) > 0 {
		return fmt.Errorf("advanced MSI cleanup completed with %d errors: %v", len(errors), errors)
	}
	
	logging.Info("Advanced MSI cleanup completed successfully")
	return nil
}

// clearMSIPendingOperations clears MSI pending operations from registry
func (pc *ProcessCleanup) clearMSIPendingOperations() error {
	// Clear pending file operations that might block MSI
	regPaths := []string{
		`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations`,
		`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired`,
	}
	
	for _, path := range regPaths {
		cmd := exec.Command("reg", "delete", path, "/f")
		if err := cmd.Run(); err != nil {
			// Registry key might not exist, which is fine
			logging.Debug("Registry key not found or already cleared", "path", path)
		} else {
			logging.Debug("Cleared registry key", "path", path)
		}
	}
	
	return nil
}

// clearMSIInProgressKeys clears MSI in-progress registry keys
func (pc *ProcessCleanup) clearMSIInProgressKeys() error {
	// Clear MSI installer in-progress markers
	regPaths := []string{
		`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress`,
	}
	
	for _, path := range regPaths {
		cmd := exec.Command("reg", "delete", path, "/f")
		if err := cmd.Run(); err != nil {
			// Registry key might not exist, which is fine
			logging.Debug("MSI in-progress registry key not found or already cleared", "path", path)
		} else {
			logging.Debug("Cleared MSI in-progress registry key", "path", path)
		}
	}
	
	return nil
}

// WaitForMSIAvailable waits for the Windows Installer service to become available with intelligent retry
func (pc *ProcessCleanup) WaitForMSIAvailable(maxWaitMinutes int) error {
	// Prevent concurrent MSI recovery operations
	msiRecoveryMutex.Lock()
	defer msiRecoveryMutex.Unlock()
	
	logging.Debug("Starting intelligent MSI service availability check", "maxWaitMinutes", maxWaitMinutes)
	
	startTime := time.Now()
	timeout := time.Now().Add(time.Duration(maxWaitMinutes) * time.Minute)
	checkCount := 0
	retryAttempt := 0
	
	for time.Now().Before(timeout) {
		checkCount++
		logging.Debug("MSI availability check", "attempt", checkCount, "retryAttempt", retryAttempt)
		
		// Get comprehensive service status
		status, err := pc.checkMSIServiceStatus()
		if err != nil {
			logging.Warn("Failed to check MSI service status", "error", err, "checkCount", checkCount)
		} else {
			// Log detailed status for debugging
			logging.Debug("MSI service status details", 
				"isRunning", status.IsRunning,
				"isResponsive", status.IsResponsive,
				"processCount", status.ProcessCount,
				"serviceState", status.ServiceState,
				"checkDuration", status.CheckDuration.Milliseconds())
		}
		
		// Check if service is available and responsive
		if status != nil && status.IsRunning && status.IsResponsive {
			duration := time.Since(startTime)
			logging.Info("MSI service is available and responsive", 
				"totalChecks", checkCount, 
				"totalDuration", duration,
				"retryAttempts", retryAttempt)
			return nil
		}
		
		// Determine strategy based on current status and retry count
		if status != nil {
			strategy := pc.determineRecoveryStrategy(status, retryAttempt)
			logging.Debug("Recovery strategy determined", "strategy", strategy, "retryAttempt", retryAttempt)
			
			switch strategy {
			case "wait":
				// Just wait - service might be starting or busy with quick operation
				time.Sleep(5 * time.Second)
				
			case "cleanup":
				logging.Info("Attempting process cleanup to resolve MSI service issues")
				if err := pc.CleanupOrphanedMSIProcesses(); err != nil {
					logging.Warn("Process cleanup failed", "error", err)
				}
				retryAttempt++
				time.Sleep(10 * time.Second)
				
			case "advanced_cleanup":
				logging.Warn("Attempting advanced cleanup to resolve MSI service issues")
				if err := pc.performAdvancedMSICleanup(); err != nil {
					logging.Warn("Advanced cleanup failed", "error", err)
				}
				retryAttempt++
				time.Sleep(15 * time.Second)
				
			case "restart_service":
				logging.Warn("Attempting service restart to resolve MSI service issues")
				if err := pc.restartWindowsInstallerService(); err != nil {
					logging.Error("Service restart failed", "error", err)
				} else {
					logging.Info("Windows Installer service restart completed")
					// Wait for service to fully initialize after restart
					time.Sleep(10 * time.Second)
					// Check if restart actually fixed the issue
					if quickStatus, _ := pc.checkMSIServiceStatus(); quickStatus != nil && quickStatus.IsRunning && quickStatus.IsResponsive {
						logging.Info("MSI service recovered after restart")
						return nil
					}
				}
				retryAttempt++
				time.Sleep(10 * time.Second) // Additional wait for stability
				
			case "aggressive_cleanup":
				logging.Warn("Attempting aggressive cleanup - most comprehensive MSI recovery")
				if err := pc.performAggressiveCleanup(); err != nil {
					logging.Error("Nuclear cleanup failed", "error", err)
				} else {
					logging.Info("Aggressive cleanup completed - waiting for stabilization")
					// Wait longer for nuclear cleanup to take effect
					time.Sleep(15 * time.Second)
					// Check if nuclear cleanup fixed the issue
					if quickStatus, _ := pc.checkMSIServiceStatus(); quickStatus != nil && quickStatus.IsRunning && quickStatus.IsResponsive {
						logging.Info("MSI service recovered after nuclear cleanup")
						return nil
					}
				}
				retryAttempt++
				time.Sleep(15 * time.Second) // Extra wait after nuclear cleanup
				
			default:
				// Fallback to waiting
				time.Sleep(5 * time.Second)
			}
		} else {
			// If we can't determine status, just wait
			time.Sleep(5 * time.Second)
		}
		
		// Check if we should give up early due to too many retry attempts
		if retryAttempt >= pc.retryConfig.MaxRetries {
			remainingTime := timeout.Sub(time.Now())
			if remainingTime > time.Minute {
				logging.Warn("Maximum retry attempts reached, waiting remaining time without intervention", 
					"retryAttempt", retryAttempt, 
					"maxRetries", pc.retryConfig.MaxRetries,
					"remainingMinutes", remainingTime.Minutes())
				
				// Wait the remaining time doing only basic checks
				for time.Now().Before(timeout) {
					checkCount++
					if basicStatus, _ := pc.testMSIServiceResponsiveness(); basicStatus {
						logging.Info("MSI service became responsive during final wait phase", "totalChecks", checkCount)
						return nil
					}
					time.Sleep(10 * time.Second)
				}
				break
			}
		}
	}
	
	// Final status check for detailed error reporting
	finalStatus, _ := pc.checkMSIServiceStatus()
	totalDuration := time.Since(startTime)
	
	errorMsg := fmt.Sprintf("MSI service did not become available within %d minutes after %d checks and %d retry attempts", 
		maxWaitMinutes, checkCount, retryAttempt)
	
	if finalStatus != nil {
		errorMsg += fmt.Sprintf(" (final status: running=%v, responsive=%v, processes=%d, state=%s)", 
			finalStatus.IsRunning, finalStatus.IsResponsive, finalStatus.ProcessCount, finalStatus.ServiceState)
	}
	
	logging.Error("MSI service availability timeout", 
		"maxWaitMinutes", maxWaitMinutes,
		"totalChecks", checkCount,
		"retryAttempts", retryAttempt,
		"totalDuration", totalDuration)
	
	return fmt.Errorf(errorMsg)
}

// determineRecoveryStrategy determines the best recovery strategy based on current status and retry count
func (pc *ProcessCleanup) determineRecoveryStrategy(status *MSIServiceStatus, retryAttempt int) string {
	// Early exit if we've exceeded max retries
	if retryAttempt >= pc.retryConfig.MaxRetries {
		return "wait" // No more intervention
	}
	
	// If service is not running at all, try service restart first (if enabled)
	if !status.IsRunning && pc.retryConfig.ServiceRestartEnabled && status.CanRestart && retryAttempt < 2 {
		return "restart_service"
	}
	
	// If service is running but unresponsive, try cleanup based on process count
	if status.IsRunning && !status.IsResponsive {
		if status.ProcessCount > 3 && retryAttempt < 2 {
			// Many processes suggest heavy cleanup needed
			return "advanced_cleanup"
		} else if status.ProcessCount > 0 && retryAttempt < 3 {
			// Some processes suggest basic cleanup
			return "cleanup"
		}
	}
	
	// Progressive strategy based on retry count
	if retryAttempt == 0 {
		return "cleanup"
	} else if retryAttempt == 1 && pc.retryConfig.EmergencyCleanupEnabled {
		return "advanced_cleanup"
	} else if retryAttempt == 2 && pc.retryConfig.ServiceRestartEnabled && status.CanRestart {
		return "restart_service"
	} else if retryAttempt == 3 {
		// Nuclear option - most aggressive cleanup
		return "aggressive_cleanup"
	}
	
	// Default strategy is to wait (no more intervention)
	return "wait"
}

// checkMSIMutex checks if the Windows Installer service mutex is locked
func (pc *ProcessCleanup) checkMSIMutex() (bool, error) {
	// Try to run a quick MSI operation to check if installer is busy
	// Use /? instead of /help for faster response
	cmd := exec.Command(commandMsi, "/?")
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	
	done := make(chan error, 1)
	go func() {
		done <- cmd.Run()
	}()

	select {
	case err := <-done:
		// If msiexec /? runs successfully, the service is available
		// Any error (including exit codes) suggests the service might be busy
		if err != nil {
			logging.Debug("MSI service check returned error", "error", err)
			return true, err
		}
		return false, nil
	case <-time.After(3 * time.Second):
		// If it takes more than 3 seconds to show help, installer is probably locked
		if cmd.Process != nil {
			cmd.Process.Kill()
		}
		return true, fmt.Errorf("MSI service appears to be locked - help command timed out")
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

	logging.Debug("Skipping MSI service availability check - proceeding directly with installation")

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
var msiRecoveryMutex sync.Mutex // Global mutex to prevent concurrent MSI recovery operations

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

// WaitForMSIAvailableV2 waits for MSI service to become available with intelligent recovery
func WaitForMSIAvailableV2(maxWaitMinutes int, cfg *config.Configuration) error {
	cleanup := GetGlobalCleanup(cfg)
	
	logging.Info("Starting intelligent MSI service availability check", "maxWaitMinutes", maxWaitMinutes)
	
	// Use the new intelligent wait function
	err := cleanup.WaitForMSIAvailable(maxWaitMinutes)
	if err == nil {
		logging.Info("MSI service is available and ready for use")
		return nil
	}
	
	// Log the specific error for troubleshooting
	logging.Error("MSI service availability check failed after all recovery attempts", 
		"error", err, 
		"maxWaitMinutes", maxWaitMinutes,
		"recommendedAction", "check_system_for_locked_installers_or_pending_reboot")
	
	return fmt.Errorf("MSI service unavailable after intelligent recovery attempts: %w", err)
}

// runMSIDirectlyV2 runs MSI with cleanup and timeout handling
func runMSIDirectlyV2(msiPath string, arguments []string, timeoutMinutes int, cfg *config.Configuration) (string, error) {
	cleanup := GetGlobalCleanup(cfg)
	return cleanup.RunMSIWithCleanup(msiPath, arguments, timeoutMinutes)
}

// runMSIUninstallWithCleanup runs MSI uninstall with cleanup and timeout handling
func runMSIUninstallWithCleanup(args []string, cfg *config.Configuration) (string, error) {
	cleanup := GetGlobalCleanup(cfg)
	
	// Pre-uninstall cleanup
	if err := cleanup.CleanupOrphanedMSIProcesses(); err != nil {
		logging.Warn("Pre-uninstall cleanup had issues", "error", err)
	}

	logging.Debug("Skipping MSI service availability check for uninstall - proceeding directly")

	// Create context with timeout
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(cfg.InstallerTimeoutMinutes)*time.Minute)
	defer cancel()

	cmd := exec.CommandContext(ctx, commandMsi, args...)
	
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
			logging.Error("MSI uninstall timed out", "args", args, "timeoutMinutes", cfg.InstallerTimeoutMinutes)
			
			// Try to forcefully terminate the process and any child processes
			if cmd.Process != nil {
				logging.Debug("Terminating timed-out MSI uninstall process", "pid", cmd.Process.Pid)
				cleanup.terminateProcessTree(cmd.Process.Pid)
			}
			
			return outputStr, fmt.Errorf("MSI uninstall timed out after %d minutes", cfg.InstallerTimeoutMinutes)
		}

		if exitErr, ok := err.(*exec.ExitError); ok {
			exitCode := exitErr.ExitCode()
			logging.Error("MSI uninstall failed", "args", args, "exitCode", exitCode, "output", outputStr)
			return outputStr, fmt.Errorf("MSI uninstall failed with exit code %d: %s", exitCode, outputStr)
		}

		logging.Error("Failed to run MSI uninstall", "args", args, "error", err, "output", outputStr)
		return outputStr, err
	}
	
	logging.Debug("MSI uninstall completed successfully", "args", args, "output", outputStr)
	
	// Post-uninstall cleanup
	if err := cleanup.CleanupOrphanedMSIProcesses(); err != nil {
		logging.Warn("Post-uninstall cleanup had issues", "error", err)
	}

	return outputStr, nil
}

// CheckMSIServiceStatus exposes the checkMSIServiceStatus method
func (pc *ProcessCleanup) CheckMSIServiceStatus() (*MSIServiceStatus, error) {
	return pc.checkMSIServiceStatus()
}

// PerformAdvancedMSICleanup exposes the performAdvancedMSICleanup method
func (pc *ProcessCleanup) PerformAdvancedMSICleanup() error {
	return pc.performAdvancedMSICleanup()
}

// RestartWindowsInstallerService exposes the restartWindowsInstallerService method
func (pc *ProcessCleanup) RestartWindowsInstallerService() error {
	return pc.restartWindowsInstallerService()
}

// getMSIProcessesPS uses PowerShell to get MSI process information (more reliable than wmic)
func (pc *ProcessCleanup) getMSIProcessesPS() ([]string, error) {
	// PowerShell command to get msiexec processes with creation time
	psCmd := `Get-Process -Name msiexec -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Id),$($_.StartTime),$($_.ProcessName)" }`
	
	cmd := exec.Command("powershell", "-Command", psCmd)
	output, err := cmd.Output()
	if err != nil {
		return nil, fmt.Errorf("PowerShell process query failed: %w", err)
	}

	lines := strings.Split(strings.TrimSpace(string(output)), "\n")
	var processes []string
	
	for _, line := range lines {
		line = strings.TrimSpace(line)
		if line != "" && strings.Contains(line, "msiexec") {
			processes = append(processes, line)
		}
	}
	
	return processes, nil
}

// terminateOldProcesses terminates processes older than 30 minutes
func (pc *ProcessCleanup) terminateOldProcesses(processName string, processes []string) error {
	cutoff := time.Now().Add(-30 * time.Minute)
	
	for _, line := range processes {
		parts := strings.Split(line, ",")
		if len(parts) >= 2 {
			pidStr := strings.TrimSpace(parts[0])
			creationTimeStr := strings.TrimSpace(parts[1])
			
			// Parse creation time - handle multiple PowerShell datetime formats
			var creationTime time.Time
			var err error
			
			// Try common PowerShell datetime formats
			formats := []string{
				"1/2/2006 3:04:05 PM",
				"2006-01-02T15:04:05",
				"2006-01-02 15:04:05",
				"01/02/2006 15:04:05",
			}
			
			for _, format := range formats {
				if creationTime, err = time.Parse(format, creationTimeStr); err == nil {
					break
				}
			}
			
			if err == nil && creationTime.Before(cutoff) {
				if pid, err := strconv.Atoi(pidStr); err == nil && pid > 0 {
					logging.Debug("Terminating old MSI process", "pid", pid, "age", time.Since(creationTime))
					exec.Command("taskkill", "/PID", pidStr, "/F").Run()
				}
			}
		}
	}
	
	return nil
}

// performAggressiveCleanup performs the most comprehensive MSI cleanup possible
// This is the most comprehensive approach when other recovery strategies fail
func (pc *ProcessCleanup) performAggressiveCleanup() error {
	logging.Warn("Performing aggressive MSI cleanup - most comprehensive recovery")
	
	// Step 1: Force kill all MSI-related processes using multiple methods
	if err := pc.aggressiveProcessCleanup(); err != nil {
		logging.Debug("Aggressive process cleanup had issues", "error", err)
	}
	
	// Step 2: Clear MSI database locks and temp files
	if err := pc.clearMSIDatabaseLocks(); err != nil {
		logging.Debug("MSI database lock cleanup had issues", "error", err)
	}
	
	// Step 3: Reset MSI service completely (stop, reset dependencies, start)
	if err := pc.nuclearServiceReset(); err != nil {
		logging.Debug("Nuclear service reset had issues", "error", err)
	}
	
	// Step 4: Clear system-level MSI locks
	if err := pc.clearSystemMSILocks(); err != nil {
		logging.Debug("System MSI lock cleanup had issues", "error", err)
	}
	
	logging.Info("Aggressive MSI cleanup completed")
	return nil
}

// aggressiveProcessCleanup uses multiple methods to force-kill MSI processes
func (pc *ProcessCleanup) aggressiveProcessCleanup() error {
	processNames := []string{"msiexec.exe", "setup.exe", "install.exe", "update.exe", "MSIExec.exe", "Windows Installer"}
	
	for _, processName := range processNames {
		// Method 1: Try PowerShell Get-Process and Stop-Process
		psCmd := fmt.Sprintf(`Get-Process -Name "%s" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue`, 
			strings.TrimSuffix(processName, ".exe"))
		cmd := exec.Command("powershell", "-Command", psCmd)
		cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
		cmd.Run() // Ignore errors - process may not exist
		
		// Method 2: Try taskkill as backup
		cmd = exec.Command("taskkill", "/F", "/IM", processName)
		cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
		cmd.Run() // Ignore errors - process may not exist
		
		logging.Debug("Aggressive process cleanup attempted", "processName", processName)
	}
	
	return nil
}

// clearMSIDatabaseLocks clears MSI database locks and temp files
func (pc *ProcessCleanup) clearMSIDatabaseLocks() error {
	// Clear MSI temp directory
	tempPaths := []string{
		`C:\Windows\Installer\`,
		`C:\Windows\Temp\MSI*.tmp`,
		`C:\Users\*\AppData\Local\Temp\MSI*.tmp`,
	}
	
	for _, path := range tempPaths {
		// Use PowerShell to clear temp files (safer than direct file operations)
		psCmd := fmt.Sprintf(`Get-ChildItem -Path "%s" -Filter "MSI*.tmp" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue`, path)
		cmd := exec.Command("powershell", "-Command", psCmd)
		cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
		cmd.Run() // Ignore errors
	}
	
	// Clear additional MSI registry locks
	registryKeys := []string{
		`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress`,
		`HKLM\SYSTEM\CurrentControlSet\Services\msiserver\Parameters`,
		`HKLM\SOFTWARE\Classes\Installer\`,
	}
	
	for _, key := range registryKeys {
		if key == `HKLM\SYSTEM\CurrentControlSet\Services\msiserver\Parameters` {
			// Reset MSI service parameters
			cmd := exec.Command("reg", "delete", key, "/v", "ServicesPipeTimeout", "/f")
			cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
			cmd.Run() // Ignore errors
		}
	}
	
	return nil
}

// nuclearServiceReset performs complete service reset including dependencies
func (pc *ProcessCleanup) nuclearServiceReset() error {
	// Stop service and all dependencies
	stopCommands := [][]string{
		{"sc", "stop", "msiserver"},
		{"sc", "stop", "RpcSs"},   // RPC service (MSI dependency) 
		{"sc", "stop", "EventLog"}, // Event Log (sometimes locks MSI)
	}
	
	for _, cmd := range stopCommands {
		command := exec.Command(cmd[0], cmd[1:]...)
		command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
		command.Run() // Ignore errors
		time.Sleep(500 * time.Millisecond)
	}
	
	// Wait a moment for services to fully stop
	time.Sleep(2 * time.Second)
	
	// Start services back up in correct order
	startCommands := [][]string{
		{"sc", "start", "RpcSs"},
		{"sc", "start", "EventLog"},
		{"sc", "start", "msiserver"},
	}
	
	for _, cmd := range startCommands {
		command := exec.Command(cmd[0], cmd[1:]...)
		command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
		command.Run() // Ignore errors
		time.Sleep(1 * time.Second)
	}
	
	return nil
}

// clearSystemMSILocks clears system-level MSI locks and mutexes
func (pc *ProcessCleanup) clearSystemMSILocks() error {
	// Use handle.exe if available to close MSI handles (advanced)
	// This is optional and will fail gracefully if handle.exe not available
	cmd := exec.Command("handle", "-p", "msiexec", "-c")
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	cmd.Run() // Ignore errors - handle.exe may not be available
	
	// Clear pagefile locks (sometimes MSI gets stuck here)
	psCmd := `Clear-RecycleBin -Force -ErrorAction SilentlyContinue`
	cmd = exec.Command("powershell", "-Command", psCmd)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	cmd.Run()
	
	return nil
}

// GetRetryConfig exposes the retry configuration
func (pc *ProcessCleanup) GetRetryConfig() *MSIRetryConfig {
	return pc.retryConfig
}
