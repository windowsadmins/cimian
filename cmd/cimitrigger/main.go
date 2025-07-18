// cmd/cimitrigger/main.go - Command-line utility to trigger Cimian updates

package main

import (
	"fmt"
	"os"
	"path/filepath"
	"time"
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
	fmt.Fprintf(os.Stderr, "  mode: 'gui' or 'headless'\n")
	fmt.Fprintf(os.Stderr, "\n")
	fmt.Fprintf(os.Stderr, "Examples:\n")
	fmt.Fprintf(os.Stderr, "  %s gui      # Trigger update with GUI (shows cimistatus window)\n", filepath.Base(os.Args[0]))
	fmt.Fprintf(os.Stderr, "  %s headless # Trigger update without GUI (silent background update)\n", filepath.Base(os.Args[0]))
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
