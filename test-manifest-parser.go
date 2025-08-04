package main

import (
	"fmt"
	"os"

	"github.com/windowsadmins/cimian/pkg/manifest"
	"gopkg.in/yaml.v3"
)

func main() {
	fileName := "test-manifest-with-apps-profiles.yaml"
	if len(os.Args) > 1 {
		fileName = os.Args[1]
	}

	data, err := os.ReadFile(fileName)
	if err != nil {
		fmt.Printf("Error reading file: %v\n", err)
		os.Exit(1)
	}

	var mf manifest.ManifestFile
	if err := yaml.Unmarshal(data, &mf); err != nil {
		fmt.Printf("Error parsing YAML: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf("Manifest Name: %s\n", mf.Name)
	fmt.Printf("Managed Installs: %v\n", mf.ManagedInstalls)
	fmt.Printf("Managed Profiles: %v\n", mf.ManagedProfiles)
	fmt.Printf("Managed Apps: %v\n", mf.ManagedApps)

	if len(mf.ConditionalItems) > 0 {
		fmt.Printf("Conditional Items:\n")
		for i, ci := range mf.ConditionalItems {
			fmt.Printf("  [%d] Managed Installs: %v\n", i, ci.ManagedInstalls)
			fmt.Printf("  [%d] Managed Profiles: %v\n", i, ci.ManagedProfiles)
			fmt.Printf("  [%d] Managed Apps: %v\n", i, ci.ManagedApps)
		}
	}

	// Test conditional evaluation
	fmt.Printf("\nTesting conditional evaluation...\n")
	installs, uninstalls, updates, optional, profiles, apps, err := manifest.EvaluateConditionalItems(mf.ConditionalItems)
	if err != nil {
		fmt.Printf("Error evaluating conditionals: %v\n", err)
	} else {
		fmt.Printf("Conditional Installs: %v\n", installs)
		fmt.Printf("Conditional Uninstalls: %v\n", uninstalls)
		fmt.Printf("Conditional Updates: %v\n", updates)
		fmt.Printf("Conditional Optional: %v\n", optional)
		fmt.Printf("Conditional Profiles: %v\n", profiles)
		fmt.Printf("Conditional Apps: %v\n", apps)
	}
}
