package main

import (
    "encoding/xml"
    "encoding/json"
    "crypto/sha256"
    "flag"
    "fmt"
    "io"
    "os"
    "log"
    "os/exec"
    "path/filepath"
    "runtime"
    "strings"
    "bytes"
    "gopkg.in/yaml.v3"
    "github.com/AlecAivazis/survey/v2"
    "github.com/rodchristiansen/gorilla/pkg/logging"
    "github.com/rodchristiansen/gorilla/pkg/config"
)

type PkgsInfo struct {
    Name                string     `yaml:"name"`
    DisplayName         string     `yaml:"display_name"`
    Version             string     `yaml:"version"`
    Description         string     `yaml:"description"`
    Catalogs            []string   `yaml:"catalogs"`
    Category            string     `yaml:"category"`
    Developer           string     `yaml:"developer"`
    UnattendedInstall   bool       `yaml:"unattended_install"`
    UnattendedUninstall bool       `yaml:"unattended_uninstall"`
    Installer           *Installer `yaml:"installer"`
    Uninstaller         *Installer `yaml:"uninstaller,omitempty"`
    SupportedArch       []string   `yaml:"supported_architectures"`
    ProductCode         string     `yaml:"product_code,omitempty"`
    UpgradeCode         string     `yaml:"upgrade_code,omitempty"`
    PreinstallScript    string     `yaml:"preinstall_script,omitempty"`
    PostinstallScript   string     `yaml:"postinstall_script,omitempty"`
    PreuninstallScript  string     `yaml:"preuninstall_script,omitempty"`
    PostuninstallScript string     `yaml:"postuninstall_script,omitempty"`
    InstallCheckScript  string     `yaml:"installcheck_script,omitempty"`
    UninstallCheckScript string    `yaml:"uninstallcheck_script,omitempty"`
}

type Installer struct {
    Location  string   `yaml:"location"`
    Hash      string   `yaml:"hash"`
    Arguments []string `yaml:"arguments,omitempty"`
    Type      string   `yaml:"type"`
}

// Configuration holds the configurable options for Gorilla in YAML format
type Configuration struct {
    RepoPath       string `yaml:"repo_path"`
    CloudProvider  string `yaml:"cloud_provider"`
    CloudBucket    string `yaml:"cloud_bucket"`
    DefaultCatalog string `yaml:"default_catalog"`
    DefaultArch    string `yaml:"default_arch"`
}

type Metadata struct {
    Title        string `xml:"title"`
    ID           string `xml:"id"`
    Version      string `xml:"version"`
    Authors      string `xml:"authors"`
    Description  string `xml:"description"`
    Tags         string `xml:"tags,omitempty"`
    Readme       string `xml:"readme,omitempty"`
    ProductCode  string // For MSI packages
    UpgradeCode  string // For MSI packages
}

func main() {
    // Load configuration.
    conf, err := config.LoadConfig()
    if err != nil {
        log.Fatalf("Error loading config: %v", err)
    }

    // Parse command-line flags.
    configFlag := flag.Bool("config", false, "Run interactive configuration setup.")
    archFlag := flag.String("arch", "", "Specify the architecture (e.g., x86_64, arm64)")
    repoPath := flag.String("repo_path", "", "Path to the Gorilla repo.")
    installerFlag := flag.String("installer", "", "Path to the installer .exe or .msi file.")
    uninstallerFlag := flag.String("uninstaller", "", "Path to the uninstaller .exe or .msi file.")
    installScriptFlag := flag.String("installscript", "", "Path to the install script (.bat or .ps1).")
    preuninstallScriptFlag := flag.String("preuninstallscript", "", "Path to the preuninstall script.")
    postuninstallScriptFlag := flag.String("postuninstallscript", "", "Path to the postuninstall script.")
    postinstallScriptFlag := flag.String("postinstallscript", "", "Path to the post-install script.")
    installCheckScriptFlag := flag.String("installcheckscript", "", "Path to the install check script.")
    uninstallCheckScriptFlag := flag.String("uninstallcheckscript", "", "Path to the uninstall check script.")
    flag.Parse()

    // Initialize the logger.
    logging.InitLogger(*conf)
    defer logging.CloseLogger()

    // Run interactive configuration setup if --config is provided.
    if *configFlag {
        configureGorillaImport()
        fmt.Println("Configuration saved successfully.")
        return
    }

    // Override config values with flags if provided.
    if *repoPath != "" {
        conf.RepoPath = *repoPath
    }
    if *archFlag != "" {
        conf.DefaultArch = *archFlag
    }

    packagePath := getInstallerPath(*installerFlag)
    if packagePath == "" {
        fmt.Println("Error: No installer provided.")
        os.Exit(1)
    }
    
    importSuccess, err := gorillaImport(
        packagePath, *conf, *installScriptFlag, *preuninstallScriptFlag,
        *postuninstallScriptFlag, *postinstallScriptFlag, *uninstallerFlag,
        *installCheckScriptFlag, *uninstallCheckScriptFlag,
    )
    if err != nil {
        logging.LogError(err, "Import Error")
        fmt.Printf("Error: %v\n", err)
        os.Exit(1)
    }

    if importSuccess && conf.CloudProvider != "none" {
        if err := uploadToCloud(*conf); err != nil {
            fmt.Printf("Error uploading to cloud: %v\n", err)
            os.Exit(1)
        }
    }

    if confirmAction("Run makecatalogs? (y/n)") {
        if err := runMakeCatalogs(); err != nil {
            log.Fatalf("makecatalogs error: %v", err)
        }
    }

    fmt.Println("Gorilla import completed successfully.")
}

func initLogger(conf config.Configuration) {
    logging.InitLogger(conf)
}

func checkTools() error {
    switch runtime.GOOS {
    case "windows":
        _, err := exec.LookPath("msiexec")
        if err != nil {
            return fmt.Errorf("msiexec is required to extract MSI metadata on Windows")
        }
    case "darwin":
        _, err := exec.LookPath("msiextract")
        if err != nil {
            return fmt.Errorf("msiextract is required on macOS. Install it with Homebrew.")
        }
    default:
        return fmt.Errorf("Unsupported platform: %s", runtime.GOOS)
    }
    return nil
}

func findMatchingItem(pkgsInfos []PkgsInfo, name, version string) *PkgsInfo {
    for _, item := range pkgsInfos {
        if item.Name == name && item.Version == version {
            return &item
        }
    }
    return nil
}

func scanRepo(repoPath string) ([]PkgsInfo, error) {
    var pkgsInfos []PkgsInfo

    err := filepath.Walk(repoPath, func(path string, info os.FileInfo, err error) error {
        if err != nil {
            return err
        }
        if filepath.Ext(path) == ".yaml" {
            content, err := os.ReadFile(path)
            if err != nil {
                return err
            }
            var pkgsInfo PkgsInfo
            if err := yaml.Unmarshal(content, &pkgsInfo); err != nil {
                return err
            }
            pkgsInfos = append(pkgsInfos, pkgsInfo)
        }
        return nil
    })

    return pkgsInfos, err
}

func getConfigPath() string {
    var configDir string

    switch runtime.GOOS {
    case "windows":
        configDir = `C:\ProgramData\ManagedInstalls`
    case "darwin":
        configDir = filepath.Join(os.Getenv("HOME"), "Library", "Application Support", "ManagedInstalls")
    default:
        configDir = filepath.Join(os.Getenv("HOME"), ".config", "ManagedInstalls")
    }

    configPath := filepath.Join(configDir, "Config.yaml")
    if _, err := os.Stat(configPath); os.IsNotExist(err) {
        panic(fmt.Sprintf("Configuration file not found: %s", configPath))
    }

    return configPath
}

func configureGorillaImport() {
    conf := config.GetDefaultConfig()

    fmt.Print("Enter Repo Path: ")
    fmt.Scanln(&conf.RepoPath)

    fmt.Print("Enter Cloud Provider (aws/azure/none): ")
    fmt.Scanln(&conf.CloudProvider)

    if conf.CloudProvider != "none" {
        fmt.Print("Enter Cloud Bucket: ")
        fmt.Scanln(&conf.CloudBucket)
    }

    fmt.Print("Enter Default Catalog: ")
    fmt.Scanln(&conf.DefaultCatalog)

    fmt.Print("Enter Default Architecture: ")
    fmt.Scanln(&conf.DefaultArch)

    if err := config.SaveConfig(conf); err != nil {
        log.Fatalf("Failed to save config: %v", err)
    }
}

func extractInstallerMetadata(packagePath string) (Metadata, error) {
    ext := strings.ToLower(filepath.Ext(packagePath))
    switch ext {
    case ".nupkg":
        return extractNuGetMetadata(packagePath)
    case ".msi":
        return extractMSIMetadata(packagePath)
    case ".exe", ".bat", ".ps1":
        return promptForMetadata(packagePath)
    default:
        return Metadata{}, fmt.Errorf("unsupported installer type: %s", ext)
    }
}

func extractNuGetMetadata(nupkgPath string) (Metadata, error) {
    tempDir, err := os.MkdirTemp("", "nuget-extract-")
    if err != nil {
        return Metadata{}, fmt.Errorf("failed to create temp directory: %v", err)
    }
    defer os.RemoveAll(tempDir)

    cmd := exec.Command("nuget", "install", nupkgPath, "-OutputDirectory", tempDir, "-NoCache")
    if err := cmd.Run(); err != nil {
        return Metadata{}, fmt.Errorf("failed to extract .nupkg: %v", err)
    }

    nuspecFiles, err := filepath.Glob(filepath.Join(tempDir, "*", "*.nuspec"))
    if err != nil || len(nuspecFiles) == 0 {
        return Metadata{}, fmt.Errorf(".nuspec file not found")
    }

    content, err := os.ReadFile(nuspecFiles[0])
    if err != nil {
        return Metadata{}, fmt.Errorf("failed to read .nuspec: %v", err)
    }

    var metadata Metadata
    if err := xml.Unmarshal(content, &metadata); err != nil {
        return Metadata{}, fmt.Errorf("failed to parse .nuspec: %v", err)
    }

    return metadata, nil
}

func extractMSIMetadata(msiFilePath string) (Metadata, error) {
    // Ensure we're on Windows
    if runtime.GOOS != "windows" {
        return Metadata{}, fmt.Errorf("MSI metadata extraction is only supported on Windows")
    }

    // Escape backslashes in the file path
    msiFilePathEscaped := strings.ReplaceAll(msiFilePath, `\`, `\\`)

    // PowerShell script to extract MSI properties
    psScript := fmt.Sprintf(`$WindowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$Database = $WindowsInstaller.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $WindowsInstaller, @("%s", 0))
$View = $Database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $Database, @('SELECT * FROM Property'))
$View.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $View, $null)
$Record = $View.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $View, $null)

$properties = @{}
while ($Record -ne $null) {
    $property = $Record.StringData(1)
    $value = $Record.StringData(2)
    $properties[$property] = $value
    $Record = $View.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $View, $null)
}

$properties | ConvertTo-Json -Compress`, msiFilePathEscaped)

    // Prepare the command to execute the PowerShell script
    cmd := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", psScript)

    // Execute the command and capture the output
    output, err := cmd.Output()
    if err != nil {
        return Metadata{}, fmt.Errorf("failed to execute PowerShell script: %v", err)
    }

    // Parse the JSON output
    var properties map[string]string
    if err := json.Unmarshal(output, &properties); err != nil {
        return Metadata{}, fmt.Errorf("failed to parse JSON output: %v", err)
    }

    // Extract the desired properties
    metadata := Metadata{
        Title:       properties["ProductName"],
        ID:          properties["ProductCode"], // Use ProductCode as ID
        Version:     properties["ProductVersion"],
        Authors:     properties["Manufacturer"],
        Description: properties["Comments"],     // If available
        ProductCode: properties["ProductCode"],
        UpgradeCode: properties["UpgradeCode"],
    }

    return metadata, nil
}

func calculateSHA256(packagePath string) (string, error) {
    file, err := os.Open(packagePath)
    if err != nil {
        return "", err
    }
    defer file.Close()

    hash := sha256.New()
    if _, err := io.Copy(hash, file); err != nil {
        return "", err
    }

    return fmt.Sprintf("%x", hash.Sum(nil)), nil
}

func copyFile(src, dst string) (int64, error) {
    srcFile, err := os.Open(src)
    if err != nil {
        return 0, err
    }
    defer srcFile.Close()

    dstFile, err := os.Create(dst)
    if err != nil {
        return 0, err
    }
    defer dstFile.Close()

    nBytes, err := io.Copy(dstFile, srcFile)
    if err != nil {
        return 0, err
    }

    return nBytes, dstFile.Sync()
}

func cleanScriptInput(script string) string {
    return strings.TrimSpace(script)
}

func indentScriptForYaml(script string) string {
    lines := strings.Split(script, "\n")
    var indentedLines []string

    for _, line := range lines {
        trimmedLine := strings.TrimSpace(line)
        if trimmedLine != "" {
            indentedLines = append(indentedLines, "    "+trimmedLine)
        } else {
            indentedLines = append(indentedLines, "")
        }
    }

    return strings.Join(indentedLines, "\n")
}

func encodeWithSelectiveBlockScalars(pkgsInfo PkgsInfo) ([]byte, error) {
    var buf bytes.Buffer
    encoder := yaml.NewEncoder(&buf)
    encoder.SetIndent(2)

    if err := encoder.Encode(&pkgsInfo); err != nil {
        return nil, fmt.Errorf("failed to encode pkgsinfo: %v", err)
    }
    return buf.Bytes(), nil
}

func handleScriptField(node *yaml.Node, value interface{}) error {
    if script, ok := value.(string); ok && script != "" {
        node.Kind = yaml.ScalarNode
        node.Style = yaml.LiteralStyle // Use block scalar style
        node.Value = "\n" + script // Ensure script starts with a newline
    } else {
        node.Kind = yaml.ScalarNode
        node.Tag = "!!null"
    }
    return nil
}

func addField(node *yaml.Node, key string, value interface{}) {
    keyNode := &yaml.Node{Kind: yaml.ScalarNode, Value: key}
    valueNode := &yaml.Node{}

    switch v := value.(type) {
    case string:
        valueNode.Kind = yaml.ScalarNode
        valueNode.Value = v
    case bool:
        valueNode.Kind = yaml.ScalarNode
        valueNode.Value = fmt.Sprintf("%v", v)
    case []string:
        valueNode.Kind = yaml.SequenceNode
        for _, item := range v {
            valueNode.Content = append(valueNode.Content, &yaml.Node{
                Kind: yaml.ScalarNode, Value: item,
            })
        }
    }

    node.Content = append(node.Content, keyNode, valueNode)
}

func addScriptField(node *yaml.Node, key string, value string) {
    keyNode := &yaml.Node{Kind: yaml.ScalarNode, Value: key}
    valueNode := &yaml.Node{
        Kind:  yaml.ScalarNode,
        Style: yaml.LiteralStyle,
        Value: value,
    }
    node.Content = append(node.Content, keyNode, valueNode)
}

func getEmptyIfEmptyString(s string) interface{} {
    if s == "" {
        return "" // Or nil to omit the field entirely
    }
    return s
}

func isScriptField(fieldName string) bool {
    scriptFields := []string{
        "preinstall_script", "postinstall_script",
        "preuninstall_script", "postuninstall_script",
        "installcheck_script", "uninstallcheck_script",
    }
    for _, field := range scriptFields {
        if fieldName == field {
            return true
        }
    }
    return false
}

func populateStandardFields(m map[string]interface{}, info PkgsInfo) {
    m["name"] = info.Name
    m["display_name"] = info.DisplayName
    m["version"] = info.Version
    m["description"] = info.Description
    m["catalogs"] = info.Catalogs
    m["category"] = info.Category
    m["developer"] = info.Developer
    m["unattended_install"] = info.UnattendedInstall
    m["unattended_uninstall"] = info.UnattendedUninstall
    m["installer"] = info.Installer
    m["supported_architectures"] = info.SupportedArch
    m["product_code"] = info.ProductCode
    m["upgrade_code"] = info.UpgradeCode

    if info.Uninstaller != nil {
        m["uninstaller"] = info.Uninstaller
    }
}

func createPkgsInfo(
    filePath string,
    outputDir string,
    name string,
    version string,
    catalogs []string,
    category string,
    developer string,
    supportedArch []string,
    repoPath string,
    installerSubPath string,
    productCode string,
    upgradeCode string,
    fileHash string,
    unattendedInstall bool,
    unattendedUninstall bool,
    preinstallScript string,
    postinstallScript string,
    preuninstallScript string,
    postuninstallScript string,
    installCheckScript string,
    uninstallCheckScript string,
    uninstaller *Installer,
) error {
    installerLocation := filepath.Join("/", installerSubPath, fmt.Sprintf("%s-%s%s", name, version, filepath.Ext(filePath)))

    pkgsInfo := PkgsInfo{
        Name:                name,
        Version:             version,
        Installer:           &Installer{Location: installerLocation, Hash: fileHash, Type: filepath.Ext(filePath)[1:]},
        Uninstaller:         uninstaller,
        Catalogs:            catalogs,
        Category:            category,
        Developer:           developer,
        SupportedArch:       supportedArch,
        ProductCode:         strings.TrimSpace(productCode),
        UpgradeCode:         strings.TrimSpace(upgradeCode),
        UnattendedInstall:   unattendedInstall,
        UnattendedUninstall: unattendedUninstall,
        PreinstallScript:    preinstallScript,
        PostinstallScript:   postinstallScript,
        PreuninstallScript:  preuninstallScript,
        PostuninstallScript: postuninstallScript,
        InstallCheckScript:  installCheckScript,
        UninstallCheckScript: uninstallCheckScript,
    }

    outputPath := filepath.Join(outputDir, fmt.Sprintf("%s-%s.yaml", name, version))
    pkgsInfoContent, err := encodeWithSelectiveBlockScalars(pkgsInfo)
    if err != nil {
        return fmt.Errorf("failed to encode pkgsinfo: %v", err)
    }

    if err := os.WriteFile(outputPath, pkgsInfoContent, 0644); err != nil {
        return fmt.Errorf("failed to write pkgsinfo to file: %v", err)
    }

    return nil
}

func findMatchingItemInAllCatalog(repoPath, productCode, upgradeCode, currentFileHash string) (*PkgsInfo, bool, error) {
    allCatalogPath := filepath.Join(repoPath, "catalogs", "All.yaml")
    fileContent, err := os.ReadFile(allCatalogPath)
    if err != nil {
        return nil, false, fmt.Errorf("failed to read All.yaml: %v", err)
    }

    var allPackages []PkgsInfo
    if err := yaml.Unmarshal(fileContent, &allPackages); err != nil {
        return nil, false, fmt.Errorf("failed to unmarshal All.yaml: %v", err)
    }

    cleanedProductCode := strings.TrimSpace(strings.ToLower(productCode))
    cleanedUpgradeCode := strings.TrimSpace(strings.ToLower(upgradeCode))

    for _, item := range allPackages {
        if strings.TrimSpace(strings.ToLower(item.ProductCode)) == cleanedProductCode &&
            strings.TrimSpace(strings.ToLower(item.UpgradeCode)) == cleanedUpgradeCode {
            return &item, item.Installer != nil && item.Installer.Hash == currentFileHash, nil
        }
    }
    return nil, false, nil
}

func findMatchingItemInAllCatalogWithDifferentVersion(repoPath, name, version string) (*PkgsInfo, error) {
    allCatalogPath := filepath.Join(repoPath, "catalogs", "All.yaml")
    fileContent, err := os.ReadFile(allCatalogPath)
    if err != nil {
        return nil, fmt.Errorf("failed to read All.yaml: %v", err)
    }

    var allPackages []PkgsInfo
    if err := yaml.Unmarshal(fileContent, &allPackages); err != nil {
        return nil, fmt.Errorf("failed to unmarshal All.yaml: %v", err)
    }

    cleanName := strings.ToLower(strings.TrimSpace(name))
    cleanVersion := strings.ToLower(strings.TrimSpace(version))

    for _, item := range allPackages {
        if strings.ToLower(strings.TrimSpace(item.Name)) == cleanName &&
            strings.ToLower(strings.TrimSpace(item.Version)) != cleanVersion {
            return &item, nil
        }
    }
    return nil, nil
}

func processScript(scriptPath, wrapperType string) (string, error) {
    if scriptPath == "" {
        return "", nil
    }

    content, err := os.ReadFile(scriptPath)
    if err != nil {
        return "", fmt.Errorf("error reading script file: %v", err)
    }

    scriptContent := strings.ReplaceAll(string(content), "\r\n", "\n")

    if wrapperType == ".bat" {
        return generateWrapperScript(scriptContent, "bat"), nil
    } else if wrapperType == ".ps1" {
        return scriptContent, nil
    }
    return scriptContent, nil
}

func processUninstaller(uninstallerPath, pkgsFolderPath, installerSubPath string) (*Installer, error) {
    if uninstallerPath == "" {
        return nil, nil
    }

    if _, err := os.Stat(uninstallerPath); os.IsNotExist(err) {
        return nil, fmt.Errorf("uninstaller '%s' does not exist", uninstallerPath)
    }

    uninstallerHash, err := calculateSHA256(uninstallerPath)
    if err != nil {
        return nil, fmt.Errorf("error calculating uninstaller hash: %v", err)
    }

    uninstallerFilename := filepath.Base(uninstallerPath)
    uninstallerDest := filepath.Join(pkgsFolderPath, uninstallerFilename)

    if _, err := copyFile(uninstallerPath, uninstallerDest); err != nil {
        return nil, fmt.Errorf("failed to copy uninstaller: %v", err)
    }

    return &Installer{
        Location: filepath.Join("/", installerSubPath, uninstallerFilename),
        Hash:     uninstallerHash,
        Type:     strings.TrimPrefix(filepath.Ext(uninstallerPath), "."),
    }, nil
}

func getInstallerPath(installerFlag string) string {
    if installerFlag != "" {
        return installerFlag
    }

    if flag.NArg() > 0 {
        return flag.Arg(0)
    }

    fmt.Print("Enter the path to the installer file: ")
    var path string
    fmt.Scanln(&path)
    return path
}

func generatePkgsInfo(config config.Configuration, installerSubPath string, info PkgsInfo) error {
    outputDir := filepath.Join(config.RepoPath, "pkgsinfo", installerSubPath)
    if err := os.MkdirAll(outputDir, 0755); err != nil {
        return fmt.Errorf("failed to create output directory: %v", err)
    }

    outputFile := filepath.Join(outputDir, fmt.Sprintf("%s-%s.yaml", info.Name, info.Version))
    pkgsInfoContent, err := encodeWithSelectiveBlockScalars(info)
    if err != nil {
        return fmt.Errorf("failed to encode pkgsinfo: %v", err)
    }

    return os.WriteFile(outputFile, pkgsInfoContent, 0644)
}

func gorillaImport(
    packagePath string,
    conf config.Configuration,
    installScriptPath, preuninstallScriptPath, postuninstallScriptPath string,
    postinstallScriptPath, uninstallerPath, installCheckScriptPath, uninstallCheckScriptPath string,
) (bool, error) {
    if _, err := os.Stat(packagePath); os.IsNotExist(err) {
        return false, fmt.Errorf("package '%s' does not exist", packagePath)
    }

    fmt.Printf("Processing package: %s\n", packagePath)

    // Extract metadata
    metadata, err := extractInstallerMetadata(packagePath)
    if err != nil {
        return false, fmt.Errorf("metadata extraction failed: %v", err)
    }

    // Process scripts
    preinstallScript, _ := processScript(installScriptPath, filepath.Ext(installScriptPath))
    postinstallScript, _ := processScript(postinstallScriptPath, filepath.Ext(postinstallScriptPath))
    preuninstallScript, _ := processScript(preuninstallScriptPath, filepath.Ext(preuninstallScriptPath))
    postuninstallScript, _ := processScript(postuninstallScriptPath, filepath.Ext(postuninstallScriptPath))
    installCheckScript, _ := processScript(installCheckScriptPath, filepath.Ext(installCheckScriptPath))
    uninstallCheckScript, _ := processScript(uninstallCheckScriptPath, filepath.Ext(uninstallCheckScriptPath))

    // Process uninstaller
    uninstaller, err := processUninstaller(uninstallerPath, filepath.Join(conf.RepoPath, "pkgs"), "apps")
    if err != nil {
        return false, fmt.Errorf("uninstaller processing failed: %v", err)
    }

    // Determine installer type
    installerType := strings.TrimPrefix(strings.ToLower(filepath.Ext(packagePath)), ".")

    // Calculate installer hash
    fileHash, err := calculateSHA256(packagePath)
    if err != nil {
        return false, fmt.Errorf("failed to calculate file hash: %v", err)
    }

    // Copy installer to pkgs directory
    installerFilename := filepath.Base(packagePath)
    pkgsFolderPath := filepath.Join(conf.RepoPath, "pkgs", "apps")
    os.MkdirAll(pkgsFolderPath, 0755)
    installerDest := filepath.Join(pkgsFolderPath, installerFilename)
    if _, err := copyFile(packagePath, installerDest); err != nil {
        return false, fmt.Errorf("failed to copy installer: %v", err)
    }

    // Create PkgsInfo struct with extracted metadata
    pkgsInfo := PkgsInfo{
        Name:                metadata.ID,
        DisplayName:         metadata.Title,
        Version:             metadata.Version,
        Developer:           metadata.Authors,
        Description:         metadata.Description,
        Catalogs:            []string{conf.DefaultCatalog},
        SupportedArch:       []string{conf.DefaultArch},
        Installer: &Installer{
            Location:  filepath.Join("/", "apps", installerFilename),
            Hash:      fileHash,
            Type:      installerType,
            Arguments: []string{}, // Add arguments if needed
        },
        Uninstaller:          uninstaller,
        PreinstallScript:     preinstallScript,
        PostinstallScript:    postinstallScript,
        PreuninstallScript:   preuninstallScript,
        PostuninstallScript:  postuninstallScript,
        InstallCheckScript:   installCheckScript,
        UninstallCheckScript: uninstallCheckScript,
        UnattendedInstall:    true,
        UnattendedUninstall:  true,
        ProductCode:          metadata.ProductCode,
        UpgradeCode:          metadata.UpgradeCode,
    }

    // Generate pkgsinfo
    if err := generatePkgsInfo(conf, "apps", pkgsInfo); err != nil {
        return false, fmt.Errorf("failed to generate pkgsinfo: %v", err)
    }

    fmt.Printf("Pkgsinfo created at: /apps/%s-%s.yaml\n", metadata.ID, metadata.Version)
    return true, nil
}

func generateWrapperScript(batchContent, scriptType string) string {
    if scriptType == "bat" {
        return fmt.Sprintf(`
$batchScriptContent = @'
%s
'@

$batchFile = "$env:TEMP\\temp_script.bat"
Set-Content -Path $batchFile -Value $batchScriptContent -Encoding ASCII
& cmd.exe /c $batchFile
Remove-Item $batchFile
        `, strings.TrimLeft(batchContent, " "))
    } else if scriptType == "ps1" {
        return batchContent
    }
    return ""
}

func promptForMetadata(packagePath string) (Metadata, error) {
    var metadata Metadata

    defaultName := strings.TrimSuffix(filepath.Base(packagePath), filepath.Ext(packagePath))

    promptSurvey(&metadata.Title, "Enter the display name", defaultName)
    promptSurvey(&metadata.ID, "Enter the package name (unique identifier)", defaultName)
    promptSurvey(&metadata.Version, "Enter the version", "1.0.0")
    promptSurvey(&metadata.Authors, "Enter the developer/author", "")
    promptSurvey(&metadata.Description, "Enter the description", "")

    return metadata, nil
}

func promptSurvey(value *string, prompt string, defaultValue string) {
    survey.AskOne(&survey.Input{
        Message: prompt,
        Default: cleanTextForPrompt(defaultValue),
    }, value)
}

func getInputWithDefault(prompt, defaultValue string) string {
    fmt.Printf("%s [%s]: ", prompt, cleanTextForPrompt(defaultValue))
    var input string
    fmt.Scanln(&input)
    if input == "" {
        return defaultValue
    }
    return input
}

func cleanTextForPrompt(input string) string {
    return strings.TrimSpace(input)
}

func confirmAction(prompt string) bool {
    fmt.Printf("%s (y/n): ", prompt)
    var response string
    fmt.Scanln(&response)
    return strings.ToLower(response) == "y"
}

func uploadToCloud(conf config.Configuration) error {
    localPkgsPath := filepath.Join(conf.RepoPath, "pkgs")

    if conf.CloudProvider == "aws" {
        cmd := exec.Command("/usr/local/bin/aws", "s3", "sync", localPkgsPath,
            fmt.Sprintf("s3://%s/pkgs/", conf.CloudBucket),
            "--exclude", "*.git/*", "--exclude", "**/.DS_Store")

        cmd.Stdout = os.Stdout
        cmd.Stderr = os.Stderr
        if err := cmd.Run(); err != nil {
            return fmt.Errorf("error syncing to S3: %v", err)
        }
    } else if conf.CloudProvider == "azure" {
        cmd := exec.Command("/opt/homebrew/bin/azcopy", "sync", localPkgsPath,
            fmt.Sprintf("https://%s/pkgs/", conf.CloudBucket),
            "--exclude-path", "*/.git/*;*/.DS_Store", "--recursive", "--put-md5")

        cmd.Stdout = os.Stdout
        cmd.Stderr = os.Stderr
        if err := cmd.Run(); err != nil {
            return fmt.Errorf("error syncing to Azure: %v", err)
        }
    }
    return nil
}

func runMakeCatalogs() error {
    var makeCatalogsBinary string

    switch runtime.GOOS {
    case "windows":
        makeCatalogsBinary = `C:\Program Files\Gorilla\bin\makecatalogs`
    case "darwin":
        makeCatalogsBinary = `/usr/local/gorilla/makecatalogs`
    default:
        return fmt.Errorf("unsupported platform: %s", runtime.GOOS)
    }

    if _, err := os.Stat(makeCatalogsBinary); os.IsNotExist(err) {
        return fmt.Errorf("makecatalogs binary not found at %s", makeCatalogsBinary)
    }

    cmd := exec.Command(makeCatalogsBinary)
    cmd.Stdout = os.Stdout
    cmd.Stderr = os.Stderr

    fmt.Printf("Running makecatalogs from: %s\n", makeCatalogsBinary)
    if err := cmd.Run(); err != nil {
        return fmt.Errorf("makecatalogs execution failed: %v", err)
    }

    fmt.Println("makecatalogs completed successfully.")
    return nil
}
