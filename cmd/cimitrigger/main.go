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
		fmt.Println("🚀 Triggering GUI update via CimianWatcher service...")
		if err := createTriggerFile(guiBootstrapFile, "GUI"); err != nil {
			fmt.Fprintf(os.Stderr, "❌ Error creating GUI trigger: %v\n", err)
			fmt.Fprintf(os.Stderr, "💡 Try running as administrator or use: cimitrigger --force gui\n")
			os.Exit(1)
		}
		fmt.Println("✅ GUI update trigger file created successfully")
		fmt.Printf("📁 Trigger file location: %s\n", guiBootstrapFile)
		fmt.Println("⏳ Waiting for CimianWatcher service to process the request...")

		// Wait and check if file gets processed
		if waitForFileProcessing(guiBootstrapFile, 15*time.Second) {
			fmt.Println("✅ CimianWatcher service processed the trigger - update should be starting!")
		} else {
			fmt.Println("⚠️  Trigger file was not processed within 15 seconds")
			fmt.Println("💡 Possible issues:")
			fmt.Println("   - CimianWatcher service is not running (check: sc query CimianWatcher)")
			fmt.Println("   - Service permissions issue")
			fmt.Println("   - Try: cimitrigger --force gui")
		}

	case "headless":
		fmt.Println("🚀 Triggering headless update via CimianWatcher service...")
		if err := createTriggerFile(headlessBootstrapFile, "headless"); err != nil {
			fmt.Fprintf(os.Stderr, "❌ Error creating headless trigger: %v\n", err)
			fmt.Fprintf(os.Stderr, "💡 Try running as administrator or use: cimitrigger --force headless\n")
			os.Exit(1)
		}
		fmt.Println("✅ Headless update trigger file created successfully")
		fmt.Printf("📁 Trigger file location: %s\n", headlessBootstrapFile)
		fmt.Println("⏳ Waiting for CimianWatcher service to process the request...")

		// Wait and check if file gets processed
		if waitForFileProcessing(headlessBootstrapFile, 15*time.Second) {
			fmt.Println("✅ CimianWatcher service processed the trigger - update should be starting!")
		} else {
			fmt.Println("⚠️  Trigger file was not processed within 15 seconds")
			fmt.Println("💡 Possible issues:")
			fmt.Println("   - CimianWatcher service is not running (check: sc query CimianWatcher)")
			fmt.Println("   - Service permissions issue")
			fmt.Println("   - Try: cimitrigger --force headless")
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
	fmt.Fprintf(os.Stderr, "  %s gui            # Trigger update with GUI via CimianWatcher service\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s headless       # Trigger update without GUI via CimianWatcher service\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s debug          # Run diagnostics to troubleshoot issues\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s --force gui    # Run update directly with elevation (for domain environments)\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s --force headless # Run update directly with elevation (for domain environments)\n", filepath.Base(os.Args[0]))
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
		if askYesNo("Would you like to test trigger file monitoring for 30 seconds?") {
			monitorTriggerResponse()
		}
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
		fmt.Println("Starting direct GUI update with elevation...")
	case "headless":
		args = []string{"--auto"}
		fmt.Println("Starting direct headless update with elevation...")
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
		fmt.Printf("Trying elevation method %d...\n", i+1)
		if err := method(execPath, args); err != nil {
			fmt.Printf("Method %d failed: %v\n", i+1, err)
			lastErr = err
			continue
		}
		fmt.Printf("Successfully started with method %d\n", i+1)
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

// askYesNo prompts user for yes/no input
func askYesNo(question string) bool {
	fmt.Printf("%s (y/n): ", question)
	var response string
	fmt.Scanln(&response)
	return response == "y" || response == "Y" || response == "yes" || response == "Yes"
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
