using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cimian.CLI.Cimipkg.Models;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Cimipkg.Services;

/// <summary>
/// Main package builder that orchestrates .pkg and .nupkg package creation.
/// </summary>
public class PackageBuilder
{
    private readonly ILogger<PackageBuilder> _logger;
    private readonly ScriptProcessor _scriptProcessor;
    private readonly ChocolateyGenerator _chocolateyGenerator;
    private readonly CodeSigner _codeSigner;
    private readonly ZipArchiveHelper _zipHelper;

    private readonly IDeserializer _yamlDeserializer;
    private readonly ISerializer _yamlSerializer;

    public PackageBuilder(
        ILogger<PackageBuilder> logger,
        ScriptProcessor scriptProcessor,
        ChocolateyGenerator chocolateyGenerator,
        CodeSigner codeSigner,
        ZipArchiveHelper zipHelper)
    {
        _logger = logger;
        _scriptProcessor = scriptProcessor;
        _chocolateyGenerator = chocolateyGenerator;
        _codeSigner = codeSigner;
        _zipHelper = zipHelper;

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    /// <summary>
    /// Builds a package from the specified project directory.
    /// </summary>
    /// <param name="projectDir">Path to the project directory.</param>
    /// <param name="options">Build options.</param>
    /// <returns>Path to the created package file.</returns>
    public string Build(string projectDir, PackageBuildOptions options)
    {
        projectDir = Path.GetFullPath(projectDir);
        _logger.LogInformation("Using project directory: {ProjectDir}", projectDir);

        // Verify project structure
        VerifyProjectStructure(projectDir);
        _logger.LogInformation("Project structure verified. Proceeding with package creation...");

        // Clean build directory
        CleanBuildDirectory(projectDir);
        _logger.LogInformation("Build directory cleaned successfully.");

        // Read build-info.yaml
        var buildInfo = ReadBuildInfo(projectDir);

        // Process dynamic version placeholders (${TIMESTAMP}, ${DATE}, ${DATETIME})
        buildInfo.DoSubstitutions();

        // Load environment variables
        var envVars = LoadEnvironmentVariables(projectDir, options.EnvFilePath);
        if (envVars.Count > 0)
        {
            _logger.LogInformation("Loaded {Count} environment variables for script injection", envVars.Count);
        }

        // Check payload
        var payloadDir = Path.Combine(projectDir, "payload");
        var hasPayloadFiles = PayloadDirectoryHasFiles(payloadDir);

        // Determine if this is an installer package
        var isInstallerPackage = buildInfo.IsInstallerPackage || !hasPayloadFiles;

        // Validate configuration
        if (hasPayloadFiles && !isInstallerPackage && string.IsNullOrEmpty(buildInfo.InstallLocation))
        {
            throw new InvalidOperationException(
                "install_location must be specified when payload exists and the package is not an installer.");
        }

        // Parse and normalize version
        var packageFormat = options.BuildNupkg ? "nupkg" : "pkg";
        var versionResult = VersionParser.Parse(buildInfo.Product.Version, packageFormat);

        _logger.LogInformation("Version from timestamp: {Original}", versionResult.OriginalVersion);
        _logger.LogDebug("Normalized version for package format: {Normalized}", versionResult.NormalizedVersion);

        // Update buildInfo with normalized version for .nuspec compatibility
        buildInfo.Product.Version = versionResult.NormalizedVersion;

        // Build the appropriate package format
        string packagePath;
        if (options.BuildNupkg)
        {
            _logger.LogInformation("Building .nupkg format (Chocolatey compatible)");
            packagePath = BuildNupkgPackage(buildInfo, projectDir, versionResult.OriginalVersion,
                isInstallerPackage, hasPayloadFiles, envVars);

            // Build .intunewin if requested
            if (options.BuildIntunewin)
            {
                _logger.LogInformation("Building .intunewin package...");
                BuildIntuneWin(packagePath);
            }
        }
        else
        {
            _logger.LogInformation("Building .pkg format (sbin-installer compatible)");
            packagePath = BuildPkgPackage(buildInfo, projectDir, versionResult.OriginalVersion, envVars);

            if (options.BuildIntunewin)
            {
                _logger.LogWarning("--intunewin flag is only supported with --nupkg format. Ignoring.");
            }
        }

        _logger.LogInformation("Package created successfully: {PackagePath}", packagePath);
        _logger.LogInformation("Done.");
        return packagePath;
    }

    /// <summary>
    /// Re-signs an existing .pkg package without recompressing.
    /// </summary>
    /// <param name="pkgPath">Path to the .pkg file.</param>
    /// <param name="certName">Certificate name (optional).</param>
    /// <param name="certThumbprint">Certificate thumbprint (optional).</param>
    public void ResignPackage(string pkgPath, string? certName, string? certThumbprint)
    {
        pkgPath = Path.GetFullPath(pkgPath);
        _logger.LogInformation("Re-signing package: {PkgPath}", pkgPath);

        if (!File.Exists(pkgPath))
        {
            throw new FileNotFoundException("Package not found.", pkgPath);
        }

        // Create temp directory for build-info.yaml
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimipkg_resign_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Extract only build-info.yaml
            var buildInfoPath = Path.Combine(tempDir, "build-info.yaml");
            if (!_zipHelper.ExtractFile(pkgPath, "build-info.yaml", buildInfoPath))
            {
                throw new InvalidOperationException("build-info.yaml not found in package.");
            }

            // Read and update build-info
            var buildInfoContent = File.ReadAllText(buildInfoPath);
            var buildInfo = _yamlDeserializer.Deserialize<BuildInfo>(buildInfoContent);

            // Set certificate if not already set
            if (string.IsNullOrEmpty(buildInfo.SigningCertificate))
            {
                if (string.IsNullOrEmpty(certName))
                {
                    throw new InvalidOperationException(
                        "No signing certificate specified in build-info.yaml or command line.");
                }
                buildInfo.SigningCertificate = certName;
            }

            if (!string.IsNullOrEmpty(certThumbprint))
            {
                buildInfo.SigningThumbprint = certThumbprint;
            }

            // Calculate signature from existing ZIP contents
            _logger.LogInformation("Calculating package signature...");
            var contentHash = _zipHelper.CalculateContentHash(pkgPath);
            var certInfo = _codeSigner.GetCertificateInfo(buildInfo.SigningCertificate, buildInfo.SigningThumbprint);

            if (certInfo == null)
            {
                throw new InvalidOperationException("Signing certificate not found.");
            }

            buildInfo.Signature = new PackageSignature
            {
                Algorithm = "SHA256",
                Certificate = certInfo,
                PackageHash = contentHash,
                ContentHash = contentHash,
                SignedHash = ComputeSignedHash(contentHash, certInfo.Thumbprint),
                Timestamp = DateTime.UtcNow.ToString("O"),
                Version = "1.0"
            };

            // Write updated build-info.yaml
            var updatedContent = _yamlSerializer.Serialize(buildInfo);
            File.WriteAllText(buildInfoPath, updatedContent);

            // Update build-info.yaml in the ZIP
            _zipHelper.UpdateFile(pkgPath, "build-info.yaml", buildInfoPath);

            _logger.LogInformation("Package re-signed successfully");
            _logger.LogInformation("Certificate: {Subject}", certInfo.Subject);
            _logger.LogInformation("Thumbprint: {Thumbprint}", certInfo.Thumbprint);
        }
        finally
        {
            // Clean up temp directory
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Creates a new project structure at the specified path.
    /// </summary>
    /// <param name="projectPath">Path to create the project at.</param>
    public void CreateNewProject(string projectPath)
    {
        projectPath = Path.GetFullPath(projectPath);
        _logger.LogInformation("Creating new project at: {ProjectPath}", projectPath);

        // Create directories
        Directory.CreateDirectory(projectPath);
        Directory.CreateDirectory(Path.Combine(projectPath, "payload"));
        Directory.CreateDirectory(Path.Combine(projectPath, "scripts"));

        // Create build-info.yaml template
        var buildInfoTemplate = @"
product:
  name: NuPkgProjectName
  version: 1.0.0
  developer: ACME Corp
  identifier: com.company.projectname
postinstall_action: none
signing_certificate: 
install_location: C:\
";
        File.WriteAllText(Path.Combine(projectPath, "build-info.yaml"), buildInfoTemplate.TrimStart());

        // Create .env template
        var envTemplate = @"# Cimian Environment Variables
# Configure your actual values for package building
# This file contains environment variables that will be injected into scripts

# Basic Authentication (if using CimianAuth)
# CimianAuthHeader='your_base64_encoded_auth_header'
# CimianManifestApiKey='your_api_key'

# Domain Rename Service Account (if using device rename functionality)
# CimianRenameUser='DOMAIN\service_account'
# CimianRenamePass='service_account_password'

# Custom environment variables for your scripts
# CUSTOM_VAR='custom_value'
";
        File.WriteAllText(Path.Combine(projectPath, ".env"), envTemplate);

        _logger.LogInformation("Project created successfully");
    }

    /// <summary>
    /// Builds a .pkg package (ZIP archive with sbin-installer structure).
    /// </summary>
    private string BuildPkgPackage(BuildInfo buildInfo, string projectDir, string filenameVersion,
        IDictionary<string, string> envVars)
    {
        var buildDir = Path.Combine(projectDir, "build");
        var pkgName = $"{buildInfo.Product.Name}-{filenameVersion}.pkg";
        var pkgPath = Path.Combine(buildDir, pkgName);

        _logger.LogInformation("Creating .pkg package: {PkgName}", pkgName);

        // Create temp directory for package structure
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimipkg_pkg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Copy payload directory if it exists
            var payloadSrc = Path.Combine(projectDir, "payload");
            if (Directory.Exists(payloadSrc))
            {
                var payloadDst = Path.Combine(tempDir, "payload");
                CopyDirectory(payloadSrc, payloadDst);
                _logger.LogInformation("Copied payload directory to temp package structure");
            }

            // Copy and process scripts directory
            var scriptsSrc = Path.Combine(projectDir, "scripts");
            if (Directory.Exists(scriptsSrc))
            {
                var scriptsDst = Path.Combine(tempDir, "scripts");
                _scriptProcessor.ProcessScriptsDirectory(scriptsSrc, scriptsDst, envVars, injectHeaders: true);
                _logger.LogInformation("Copied and processed scripts directory to temp package structure");

                // Sign PowerShell scripts if certificate specified
                if (!string.IsNullOrEmpty(buildInfo.SigningCertificate) ||
                    !string.IsNullOrEmpty(buildInfo.SigningThumbprint))
                {
                    _logger.LogInformation("Signing PowerShell scripts...");
                    _codeSigner.SignPowerShellScriptsInDirectory(
                        scriptsDst, buildInfo.SigningCertificate, buildInfo.SigningThumbprint);
                }
            }

            // Prepare build-info.yaml with signature
            // Use the already-substituted buildInfo object (which has ${TIMESTAMP} etc resolved)
            // instead of re-reading from disk which would have the unsubstituted values
            var buildInfoForSigning = buildInfo;

            // Create package signature if certificate provided
            if (!string.IsNullOrEmpty(buildInfo.SigningCertificate) ||
                !string.IsNullOrEmpty(buildInfo.SigningThumbprint))
            {
                var signature = _codeSigner.CreatePackageSignature(
                    tempDir, buildInfo.SigningCertificate, buildInfo.SigningThumbprint);

                if (signature != null)
                {
                    buildInfoForSigning.Signature = signature;
                    _logger.LogInformation("Package signature metadata embedded");
                }
            }
            else
            {
                _logger.LogInformation("No signing certificate specified - package will be created without signature metadata");
            }

            // Write updated build-info.yaml
            var buildInfoDst = Path.Combine(tempDir, "build-info.yaml");
            var updatedBuildInfo = _yamlSerializer.Serialize(buildInfoForSigning);
            File.WriteAllText(buildInfoDst, updatedBuildInfo);
            _logger.LogInformation("Copied build-info.yaml to temp package structure with signature metadata");

            // Create the .pkg archive
            _zipHelper.CreateArchive(tempDir, pkgPath);

            // Sign the package if certificate specified
            if (!string.IsNullOrEmpty(buildInfo.SigningCertificate))
            {
                _codeSigner.SignNuGetPackage(pkgPath, buildInfo.SigningCertificate);
            }
            else
            {
                _logger.LogInformation("No signing certificate provided. Skipping signing.");
            }

            _logger.LogInformation(".pkg package created successfully: {PkgPath}", pkgPath);
            return pkgPath;
        }
        finally
        {
            // Clean up temp directory
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Builds a .nupkg package (Chocolatey compatible).
    /// </summary>
    private string BuildNupkgPackage(BuildInfo buildInfo, string projectDir, string filenameVersion,
        bool isInstallerPackage, bool hasPayloadFiles, IDictionary<string, string> envVars)
    {
        _logger.LogInformation("Creating .nupkg package (legacy Chocolatey format)");

        // Create tools directory
        var toolsDir = Path.Combine(projectDir, "tools");
        Directory.CreateDirectory(toolsDir);

        try
        {
            // Copy payload to tools/payload if it exists
            var payloadSrc = Path.Combine(projectDir, "payload");
            if (hasPayloadFiles && Directory.Exists(payloadSrc))
            {
                var payloadDst = Path.Combine(toolsDir, "payload");
                CopyDirectory(payloadSrc, payloadDst);
                _logger.LogDebug("Copied payload to tools directory");
            }

            // Generate chocolateyInstall.ps1
            _chocolateyGenerator.CreateChocolateyInstallScript(
                buildInfo, projectDir, toolsDir, isInstallerPackage, hasPayloadFiles, envVars);

            // Generate chocolateyUninstall.ps1
            _chocolateyGenerator.CreateChocolateyUninstallScript(
                buildInfo, projectDir, toolsDir, isInstallerPackage, envVars);

            // Sign PowerShell scripts if certificate specified
            if (!string.IsNullOrEmpty(buildInfo.SigningCertificate) ||
                !string.IsNullOrEmpty(buildInfo.SigningThumbprint))
            {
                _codeSigner.SignPowerShellScriptsInDirectory(
                    toolsDir, buildInfo.SigningCertificate, buildInfo.SigningThumbprint);
            }

            // Generate .nuspec
            var nuspecPath = _chocolateyGenerator.GenerateNuspec(buildInfo, projectDir);

            // Check for nuget
            CheckNuGet();

            // Build package
            var buildDir = Path.Combine(projectDir, "build");
            Directory.CreateDirectory(buildDir);

            RunCommand("nuget", $"pack \"{nuspecPath}\" -OutputDirectory \"{buildDir}\" -NoPackageAnalysis -NoDefaultExcludes");

            // Find and rename the generated package
            var searchPattern = $"{buildInfo.Product.Identifier}*.nupkg";
            var generatedPackages = Directory.GetFiles(buildDir, searchPattern);

            var finalPkgName = $"{buildInfo.Product.Name}-{filenameVersion}.nupkg";
            var finalPkgPath = Path.Combine(buildDir, finalPkgName);

            if (generatedPackages.Length > 0)
            {
                File.Move(generatedPackages[0], finalPkgPath, overwrite: true);
            }

            // Sign package if certificate specified
            if (!string.IsNullOrEmpty(buildInfo.SigningCertificate))
            {
                _codeSigner.SignNuGetPackage(finalPkgPath, buildInfo.SigningCertificate);
            }

            // Clean up generated files
            File.Delete(nuspecPath);

            return finalPkgPath;
        }
        finally
        {
            // Remove tools directory
            try
            {
                Directory.Delete(toolsDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Builds a .intunewin package from a .nupkg.
    /// </summary>
    private void BuildIntuneWin(string nupkgPath)
    {
        // Check for IntuneWinAppUtil.exe
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "IntuneWinAppUtil.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();

            if (process?.ExitCode != 0)
            {
                _logger.LogWarning("IntuneWinAppUtil.exe not found in PATH. Skipping .intunewin generation.");
                return;
            }
        }
        catch
        {
            _logger.LogWarning("Could not locate IntuneWinAppUtil.exe. Skipping .intunewin generation.");
            return;
        }

        // Create temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"intunewin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Copy nupkg to temp
            var nupkgName = Path.GetFileName(nupkgPath);
            var destNupkg = Path.Combine(tempDir, nupkgName);
            File.Copy(nupkgPath, destNupkg);

            // Create Install.ps1
            var installPs1 = CreateIntuneWinInstallScript(nupkgName);
            var installPath = Path.Combine(tempDir, "Install.ps1");
            File.WriteAllText(installPath, installPs1);

            // Run IntuneWinAppUtil
            var outDir = Path.GetDirectoryName(nupkgPath) ?? ".";
            RunCommand("IntuneWinAppUtil.exe",
                $"-c \"{tempDir}\" -s \"Install.ps1\" -o \"{outDir}\"");

            // Rename output
            var baseName = Path.GetFileNameWithoutExtension(nupkgName);
            var defaultIntunewin = Path.Combine(outDir, "Install.intunewin");
            var finalIntunewin = Path.Combine(outDir, $"{baseName}.intunewin");

            if (File.Exists(defaultIntunewin))
            {
                File.Move(defaultIntunewin, finalIntunewin, overwrite: true);
                _logger.LogInformation("Created .intunewin: {Path}", finalIntunewin);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore
            }
        }
    }

    private string CreateIntuneWinInstallScript(string nupkgName)
    {
        return $@"# Install.ps1 generated by cimipkg
param(
    [string]$PkgFile = "".\{nupkgName}""
)

Write-Host ""Checking for Chocolatey...""
$chocoExe = ""C:\ProgramData\chocolatey\bin\choco.exe""
if (!(Test-Path $chocoExe)) {{
    Write-Host ""Chocolatey not found. Installing...""
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ""Set-ExecutionPolicy Bypass -Scope Process -Force; [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))""

    if (!(Test-Path $chocoExe)) {{
        Write-Error 'Failed to install Chocolatey.'
        exit 1
    }}
}}

# Extract package ID and version from nuspec
$tempExtract = Join-Path $env:TEMP (""nuspec_"" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tempExtract | Out-Null
Expand-Archive -LiteralPath $PkgFile -DestinationPath $tempExtract -Force

$nuspec = Get-ChildItem -Path $tempExtract -Recurse -Filter *.nuspec | Select-Object -First 1
if (!$nuspec) {{
    Write-Error ""No .nuspec found inside $PkgFile""
    exit 1
}}

[xml]$xml = Get-Content $nuspec.FullName
$pkgId = $xml.package.metadata.id
$pkgVersion = $xml.package.metadata.version

# Rename package for Chocolatey
$newNupkgName = ""$($pkgId).$($pkgVersion).nupkg""
Rename-Item -Path $PkgFile -NewName $newNupkgName -Force

# Clean up
Remove-Item $tempExtract -Recurse -Force

# Install/upgrade with Chocolatey
$installed = choco list --local-only --limit-output --exact $pkgId 2>$null
if ($installed -match $pkgId) {{
    choco upgrade $pkgId --version $pkgVersion --source ""."" -y --force --allowdowngrade
}} else {{
    choco install $pkgId --version $pkgVersion --source ""."" -y --force --allowdowngrade
}}

exit $LASTEXITCODE
";
    }

    private void VerifyProjectStructure(string projectDir)
    {
        if (!Directory.Exists(projectDir))
        {
            throw new DirectoryNotFoundException($"Project directory not found: {projectDir}");
        }

        var buildInfoPath = Path.Combine(projectDir, "build-info.yaml");
        if (!File.Exists(buildInfoPath))
        {
            throw new FileNotFoundException("build-info.yaml not found in project directory.", buildInfoPath);
        }
    }

    private void CleanBuildDirectory(string projectDir)
    {
        var buildDir = Path.Combine(projectDir, "build");
        if (Directory.Exists(buildDir))
        {
            Directory.Delete(buildDir, recursive: true);
        }
        Directory.CreateDirectory(buildDir);
    }

    private BuildInfo ReadBuildInfo(string projectDir)
    {
        var buildInfoPath = Path.Combine(projectDir, "build-info.yaml");
        var content = File.ReadAllText(buildInfoPath);
        var buildInfo = _yamlDeserializer.Deserialize<BuildInfo>(content);

        if (buildInfo == null)
        {
            throw new InvalidOperationException("Failed to parse build-info.yaml");
        }

        if (string.IsNullOrEmpty(buildInfo.Product?.Name))
        {
            throw new InvalidOperationException("product.name is required in build-info.yaml");
        }

        if (string.IsNullOrEmpty(buildInfo.Product?.Identifier))
        {
            throw new InvalidOperationException("product.identifier is required in build-info.yaml");
        }

        return buildInfo;
    }

    private Dictionary<string, string> LoadEnvironmentVariables(string projectDir, string? envFilePath)
    {
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Auto-detect .env file if not specified
        if (string.IsNullOrEmpty(envFilePath))
        {
            var candidatePath = Path.Combine(projectDir, ".env");
            if (File.Exists(candidatePath))
            {
                envFilePath = candidatePath;
            }
        }

        if (!string.IsNullOrEmpty(envFilePath) && File.Exists(envFilePath))
        {
            foreach (var line in File.ReadLines(envFilePath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex > 0)
                {
                    var key = trimmed[..eqIndex].Trim();
                    var value = trimmed[(eqIndex + 1)..].Trim().Trim('\'', '"');
                    envVars[key] = value;
                }
            }

            _logger.LogDebug("Loaded {Count} variables from {EnvFile}", envVars.Count, envFilePath);
        }

        return envVars;
    }

    private bool PayloadDirectoryHasFiles(string payloadDir)
    {
        if (!Directory.Exists(payloadDir))
        {
            return false;
        }

        return Directory.EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories).Any();
    }

    private void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    private void CheckNuGet()
    {
        try
        {
            RunCommand("nuget", "help");
        }
        catch
        {
            throw new InvalidOperationException(
                "NuGet is not installed or not in PATH. Please install NuGet CLI.");
        }
    }

    private void RunCommand(string command, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start process: {command}");
        }

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"Command failed with exit code {process.ExitCode}: {error}");
        }
    }

    private static string ComputeSignedHash(string contentHash, string thumbprint)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(contentHash + thumbprint));
        return Convert.ToBase64String(bytes);
    }
}

/// <summary>
/// Options for package building.
/// </summary>
public record PackageBuildOptions
{
    /// <summary>
    /// Build legacy .nupkg format (default is .pkg).
    /// </summary>
    public bool BuildNupkg { get; init; }

    /// <summary>
    /// Also generate .intunewin from .nupkg (only with BuildNupkg).
    /// </summary>
    public bool BuildIntunewin { get; init; }

    /// <summary>
    /// Path to .env file for environment variables.
    /// </summary>
    public string? EnvFilePath { get; init; }

    /// <summary>
    /// Enable verbose logging.
    /// </summary>
    public bool Verbose { get; init; }
}
