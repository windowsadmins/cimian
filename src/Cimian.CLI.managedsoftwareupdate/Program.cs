using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Reflection;
using Cimian.Core.Models;
using Cimian.Engine.Predicates;

namespace Cimian.CLI.managedsoftwareupdate;

/// <summary>
/// Main entry point for the managedsoftwareupdate CLI tool
/// Migrated from Go cmd/managedsoftwareupdate/main.go
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Handle --version flag early without logging
        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-V"))
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        // Configure Serilog early for startup logging
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/managedsoftwareupdate-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Log.Information("Starting managedsoftwareupdate v{Version}", GetVersion());

            // Parse command line arguments
            var parseResult = Parser.Default.ParseArguments<Options>(args);
            
            return await parseResult.MapResult(
                async (Options opts) => await RunAsync(opts),
                errors => Task.FromResult(1));
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

    private static async Task<int> RunAsync(Options options)
    {
        var hostBuilder = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                // Register core services
                services.AddSingleton(options);
                services.AddTransient<IPredicateEngine, PredicateEngine>();
                
                // Configure HTTP client
                services.AddHttpClient("cimian", client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(options.Timeout);
                    client.DefaultRequestHeaders.Add("User-Agent", $"Cimian-ManagedSoftwareUpdate/{GetVersion()}");
                });
            });

        using var host = hostBuilder.Build();
        
        try
        {
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            
            logger.LogInformation("Managedsoftwareupdate starting with options: {@Options}", options);

            if (options.Bootstrap)
            {
                logger.LogInformation("Running in bootstrap mode");
                return await RunBootstrapModeAsync(host.Services, options);
            }

            if (options.Auto)
            {
                logger.LogInformation("Running in auto mode");
                return await RunAutoModeAsync(host.Services, options);
            }

            if (options.CheckOnly)
            {
                logger.LogInformation("Running check-only mode");
                return await RunCheckOnlyModeAsync(host.Services, options);
            }

            // Default: run manual install mode
            logger.LogInformation("Running manual install mode");
            return await RunManualModeAsync(host.Services, options);
        }
        catch (Exception ex)
        {
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Error during execution");
            return 1;
        }
    }

    private static async Task<int> RunBootstrapModeAsync(IServiceProvider services, Options options)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        
        // TODO: Implement bootstrap mode logic
        // This should:
        // 1. Check for CimianWatcher service
        // 2. Launch CimianStatus GUI if requested
        // 3. Process any pending installations
        // 4. Self-update if newer version available
        
        logger.LogInformation("Bootstrap mode implementation pending");
        await Task.Delay(100); // Placeholder
        return 0;
    }

    private static async Task<int> RunAutoModeAsync(IServiceProvider services, Options options)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        
        // TODO: Implement auto mode logic
        // This should:
        // 1. Download and process catalogs
        // 2. Evaluate conditional manifests
        // 3. Install/update packages automatically
        // 4. Report progress and results
        
        logger.LogInformation("Auto mode implementation pending");
        await Task.Delay(100); // Placeholder
        return 0;
    }

    private static async Task<int> RunCheckOnlyModeAsync(IServiceProvider services, Options options)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        
        // TODO: Implement check-only mode logic
        // This should:
        // 1. Check for available updates
        // 2. Evaluate conditions without installing
        // 3. Report what would be installed
        
        logger.LogInformation("Check-only mode implementation pending");
        await Task.Delay(100); // Placeholder
        return 0;
    }

    private static async Task<int> RunManualModeAsync(IServiceProvider services, Options options)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        
        // TODO: Implement manual mode logic
        // This should handle specific package installations
        
        logger.LogInformation("Manual mode implementation pending");
        await Task.Delay(100); // Placeholder
        return 0;
    }

    private static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        
        // Try to get the informational version first (preserves exact format)
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informationalVersion))
        {
            // Remove git hash if present (after +)
            var plusIndex = informationalVersion.IndexOf('+');
            if (plusIndex >= 0)
            {
                return informationalVersion.Substring(0, plusIndex);
            }
            return informationalVersion;
        }
        
        // Fallback to file version (also preserves format)
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrEmpty(fileVersion))
        {
            return fileVersion;
        }
        
        // Last resort: assembly version (may lose leading zeros)
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "UNKNOWN";
    }
}

/// <summary>
/// Command line options for managedsoftwareupdate
/// Maintains compatibility with original Go implementation
/// </summary>
public class Options
{
    [Option('a', "auto", Required = false, HelpText = "Run in automatic mode")]
    public bool Auto { get; set; } = false;

    [Option('b', "bootstrap", Required = false, HelpText = "Run in bootstrap mode")]
    public bool Bootstrap { get; set; } = false;

    [Option('c', "checkonly", Required = false, HelpText = "Check for updates without installing")]
    public bool CheckOnly { get; set; } = false;

    [Option('v', "verbose", Required = false, HelpText = "Enable verbose logging")]
    public bool Verbose { get; set; } = false;

    [Option('q', "quiet", Required = false, HelpText = "Suppress output")]
    public bool Quiet { get; set; } = false;

    [Option("config", Required = false, HelpText = "Path to configuration file")]
    public string? ConfigPath { get; set; } = null;

    [Option("catalog", Required = false, HelpText = "Path to catalog file")]
    public string? CatalogPath { get; set; } = null;

    [Option("manifest", Required = false, HelpText = "Path to manifest file")]
    public string? ManifestPath { get; set; } = null;

    [Option("logpath", Required = false, HelpText = "Path for log files")]
    public string? LogPath { get; set; } = null;

    [Option("timeout", Required = false, Default = 300, HelpText = "Timeout in seconds")]
    public int Timeout { get; set; } = 300;

    [Option("force", Required = false, HelpText = "Force installation even if already installed")]
    public bool Force { get; set; } = false;

    [Option("gui", Required = false, HelpText = "Launch GUI interface")]
    public bool Gui { get; set; } = false;

    [Value(0, MetaName = "package", HelpText = "Specific package to install")]
    public string? Package { get; set; } = null;
}
