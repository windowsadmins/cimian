package main

import (
	"encoding/xml"
	"flag"
	"fmt"
	"io/fs"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"gopkg.in/yaml.v2"
)

// BuildInfo holds package build information parsed from YAML.
type BuildInfo struct {
	InstallLocation    string `yaml:"install_location"`
	PostInstallAction  string `yaml:"postinstall_action"`
	SigningCertificate string `yaml:"signing_certificate,omitempty"`
	Product            struct {
		Identifier  string `yaml:"identifier"`
		Version     string `yaml:"version"`
		Name        string `yaml:"name"`
		Developer   string `yaml:"developer"`
		Description string `yaml:"description,omitempty"`
	} `yaml:"product"`
}

// Package defines the structure of a .nuspec package.
type Package struct {
	XMLName  xml.Name  `xml:"package"`
	Metadata Metadata  `xml:"metadata"`
	Files    []FileRef `xml:"files>file,omitempty"`
}

// Metadata stores the package metadata.
type Metadata struct {
	ID          string `xml:"id"`
	Version     string `xml:"version"`
	Authors     string `xml:"authors"`
	Description string `xml:"description"`
	Tags        string `xml:"tags,omitempty"`
	Readme      string `xml:"readme,omitempty"`
}

// FileRef defines the source and target paths for files.
type FileRef struct {
	Src    string `xml:"src,attr"`
	Target string `xml:"target,attr"`
}

func setupLogging(verbose bool) {
	log.SetFlags(log.Ldate | log.Ltime | log.Lshortfile)
	if verbose {
		log.SetOutput(os.Stdout)
	} else {
		log.SetOutput(os.Stderr)
	}
}

func verifyProjectStructure(projectDir string) error {
	payloadPath := filepath.Join(projectDir, "payload")
	scriptsPath := filepath.Join(projectDir, "scripts")

	payloadExists := false
	scriptsExists := false

	if _, err := os.Stat(payloadPath); !os.IsNotExist(err) {
		payloadExists = true
	}

	if _, err := os.Stat(scriptsPath); !os.IsNotExist(err) {
		scriptsExists = true
	}

	if !payloadExists && !scriptsExists {
		return fmt.Errorf("either 'payload' or 'scripts' directory must exist in the project directory")
	}

	buildInfoPath := filepath.Join(projectDir, "build-info.yaml")
	if _, err := os.Stat(buildInfoPath); os.IsNotExist(err) {
		return fmt.Errorf("'build-info.yaml' file is missing in the project directory")
	}

	return nil
}

func NormalizePath(input string) string {
	return filepath.FromSlash(strings.ReplaceAll(input, "\\", "/"))
}

func readBuildInfo(projectDir string) (*BuildInfo, error) {
	path := filepath.Join(projectDir, "build-info.yaml")
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("error reading build-info.yaml: %w", err)
	}

	var buildInfo BuildInfo
	if err := yaml.Unmarshal(data, &buildInfo); err != nil {
		return nil, fmt.Errorf("error parsing YAML: %w", err)
	}

	return &buildInfo, nil
}

func parseVersion(versionStr string) (string, error) {
	parts := strings.Split(versionStr, ".")
	var numericParts []string

	for _, part := range parts {
		if _, err := strconv.Atoi(part); err != nil {
			return "", fmt.Errorf("invalid version part: %q is not a number", part)
		}
		numericParts = append(numericParts, part)
	}

	return strings.Join(numericParts, "."), nil
}

func createProjectDirectory(projectDir string) error {
	subDirs := []string{
		"payload",
		"scripts",
		"build",
		"tools",
	}

	for _, subDir := range subDirs {
		fullPath := filepath.Join(projectDir, subDir)
		if err := os.MkdirAll(fullPath, os.ModePerm); err != nil {
			return fmt.Errorf("failed to create directory %s: %w", fullPath, err)
		}
	}
	return nil
}

func normalizeInstallLocation(path string) string {
	path = strings.ReplaceAll(path, "/", `\`)
	if !strings.HasSuffix(path, `\`) {
		path += `\`
	}
	return path
}

// getPreinstallScripts returns all scripts matching `preinstall*.ps1`
func getPreinstallScripts(projectDir string) ([]string, error) {
	scriptsDir := filepath.Join(projectDir, "scripts")
	var preScripts []string
	if _, err := os.Stat(scriptsDir); os.IsNotExist(err) {
		return preScripts, nil
	}

	entries, err := os.ReadDir(scriptsDir)
	if err != nil {
		return nil, err
	}

	for _, entry := range entries {
		if entry.Type().IsRegular() && strings.HasPrefix(strings.ToLower(entry.Name()), "preinstall") && strings.HasSuffix(strings.ToLower(entry.Name()), ".ps1") {
			preScripts = append(preScripts, entry.Name())
		}
	}

	sort.Strings(preScripts)
	return preScripts, nil
}

// getPostinstallScripts returns all scripts matching `postinstall*.ps1`
func getPostinstallScripts(projectDir string) ([]string, error) {
	scriptsDir := filepath.Join(projectDir, "scripts")
	var postScripts []string
	if _, err := os.Stat(scriptsDir); os.IsNotExist(err) {
		return postScripts, nil
	}

	entries, err := os.ReadDir(scriptsDir)
	if err != nil {
		return nil, err
	}

	for _, entry := range entries {
		if entry.Type().IsRegular() && strings.HasPrefix(strings.ToLower(entry.Name()), "postinstall") && strings.HasSuffix(strings.ToLower(entry.Name()), ".ps1") {
			postScripts = append(postScripts, entry.Name())
		}
	}

	sort.Strings(postScripts)
	return postScripts, nil
}

// includePreinstallScripts bundles all preinstall*.ps1 scripts into chocolateyBeforeModify.ps1
func includePreinstallScripts(projectDir string) error {
	preScripts, err := getPreinstallScripts(projectDir)
	if err != nil {
		return err
	}

	if len(preScripts) == 0 {
		return nil
	}

	// Create or overwrite chocolateyBeforeModify.ps1 with concatenation of all preinstall scripts.
	beforeModifyPath := filepath.Join(projectDir, "tools", "chocolateyBeforeModify.ps1")
	var combined []byte

	for _, script := range preScripts {
		content, err := os.ReadFile(filepath.Join(projectDir, "scripts", script))
		if err != nil {
			return fmt.Errorf("failed to read %s: %w", script, err)
		}
		combined = append(combined, []byte(fmt.Sprintf("# Contents of %s\n", script))...)
		combined = append(combined, content...)
		combined = append(combined, []byte("\n")...)
	}

	if err := os.MkdirAll(filepath.Dir(beforeModifyPath), os.ModePerm); err != nil {
		return fmt.Errorf("failed to create tools directory: %w", err)
	}

	if err := os.WriteFile(beforeModifyPath, combined, 0644); err != nil {
		return fmt.Errorf("failed to write chocolateyBeforeModify.ps1: %w", err)
	}
	return nil
}

// createChocolateyInstallScript generates chocolateyInstall.ps1 and appends postinstall scripts.
func createChocolateyInstallScript(buildInfo *BuildInfo, projectDir string) error {
	scriptPath := filepath.Join(projectDir, "tools", "chocolateyInstall.ps1")

	// Check if the payload folder has any files
	payloadPath := filepath.Join(projectDir, "payload")
	hasPayloadFiles, err := payloadDirectoryHasFiles(payloadPath)
	if err != nil {
		return fmt.Errorf("failed to check payload folder: %w", err)
	}

	installLocation := normalizeInstallLocation(buildInfo.InstallLocation)

	var scriptBuilder strings.Builder
	scriptBuilder.WriteString("$ErrorActionPreference = 'Stop'\n\n")
	scriptBuilder.WriteString(fmt.Sprintf("$installLocation = '%s'\n\n", installLocation))

	// If the payload folder actually has files, do the normal create/copy
	if hasPayloadFiles {
		scriptBuilder.WriteString(`if ($installLocation -and $installLocation -ne '') {
    try {
        New-Item -ItemType Directory -Force -Path $installLocation | Out-Null
        Write-Host "Created or verified install location: $installLocation"
    } catch {
        Write-Error "Failed to create or access: $installLocation"
        exit 1
    }
} else {
    Write-Host "No install location specified, skipping creation of directories."
}

$payloadPath = "$PSScriptRoot\..\payload"
$payloadPath = [System.IO.Path]::GetFullPath($payloadPath)
$payloadPath = $payloadPath.TrimEnd('\', '/')

Write-Host "Payload path: $payloadPath"
Get-ChildItem -Path $payloadPath -Recurse | ForEach-Object {
    $fullName = $_.FullName
    $relativePath = $fullName.Substring($payloadPath.Length)
    $relativePath = $relativePath.TrimStart('\', '/')
    $destinationPath = Join-Path $installLocation $relativePath

    if ($_.PSIsContainer) {
        New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
        Write-Host "Created directory: $destinationPath"
    } else {
        Copy-Item -Path $fullName -Destination $destinationPath -Force
        Write-Host "Copied: $($fullName) -> $destinationPath"

        if (-not (Test-Path -Path $destinationPath)) {
            Write-Error "Failed to copy: $($fullName)"
            exit 1
        }
    }
}
`)
	} else {
		// Script-only scenario
		scriptBuilder.WriteString(`Write-Host "No payload files found. Script-only install - skipping directory creation and file copy."
`)
	}

	// Handle post-install action if provided
	if action := strings.ToLower(buildInfo.PostInstallAction); action != "" {
		scriptBuilder.WriteString("\n# Executing post-install action\n")
		switch action {
		case "logout":
			scriptBuilder.WriteString("Write-Host 'Logging out...'\nshutdown /l\n")
		case "restart":
			scriptBuilder.WriteString("Write-Host 'Restarting system...'\nshutdown /r /t 0\n")
		case "none":
			scriptBuilder.WriteString("Write-Host 'No post-install action required.'\n")
		default:
			return fmt.Errorf("unsupported post-install action: %s", action)
		}
	}

	// Write the base chocolateyInstall.ps1
	if err := os.MkdirAll(filepath.Dir(scriptPath), os.ModePerm); err != nil {
		return fmt.Errorf("failed to create tools directory: %w", err)
	}
	if err := os.WriteFile(scriptPath, []byte(scriptBuilder.String()), 0644); err != nil {
		return fmt.Errorf("failed to write chocolateyInstall.ps1: %w", err)
	}

	// Append postinstall scripts if any
	postScripts, err := getPostinstallScripts(projectDir)
	if err != nil {
		return err
	}
	if len(postScripts) > 0 {
		f, err := os.OpenFile(scriptPath, os.O_APPEND|os.O_WRONLY, 0600)
		if err != nil {
			return fmt.Errorf("failed to open chocolateyInstall.ps1 for append: %w", err)
		}
		defer f.Close()

		for _, script := range postScripts {
			content, err := os.ReadFile(filepath.Join(projectDir, "scripts", script))
			if err != nil {
				return fmt.Errorf("failed to read %s: %w", script, err)
			}
			if _, err := f.WriteString(fmt.Sprintf("\n# Post-install script: %s\n", script)); err != nil {
				return err
			}
			if _, err := f.Write(content); err != nil {
				return err
			}
			if _, err := f.WriteString("\n"); err != nil {
				return err
			}
		}
	}

	return nil
}

// generateNuspec builds the .nuspec file
func generateNuspec(buildInfo *BuildInfo, projectDir string) (string, error) {
	nuspecPath := filepath.Join(projectDir, buildInfo.Product.Name+".nuspec")

	description := buildInfo.Product.Description
	if description == "" {
		description = fmt.Sprintf(
			"%s version %s for %s by %s",
			buildInfo.Product.Name, buildInfo.Product.Version,
			buildInfo.Product.Identifier, buildInfo.Product.Developer,
		)
	}

	nuspec := Package{
		Metadata: Metadata{
			ID:          buildInfo.Product.Identifier,
			Version:     buildInfo.Product.Version,
			Authors:     buildInfo.Product.Developer,
			Description: description,
			Tags:        "admin",
		},
	}

	payloadPath := filepath.Join(projectDir, "payload")
	if _, err := os.Stat(payloadPath); !os.IsNotExist(err) {
		err := filepath.Walk(payloadPath, func(path string, info fs.FileInfo, err error) error {
			if err != nil {
				return err
			}
			if !info.IsDir() {
				relPath, _ := filepath.Rel(projectDir, path)
				relPath = filepath.ToSlash(relPath)
				nuspec.Files = append(nuspec.Files, FileRef{
					Src:    relPath,
					Target: relPath,
				})
			}
			return nil
		})
		if err != nil {
			return "", fmt.Errorf("error walking payload directory: %w", err)
		}
	}

	// Always include chocolateyInstall.ps1
	nuspec.Files = append(nuspec.Files, FileRef{
		Src:    filepath.Join("tools", "chocolateyInstall.ps1"),
		Target: filepath.Join("tools", "chocolateyInstall.ps1"),
	})

	// If we have preinstall scripts, they are combined into chocolateyBeforeModify.ps1
	preScripts, err := getPreinstallScripts(projectDir)
	if err != nil {
		return "", err
	}
	if len(preScripts) > 0 {
		// We know chocolateyBeforeModify.ps1 will be created if preinstall scripts exist
		nuspec.Files = append(nuspec.Files, FileRef{
			Src:    filepath.Join("tools", "chocolateyBeforeModify.ps1"),
			Target: filepath.Join("tools", "chocolateyBeforeModify.ps1"),
		})
	}

	// Postinstall scripts are appended directly into chocolateyInstall.ps1 content,
	// so we don't need to add them separately as files (they are not separate tools/* files).
	// They are merged into chocolateyInstall.ps1 content.

	file, err := os.Create(nuspecPath)
	if err != nil {
		return "", fmt.Errorf("failed to create .nuspec file: %w", err)
	}
	defer file.Close()

	encoder := xml.NewEncoder(file)
	encoder.Indent("", "  ")
	if err := encoder.Encode(nuspec); err != nil {
		return "", fmt.Errorf("failed to encode .nuspec: %w", err)
	}

	return nuspecPath, nil
}

func runCommand(command string, args ...string) error {
	log.Printf("Running: %s %v", command, args)
	cmd := exec.Command(command, args...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}

func signPackage(nupkgFile, certificate string) error {
	log.Printf("Signing package: %s with certificate: %s", nupkgFile, certificate)
	return runCommand(
		"signtool", "sign", "/n", certificate,
		"/fd", "SHA256", "/tr", "http://timestamp.digicert.com",
		"/td", "SHA256", nupkgFile,
	)
}

func checkNuGet() {
	if err := runCommand("nuget", "locals", "all", "-list"); err != nil {
		log.Fatalf(`NuGet is not installed or not in PATH.
You can install it via Chocolatey:
  choco install nuget.commandline`)
	}
}

func checkSignTool() {
	if err := runCommand("signtool", "-?"); err != nil {
		log.Fatalf("SignTool is not installed or not available: %v", err)
	}
}

func payloadDirectoryHasFiles(payloadDir string) (bool, error) {
	if _, err := os.Stat(payloadDir); os.IsNotExist(err) {
		// Payload folder doesn't exist at all
		return false, nil
	}

	hasFiles := false
	err := filepath.Walk(payloadDir, func(path string, info fs.FileInfo, err error) error {
		if err != nil {
			return err
		}
		// If we find at least one regular file, we consider the payload non-empty
		if !info.IsDir() {
			hasFiles = true
			return filepath.SkipDir // no need to keep walking further
		}
		return nil
	})
	return hasFiles, err
}

func main() {
	var verbose bool
	flag.BoolVar(&verbose, "verbose", false, "Enable verbose logging")
	flag.Parse()

	if flag.NArg() < 1 {
		log.Fatalf("Usage: %s <project_directory>", os.Args[0])
	}
	projectDir := NormalizePath(flag.Arg(0))

	setupLogging(verbose)
	log.Printf("Using project directory: %s", projectDir)

	if err := verifyProjectStructure(projectDir); err != nil {
		log.Fatalf("Error verifying project structure: %v", err)
	}
	log.Println("Project structure verified. Proceeding with package creation...")

	buildInfo, err := readBuildInfo(projectDir)
	if err != nil {
		log.Fatalf("Error reading build-info.yaml: %v", err)
	}

	// Check if the payload folder exists and has files
	payloadPath := filepath.Join(projectDir, "payload")
	hasPayloadFiles, err := payloadDirectoryHasFiles(payloadPath)
	if err != nil {
		log.Fatalf("Error checking payload folder: %v", err)
	}

	// Only require install_location if the payload folder actually has files.
	if hasPayloadFiles && buildInfo.InstallLocation == "" {
		log.Fatalf("Error: 'install_location' must be specified in build-info.yaml because your payload folder is not empty.")
	}

	// Validate version format
	if _, err = parseVersion(buildInfo.Product.Version); err != nil {
		log.Fatalf("Error parsing version: %v", err)
	}

	if err := createProjectDirectory(projectDir); err != nil {
		log.Fatalf("Error creating directories: %v", err)
	}
	log.Println("Directories created successfully.")

	// Include all preinstall scripts
	if err := includePreinstallScripts(projectDir); err != nil {
		log.Fatalf("Error including preinstall scripts: %v", err)
	}

	// Create chocolateyInstall.ps1 (and optionally copy payload / append postinstall scripts)
	if err := createChocolateyInstallScript(buildInfo, projectDir); err != nil {
		log.Fatalf("Error generating chocolateyInstall.ps1: %v", err)
	}

	nuspecPath, err := generateNuspec(buildInfo, projectDir)
	if err != nil {
		log.Fatalf("Error generating .nuspec: %v", err)
	}
	defer os.Remove(nuspecPath)
	log.Printf(".nuspec generated at: %s", nuspecPath)

	checkNuGet()

	buildDir := filepath.Join(projectDir, "build")
	builtPkgName := buildInfo.Product.Name + "-" + buildInfo.Product.Version + ".nupkg"
	builtPkgPath := filepath.Join(buildDir, builtPkgName)

	if err := runCommand("nuget", "pack", nuspecPath, "-OutputDirectory", buildDir, "-NoPackageAnalysis"); err != nil {
		log.Fatalf("Error creating package: %v", err)
	}

	searchPattern := filepath.Join(buildDir, buildInfo.Product.Identifier+"*.nupkg")
	matches, _ := filepath.Glob(searchPattern)

	var finalPkgPath string
	if len(matches) > 0 {
		log.Printf("Renaming package: %s to %s", matches[0], builtPkgPath)
		if err := os.Rename(matches[0], builtPkgPath); err != nil {
			log.Fatalf("Failed to rename package: %v", err)
		}
		finalPkgPath = builtPkgPath
	} else {
		log.Printf("Package matching pattern not found, using: %s", builtPkgPath)
		finalPkgPath = builtPkgPath
	}

	// Sign if specified
	if buildInfo.SigningCertificate != "" {
		checkSignTool()
		if err := signPackage(finalPkgPath, buildInfo.SigningCertificate); err != nil {
			log.Fatalf("Failed to sign package %s: %v", finalPkgPath, err)
		}
	} else {
		log.Println("No signing certificate provided. Skipping signing.")
	}

	// Optional: remove the tools directory
	toolsDir := filepath.Join(projectDir, "tools")
	if err := os.RemoveAll(toolsDir); err != nil {
		log.Printf("Warning: Failed to remove tools directory: %v", err)
	} else {
		log.Println("Tools directory removed successfully.")
	}

	log.Printf("Package created successfully: %s", finalPkgPath)
}
