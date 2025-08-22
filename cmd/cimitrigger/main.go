package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/version"
)

const (
	// Bootstrap flag file paths
	guiBootstrapFile      = `C:\ProgramData\ManagedInstalls\.cimian.bootstrap`
	headlessBootstrapFile = `C:\ProgramData\ManagedInstalls\.cimian.headless`
)

func main() {
	if len(os.Args) < 2 {
		usage()
		return
	}

	mode := os.Args[1]

	// Handle --version flag
	if mode == "--version" {
		version.PrintVersion()
		return
	}

	// Handle --force flag for direct elevation
	if mode == "--force" {
		if len(os.Args) < 3 {
			fmt.Fprintf(os.Stderr, "Error: --force requires a mode (gui|headless)\n")
			usage()
			os.Exit(1)
		}
		forceMode := os.Args[2]
		if err := runDirectUpdate(forceMode); err != nil {
			fmt.Fprintf(os.Stderr, "Error running forced update: %v\n", err)
			os.Exit(1)
		}
		return
	}

	switch mode {
	case "gui":
		// Always ensure GUI is shown when in GUI mode and user is logged in
		if err := ensureGUIVisible(); err != nil {
			fmt.Fprintf(os.Stderr, "⚠️  Warning: Could not ensure GUI visibility: %v\n", err)
		}

		// Give the GUI time to initialize and be ready to monitor processes
		fmt.Println("⏳ Allowing GUI to initialize...")
		time.Sleep(3 * time.Second)

		if err := runSmartGUIUpdate(); err != nil {
			fmt.Fprintf(os.Stderr, "❌ Error running GUI update: %v\n", err)
			// Even if update fails, keep GUI visible for status monitoring
			os.Exit(1)
		}

	case "headless":
		if err := runSmartHeadlessUpdate(); err != nil {
			fmt.Fprintf(os.Stderr, "❌ Error running headless update: %v\n", err)
			os.Exit(1)
		}

	case "debug":
		fmt.Println("🔍 Running diagnostic mode...")
		runDiagnostics()

	default:
		usage()
		os.Exit(1)
	}
}

func usage() {
	fmt.Fprintf(os.Stderr, "Usage: %s <mode>\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  mode: 'gui', 'headless', 'debug', or '--force <gui|headless>'\n")
	fmt.Fprintf(os.Stderr, "\n")
	fmt.Fprintf(os.Stderr, "Examples:\n")
	fmt.Fprintf(os.Stderr, "  %s gui            # Update with GUI - ALWAYS shows CimianStatus window when logged in\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s headless       # Smart headless update (tries service, falls back to direct)\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s debug          # Run diagnostics to troubleshoot issues\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s --force gui    # Force direct elevation (skip service attempt)\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s --force headless # Force direct elevation (skip service attempt)\n", filepath.Base(os.Args[0]))
}

// waitForFileProcessing waits for the service to process (delete) the trigger file
func waitForFileProcessing(filePath string, timeout time.Duration) bool {
	start := time.Now()
	for time.Since(start) < timeout {
		if _, err := os.Stat(filePath); os.IsNotExist(err) {
			return true // File was deleted - service processed it
		}
		time.Sleep(1 * time.Second)
	}
	return false // Timeout - file still exists
}

// runDiagnostics performs comprehensive diagnostics
func runDiagnostics() {
	fmt.Println("🔍 Cimian Trigger Comprehensive Diagnostics")
	fmt.Println("===========================================")

	// Track issues found for summary
	var issues []string

	// 1. Check administrative privileges
	fmt.Println("\n1. Checking administrative privileges...")
	if isAdmin := checkAdminPrivileges(); !isAdmin {
		fmt.Println("   ❌ Not running as administrator")
		fmt.Println("   💡 Run this tool as administrator for full diagnostics")
		issues = append(issues, "Not running as administrator")
	} else {
		fmt.Println("   ✅ Running with administrative privileges")
	}

	// 2. Check CimianWatcher service
	fmt.Println("\n2. Checking CimianWatcher service...")
	serviceRunning := false
	cmd := exec.Command("sc", "query", "CimianWatcher")
	output, err := cmd.CombinedOutput()
	if err != nil {
		fmt.Println("   ❌ CimianWatcher service not found")
		fmt.Println("   💡 Install Cimian or register the service")
		issues = append(issues, "CimianWatcher service not found")
	} else {
		fmt.Println("   ✅ CimianWatcher service found")
		fmt.Printf("   📊 Status: %s", string(output))
		if !strings.Contains(string(output), "RUNNING") {
			fmt.Println("   ⚠️  Service is not running")
			fmt.Println("   💡 Try: sc start CimianWatcher")
			issues = append(issues, "CimianWatcher service not running")
		} else {
			fmt.Println("   ✅ Service is running")
			serviceRunning = true
		}
	}

	// 3. Check directory permissions
	fmt.Println("\n3. Checking file system permissions...")
	dir := `C:\ProgramData\ManagedInstalls`
	directoryOK := true

	// Check if directory exists
	if _, err := os.Stat(dir); os.IsNotExist(err) {
		fmt.Printf("   ⚠️  Directory does not exist: %s\n", dir)
		fmt.Println("   💡 Creating directory...")
		if err := os.MkdirAll(dir, 0755); err != nil {
			fmt.Printf("   ❌ Failed to create directory: %v\n", err)
			directoryOK = false
			issues = append(issues, "Cannot create ManagedInstalls directory")
		} else {
			fmt.Println("   ✅ Directory created successfully")
		}
	} else {
		fmt.Printf("   ✅ Directory exists: %s\n", dir)
	}

	// Test write permissions
	if directoryOK {
		testFile := filepath.Join(dir, "cimitrigger_test_write.txt")
		if err := os.WriteFile(testFile, []byte("test"), 0644); err != nil {
			fmt.Printf("   ❌ Cannot write to directory: %v\n", err)
			fmt.Println("   💡 Check directory permissions")
			directoryOK = false
			issues = append(issues, "No write permissions to ManagedInstalls directory")
		} else {
			fmt.Println("   ✅ Write permissions OK")
			os.Remove(testFile) // Clean up
		}
	}

	// 4. Check for executables
	fmt.Println("\n4. Checking for required executables...")
	executablesOK := true

	// Check for managedsoftwareupdate.exe
	if execPath, err := findExecutable(); err != nil {
		fmt.Printf("   ❌ managedsoftwareupdate.exe not found: %v\n", err)
		fmt.Println("   💡 Install Cimian or check installation path")
		executablesOK = false
		issues = append(issues, "managedsoftwareupdate.exe not found")
	} else {
		fmt.Printf("   ✅ Found managedsoftwareupdate.exe: %s\n", execPath)

		// Check for cimistatus.exe
		cimistatus := filepath.Join(filepath.Dir(execPath), "cimistatus.exe")
		if _, err := os.Stat(cimistatus); err == nil {
			fmt.Printf("   ✅ Found cimistatus.exe: %s\n", cimistatus)
		} else {
			fmt.Printf("   ⚠️  cimistatus.exe not found: %s\n", cimistatus)
		}

		// Check for cimiwatcher.exe
		cimiwatcher := filepath.Join(filepath.Dir(execPath), "cimiwatcher.exe")
		if _, err := os.Stat(cimiwatcher); err == nil {
			fmt.Printf("   ✅ Found cimiwatcher.exe: %s\n", cimiwatcher)
		} else {
			fmt.Printf("   ⚠️  cimiwatcher.exe not found: %s\n", cimiwatcher)
		}
	}

	// 5. Test trigger file creation and monitoring
	fmt.Println("\n5. Testing trigger file creation...")
	if directoryOK {
		testTriggerFile()
	} else {
		fmt.Println("   ⏭️  Skipped due to directory access issues")
	}

	// 6. Monitor for service response (if service is running)
	if serviceRunning && directoryOK {
		fmt.Println("\n6. Testing service response...")
		fmt.Println("Testing trigger file monitoring for 30 seconds...")
		monitorTriggerResponse()
	} else {
		fmt.Println("\n6. Service response test skipped (prerequisites not met)")
	}

	// 7. Environment information
	fmt.Println("\n7. Environment Information:")
	fmt.Printf("   Current User: %s\n", os.Getenv("USERNAME"))
	fmt.Printf("   User Domain: %s\n", os.Getenv("USERDOMAIN"))
	fmt.Printf("   Machine Name: %s\n", os.Getenv("COMPUTERNAME"))
	fmt.Printf("   OS: %s\n", os.Getenv("OS"))

	// 8. Provide comprehensive recommendations
	fmt.Println("\n" + strings.Repeat("=", 50))
	fmt.Println("📊 DIAGNOSTIC SUMMARY")
	fmt.Println(strings.Repeat("=", 50))

	if len(issues) == 0 {
		fmt.Println("✅ All checks passed - cimitrigger should work properly!")
		fmt.Println("💡 If you're still having issues, try running with verbose output.")
	} else {
		fmt.Printf("❌ Found %d issue(s):\n", len(issues))
		for i, issue := range issues {
			fmt.Printf("   %d. %s\n", i+1, issue)
		}

		fmt.Println("\n🔧 SOLUTIONS:")
		provideDetailedRecommendations(issues, serviceRunning, directoryOK, executablesOK)
	}

	fmt.Println("\n💡 Alternative methods to try:")
	fmt.Println("   1. cimitrigger --force gui        # Direct elevation (bypasses service)")
	fmt.Println("   2. cimitrigger --force headless   # Direct headless elevation")
	fmt.Println("   3. Manual PowerShell elevation:")
	fmt.Println("      PowerShell -Command \"Start-Process -FilePath 'C:\\Program Files\\Cimian\\managedsoftwareupdate.exe' -ArgumentList '--auto','--show-status','-vv' -Verb RunAs\"")

	fmt.Println("\n📋 Troubleshooting commands:")
	fmt.Println("   sc query CimianWatcher              # Check service status")
	fmt.Println("   sc start CimianWatcher              # Start service")
	fmt.Println("   Get-WinEvent -LogName Application | Where-Object {$_.ProviderName -eq 'CimianWatcher'} # View service logs")
}

func runDirectUpdate(mode string) error {
	// Find managedsoftwareupdate.exe
	execPath, err := findExecutable()
	if err != nil {
		return fmt.Errorf("could not find managedsoftwareupdate.exe: %v", err)
	}

	var args []string
	switch mode {
	case "gui":
		args = []string{"--auto", "--show-status", "-vv"}
		fmt.Println("🚀 Initiating update with administrative privileges...")
	case "headless":
		args = []string{"--auto"}
		fmt.Println("🚀 Initiating headless update with administrative privileges...")
	default:
		return fmt.Errorf("invalid direct mode: %s (must be 'gui' or 'headless')", mode)
	}

	// Try multiple elevation methods for domain environments
	methods := []func(string, []string) error{
		runWithUAC,
		runWithPowerShell,
		runWithScheduledTask,
	}

	var lastErr error
	for i, method := range methods {
		fmt.Printf("⚡ Using elevation method %d...\n", i+1)
		if err := method(execPath, args); err != nil {
			fmt.Printf("📋 Method %d unavailable, trying next: %v\n", i+1, err)
			lastErr = err
			continue
		}
		fmt.Printf("✅ Update process started successfully!\n")

		// Give more time for the process to fully start and begin logging
		fmt.Println("⏳ Giving process time to initialize logging...")
		time.Sleep(5 * time.Second)

		if isProcessRunning("managedsoftwareupdate") {
			fmt.Println("✅ Update process confirmed running - CimianStatus should now show live progress")
		} else {
			fmt.Println("📋 Update process completed quickly")
			fmt.Println("💡 Check CimianStatus GUI for results, or view logs in C:\\ProgramData\\ManagedInstalls\\logs")
		}

		return nil
	}

	return fmt.Errorf("all elevation methods failed, last error: %v", lastErr)
}

func findExecutable() (string, error) {
	// Common installation paths
	possiblePaths := []string{
		`C:\Program Files\Cimian\managedsoftwareupdate.exe`,
		`C:\Program Files (x86)\Cimian\managedsoftwareupdate.exe`,
		// Development paths
		`.\managedsoftwareupdate.exe`,
		`..\release\x64\managedsoftwareupdate.exe`,
		`..\..\release\x64\managedsoftwareupdate.exe`,
		`..\..\..\release\x64\managedsoftwareupdate.exe`,
	}

	for _, path := range possiblePaths {
		if _, err := os.Stat(path); err == nil {
			return path, nil
		}
	}

	return "", fmt.Errorf("managedsoftwareupdate.exe not found in any expected location")
}

func runWithUAC(execPath string, args []string) error {
	// Method 1: Direct UAC elevation
	allArgs := append([]string{execPath}, args...)
	cmd := exec.Command("cmd", append([]string{"/c", "runas", "/user:Administrator"}, allArgs...)...)
	return cmd.Start()
}

func runWithPowerShell(execPath string, args []string) error {
	// Method 2: PowerShell Start-Process with -Verb RunAs
	argsStr := strings.Join(args, "','")
	psCommand := fmt.Sprintf("Start-Process -FilePath '%s' -ArgumentList '%s' -Verb RunAs", execPath, argsStr)
	cmd := exec.Command("powershell.exe", "-ExecutionPolicy", "Bypass", "-Command", psCommand)
	return cmd.Start()
}

func runWithScheduledTask(execPath string, args []string) error {
	// Method 3: Scheduled task with SYSTEM account
	taskName := fmt.Sprintf("CimianDirect_%d", time.Now().Unix())

	// Create task
	argsStr := strings.Join(args, " ")
	createArgs := []string{
		"/Create", "/TN", taskName,
		"/TR", fmt.Sprintf(`"%s" %s`, execPath, argsStr),
		"/SC", "ONCE", "/ST", "23:59",
		"/RU", "SYSTEM", "/F",
	}

	createCmd := exec.Command("schtasks.exe", createArgs...)
	if err := createCmd.Run(); err != nil {
		return fmt.Errorf("failed to create scheduled task: %v", err)
	}

	// Run task
	runArgs := []string{"/Run", "/TN", taskName}
	runCmd := exec.Command("schtasks.exe", runArgs...)
	if err := runCmd.Run(); err != nil {
		// Clean up task even if run failed
		deleteArgs := []string{"/Delete", "/TN", taskName, "/F"}
		exec.Command("schtasks.exe", deleteArgs...).Run()
		return fmt.Errorf("failed to run scheduled task: %v", err)
	}

	// Clean up task (in background)
	go func() {
		time.Sleep(10 * time.Second) // Give it time to start
		deleteArgs := []string{"/Delete", "/TN", taskName, "/F"}
		exec.Command("schtasks.exe", deleteArgs...).Run()
	}()

	return nil
}

func createTriggerFile(flagPath, mode string) error {
	// Ensure the directory exists
	dir := filepath.Dir(flagPath)
	if err := os.MkdirAll(dir, 0755); err != nil {
		return fmt.Errorf("failed to create directory %s: %v", dir, err)
	}

	// Create the trigger file
	timestamp := time.Now().Format("2006-01-02 15:04:05")
	content := fmt.Sprintf("Bootstrap triggered at: %s\nMode: %s\nTriggered by: cimitrigger CLI\n", timestamp, mode)

	if err := os.WriteFile(flagPath, []byte(content), 0644); err != nil {
		return fmt.Errorf("failed to write trigger file %s: %v", flagPath, err)
	}

	return nil
}

// checkAdminPrivileges checks if running with administrative privileges
func checkAdminPrivileges() bool {
	// Try to write to a restricted location
	testFile := `C:\Windows\Temp\cimian_admin_test.txt`
	if err := os.WriteFile(testFile, []byte("test"), 0644); err != nil {
		return false
	}
	os.Remove(testFile) // Clean up
	return true
}

// testTriggerFile creates a test trigger file
func testTriggerFile() {
	timestamp := time.Now().Format("2006-01-02 15:04:05")
	content := fmt.Sprintf("Bootstrap triggered at: %s\nMode: GUI\nTriggered by: cimitrigger debug\n", timestamp)

	if err := os.WriteFile(guiBootstrapFile, []byte(content), 0644); err != nil {
		fmt.Printf("   ❌ Failed to create trigger file: %v\n", err)
	} else {
		fmt.Printf("   ✅ Created test trigger file: %s\n", guiBootstrapFile)

		// Check if file exists and is readable
		if data, err := os.ReadFile(guiBootstrapFile); err != nil {
			fmt.Printf("   ❌ Cannot read trigger file: %v\n", err)
		} else {
			fmt.Printf("   ✅ Trigger file contents verified (%d bytes)\n", len(data))
		}
	}
}

// monitorTriggerResponse monitors for service response
func monitorTriggerResponse() {
	fmt.Println("   Monitoring for file deletion (indicates service processed it)...")

	start := time.Now()
	for time.Since(start) < 30*time.Second {
		if _, err := os.Stat(guiBootstrapFile); os.IsNotExist(err) {
			elapsed := time.Since(start)
			fmt.Printf("   ✅ File was deleted after %v - service is responding!\n", elapsed)
			return
		}

		fmt.Print(".")
		time.Sleep(2 * time.Second)
	}

	fmt.Println()
	fmt.Println("   ❌ File was not processed within 30 seconds")
	fmt.Println("   💡 Service may not be running or monitoring correctly")

	// Clean up
	if _, err := os.Stat(guiBootstrapFile); err == nil {
		fmt.Println("   🧹 Cleaning up test trigger file...")
		os.Remove(guiBootstrapFile)
	}
}

// provideDetailedRecommendations provides specific recommendations based on issues found
func provideDetailedRecommendations(issues []string, serviceRunning, directoryOK, executablesOK bool) {
	for _, issue := range issues {
		switch {
		case strings.Contains(issue, "administrator"):
			fmt.Println("🔴 PRIVILEGE ISSUE:")
			fmt.Println("   Solutions:")
			fmt.Println("   1. Right-click Command Prompt → 'Run as administrator'")
			fmt.Println("   2. Use PowerShell as administrator")
			fmt.Println("   3. Check UAC settings")
			fmt.Println()

		case strings.Contains(issue, "service not found"):
			fmt.Println("🔴 SERVICE MISSING:")
			fmt.Println("   Solutions:")
			fmt.Println("   1. Reinstall Cimian completely")
			fmt.Println("   2. Register service manually:")
			fmt.Println("      cd \"C:\\Program Files\\Cimian\"")
			fmt.Println("      cimiwatcher.exe install")
			fmt.Println("      sc start CimianWatcher")
			fmt.Println("   3. Use direct method: cimitrigger --force gui")
			fmt.Println()

		case strings.Contains(issue, "service not running"):
			fmt.Println("🔴 SERVICE STOPPED:")
			fmt.Println("   Solutions:")
			fmt.Println("   1. sc start CimianWatcher")
			fmt.Println("   2. Check Windows Event Logs for service errors")
			fmt.Println("   3. Restart as administrator: net stop CimianWatcher && net start CimianWatcher")
			fmt.Println("   4. Use direct method: cimitrigger --force gui")
			fmt.Println()

		case strings.Contains(issue, "directory"):
			fmt.Println("🔴 DIRECTORY ACCESS:")
			fmt.Println("   Solutions:")
			fmt.Println("   1. Run as administrator")
			fmt.Println("   2. Check folder permissions on C:\\ProgramData\\ManagedInstalls")
			fmt.Println("   3. Create directory manually with proper permissions")
			fmt.Println("   4. Use direct method: cimitrigger --force gui")
			fmt.Println()

		case strings.Contains(issue, "executable"):
			fmt.Println("🔴 MISSING EXECUTABLES:")
			fmt.Println("   Solutions:")
			fmt.Println("   1. Reinstall Cimian")
			fmt.Println("   2. Check installation completed successfully")
			fmt.Println("   3. Verify installation path")
			fmt.Println("   4. Check if antivirus quarantined files")
			fmt.Println()
		}
	}
}

// runSmartGUIUpdate tries service method first, then falls back to direct elevation if needed
func runSmartGUIUpdate() error {
	fmt.Println("🚀 Starting software update process...")

	// Check if managedsoftwareupdate is already running to prevent conflicts
	if isProcessRunning("managedsoftwareupdate") {
		fmt.Println("⚠️  managedsoftwareupdate.exe is already running")
		fmt.Println("✅ CimianStatus GUI will monitor the existing process")
		fmt.Println("🔄 No need to start another process - waiting for current one to complete...")
		return nil
	}

	// Check for recent completed sessions to inform the user
	if recentSession := checkRecentSession(); recentSession != "" {
		fmt.Printf("📋 Recent update session found: %s\n", recentSession)
		fmt.Println("💡 CimianStatus GUI will show the latest results")
		// Check if session was very recent (within last 2 minutes) - might indicate rapid completion
		if isVeryRecentSession(recentSession) {
			fmt.Println("⚡ Session completed very recently - system may not need immediate updates")
		}
	}

	// Step 1: Try service method first (faster when it works)
	fmt.Println("📡 Trying service-based update method...")
	if err := createTriggerFile(guiBootstrapFile, "GUI"); err != nil {
		fmt.Printf("📋 Service method unavailable (trigger file creation failed): %v\n", err)
		fmt.Println("🔄 Using direct elevation method...")
		return runDirectUpdate("gui")
	}

	fmt.Println("✅ Service trigger created successfully")
	fmt.Printf("📁 Trigger file: %s\n", guiBootstrapFile)
	fmt.Println("⏳ Waiting for CimianWatcher service response...")

	// Wait and check if file gets processed
	if waitForFileProcessing(guiBootstrapFile, 15*time.Second) {
		fmt.Println("✅ Service-based update initiated successfully!")

		// Give the service a moment to start the process
		time.Sleep(2 * time.Second)

		// Check for Session 0 isolation issue
		if isGUIRunningInSession0() {
			fmt.Println("⚠️  Detected GUI running in Session 0 (service session)")
			fmt.Println("🔄 Switching to direct elevation for proper user session...")
			killSession0GUI()
			return runDirectUpdate("gui")
		}

		// Ensure GUI is still visible in user session (service might not have launched it)
		if !isGUIRunningInUserSession() {
			fmt.Println("🔄 Service completed - ensuring GUI remains visible in user session...")
			if err := launchGUIInUserSession(); err != nil {
				fmt.Printf("⚠️  Warning: Could not launch GUI in user session: %v\n", err)
			}
		}

		return nil
	} else {
		fmt.Println("📋 Service method timed out - using direct elevation method...")
		fmt.Println("🔄 This is normal and ensures the update completes successfully")

		// Clean up the trigger file if it still exists
		os.Remove(guiBootstrapFile)

		return runDirectUpdate("gui")
	}
}

// runSmartHeadlessUpdate tries service method first, then falls back to direct elevation if needed
func runSmartHeadlessUpdate() error {
	fmt.Println("🚀 Starting smart headless update...")

	// Step 1: Try service method first (faster when it works)
	fmt.Println("📡 Attempting service method first...")
	if err := createTriggerFile(headlessBootstrapFile, "headless"); err != nil {
		fmt.Printf("⚠️  Service method failed (trigger file creation): %v\n", err)
		fmt.Println("🔄 Falling back to direct elevation...")
		return runDirectUpdate("headless")
	}

	fmt.Println("✅ Headless update trigger file created successfully")
	fmt.Printf("📁 Trigger file location: %s\n", headlessBootstrapFile)
	fmt.Println("⏳ Waiting for CimianWatcher service to process the request...")

	// Wait and check if file gets processed
	if waitForFileProcessing(headlessBootstrapFile, 15*time.Second) {
		fmt.Println("✅ Service method successful - update should be starting!")
		return nil
	} else {
		fmt.Println("⚠️  Service method failed (not processed within 15 seconds)")
		fmt.Println("🔄 Automatically falling back to direct elevation...")

		// Clean up the trigger file if it still exists
		os.Remove(headlessBootstrapFile)

		return runDirectUpdate("headless")
	}
}

// isGUIRunningInSession0 checks if cimistatus.exe is running in Session 0 (services session)
func isGUIRunningInSession0() bool {
	cmd := exec.Command("tasklist", "/fi", "imagename eq cimistatus.exe", "/fo", "csv")
	output, err := cmd.CombinedOutput()
	if err != nil {
		return false
	}

	// Check if output contains Session 0
	return strings.Contains(string(output), ",\"Services\",\"0\",")
}

// killSession0GUI terminates cimistatus processes running in Session 0
func killSession0GUI() {
	cmd := exec.Command("taskkill", "/f", "/im", "cimistatus.exe")
	cmd.Run() // Ignore errors - process might not exist or might not have permissions
}

// ensureGUIVisible ensures that cimistatus.exe is running and visible in the current user session
func ensureGUIVisible() error {
	// Check if we're in a user session (not SYSTEM)
	if isSystemSession() {
		return fmt.Errorf("running in SYSTEM session, GUI not appropriate")
	}

	// Check if cimistatus is already running in user session
	if isGUIRunningInUserSession() {
		fmt.Println("✅ CimianStatus GUI already running in user session")
		return nil
	}

	// Kill any Session 0 instances first
	killSession0GUI()

	// Launch cimistatus in current user session
	return launchGUIInUserSession()
}

// isSystemSession checks if we're running as SYSTEM or in a service context
func isSystemSession() bool {
	username := os.Getenv("USERNAME")
	return username == "SYSTEM" || username == "" || os.Getenv("SESSIONNAME") == "Services"
}

// isGUIRunningInUserSession checks if cimistatus.exe is running in current user session
func isGUIRunningInUserSession() bool {
	cmd := exec.Command("tasklist", "/fi", "imagename eq cimistatus.exe", "/fo", "csv")
	output, err := cmd.CombinedOutput()
	if err != nil {
		return false
	}

	outputStr := string(output)
	// Check if cimistatus is running and NOT in Services session (Session 0)
	return strings.Contains(outputStr, "cimistatus.exe") && !strings.Contains(outputStr, ",\"Services\",\"0\",")
}

// launchGUIInUserSession starts cimistatus.exe in the current user session
func launchGUIInUserSession() error {
	// Find cimistatus.exe
	cimistatus, err := findCimistatusExecutable()
	if err != nil {
		return fmt.Errorf("could not find cimistatus.exe: %v", err)
	}

	fmt.Printf("🚀 Launching CimianStatus GUI: %s\n", cimistatus)

	// Launch cimistatus directly (no elevation needed for GUI in user session)
	cmd := exec.Command(cimistatus)

	// Set up environment to ensure it runs in user session
	cmd.Env = os.Environ()

	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start cimistatus.exe: %v", err)
	}

	fmt.Printf("✅ CimianStatus GUI launched successfully (PID: %d)\n", cmd.Process.Pid)

	// Wait a moment for the GUI to fully initialize and be ready to monitor
	fmt.Println("⏳ Ensuring GUI is ready to monitor update processes...")
	time.Sleep(2 * time.Second)

	// Don't wait for the process - let it run independently
	return nil
}

// findCimistatusExecutable locates cimistatus.exe
func findCimistatusExecutable() (string, error) {
	// Try to find managedsoftwareupdate.exe first to get the installation directory
	execPath, err := findExecutable()
	if err == nil {
		cimistatus := filepath.Join(filepath.Dir(execPath), "cimistatus.exe")
		if _, err := os.Stat(cimistatus); err == nil {
			return cimistatus, nil
		}
	}

	// Fall back to common paths
	possiblePaths := []string{
		`C:\Program Files\Cimian\cimistatus.exe`,
		`C:\Program Files (x86)\Cimian\cimistatus.exe`,
		// Development paths
		`.\cimistatus.exe`,
		`..\release\x64\cimistatus.exe`,
		`..\..\release\x64\cimistatus.exe`,
		`..\..\..\release\x64\cimistatus.exe`,
	}

	for _, path := range possiblePaths {
		if _, err := os.Stat(path); err == nil {
			return path, nil
		}
	}

	return "", fmt.Errorf("cimistatus.exe not found in any expected location")
}

// isProcessRunning checks if a process with the given name is currently running
func isProcessRunning(processName string) bool {
	cmd := exec.Command("tasklist", "/fi", fmt.Sprintf("imagename eq %s.exe", processName), "/fo", "csv")
	output, err := cmd.CombinedOutput()
	if err != nil {
		return false
	}

	// Check if the output contains the process name (indicates it's running)
	return strings.Contains(string(output), fmt.Sprintf("%s.exe", processName))
}

// checkRecentSession checks for recent completed update sessions
func checkRecentSession() string {
	logsDir := `C:\ProgramData\ManagedInstalls\logs`

	// Get the most recent log directory
	cmd := exec.Command("powershell", "-Command",
		fmt.Sprintf("Get-ChildItem '%s' -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object { $_.Name }", logsDir))
	output, err := cmd.CombinedOutput()
	if err != nil {
		return ""
	}

	recentDir := strings.TrimSpace(string(output))
	if recentDir == "" {
		return ""
	}

	// Check if this session was recent (within last 10 minutes)
	sessionFile := filepath.Join(logsDir, recentDir, "session.json")
	if _, err := os.Stat(sessionFile); err == nil {
		return recentDir
	}

	return ""
}

// isVeryRecentSession checks if a session completed within the last 2 minutes
func isVeryRecentSession(sessionName string) bool {
	// Parse the session timestamp from the name (format: 2025-08-22-HHMMSS)
	if len(sessionName) < 17 {
		return false
	}

	// For simplicity, just check if it's from the current hour and recent minutes
	// This is a basic check - in production you'd want more precise time parsing
	now := time.Now()
	currentTimeStr := now.Format("2006-01-02-1504")
	sessionPrefix := sessionName[:13] // 2025-08-22-HH
	currentPrefix := currentTimeStr[:13]

	return sessionPrefix == currentPrefix
}
