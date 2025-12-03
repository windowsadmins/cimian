using CommandLine;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Cimian.CLI.managedsoftwareupdate;

/// <summary>
/// Main entry point for the managedsoftwareupdate CLI tool
/// Migrated from Go cmd/managedsoftwareupdate/main.go (3,527 LOC)
/// </summary>
public class Program
{
    private const string MutexName = "Global\\CimianManagedSoftwareUpdate_v2";
    private static Mutex? _singleInstanceMutex;

    public static async Task<int> Main(string[] args)
    {
        // Handle --version flag early
        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-V"))
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        // Enable ANSI console colors
        EnableAnsiConsole();

        // Parse command line arguments
        var parseResult = Parser.Default.ParseArguments<Options>(args);
        
        return await parseResult.MapResult(
            async (Options opts) => await RunAsync(opts),
            errors => Task.FromResult(1));
    }

    private static async Task<int> RunAsync(Options options)
    {
        // Handle special flags that exit immediately
        if (options.ShowConfig)
        {
            return ShowConfig();
        }

        if (options.SetBootstrapMode)
        {
            StatusService.EnableBootstrapMode();
            Console.WriteLine("[SUCCESS] Bootstrap mode enabled. System will enter bootstrap mode on next boot.");
            return 0;
        }

        if (options.ClearBootstrapMode)
        {
            StatusService.DisableBootstrapMode();
            Console.WriteLine("[SUCCESS] Bootstrap mode disabled.");
            return 0;
        }

        if (options.CacheStatus)
        {
            return ShowCacheStatus();
        }

        if (options.ValidateCache)
        {
            return ValidateCache();
        }

        if (options.SelfUpdateStatus)
        {
            return ShowSelfUpdateStatus();
        }

        // Check for single instance
        if (!TryAcquireSingleInstance())
        {
            Console.Error.WriteLine("Another instance of managedsoftwareupdate is running. Exiting.");
            return 1;
        }

        try
        {
            // Load configuration
            var configService = new ConfigurationService();
            var config = configService.LoadConfig(options.ConfigPath ?? CimianConfig.ConfigPath);

            // Apply verbosity from command line
            if (options.Verbose)
            {
                config.Verbose = true;
                config.LogLevel = "INFO";
            }

            if (options.VerbosityLevel >= 3)
            {
                config.Debug = true;
                config.LogLevel = "DEBUG";
            }

            // Create and run update engine
            var engine = new UpdateEngine(config);

            var result = await engine.RunAsync(
                checkOnly: options.CheckOnly,
                installOnly: options.InstallOnly,
                auto: options.Auto,
                bootstrap: options.Bootstrap,
                verbosity: options.VerbosityLevel,
                manifestTarget: options.ManifestTarget,
                localManifest: options.LocalOnlyManifest,
                skipPreflight: options.NoPreflight,
                skipPostflight: options.NoPostflight);

            return result;
        }
        finally
        {
            ReleaseSingleInstance();
        }
    }

    private static int ShowConfig()
    {
        var configService = new ConfigurationService();
        var config = configService.LoadConfig();

        Console.WriteLine($"Configuration file location: {CimianConfig.ConfigPath}");
        Console.WriteLine();
        Console.WriteLine("Current configuration:");
        Console.WriteLine($"  SoftwareRepoURL: {config.SoftwareRepoURL}");
        Console.WriteLine($"  ClientIdentifier: {config.ClientIdentifier}");
        Console.WriteLine($"  CachePath: {config.CachePath}");
        Console.WriteLine($"  CatalogsPath: {config.CatalogsPath}");
        Console.WriteLine($"  ManifestsPath: {config.ManifestsPath}");
        Console.WriteLine($"  LogLevel: {config.LogLevel}");
        Console.WriteLine($"  InstallerTimeout: {config.InstallerTimeout}s");
        Console.WriteLine($"  Catalogs: {string.Join(", ", config.Catalogs)}");
        Console.WriteLine($"  NoPreflight: {config.NoPreflight}");
        Console.WriteLine($"  NoPostflight: {config.NoPostflight}");
        Console.WriteLine($"  PreflightFailureAction: {config.PreflightFailureAction}");
        Console.WriteLine($"  PostflightFailureAction: {config.PostflightFailureAction}");

        return 0;
    }

    private static int ShowCacheStatus()
    {
        var configService = new ConfigurationService();
        var config = configService.LoadConfig();
        var downloadService = new DownloadService(config);

        var (fileCount, totalSize, corruptCount) = downloadService.GetCacheStatus();

        Console.WriteLine("Cimian Cache Status");
        Console.WriteLine("═══════════════════════");
        Console.WriteLine($"Cache Path: {config.CachePath}");
        Console.WriteLine($"Total Files: {fileCount}");
        Console.WriteLine($"Total Size: {totalSize / (1024.0 * 1024.0):F2} MB");

        if (corruptCount > 0)
        {
            Console.WriteLine($"[WARNING] Corrupt Files: {corruptCount} (0-byte files detected)");
            Console.WriteLine("[INFO] Run with --validate-cache to clean up corrupt files");
        }
        else
        {
            Console.WriteLine("No corruption detected");
        }

        return 0;
    }

    private static int ValidateCache()
    {
        Console.WriteLine("Validating cache integrity...");

        var configService = new ConfigurationService();
        var config = configService.LoadConfig();
        var downloadService = new DownloadService(config);

        downloadService.ValidateAndCleanCache();

        Console.WriteLine("Cache validation completed successfully");
        return 0;
    }

    private static int ShowSelfUpdateStatus()
    {
        Console.WriteLine("Cimian Self-Update Status");
        Console.WriteLine("════════════════════════════");

        // Check for pending self-update flag
        var flagPath = @"C:\ProgramData\ManagedInstalls\.selfupdate_pending";
        
        if (File.Exists(flagPath))
        {
            Console.WriteLine("[STATUS]: Self-update pending");
            Console.WriteLine();
            Console.WriteLine("[INFO] To trigger the update:");
            Console.WriteLine("   managedsoftwareupdate --restart-service");
        }
        else
        {
            Console.WriteLine("[STATUS]: No self-update pending");
            Console.WriteLine("Cimian is up to date");
        }

        return 0;
    }

    private static bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
            return createdNew;
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseSingleInstance()
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    private static void EnableAnsiConsole()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
            if (handle != IntPtr.Zero && GetConsoleMode(handle, out var mode))
            {
                mode |= 0x0004; // ENABLE_VIRTUAL_TERMINAL_PROCESSING
                SetConsoleMode(handle, mode);
            }
        }
        catch
        {
            // Ignore - ANSI support is optional
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+');
            if (plusIndex >= 0)
            {
                return informationalVersion[..plusIndex];
            }
            return informationalVersion;
        }
        
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrEmpty(fileVersion))
        {
            return fileVersion;
        }
        
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
    // Run mode flags
    [Option('a', "auto", Required = false, HelpText = "Perform automatic updates")]
    public bool Auto { get; set; }

    [Option('c', "checkonly", Required = false, HelpText = "Check for updates, but don't install them")]
    public bool CheckOnly { get; set; }

    [Option('i', "installonly", Required = false, HelpText = "Install pending updates without checking for new ones")]
    public bool InstallOnly { get; set; }

    // Bootstrap mode flags
    [Option("set-bootstrap-mode", Required = false, HelpText = "Enable bootstrap mode for next boot")]
    public bool SetBootstrapMode { get; set; }

    [Option("clear-bootstrap-mode", Required = false, HelpText = "Disable bootstrap mode")]
    public bool ClearBootstrapMode { get; set; }

    [Option('b', "bootstrap", Required = false, HelpText = "Run in bootstrap mode (for service startup)")]
    public bool Bootstrap { get; set; }

    // Self-update flags
    [Option("clear-selfupdate", Required = false, HelpText = "Clear pending self-update flag")]
    public bool ClearSelfUpdate { get; set; }

    [Option("check-selfupdate", Required = false, HelpText = "Check if self-update is pending")]
    public bool CheckSelfUpdate { get; set; }

    [Option("selfupdate-status", Required = false, HelpText = "Show self-update status and exit")]
    public bool SelfUpdateStatus { get; set; }

    [Option("restart-service", Required = false, HelpText = "Restart CimianWatcher service and exit")]
    public bool RestartService { get; set; }

    // Cache management flags
    [Option("validate-cache", Required = false, HelpText = "Validate cache integrity and remove corrupt files")]
    public bool ValidateCache { get; set; }

    [Option("cache-status", Required = false, HelpText = "Show cache status and statistics")]
    public bool CacheStatus { get; set; }

    // Script control flags
    [Option("no-preflight", Required = false, HelpText = "Skip preflight script execution")]
    public bool NoPreflight { get; set; }

    [Option("no-postflight", Required = false, HelpText = "Skip postflight script execution")]
    public bool NoPostflight { get; set; }

    [Option("preflight-only", Required = false, HelpText = "Run only the preflight script and exit")]
    public bool PreflightOnly { get; set; }

    [Option("postflight-only", Required = false, HelpText = "Run only the postflight script and exit")]
    public bool PostflightOnly { get; set; }

    // Manifest options
    [Option("local-only-manifest", Required = false, HelpText = "Use specified local manifest file instead of server manifest")]
    public string? LocalOnlyManifest { get; set; }

    [Option('m', "manifest", Required = false, HelpText = "Process only the specified manifest from server")]
    public string? ManifestTarget { get; set; }

    // Item filter options
    [Option("item", Required = false, HelpText = "Process only the specified item(s)")]
    public IEnumerable<string>? Items { get; set; }

    // Display options
    [Option("show-config", Required = false, HelpText = "Display the current configuration and exit")]
    public bool ShowConfig { get; set; }

    [Option("show-status", Required = false, HelpText = "Show status window during operations")]
    public bool ShowStatus { get; set; }

    // Verbosity options
    [Option('v', "verbose", Required = false, HelpText = "Enable verbose logging")]
    public bool Verbose { get; set; }

    [Option('q', "quiet", Required = false, HelpText = "Suppress output")]
    public bool Quiet { get; set; }

    // Count of -v flags for verbosity level
    public int VerbosityLevel => Verbose ? 1 : 0;

    // Configuration paths
    [Option("config", Required = false, HelpText = "Path to configuration file")]
    public string? ConfigPath { get; set; }

    // Version flag handled separately in Main
    [Option('V', "version", Required = false, HelpText = "Print the version and exit")]
    public bool Version { get; set; }
}
