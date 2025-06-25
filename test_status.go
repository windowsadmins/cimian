// test_status.go - Simple test for the status reporter
package main

import (
	"context"
	"fmt"
	"time"

	"github.com/windowsadmins/cimian/pkg/status"
)

func main() {
	fmt.Println("Testing CimianStatus integration...")

	reporter := status.NewPipeReporter()
	if err := reporter.Start(context.Background()); err != nil {
		fmt.Printf("Failed to start reporter: %v\n", err)
		return
	}
	defer reporter.Stop()

	fmt.Println("Connected to CimianStatus GUI")

	// Send various status updates
	reporter.Message("Checking for updates...")
	time.Sleep(2 * time.Second)

	reporter.Detail("Downloading catalog...")
	reporter.Percent(10)
	time.Sleep(2 * time.Second)

	reporter.Detail("Processing manifest...")
	reporter.Percent(30)
	time.Sleep(2 * time.Second)

	reporter.Detail("Downloading packages...")
	reporter.Percent(50)
	time.Sleep(2 * time.Second)

	reporter.Message("Installing packages...")
	reporter.Detail("Installing Adobe Reader...")
	reporter.Percent(75)
	time.Sleep(2 * time.Second)

	reporter.Detail("Running post-install scripts...")
	reporter.Percent(90)
	time.Sleep(2 * time.Second)

	reporter.Message("Installation complete!")
	reporter.Detail("All packages installed successfully")
	reporter.Percent(100)
	time.Sleep(2 * time.Second)

	// Show error example
	reporter.Error(fmt.Errorf("example error - this is just a test"))
	time.Sleep(3 * time.Second)

	reporter.Message("Test completed successfully!")
	time.Sleep(2 * time.Second)

	fmt.Println("Test completed")
}
