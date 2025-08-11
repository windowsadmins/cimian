// cmd/cimianwatcher/main.go - Windows service for monitoring bootstrap flag file

package main

import (
	"context"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"sync"
	"time"

	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/debug"
	"golang.org/x/sys/windows/svc/eventlog"
	"golang.org/x/sys/windows/svc/mgr"
)

const (
	serviceName        = "CimianWatcher"
	serviceDisplayName = "Cimian Bootstrap File Watcher"
	serviceDescription = "Monitors for Cimian bootstrap flag files and triggers software updates"

	// Bootstrap flag file paths
	bootstrapFlagFile = `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` // With GUI
	headlessFlagFile  = `C:\ProgramData\ManagedInstalls\.cimian.headless`  // Without GUI
	// Cimian executable path
	cimianExePath = `C:\Program Files\Cimian\managedsoftwareupdate.exe`
	// Polling interval for checking the flag files
	pollInterval = 10 * time.Second
)

var logger debug.Log

type cimianWatcherService struct {
	ctx              context.Context
	cancel           context.CancelFunc
	isRunning        bool
	mu               sync.Mutex
	lastSeenGUI      time.Time // For bootstrap file with GUI
	lastSeenHeadless time.Time // For headless bootstrap file
}

func main() {
	isIntSess, err := svc.IsAnInteractiveSession()
	if err != nil {
		log.Fatalf("failed to determine if we are running in an interactive session: %v", err)
	}

	if !isIntSess {
		runService(serviceName, false)
		return
	}

	if len(os.Args) < 2 {
		usage()
	}

	cmd := os.Args[1]
	switch cmd {
	case "debug":
		runService(serviceName, true)
		return
	case "install":
		err = installService(serviceName, serviceDisplayName, serviceDescription)
	case "remove":
		err = removeService(serviceName)
	case "start":
		err = startService(serviceName)
	case "stop":
		err = controlService(serviceName, svc.Stop, svc.Stopped)
	case "pause":
		err = controlService(serviceName, svc.Pause, svc.Paused)
	case "continue":
		err = controlService(serviceName, svc.Continue, svc.Running)
	default:
		usage()
	}

	if err != nil {
		log.Fatalf("failed to %s %s: %v", cmd, serviceName, err)
	}
}

func usage() {
	fmt.Fprintf(os.Stderr, "usage: %s <command>\n", os.Args[0])
	fmt.Fprintf(os.Stderr, "       where <command> is one of install, remove, debug, start, stop, pause or continue.\n")
	os.Exit(2)
}

func runService(name string, isDebug bool) {
	var err error
	if isDebug {
		logger = debug.New(name)
	} else {
		logger, err = eventlog.Open(name)
		if err != nil {
			return
		}
	}
	defer logger.Close()

	logger.Info(1, fmt.Sprintf("starting %s service", name))
	run := &cimianWatcherService{}

	if isDebug {
		// For debug mode, create dummy channels
		run.Execute(nil, make(chan svc.ChangeRequest), make(chan svc.Status))
	} else {
		svc.Run(name, run)
	}

	logger.Info(1, fmt.Sprintf("stopping %s service", name))
}

func (m *cimianWatcherService) Execute(args []string, r <-chan svc.ChangeRequest, s chan<- svc.Status) (svcSpecificEC bool, exitCode uint32) {
	const cmdsAccepted = svc.AcceptStop | svc.AcceptShutdown | svc.AcceptPauseAndContinue
	s <- svc.Status{State: svc.StartPending}

	// Initialize context
	m.ctx, m.cancel = context.WithCancel(context.Background())

	// Start the file monitoring
	if err := m.startMonitoring(); err != nil {
		logger.Error(1, fmt.Sprintf("Failed to start file monitoring: %v", err))
		s <- svc.Status{State: svc.Stopped}
		return
	}

	s <- svc.Status{State: svc.Running, Accepts: cmdsAccepted}
	logger.Info(1, "Cimian watcher service is now running")

loop:
	for {
		select {
		case c := <-r:
			switch c.Cmd {
			case svc.Interrogate:
				s <- c.CurrentStatus
			case svc.Stop, svc.Shutdown:
				logger.Info(1, "Received stop/shutdown signal")
				break loop
			case svc.Pause:
				s <- svc.Status{State: svc.Paused, Accepts: cmdsAccepted}
				m.pauseMonitoring()
			case svc.Continue:
				s <- svc.Status{State: svc.Running, Accepts: cmdsAccepted}
				m.resumeMonitoring()
			default:
				logger.Error(1, fmt.Sprintf("unexpected control request #%d", c))
			}
		case <-m.ctx.Done():
			break loop
		}
	}

	m.stopMonitoring()
	s <- svc.Status{State: svc.StopPending}
	return
}

func (m *cimianWatcherService) startMonitoring() error {
	// Check if bootstrap files already exist at startup
	if fileInfo, err := os.Stat(bootstrapFlagFile); err == nil {
		logger.Info(1, "Bootstrap flag file (GUI) exists at startup - triggering update with GUI")
		m.lastSeenGUI = fileInfo.ModTime()
		go m.triggerBootstrapUpdate(true) // true = with GUI
	}

	if fileInfo, err := os.Stat(headlessFlagFile); err == nil {
		logger.Info(1, "Headless flag file exists at startup - triggering update without GUI")
		m.lastSeenHeadless = fileInfo.ModTime()
		go m.triggerBootstrapUpdate(false) // false = without GUI
	}

	// Start the polling goroutine
	go m.pollBootstrapFiles()

	m.isRunning = true
	logger.Info(1, fmt.Sprintf("Started monitoring bootstrap flag files: %s and %s", bootstrapFlagFile, headlessFlagFile))
	return nil
}

func (m *cimianWatcherService) pollBootstrapFiles() {
	ticker := time.NewTicker(pollInterval)
	defer ticker.Stop()

	for {
		select {
		case <-ticker.C:
			if !m.isRunning {
				continue
			}

			// Check GUI bootstrap file
			fileInfo, err := os.Stat(bootstrapFlagFile)
			if err == nil {
				// File exists, check if this is a new file or if it was modified since last seen
				if m.lastSeenGUI.IsZero() || fileInfo.ModTime().After(m.lastSeenGUI) {
					logger.Info(1, "Bootstrap flag file (GUI) detected - triggering update with GUI")
					m.lastSeenGUI = fileInfo.ModTime()
					go m.triggerBootstrapUpdate(true) // true = with GUI
				}
			}

			// Check headless bootstrap file
			fileInfo, err = os.Stat(headlessFlagFile)
			if err == nil {
				// File exists, check if this is a new file or if it was modified since last seen
				if m.lastSeenHeadless.IsZero() || fileInfo.ModTime().After(m.lastSeenHeadless) {
					logger.Info(1, "Headless flag file detected - triggering update without GUI")
					m.lastSeenHeadless = fileInfo.ModTime()
					go m.triggerBootstrapUpdate(false) // false = without GUI
				}
			}

		case <-m.ctx.Done():
			return
		}
	}
}

func (m *cimianWatcherService) triggerBootstrapUpdate(withGUI bool) {
	m.mu.Lock()
	defer m.mu.Unlock()

	var flagFile string
	var updateType string

	if withGUI {
		flagFile = bootstrapFlagFile
		updateType = "GUI"
	} else {
		flagFile = headlessFlagFile
		updateType = "headless"
	}

	// Verify the file still exists (race condition protection)
	if _, err := os.Stat(flagFile); os.IsNotExist(err) {
		logger.Info(1, fmt.Sprintf("%s flag file no longer exists - skipping update", updateType))
		return
	}

	// Execute the bootstrap update
	logger.Info(1, fmt.Sprintf("Starting %s bootstrap update process", updateType))

	// First, start managedsoftwareupdate in the background
	var updateCmd *exec.Cmd
	if withGUI {
		// Run with verbose output for GUI monitoring
		updateCmd = exec.Command(cimianExePath, "--auto", "--show-status", "-vv")
	} else {
		// Run headless mode
		updateCmd = exec.Command(cimianExePath, "--auto")
	}

	// Start the update process
	if err := updateCmd.Start(); err != nil {
		logger.Error(1, fmt.Sprintf("Failed to start %s bootstrap update: %v", updateType, err))
		return
	}

	logger.Info(1, fmt.Sprintf("Started managedsoftwareupdate process (PID: %d)", updateCmd.Process.Pid))

	// If GUI mode, also launch cimistatus to provide UI monitoring
	if withGUI {
		cimistatus := filepath.Join(filepath.Dir(cimianExePath), "cimistatus.exe")

		// Check if cimistatus exists
		if _, err := os.Stat(cimistatus); err == nil {
			logger.Info(1, "Starting CimianStatus UI for monitoring")

			guiCmd := exec.Command(cimistatus)
			if err := guiCmd.Start(); err != nil {
				logger.Error(1, fmt.Sprintf("Failed to start CimianStatus UI: %v", err))
			} else {
				logger.Info(1, fmt.Sprintf("Started CimianStatus UI (PID: %d)", guiCmd.Process.Pid))
			}
		} else {
			logger.Error(1, fmt.Sprintf("CimianStatus not found at: %s", cimistatus))
		}
	}

	// Monitor the main update process in a separate goroutine
	go func() {
		if err := updateCmd.Wait(); err != nil {
			logger.Error(1, fmt.Sprintf("%s bootstrap update process failed: %v", updateType, err))
		} else {
			logger.Info(1, fmt.Sprintf("%s bootstrap update process completed successfully", updateType))
		}

		// Clean up the flag file after completion
		if err := os.Remove(flagFile); err != nil {
			logger.Error(1, fmt.Sprintf("Failed to remove %s flag file: %v", updateType, err))
		} else {
			logger.Info(1, fmt.Sprintf("Removed %s flag file after completion", updateType))
		}
	}()
}

func (m *cimianWatcherService) pauseMonitoring() {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.isRunning = false
	logger.Info(1, "Monitoring paused")
}

func (m *cimianWatcherService) resumeMonitoring() {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.isRunning = true
	logger.Info(1, "Monitoring resumed")
}

func (m *cimianWatcherService) stopMonitoring() {
	if m.cancel != nil {
		m.cancel()
	}
	logger.Info(1, "File monitoring stopped")
}

// Service management functions
func installService(name, displayName, desc string) error {
	exepath, err := exePath()
	if err != nil {
		return err
	}
	m, err := mgr.Connect()
	if err != nil {
		return err
	}
	defer m.Disconnect()
	s, err := m.OpenService(name)
	if err == nil {
		// Service already exists - this is OK during upgrades
		s.Close()
		log.Printf("Service %s already exists, skipping installation", name)
		return nil
	}
	s, err = m.CreateService(name, exepath, mgr.Config{
		DisplayName: displayName,
		Description: desc,
		StartType:   mgr.StartAutomatic,
	})
	if err != nil {
		return err
	}
	defer s.Close()
	err = eventlog.InstallAsEventCreate(name, eventlog.Error|eventlog.Warning|eventlog.Info)
	if err != nil {
		s.Delete()
		return fmt.Errorf("SetupEventLogSource() failed: %s", err)
	}
	return nil
}

func removeService(name string) error {
	m, err := mgr.Connect()
	if err != nil {
		return err
	}
	defer m.Disconnect()
	s, err := m.OpenService(name)
	if err != nil {
		return fmt.Errorf("service %s is not installed", name)
	}
	defer s.Close()
	err = s.Delete()
	if err != nil {
		return err
	}
	err = eventlog.Remove(name)
	if err != nil {
		return fmt.Errorf("RemoveEventLogSource() failed: %s", err)
	}
	return nil
}

func startService(name string) error {
	m, err := mgr.Connect()
	if err != nil {
		return err
	}
	defer m.Disconnect()
	s, err := m.OpenService(name)
	if err != nil {
		return fmt.Errorf("could not access service: %v", err)
	}
	defer s.Close()

	// Check if service is already running
	status, err := s.Query()
	if err != nil {
		return fmt.Errorf("could not query service status: %v", err)
	}

	if status.State == svc.Running {
		log.Printf("Service %s is already running", name)
		return nil
	}

	err = s.Start("service")
	if err != nil {
		return fmt.Errorf("could not start service: %v", err)
	}
	return nil
}

func controlService(name string, c svc.Cmd, to svc.State) error {
	m, err := mgr.Connect()
	if err != nil {
		return err
	}
	defer m.Disconnect()
	s, err := m.OpenService(name)
	if err != nil {
		return fmt.Errorf("could not access service: %v", err)
	}
	defer s.Close()
	status, err := s.Control(c)
	if err != nil {
		return fmt.Errorf("could not send control=%d: %v", c, err)
	}
	timeout := time.Now().Add(10 * time.Second)
	for status.State != to {
		if timeout.Before(time.Now()) {
			return fmt.Errorf("timeout waiting for service to go to state=%d", to)
		}
		time.Sleep(300 * time.Millisecond)
		status, err = s.Query()
		if err != nil {
			return fmt.Errorf("could not retrieve service status: %v", err)
		}
	}
	return nil
}

func exePath() (string, error) {
	prog := os.Args[0]
	p, err := filepath.Abs(prog)
	if err != nil {
		return "", err
	}
	fi, err := os.Stat(p)
	if err != nil {
		return "", err
	}
	if !fi.Mode().IsRegular() {
		return "", fmt.Errorf("%s is not a regular file", p)
	}
	return p, nil
}
