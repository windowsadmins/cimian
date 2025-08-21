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

	// Handle --direct flag for domain environments
	if mode == "--direct" {
		if len(os.Args) < 3 {
			fmt.Fprintf(os.Stderr, "Error: --direct requires a mode (gui|headless)\n")
			usage()
			os.Exit(1)
		}
		directMode := os.Args[2]
		if err := runDirectUpdate(directMode); err != nil {
			fmt.Fprintf(os.Stderr, "Error running direct update: %v\n", err)
			os.Exit(1)
		}
		return
	}

	switch mode {
	case "gui":
		if err := createTriggerFile(guiBootstrapFile, "GUI"); err != nil {
			fmt.Fprintf(os.Stderr, "Error creating GUI trigger: %v\n", err)
			os.Exit(1)
		}
		fmt.Println("GUI update triggered successfully")

	case "headless":
		if err := createTriggerFile(headlessBootstrapFile, "headless"); err != nil {
			fmt.Fprintf(os.Stderr, "Error creating headless trigger: %v\n", err)
			os.Exit(1)
		}
		fmt.Println("Headless update triggered successfully")

	default:
		usage()
		os.Exit(1)
	}
}

func usage() {
	fmt.Fprintf(os.Stderr, "Usage: %s <mode>\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  mode: 'gui', 'headless', or '--direct <gui|headless>'\n")
	fmt.Fprintf(os.Stderr, "\n")
	fmt.Fprintf(os.Stderr, "Examples:\n")
	fmt.Fprintf(os.Stderr, "  %s gui            # Trigger update with GUI via CimianWatcher service\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s headless       # Trigger update without GUI via CimianWatcher service\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s --direct gui   # Run update directly with elevation (for domain environments)\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s --direct headless # Run update directly with elevation (for domain environments)\n", filepath.Base(os.Args[0]))
}

func runDirectUpdate(mode string) error {
	// Find managedsoftwareupdate.exe
	execPath, err := findExecutable()
	if err != nil {
		return fmt.Errorf("could not find managedsoftwareupdate.exe: %v", err)
	}

	var args []string
	if mode == "gui" {
		args = []string{"--auto", "--show-status", "-vv"}
		fmt.Println("Starting direct GUI update with elevation...")
	} else if mode == "headless" {
		args = []string{"--auto"}
		fmt.Println("Starting direct headless update with elevation...")
	} else {
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
	cmd := exec.Command("powershell.exe", "-Command", psCommand)
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
