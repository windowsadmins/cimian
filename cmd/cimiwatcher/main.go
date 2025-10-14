// cmd/cimianwatcher/main.go - Windows service for monitoring bootstrap flag file

package main

import (
	"bytes"
	"context"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"sync"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/debug"
	"golang.org/x/sys/windows/svc/eventlog"
	"golang.org/x/sys/windows/svc/mgr"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/selfupdate"
	"github.com/windowsadmins/cimian/pkg/version"
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

// Win32 API constants and types for session-aware process launching
var (
	wtsapi32                    = syscall.NewLazyDLL("wtsapi32.dll")
	kernel32                    = syscall.NewLazyDLL("kernel32.dll")
	advapi32                    = syscall.NewLazyDLL("advapi32.dll")
	userenv                     = syscall.NewLazyDLL("userenv.dll")
	
	procWTSGetActiveConsoleSessionId = kernel32.NewProc("WTSGetActiveConsoleSessionId")
	procWTSQueryUserToken            = wtsapi32.NewProc("WTSQueryUserToken")
	procDuplicateTokenEx             = advapi32.NewProc("DuplicateTokenEx")
	procCreateEnvironmentBlock       = userenv.NewProc("CreateEnvironmentBlock")
	procDestroyEnvironmentBlock      = userenv.NewProc("DestroyEnvironmentBlock")
	procCreateProcessAsUser          = advapi32.NewProc("CreateProcessAsUserW")
)

const (
	TOKEN_DUPLICATE       = 0x0002
	TOKEN_QUERY           = 0x0008
	TOKEN_ASSIGN_PRIMARY  = 0x0001
	MAXIMUM_ALLOWED       = 0x02000000
	CREATE_UNICODE_ENVIRONMENT = 0x00000400
	CREATE_NO_WINDOW           = 0x08000000
	NORMAL_PRIORITY_CLASS      = 0x00000020
	CREATE_NEW_CONSOLE         = 0x00000010
)

type STARTUPINFO struct {
	Cb              uint32
	_               *uint16
	Desktop         *uint16
	Title           *uint16
	X               uint32
	Y               uint32
	XSize           uint32
	YSize           uint32
	XCountChars     uint32
	YCountChars     uint32
	FillAttribute   uint32
	Flags           uint32
	ShowWindow      uint16
	_               uint16
	_               *byte
	StdInput        syscall.Handle
	StdOutput       syscall.Handle
	StdError        syscall.Handle
}

type PROCESS_INFORMATION struct {
	Process   syscall.Handle
	Thread    syscall.Handle
	ProcessId uint32
	ThreadId  uint32
}

var logger debug.Log

// hasActiveUserSession checks if there are any active user sessions (logged in users)
func hasActiveUserSession() bool {
	sessionID, _, _ := procWTSGetActiveConsoleSessionId.Call()
	if sessionID == 0xFFFFFFFF {
		return false
	}
	
	// Session ID 0 is typically the services session, Session ID 1+ are user sessions
	// Try to get user token for the session to verify it's actually an active user
	if sessionID > 0 {
		var userToken syscall.Handle
		ret, _, _ := procWTSQueryUserToken.Call(uintptr(sessionID), uintptr(unsafe.Pointer(&userToken)))
		if ret != 0 {
			syscall.CloseHandle(userToken)
			return true
		}
	}
	
	return false
}

// launchGUIInUserSession launches a GUI application in the active user session
// This is necessary when running from a Windows service (Session 0) to display UI
func launchGUIInUserSession(exePath string, logger debug.Log) error {
	// Get the active console session ID
	sessionID, _, _ := procWTSGetActiveConsoleSessionId.Call()
	if sessionID == 0xFFFFFFFF { // WTS_CURRENT_SESSION means no active session
		return fmt.Errorf("no active user session found")
	}

	logger.Info(1, fmt.Sprintf("Active console session ID: %d", sessionID))

	// Get user token for the active session
	var userToken syscall.Handle
	ret, _, err := procWTSQueryUserToken.Call(uintptr(sessionID), uintptr(unsafe.Pointer(&userToken)))
	if ret == 0 {
		return fmt.Errorf("WTSQueryUserToken failed: %v", err)
	}
	defer syscall.CloseHandle(userToken)

	// Duplicate the token to get a primary token
	var duplicateToken syscall.Handle
	ret, _, err = procDuplicateTokenEx.Call(
		uintptr(userToken),
		MAXIMUM_ALLOWED,
		0, // No security attributes
		2, // SecurityImpersonation level
		1, // TokenPrimary
		uintptr(unsafe.Pointer(&duplicateToken)),
	)
	if ret == 0 {
		return fmt.Errorf("DuplicateTokenEx failed: %v", err)
	}
	defer syscall.CloseHandle(duplicateToken)

	// Create environment block for the user
	var environment uintptr
	ret, _, err = procCreateEnvironmentBlock.Call(
		uintptr(unsafe.Pointer(&environment)),
		uintptr(duplicateToken),
		0, // Don't inherit from calling process
	)
	if ret == 0 {
		return fmt.Errorf("CreateEnvironmentBlock failed: %v", err)
	}
	defer procDestroyEnvironmentBlock.Call(environment)

	// Set working directory to exe directory (needed for multi-file .NET builds)
	workingDir := filepath.Dir(exePath)

	// Setup STARTUPINFO and PROCESS_INFORMATION structures
	si := STARTUPINFO{
		Cb: uint32(unsafe.Sizeof(STARTUPINFO{})),
	}
	
	// Set desktop to interactive desktop
	desktopName, _ := syscall.UTF16PtrFromString("winsta0\\default")
	si.Desktop = desktopName

	pi := PROCESS_INFORMATION{}

	// Convert paths to UTF16
	exePathPtr, _ := syscall.UTF16PtrFromString(exePath)
	workingDirPtr, _ := syscall.UTF16PtrFromString(workingDir)

	// Create process as user in the active session
	creationFlags := CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE | NORMAL_PRIORITY_CLASS
	ret, _, err = procCreateProcessAsUser.Call(
		uintptr(duplicateToken),
		uintptr(unsafe.Pointer(exePathPtr)),
		0, // No command line args
		0, // No process security attributes
		0, // No thread security attributes
		0, // Don't inherit handles
		uintptr(creationFlags),
		environment,
		uintptr(unsafe.Pointer(workingDirPtr)),
		uintptr(unsafe.Pointer(&si)),
		uintptr(unsafe.Pointer(&pi)),
	)
	
	if ret == 0 {
		return fmt.Errorf("CreateProcessAsUser failed: %v", err)
	}

	// Close process and thread handles (we don't need to wait)
	syscall.CloseHandle(pi.Process)
	syscall.CloseHandle(pi.Thread)

	logger.Info(1, fmt.Sprintf("Launched GUI process (PID: %d) in session %d", pi.ProcessId, sessionID))
	return nil
}

// launchAtLoginScreen launches a GUI application at the Windows login screen
// Uses SYSTEM privileges to display UI when no user is logged in
func launchAtLoginScreen(exePath string, logger debug.Log) error {
	logger.Info(1, "Launching GUI at login screen (no active user session)")
	
	// For login screen, we run with SYSTEM privileges and --login-screen flag
	// The application will handle the UI display in Session 0 or console session
	cmd := exec.Command(exePath, "--login-screen")
	
	// Set working directory
	cmd.Dir = filepath.Dir(exePath)
	
	// Configure to run as SYSTEM with window visible
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    false,
		CreationFlags: CREATE_NEW_CONSOLE,
	}
	
	// Start the process
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start GUI at login screen: %v", err)
	}
	
	logger.Info(1, fmt.Sprintf("Launched GUI at login screen (PID: %d)", cmd.Process.Pid))
	return nil
}

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
	case "--version":
		version.PrintVersion()
		return
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
		// For debug mode, create buffered channels and drain them
		changes := make(chan svc.ChangeRequest, 10)
		status := make(chan svc.Status, 10)
		
		// Drain status channel in background
		go func() {
			for range status {
			}
		}()
		
		run.Execute(nil, changes, status)
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

	// Check for pending self-updates on service start
	// TEMPORARILY DISABLED: self-update is crashing the service
	// m.checkAndPerformSelfUpdate()

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
	// Since this service runs as SYSTEM, we need to launch the process properly
	// The executable should run with the service's SYSTEM privileges
	var updateCmd *exec.Cmd
	if withGUI {
		// Run with verbose output for GUI monitoring
		updateCmd = exec.Command(cimianExePath, "--auto", "--show-status", "-vv")
	} else {
		// Run headless mode
		updateCmd = exec.Command(cimianExePath, "--auto")
	}

	// Set working directory to avoid path issues
	updateCmd.Dir = filepath.Dir(cimianExePath)

	// Capture output for debugging errors
	var stdoutBuf, stderrBuf bytes.Buffer
	updateCmd.Stdout = &stdoutBuf
	updateCmd.Stderr = &stderrBuf

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

			// Check if there's an active user session
			if hasActiveUserSession() {
				// Active user logged in - launch in their session
				logger.Info(1, "Active user session detected - launching in user session")
				if err := launchGUIInUserSession(cimistatus, logger); err != nil {
					logger.Error(1, fmt.Sprintf("Failed to start CimianStatus UI in user session: %v", err))
				} else {
					logger.Info(1, "Successfully launched CimianStatus UI in user session")
				}
			} else {
				// No user logged in - launch at login screen
				logger.Info(1, "No active user session - launching at login screen")
				if err := launchAtLoginScreen(cimistatus, logger); err != nil {
					logger.Error(1, fmt.Sprintf("Failed to start CimianStatus UI at login screen: %v", err))
				} else {
					logger.Info(1, "Successfully launched CimianStatus UI at login screen")
				}
			}
		} else {
			logger.Error(1, fmt.Sprintf("CimianStatus not found at: %s", cimistatus))
		}
	}

	// Monitor the main update process in a separate goroutine
	go func() {
		if err := updateCmd.Wait(); err != nil {
			logger.Error(1, fmt.Sprintf("%s bootstrap update process failed: %v", updateType, err))
			// Log captured error output for debugging
			if stderrBuf.Len() > 0 {
				logger.Error(1, fmt.Sprintf("Process stderr: %s", stderrBuf.String()))
			}
			if stdoutBuf.Len() > 0 {
				logger.Info(1, fmt.Sprintf("Process stdout: %s", stdoutBuf.String()))
			}
		} else {
			logger.Info(1, fmt.Sprintf("%s bootstrap update process completed successfully", updateType))
			// Log output on success too for debugging
			if stdoutBuf.Len() > 0 {
				logger.Info(1, fmt.Sprintf("Process output: %s", stdoutBuf.String()[:min(500, stdoutBuf.Len())]))
			}
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

// checkAndPerformSelfUpdate checks for pending self-updates and executes them
func (m *cimianWatcherService) checkAndPerformSelfUpdate() {
	// Check if self-update is pending
	pending, metadata, err := selfupdate.GetSelfUpdateStatus()
	if err != nil {
		logger.Error(1, fmt.Sprintf("Failed to check self-update status: %v", err))
		return
	}

	if !pending {
		logger.Info(1, "No self-update pending")
		return
	}

	logger.Info(1, fmt.Sprintf("Self-update detected, executing update for %s", metadata["Item"]))

	// Load configuration for self-update
	cfg, err := config.LoadConfig()
	if err != nil {
		logger.Error(1, fmt.Sprintf("Failed to load configuration for self-update: %v", err))
		return
	}

	// Initialize logging for self-update operations
	if err := logging.Init(cfg); err != nil {
		logger.Error(1, fmt.Sprintf("Failed to initialize logging for self-update: %v", err))
		return
	}
	defer logging.CloseLogger()

	// Perform the self-update
	selfUpdateManager := selfupdate.NewSelfUpdateManager()
	if err := selfUpdateManager.PerformSelfUpdate(cfg); err != nil {
		logger.Error(1, fmt.Sprintf("Self-update failed: %v", err))
		logging.Error("Self-update failed in CimianWatcher service: %v", err)
	} else {
		logger.Info(1, "Self-update completed successfully")
		logging.Success("Self-update completed successfully in CimianWatcher service")
	}
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

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
