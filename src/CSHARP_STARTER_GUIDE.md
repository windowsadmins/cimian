# Cimian C# Migration Starter Guide

This document provides the immediate next steps to begin the C# migration with a concrete project structure and initial implementations.

## Immediate Action Plan

### Step 1: Create Solution Structure
Create the following directory structure in the `csharp` branch:

```
src/
├── Cimian.sln                           # Main solution file
├── Directory.Build.props                # Common MSBuild properties
├── Directory.Packages.props             # Central package management
├── Cimian.Core/
│   ├── Cimian.Core.csproj
│   ├── Models/
│   │   ├── CatalogItem.cs
│   │   ├── Manifest.cs
│   │   ├── Configuration.cs
│   │   ├── ConditionalItem.cs
│   │   └── SystemFacts.cs
│   ├── Configuration/
│   │   ├── IConfigurationService.cs
│   │   └── ConfigurationService.cs
│   ├── Logging/
│   │   ├── ILoggingService.cs
│   │   └── LoggingService.cs
│   └── Services/
│       ├── ISystemFactsCollector.cs
│       └── SystemFactsCollector.cs
├── Cimian.Engine/
│   ├── Cimian.Engine.csproj
│   ├── Predicates/
│   │   ├── IPredicateEngine.cs
│   │   ├── PredicateEngine.cs
│   │   ├── ExpressionParser.cs
│   │   └── Models/
│   ├── Manifest/
│   │   ├── IManifestProcessor.cs
│   │   └── ManifestProcessor.cs
│   └── Package/
│       ├── IPackageManager.cs
│       └── PackageManager.cs
├── Cimian.Infrastructure/
│   ├── Cimian.Infrastructure.csproj
│   ├── Download/
│   │   ├── IDownloadService.cs
│   │   └── DownloadService.cs
│   ├── Installers/
│   │   ├── IInstaller.cs
│   │   ├── InstallerFactory.cs
│   │   ├── MsiInstaller.cs
│   │   ├── ExeInstaller.cs
│   │   └── PowerShellInstaller.cs
│   └── Process/
│       ├── IProcessManager.cs
│       └── WindowsProcessManager.cs
└── Cimian.CLI.managedsoftwareupdate/
    ├── Cimian.CLI.managedsoftwareupdate.csproj
    ├── Program.cs
    ├── Commands/
    │   ├── AutoCommand.cs
    │   ├── CheckOnlyCommand.cs
    │   └── InstallOnlyCommand.cs
    └── Services/
        ├── IUpdateService.cs
        └── UpdateService.cs
```

### Step 2: Create Solution File

Create `src/Cimian.sln`:
```xml
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1

Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Cimian.Core", "Cimian.Core\Cimian.Core.csproj", "{12345678-1234-1234-1234-123456789012}"
EndProject

Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Cimian.Engine", "Cimian.Engine\Cimian.Engine.csproj", "{12345678-1234-1234-1234-123456789013}"
EndProject

Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Cimian.Infrastructure", "Cimian.Infrastructure\Cimian.Infrastructure.csproj", "{12345678-1234-1234-1234-123456789014}"
EndProject

Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Cimian.CLI.managedsoftwareupdate", "Cimian.CLI.managedsoftwareupdate\Cimian.CLI.managedsoftwareupdate.csproj", "{12345678-1234-1234-1234-123456789015}"
EndProject

Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Debug|x64 = Debug|x64
		Debug|ARM64 = Debug|ARM64
		Release|Any CPU = Release|Any CPU
		Release|x64 = Release|x64
		Release|ARM64 = Release|ARM64
	EndGlobalSection
	
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{12345678-1234-1234-1234-123456789012}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{12345678-1234-1234-1234-123456789012}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{12345678-1234-1234-1234-123456789012}.Debug|x64.ActiveCfg = Debug|x64
		{12345678-1234-1234-1234-123456789012}.Debug|x64.Build.0 = Debug|x64
		{12345678-1234-1234-1234-123456789012}.Debug|ARM64.ActiveCfg = Debug|ARM64
		{12345678-1234-1234-1234-123456789012}.Debug|ARM64.Build.0 = Debug|ARM64
		{12345678-1234-1234-1234-123456789012}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{12345678-1234-1234-1234-123456789012}.Release|Any CPU.Build.0 = Release|Any CPU
		{12345678-1234-1234-1234-123456789012}.Release|x64.ActiveCfg = Release|x64
		{12345678-1234-1234-1234-123456789012}.Release|x64.Build.0 = Release|x64
		{12345678-1234-1234-1234-123456789012}.Release|ARM64.ActiveCfg = Release|ARM64
		{12345678-1234-1234-1234-123456789012}.Release|ARM64.Build.0 = Release|ARM64
	EndGlobalSection
EndGlobal
```

### Step 3: Create Directory.Build.props

Create `src/Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <UseWindowsForms>false</UseWindowsForms>
    <UseWPF>false</UseWPF>
    <OutputType>Exe</OutputType>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>false</SelfContained>
    <PublishReadyToRun>true</PublishReadyToRun>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    
    <!-- Version Information -->
    <Version>$(RELEASE_VERSION)</Version>
    <AssemblyVersion>$(RELEASE_VERSION)</AssemblyVersion>
    <FileVersion>$(RELEASE_VERSION)</FileVersion>
    <Product>Cimian</Product>
    <Company>WindowsAdmins</Company>
    <Copyright>Copyright © WindowsAdmins</Copyright>
    
    <!-- Compiler Settings -->
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>NU1603</WarningsNotAsErrors>
    
    <!-- Output Settings -->
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  
  <PropertyGroup Condition="'$(Configuration)'=='Release'">
    <Optimize>true</Optimize>
    <DebugType>portable</DebugType>
    <DebugSymbols>true</DebugSymbols>
  </PropertyGroup>
</Project>
```

### Step 4: Create Central Package Management

Create `src/Directory.Packages.props`:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  
  <ItemGroup>
    <!-- Configuration -->
    <PackageVersion Include="Microsoft.Extensions.Configuration" Version="8.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Configuration.Binder" Version="8.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Configuration.Json" Version="8.0.0" />
    <PackageVersion Include="YamlDotNet" Version="13.7.1" />
    
    <!-- Dependency Injection -->
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
    
    <!-- Logging -->
    <PackageVersion Include="Serilog" Version="3.1.1" />
    <PackageVersion Include="Serilog.Extensions.Logging" Version="8.0.0" />
    <PackageVersion Include="Serilog.Sinks.Console" Version="5.0.1" />
    <PackageVersion Include="Serilog.Sinks.File" Version="5.0.0" />
    <PackageVersion Include="Serilog.Sinks.EventLog" Version="3.1.0" />
    
    <!-- HTTP and Networking -->
    <PackageVersion Include="Polly" Version="8.2.0" />
    <PackageVersion Include="Polly.Extensions.Http" Version="3.0.0" />
    
    <!-- Command Line -->
    <PackageVersion Include="CommandLineParser" Version="2.9.1" />
    
    <!-- Cloud Storage -->
    <PackageVersion Include="AWS.S3" Version="3.7.307.24" />
    <PackageVersion Include="Azure.Storage.Blobs" Version="12.19.1" />
    
    <!-- Windows APIs -->
    <PackageVersion Include="Microsoft.Win32.Registry" Version="5.0.0" />
    <PackageVersion Include="System.Management" Version="8.0.0" />
    <PackageVersion Include="System.ServiceProcess.ServiceController" Version="8.0.0" />
    
    <!-- Testing -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageVersion Include="xunit" Version="2.6.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.5.3" />
    <PackageVersion Include="Moq" Version="4.20.69" />
    <PackageVersion Include="FluentAssertions" Version="6.12.0" />
    <PackageVersion Include="coverlet.collector" Version="6.0.0" />
  </ItemGroup>
</Project>
```

### Step 5: Start with Core Models

Create `src/Cimian.Core/Models/CatalogItem.cs`:
```csharp
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace Cimian.Core.Models;

/// <summary>
/// Represents a software package item in a catalog
/// </summary>
public class CatalogItem
{
    [JsonPropertyName("name")]
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    [YamlMember(Alias = "display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("identifier")]
    [YamlMember(Alias = "identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [YamlMember(Alias = "description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    [YamlMember(Alias = "category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("developer")]
    [YamlMember(Alias = "developer")]
    public string Developer { get; set; } = string.Empty;

    [JsonPropertyName("catalogs")]
    [YamlMember(Alias = "catalogs")]
    public List<string> Catalogs { get; set; } = new();

    [JsonPropertyName("supported_architectures")]
    [YamlMember(Alias = "supported_architectures")]
    public List<string> SupportedArchitectures { get; set; } = new();

    [JsonPropertyName("minimum_os_version")]
    [YamlMember(Alias = "minimum_os_version")]
    public string? MinimumOsVersion { get; set; }

    [JsonPropertyName("maximum_os_version")]
    [YamlMember(Alias = "maximum_os_version")]
    public string? MaximumOsVersion { get; set; }

    [JsonPropertyName("installer")]
    [YamlMember(Alias = "installer")]
    public InstallerInfo? Installer { get; set; }

    [JsonPropertyName("uninstaller")]
    [YamlMember(Alias = "uninstaller")]
    public InstallerInfo? Uninstaller { get; set; }

    [JsonPropertyName("installs")]
    [YamlMember(Alias = "installs")]
    public List<InstallItem> Installs { get; set; } = new();

    [JsonPropertyName("uninstalls")]
    [YamlMember(Alias = "uninstalls")]
    public List<UninstallItem> Uninstalls { get; set; } = new();

    [JsonPropertyName("requires")]
    [YamlMember(Alias = "requires")]
    public List<string> Requires { get; set; } = new();

    [JsonPropertyName("update_for")]
    [YamlMember(Alias = "update_for")]
    public List<string> UpdateFor { get; set; } = new();

    [JsonPropertyName("preinstall_script")]
    [YamlMember(Alias = "preinstall_script")]
    public string? PreinstallScript { get; set; }

    [JsonPropertyName("postinstall_script")]
    [YamlMember(Alias = "postinstall_script")]
    public string? PostinstallScript { get; set; }

    [JsonPropertyName("preuninstall_script")]
    [YamlMember(Alias = "preuninstall_script")]
    public string? PreuninstallScript { get; set; }

    [JsonPropertyName("postuninstall_script")]
    [YamlMember(Alias = "postuninstall_script")]
    public string? PostuninstallScript { get; set; }

    [JsonPropertyName("installcheck_script")]
    [YamlMember(Alias = "installcheck_script")]
    public string? InstallCheckScript { get; set; }

    [JsonPropertyName("uninstallcheck_script")]
    [YamlMember(Alias = "uninstallcheck_script")]
    public string? UninstallCheckScript { get; set; }

    [JsonPropertyName("unattended_install")]
    [YamlMember(Alias = "unattended_install")]
    public bool UnattendedInstall { get; set; } = true;

    [JsonPropertyName("unattended_uninstall")]
    [YamlMember(Alias = "unattended_uninstall")]
    public bool UnattendedUninstall { get; set; } = true;

    [JsonPropertyName("uninstallable")]
    [YamlMember(Alias = "uninstallable")]
    public bool Uninstallable { get; set; } = true;

    [JsonPropertyName("OnDemand")]
    [YamlMember(Alias = "OnDemand")]
    public bool OnDemand { get; set; } = false;
}

/// <summary>
/// Installer configuration information
/// </summary>
public class InstallerInfo
{
    [JsonPropertyName("type")]
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    [YamlMember(Alias = "location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    [YamlMember(Alias = "hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    [YamlMember(Alias = "size")]
    public long Size { get; set; }

    [JsonPropertyName("arguments")]
    [YamlMember(Alias = "arguments")]
    public List<string> Arguments { get; set; } = new();

    [JsonPropertyName("flags")]
    [YamlMember(Alias = "flags")]
    public List<string> Flags { get; set; } = new();

    [JsonPropertyName("switches")]
    [YamlMember(Alias = "switches")]
    public List<string> Switches { get; set; } = new();

    [JsonPropertyName("verb")]
    [YamlMember(Alias = "verb")]
    public string? Verb { get; set; }

    [JsonPropertyName("msi_properties")]
    [YamlMember(Alias = "msi_properties")]
    public Dictionary<string, string> MsiProperties { get; set; } = new();

    [JsonPropertyName("product_code")]
    [YamlMember(Alias = "product_code")]
    public string? ProductCode { get; set; }

    [JsonPropertyName("upgrade_code")]
    [YamlMember(Alias = "upgrade_code")]
    public string? UpgradeCode { get; set; }

    [JsonPropertyName("timeout_minutes")]
    [YamlMember(Alias = "timeout_minutes")]
    public int? TimeoutMinutes { get; set; }
}

/// <summary>
/// Represents an installed file or registry entry
/// </summary>
public class InstallItem
{
    [JsonPropertyName("type")]
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    [JsonPropertyName("md5checksum")]
    [YamlMember(Alias = "md5checksum")]
    public string? Md5Checksum { get; set; }

    [JsonPropertyName("sha256checksum")]
    [YamlMember(Alias = "sha256checksum")]
    public string? Sha256Checksum { get; set; }
}

/// <summary>
/// Represents an item to be uninstalled
/// </summary>
public class UninstallItem
{
    [JsonPropertyName("type")]
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("recursive")]
    [YamlMember(Alias = "recursive")]
    public bool Recursive { get; set; } = false;

    [JsonPropertyName("force")]
    [YamlMember(Alias = "force")]
    public bool Force { get; set; } = false;
}
```

### Step 6: Create Core Project File

Create `src/Cimian.Core/Cimian.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Library</OutputType>
    <Description>Core models and services for Cimian software deployment system</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="YamlDotNet" />
    <PackageReference Include="Serilog" />
    <PackageReference Include="System.Management" />
    <PackageReference Include="Microsoft.Win32.Registry" />
  </ItemGroup>

</Project>
```

### Step 7: Create Basic CLI Tool Structure

Create `src/Cimian.CLI.managedsoftwareupdate/Program.cs`:
```csharp
using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Cimian.Core.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/managedsoftwareupdate.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    // Register services here
                    services.AddSingleton<ISystemFactsCollector, SystemFactsCollector>();
                    // Add other services as they're implemented
                })
                .Build();

            var result = await Parser.Default.ParseArguments<Options>(args)
                .MapResult(
                    async options => await RunAsync(options, host.Services),
                    errors => Task.FromResult(1));

            return result;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    static async Task<int> RunAsync(Options options, IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        
        logger.LogInformation("Starting managedsoftwareupdate with options: {@Options}", options);

        // TODO: Implement the main logic based on options
        // This is where the Go main.go logic will be migrated

        if (options.Version)
        {
            Console.WriteLine($"managedsoftwareupdate version {typeof(Program).Assembly.GetName().Version}");
            return 0;
        }

        if (options.ShowConfig)
        {
            // TODO: Implement config display
            logger.LogInformation("Configuration display requested");
            return 0;
        }

        // TODO: Implement all other command line options
        logger.LogWarning("Not yet implemented - this is the initial C# migration structure");
        
        return 0;
    }
}

[Verb("auto", HelpText = "Perform automatic updates")]
public class Options
{
    [Option("auto", HelpText = "Perform automatic updates")]
    public bool Auto { get; set; }

    [Option("cache-status", HelpText = "Show cache status and statistics")]
    public bool CacheStatus { get; set; }

    [Option("check-selfupdate", HelpText = "Check if self-update is pending")]
    public bool CheckSelfUpdate { get; set; }

    [Option("checkonly", HelpText = "Check for updates, but don't install them")]
    public bool CheckOnly { get; set; }

    [Option("clear-bootstrap-mode", HelpText = "Disable bootstrap mode")]
    public bool ClearBootstrapMode { get; set; }

    [Option("clear-selfupdate", HelpText = "Clear pending self-update flag")]
    public bool ClearSelfUpdate { get; set; }

    [Option("installonly", HelpText = "Install pending updates without checking for new ones")]
    public bool InstallOnly { get; set; }

    [Option("item", HelpText = "Install only the specified package name(s)")]
    public IEnumerable<string>? Item { get; set; }

    [Option("local-only-manifest", HelpText = "Use specified local manifest file instead of server manifest")]
    public string? LocalOnlyManifest { get; set; }

    [Option("manifest", HelpText = "Process only the specified manifest from server")]
    public string? Manifest { get; set; }

    [Option("no-postflight", HelpText = "Skip postflight script execution")]
    public bool NoPostflight { get; set; }

    [Option("no-preflight", HelpText = "Skip preflight script execution")]
    public bool NoPreflight { get; set; }

    [Option("perform-selfupdate", HelpText = "Perform pending self-update (internal use)")]
    public bool PerformSelfUpdate { get; set; }

    [Option("postflight-only", HelpText = "Run only the postflight script and exit")]
    public bool PostflightOnly { get; set; }

    [Option("preflight-only", HelpText = "Run only the preflight script and exit")]
    public bool PreflightOnly { get; set; }

    [Option("restart-service", HelpText = "Restart CimianWatcher service and exit")]
    public bool RestartService { get; set; }

    [Option("selfupdate-status", HelpText = "Show self-update status and exit")]
    public bool SelfUpdateStatus { get; set; }

    [Option("set-bootstrap-mode", HelpText = "Enable bootstrap mode for next boot")]
    public bool SetBootstrapMode { get; set; }

    [Option("show-config", HelpText = "Display the current configuration and exit")]
    public bool ShowConfig { get; set; }

    [Option("show-status", HelpText = "Show status window during operations (bootstrap mode)")]
    public bool ShowStatus { get; set; }

    [Option("validate-cache", HelpText = "Validate cache integrity and remove corrupt files")]
    public bool ValidateCache { get; set; }

    [Option('v', "verbose", HelpText = "Increase verbosity")]
    public int Verbose { get; set; }

    [Option("version", HelpText = "Print the version and exit")]
    public bool Version { get; set; }
}
```

### Step 8: Create CLI Project File

Create `src/Cimian.CLI.managedsoftwareupdate/Cimian.CLI.managedsoftwareupdate.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <AssemblyName>managedsoftwareupdate</AssemblyName>
    <Description>Primary client-side component for Cimian software management</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Cimian.Core\Cimian.Core.csproj" />
    <ProjectReference Include="..\Cimian.Engine\Cimian.Engine.csproj" />
    <ProjectReference Include="..\Cimian.Infrastructure\Cimian.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="CommandLineParser" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Serilog.Extensions.Logging" />
    <PackageReference Include="Serilog.Sinks.Console" />
    <PackageReference Include="Serilog.Sinks.File" />
  </ItemGroup>

</Project>
```

### Step 9: Initial Build and Test

Run these commands to validate the initial structure:

```powershell
# Navigate to the src directory
cd src

# Create the solution structure
dotnet new sln -n Cimian

# Create the projects
dotnet new classlib -n Cimian.Core
dotnet new classlib -n Cimian.Engine  
dotnet new classlib -n Cimian.Infrastructure
dotnet new console -n Cimian.CLI.managedsoftwareupdate

# Add projects to solution
dotnet sln add Cimian.Core/Cimian.Core.csproj
dotnet sln add Cimian.Engine/Cimian.Engine.csproj
dotnet sln add Cimian.Infrastructure/Cimian.Infrastructure.csproj
dotnet sln add Cimian.CLI.managedsoftwareupdate/Cimian.CLI.managedsoftwareupdate.csproj

# Add project references
dotnet add Cimian.CLI.managedsoftwareupdate reference Cimian.Core
dotnet add Cimian.CLI.managedsoftwareupdate reference Cimian.Engine
dotnet add Cimian.CLI.managedsoftwareupdate reference Cimian.Infrastructure

# Restore packages
dotnet restore

# Build the solution
dotnet build
```

## Next Steps Priority Order

### Week 1: Foundation
1. **Create the solution structure** as outlined above
2. **Implement core models** (CatalogItem, Manifest, Configuration)
3. **Set up logging infrastructure** with Serilog
4. **Create basic configuration service** for YAML reading
5. **Validate** that basic structure compiles and runs

### Week 2: System Facts Collection
1. **Implement SystemFactsCollector** service
2. **Add Windows API integration** for hostname, domain, architecture
3. **Test fact collection** on different Windows systems
4. **Compare output** with Go implementation

### Week 3: Begin Conditional Engine
1. **Start expression parser** implementation
2. **Implement basic operators** (==, !=, CONTAINS)
3. **Create unit tests** for parser
4. **Test with simple expressions** from existing manifests

This starter guide provides a concrete foundation to begin the migration while maintaining the exact functionality and behavior of the existing Go implementation. Each step includes validation criteria to ensure nothing is lost during the transition.
