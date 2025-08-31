// pkg/selfupdate/selfupdate.go - Handles Cimian self-update operations safely

package selfupdate

import (
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/catalog"
	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/rollback"
)

const (
	// Self-update flag file - indicates a self-update is pending
	SelfUpdateFlagFile = `C:\ProgramData\ManagedInstalls\.cimian.selfupdate`
	// Backup directory for current installation during self-update
	SelfUpdateBackupDir = `C:\ProgramData\ManagedInstalls\SelfUpdateBackup`
	// Installation directory for Cimian
	CimianInstallDir = `C:\Program Files\Cimian`
)

// SelfUpdateManager handles safe self-updates of Cimian
type SelfUpdateManager struct {
	rollbackManager *rollback.RollbackManager
	backupDir       string
	installDir      string
}

// NewSelfUpdateManager creates a new self-update manager
func NewSelfUpdateManager() *SelfUpdateManager {
	return &SelfUpdateManager{
		rollbackManager: &rollback.RollbackManager{},
		backupDir:       SelfUpdateBackupDir,
		installDir:      CimianInstallDir,
	}
}

// IsCimianPackage checks if the given item is a Cimian self-update package
// Only matches the core Cimian installation packages, not supporting tools
func IsCimianPackage(item catalog.Item) bool {
	itemName := strings.ToLower(item.Name)

	// Only check for the main Cimian installation packages
	cimianMainPackages := []string{
		"cimian",      // Main Cimian package (exact match preferred)
		"cimiantools", // Alternative name for the main package
	}

	// First, check for exact matches (preferred)
	for _, packageName := range cimianMainPackages {
		if itemName == packageName {
			logging.Info("Detected Cimian self-update package (exact match)", "item", item.Name)
			return true
		}
	}

	// Then check for exact matches with common suffixes
	for _, packageName := range cimianMainPackages {
		suffixes := []string{"-msi", "-nupkg", "-tools", ".msi", ".nupkg"}
		for _, suffix := range suffixes {
			if itemName == packageName+suffix {
				logging.Info("Detected Cimian self-update package (with suffix)", "item", item.Name, "suffix", suffix)
				return true
			}
		}
	}

	// Check installer location for main Cimian packages (be more specific)
	installerLocation := strings.ToLower(item.Installer.Location)
	if strings.Contains(installerLocation, "/cimian-") ||
		strings.Contains(installerLocation, "/cimiantools-") ||
		(strings.Contains(installerLocation, "/cimian.") && (strings.HasSuffix(installerLocation, ".msi") || strings.HasSuffix(installerLocation, ".nupkg"))) {
		logging.Info("Detected Cimian package by installer location", "item", item.Name, "location", item.Installer.Location)
		return true
	}

	// Explicitly exclude Cimian supporting tools/components
	excludedPrefixes := []string{
		"cimianpreflight",
		"cimianauth",
		"cimianbrowser",
		"cimianhelper",
		"cimianconfig",
		"cimianreport",
		"cimianlog",
	}

	for _, excluded := range excludedPrefixes {
		if strings.HasPrefix(itemName, excluded) {
			logging.Debug("Excluding Cimian supporting package", "item", item.Name, "reason", "not main package")
			return false
		}
	}

	return false
}

// IsSelfUpdatePending checks if a self-update is already scheduled
func IsSelfUpdatePending() bool {
	_, err := os.Stat(SelfUpdateFlagFile)
	return err == nil
}

// ScheduleSelfUpdate schedules a self-update to be performed on next service restart
func (sum *SelfUpdateManager) ScheduleSelfUpdate(item catalog.Item, localFile string, cfg *config.Configuration) error {
	logging.Info("Scheduling Cimian self-update for next service restart", "item", item.Name, "version", item.Version)

	// Create self-update flag file with metadata
	flagData := fmt.Sprintf(`# Cimian Self-Update Scheduled
Item: %s
Version: %s
InstallerType: %s
LocalFile: %s
ScheduledAt: %s
`, item.Name, item.Version, item.Installer.Type, localFile, time.Now().Format(time.RFC3339))

	if err := os.WriteFile(SelfUpdateFlagFile, []byte(flagData), 0644); err != nil {
		return fmt.Errorf("failed to create self-update flag file: %w", err)
	}

	// Log the self-update scheduling event
	logging.LogEventEntry("selfupdate", "schedule", "scheduled",
		fmt.Sprintf("Scheduled self-update for %s version %s", item.Name, item.Version),
		logging.WithPackage(item.Name, item.Version),
		logging.WithContext("installer_type", item.Installer.Type),
		logging.WithContext("local_file", localFile))

	logging.Success("Self-update scheduled successfully. Cimian will update on next service restart.")
	return nil
}

// PerformSelfUpdate executes the actual self-update (called during service restart)
func (sum *SelfUpdateManager) PerformSelfUpdate(cfg *config.Configuration) error {
	if !IsSelfUpdatePending() {
		return nil // No self-update pending
	}

	logging.Info("Performing scheduled Cimian self-update...")

	// Read self-update metadata
	flagData, err := os.ReadFile(SelfUpdateFlagFile)
	if err != nil {
		return fmt.Errorf("failed to read self-update flag file: %w", err)
	}

	// Parse metadata (simple line-based parsing)
	metadata := make(map[string]string)
	lines := strings.Split(string(flagData), "\n")
	for _, line := range lines {
		if strings.Contains(line, ":") && !strings.HasPrefix(line, "#") {
			parts := strings.SplitN(line, ":", 2)
			if len(parts) == 2 {
				metadata[strings.TrimSpace(parts[0])] = strings.TrimSpace(parts[1])
			}
		}
	}

	localFile, exists := metadata["LocalFile"]
	if !exists || localFile == "" {
		return fmt.Errorf("self-update metadata missing LocalFile information")
	}

	itemName := metadata["Item"]
	version := metadata["Version"]
	installerType := metadata["InstallerType"]

	logging.Info("Executing self-update", "item", itemName, "version", version, "type", installerType, "file", localFile)

	// Log self-update execution start
	logging.LogEventEntry("selfupdate", "execute", "started",
		fmt.Sprintf("Starting self-update execution for %s version %s", itemName, version),
		logging.WithPackage(itemName, version),
		logging.WithContext("installer_type", installerType),
		logging.WithContext("local_file", localFile))

	// Create backup of current installation
	if err := sum.createBackup(); err != nil {
		logging.Error("Failed to create backup before self-update", "error", err)
		return fmt.Errorf("backup creation failed: %w", err)
	}

	// Execute the update based on installer type
	var updateErr error
	switch strings.ToLower(installerType) {
	case "msi":
		updateErr = sum.performMSIUpdate(localFile, itemName)
	case "nupkg":
		updateErr = sum.performNupkgUpdate(localFile, itemName, cfg)
	default:
		updateErr = fmt.Errorf("unsupported installer type for self-update: %s", installerType)
	}

	if updateErr != nil {
		logging.Error("Self-update failed, attempting rollback", "error", updateErr)

		// Log self-update failure
		status := logging.StatusFromError("selfupdate", updateErr)
		logging.LogEventEntry("selfupdate", "execute", status,
			fmt.Sprintf("Self-update failed for %s: %v", itemName, updateErr),
			logging.WithPackage(itemName, version),
			logging.WithError(updateErr))

		// Attempt rollback
		if rollbackErr := sum.performRollback(); rollbackErr != nil {
			logging.Error("Rollback also failed", "rollback_error", rollbackErr, "original_error", updateErr)
			return fmt.Errorf("self-update failed and rollback failed: update_error=%v, rollback_error=%v", updateErr, rollbackErr)
		}

		logging.Success("Rollback completed successfully")
		return updateErr
	}

	// Self-update successful - update registry with new version
	if err := config.WriteCimianVersionToRegistry(version); err != nil {
		logging.Warn("Failed to update Cimian version in registry after self-update", "error", err)
	}

	// Self-update successful - clean up
	if err := sum.cleanupAfterSuccess(); err != nil {
		logging.Warn("Failed to clean up after successful self-update", "error", err)
	}

	// Log successful self-update
	logging.LogEventEntry("selfupdate", "execute", "completed",
		fmt.Sprintf("Self-update completed successfully for %s version %s", itemName, version),
		logging.WithPackage(itemName, version))

	logging.Success("Cimian self-update completed successfully")
	return nil
}

// createBackup backs up the current Cimian installation
func (sum *SelfUpdateManager) createBackup() error {
	logging.Info("Creating backup of current Cimian installation...")

	// Remove any existing backup
	if err := os.RemoveAll(sum.backupDir); err != nil && !os.IsNotExist(err) {
		return fmt.Errorf("failed to remove existing backup: %w", err)
	}

	// Create backup directory
	if err := os.MkdirAll(sum.backupDir, 0755); err != nil {
		return fmt.Errorf("failed to create backup directory: %w", err)
	}

	// Copy current installation to backup
	if err := sum.copyDirectory(sum.installDir, sum.backupDir); err != nil {
		return fmt.Errorf("failed to copy installation to backup: %w", err)
	}

	// Add rollback action
	sum.rollbackManager.AddRollbackAction(rollback.RollbackAction{
		Description: "Restore Cimian installation from backup",
		Execute: func() error {
			return sum.performRollback()
		},
	})

	logging.Success("Backup created successfully", "backup_dir", sum.backupDir)
	return nil
}

// performMSIUpdate performs self-update using MSI installer
func (sum *SelfUpdateManager) performMSIUpdate(msiPath, itemName string) error {
	logging.Info("Performing MSI-based self-update", "msi", msiPath)

	// Stop Cimian services before update
	if err := sum.stopCimianServices(); err != nil {
		logging.Warn("Failed to stop some Cimian services", "error", err)
	}

	// Run MSI installer in quiet mode
	args := []string{
		"/i", msiPath,
		"/quiet",
		"/norestart",
		"/l*v", filepath.Join(logging.GetCurrentLogDir(), "selfupdate_msi.log"),
		"REINSTALLMODE=vamus", // Reinstall all, update existing
		"REINSTALL=ALL",
	}

	if err := sum.runCommand("msiexec.exe", args); err != nil {
		return fmt.Errorf("MSI installation failed: %w", err)
	}

	logging.Success("MSI self-update completed successfully")
	return nil
}

// performNupkgUpdate performs self-update using Chocolatey nupkg
func (sum *SelfUpdateManager) performNupkgUpdate(nupkgPath, itemName string, cfg *config.Configuration) error {
	logging.Info("Performing Chocolatey-based self-update", "nupkg", nupkgPath)

	chocolateyBin := filepath.Join(os.Getenv("ProgramData"), "chocolatey", "bin", "choco.exe")
	if _, err := os.Stat(chocolateyBin); os.IsNotExist(err) {
		return fmt.Errorf("chocolatey not found at %s", chocolateyBin)
	}

	// Stop Cimian services before update
	if err := sum.stopCimianServices(); err != nil {
		logging.Warn("Failed to stop some Cimian services", "error", err)
	}

	// Extract package ID from nupkg (simplified approach)
	packageID := "cimiantools" // Default package ID
	sourceDir := filepath.Dir(nupkgPath)

	// Run chocolatey upgrade
	args := []string{
		"upgrade", packageID,
		"--source", sourceDir,
		"-y",
		"--force",
		"--allowdowngrade",
		"--log-file", filepath.Join(logging.GetCurrentLogDir(), "selfupdate_choco.log"),
	}

	if err := sum.runCommand(chocolateyBin, args); err != nil {
		return fmt.Errorf("chocolatey upgrade failed: %w", err)
	}

	logging.Success("Chocolatey self-update completed successfully")
	return nil
}

// stopCimianServices stops Cimian services to allow file replacement
func (sum *SelfUpdateManager) stopCimianServices() error {
	logging.Info("Stopping Cimian services for self-update...")

	// Services to stop
	services := []string{
		"CimianWatcher",
		"Cimian Bootstrap File Watcher",
	}

	// Processes to terminate
	processes := []string{
		"cimiwatcher",
		"cimistatus",
		"managedsoftwareupdate",
	}

	// Stop services using sc command
	for _, service := range services {
		args := []string{"stop", service}
		if err := sum.runCommand("sc.exe", args); err != nil {
			logging.Warn("Failed to stop service", "service", service, "error", err)
		} else {
			logging.Info("Stopped service", "service", service)
		}
	}

	// Terminate processes using taskkill
	for _, process := range processes {
		args := []string{"/F", "/IM", process + ".exe"}
		if err := sum.runCommand("taskkill.exe", args); err != nil {
			logging.Debug("Process not running or failed to terminate", "process", process)
		} else {
			logging.Info("Terminated process", "process", process)
		}
	}

	// Give system time to release file handles
	time.Sleep(5 * time.Second)

	return nil
}

// performRollback restores the previous installation
func (sum *SelfUpdateManager) performRollback() error {
	logging.Info("Performing self-update rollback...")

	if _, err := os.Stat(sum.backupDir); os.IsNotExist(err) {
		return fmt.Errorf("backup directory not found: %s", sum.backupDir)
	}

	// Stop services again
	sum.stopCimianServices()

	// Remove current installation
	if err := os.RemoveAll(sum.installDir); err != nil {
		logging.Warn("Failed to remove current installation during rollback", "error", err)
	}

	// Restore from backup
	if err := sum.copyDirectory(sum.backupDir, sum.installDir); err != nil {
		return fmt.Errorf("failed to restore from backup: %w", err)
	}

	logging.Success("Rollback completed successfully")
	return nil
}

// cleanupAfterSuccess cleans up temporary files after successful update
func (sum *SelfUpdateManager) cleanupAfterSuccess() error {
	logging.Info("Cleaning up after successful self-update...")

	// Remove self-update flag file
	if err := os.Remove(SelfUpdateFlagFile); err != nil && !os.IsNotExist(err) {
		logging.Warn("Failed to remove self-update flag file", "error", err)
	}

	// Remove backup directory after successful update
	if err := os.RemoveAll(sum.backupDir); err != nil {
		logging.Warn("Failed to remove backup directory", "error", err)
	}

	logging.Success("Cleanup completed successfully")
	return nil
}

// ClearSelfUpdateFlag removes the self-update flag file (for manual intervention)
func ClearSelfUpdateFlag() error {
	if err := os.Remove(SelfUpdateFlagFile); err != nil && !os.IsNotExist(err) {
		return fmt.Errorf("failed to remove self-update flag file: %w", err)
	}
	logging.Info("Self-update flag cleared")
	return nil
}

// Helper functions

func (sum *SelfUpdateManager) runCommand(command string, args []string) error {
	logging.Debug("Running command", "cmd", command, "args", args)

	cmd := exec.Command(command, args...)

	// Capture output for logging
	output, err := cmd.CombinedOutput()
	if err != nil {
		logging.Error("Command execution failed", "cmd", command, "args", args, "output", string(output), "error", err)
		return fmt.Errorf("command failed: %w", err)
	}

	logging.Debug("Command completed successfully", "cmd", command, "output", string(output))
	return nil
}

func (sum *SelfUpdateManager) copyDirectory(src, dst string) error {
	logging.Debug("Copying directory", "src", src, "dst", dst)

	// Get source directory info
	srcInfo, err := os.Stat(src)
	if err != nil {
		return fmt.Errorf("failed to stat source directory: %w", err)
	}

	// Create destination directory
	if err := os.MkdirAll(dst, srcInfo.Mode()); err != nil {
		return fmt.Errorf("failed to create destination directory: %w", err)
	}

	// Read source directory entries
	entries, err := os.ReadDir(src)
	if err != nil {
		return fmt.Errorf("failed to read source directory: %w", err)
	}

	// Copy each entry
	for _, entry := range entries {
		srcPath := filepath.Join(src, entry.Name())
		dstPath := filepath.Join(dst, entry.Name())

		if entry.IsDir() {
			// Recursively copy subdirectory
			if err := sum.copyDirectory(srcPath, dstPath); err != nil {
				return fmt.Errorf("failed to copy subdirectory %s: %w", entry.Name(), err)
			}
		} else {
			// Copy file
			if err := sum.copyFile(srcPath, dstPath); err != nil {
				return fmt.Errorf("failed to copy file %s: %w", entry.Name(), err)
			}
		}
	}

	return nil
}

func (sum *SelfUpdateManager) copyFile(src, dst string) error {
	// Open source file
	srcFile, err := os.Open(src)
	if err != nil {
		return fmt.Errorf("failed to open source file: %w", err)
	}
	defer srcFile.Close()

	// Get source file info
	srcInfo, err := srcFile.Stat()
	if err != nil {
		return fmt.Errorf("failed to stat source file: %w", err)
	}

	// Create destination file
	dstFile, err := os.OpenFile(dst, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, srcInfo.Mode())
	if err != nil {
		return fmt.Errorf("failed to create destination file: %w", err)
	}
	defer dstFile.Close()

	// Copy file contents
	_, err = io.Copy(dstFile, srcFile)
	if err != nil {
		return fmt.Errorf("failed to copy file contents: %w", err)
	}

	return nil
}

// GetSelfUpdateStatus returns information about pending self-updates
func GetSelfUpdateStatus() (bool, map[string]string, error) {
	pending := IsSelfUpdatePending()
	metadata := make(map[string]string)

	if pending {
		flagData, err := os.ReadFile(SelfUpdateFlagFile)
		if err != nil {
			return pending, metadata, fmt.Errorf("failed to read self-update flag: %w", err)
		}

		// Parse metadata
		lines := strings.Split(string(flagData), "\n")
		for _, line := range lines {
			if strings.Contains(line, ":") && !strings.HasPrefix(line, "#") {
				parts := strings.SplitN(line, ":", 2)
				if len(parts) == 2 {
					metadata[strings.TrimSpace(parts[0])] = strings.TrimSpace(parts[1])
				}
			}
		}
	}

	return pending, metadata, nil
}
