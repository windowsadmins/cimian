using CommandLine;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Services;
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

    // Track verbosity level from command line preprocessing
    private static int _verbosityLevel = 0;

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

        // Preprocess args to handle -vvv (multiple v's) or multiple -v flags
        var (processedArgs, verbosityLevel) = PreprocessVerbosity(args);
        _verbosityLevel = verbosityLevel;

        // Parse command line arguments
        var parseResult = Parser.Default.ParseArguments<Options>(processedArgs);
        
        return await parseResult.MapResult(
            async (Options opts) => await RunAsync(opts),
            errors => Task.FromResult(1));
    }

    /// <summary>
    /// Preprocess arguments to handle Go-style verbosity flags.
    /// Supports: -v, -vv, -vvv, -v -v -v, etc.
    /// Returns cleaned args (without -v flags) and the verbosity count.
    /// </summary>
    private static (string[] args, int verbosity) PreprocessVerbosity(string[] args)
    {
        var result = new List<string>();
        int verbosity = 0;

        foreach (var arg in args)
        {
            // Handle combined verbose flags like -vvv
            if (arg.StartsWith("-v") && !arg.StartsWith("--") && !arg.Contains("="))
            {
                // Count consecutive 'v' characters after the dash
                var vCount = arg.Skip(1).TakeWhile(c => c == 'v').Count();
                if (vCount == arg.Length - 1) // All remaining chars are 'v'
                {
                    verbosity += vCount;
                    continue; // Don't add to result
                }
            }
            // Handle --verbose flag
            else if (arg == "--verbose")
            {
                verbosity++;
                continue; // Don't add to result
            }
            
            result.Add(arg);
        }

        return (result.ToArray(), verbosity);
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

        if (options.PerformSelfUpdate)
        {
            return PerformSelfUpdate();
        }

        // Handle preflight-only: run preflight and exit
        if (options.PreflightOnly)
        {
            return await RunPreflightOnlyAsync(options);
        }

        // Handle postflight-only: run postflight and exit
        if (options.PostflightOnly)
        {
            return await RunPostflightOnlyAsync(options);
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

            // Apply verbosity from command line (use preprocessed _verbosityLevel)
            var effectiveVerbosity = _verbosityLevel > 0 ? _verbosityLevel : (options.Verbose ? 1 : 0);
            
            if (effectiveVerbosity >= 1)
            {
                config.Verbose = true;
                config.LogLevel = "INFO";
            }

            if (effectiveVerbosity >= 3)
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
                verbosity: effectiveVerbosity,
                manifestTarget: options.ManifestTarget,
                localManifest: options.LocalOnlyManifest,
                skipPreflight: options.NoPreflight,
                skipPostflight: options.NoPostflight,
                showStatus: options.ShowStatus);

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
        Console.WriteLine($"  Catalogs: [{string.Join(", ", config.Catalogs)}]");
        Console.WriteLine($"  LogLevel: {config.LogLevel}");
        Console.WriteLine($"  Verbose: {config.Verbose}");
        Console.WriteLine($"  Debug: {config.Debug}");
        Console.WriteLine($"  CheckOnly: {config.CheckOnly}");
        Console.WriteLine($"  InstallerTimeout: {config.InstallerTimeout}s");
        Console.WriteLine($"  NoPreflight: {config.NoPreflight}");
        Console.WriteLine($"  NoPostflight: {config.NoPostflight}");
        Console.WriteLine($"  PreflightFailureAction: {config.PreflightFailureAction}");
        Console.WriteLine($"  PostflightFailureAction: {config.PostflightFailureAction}");
        Console.WriteLine($"  LocalOnlyManifest: {config.LocalOnlyManifest ?? "(not set)"}");
        Console.WriteLine($"  SkipSelfService: {config.SkipSelfService}");
        Console.WriteLine($"  AuthUser: {(string.IsNullOrEmpty(config.AuthUser) ? "(not set)" : "***")}");
        Console.WriteLine($"  AuthToken: {(string.IsNullOrEmpty(config.AuthToken) ? "(not set)" : "***")}");

        return 0;
    }

    private static async Task<int> RunPreflightOnlyAsync(Options options)
    {
        var configService = new ConfigurationService();
        var config = configService.LoadConfig(options.ConfigPath ?? CimianConfig.ConfigPath);

        // Apply verbosity
        var effectiveVerbosity = _verbosityLevel > 0 ? _verbosityLevel : (options.Verbose ? 1 : 0);
        if (effectiveVerbosity >= 1)
        {
            config.Verbose = true;
            config.LogLevel = "INFO";
        }

        var scriptService = new ScriptService();
        var (success, output) = await scriptService.RunPreflightAsync(CancellationToken.None);

        // Print preflight output
        if (!string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(output);
        }

        if (success)
        {
            ConsoleLogger.Success("Preflight completed successfully");
            return 0;
        }
        else
        {
            ConsoleLogger.Error("Preflight script failed");
            return 1;
        }
    }

    private static async Task<int> RunPostflightOnlyAsync(Options options)
    {
        var configService = new ConfigurationService();
        var config = configService.LoadConfig(options.ConfigPath ?? CimianConfig.ConfigPath);

        // Apply verbosity
        var effectiveVerbosity = _verbosityLevel > 0 ? _verbosityLevel : (options.Verbose ? 1 : 0);
        if (effectiveVerbosity >= 1)
        {
            config.Verbose = true;
            config.LogLevel = "INFO";
        }

        var scriptService = new ScriptService();
        var (success, output) = await scriptService.RunPostflightAsync(CancellationToken.None);

        // Print postflight output
        if (!string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(output);
        }

        if (success)
        {
            ConsoleLogger.Success("Postflight completed successfully");
            return 0;
        }
        else
        {
            ConsoleLogger.Error("Postflight script failed");
            return 1;
        }
    }

    private static int ShowCacheStatus()
    {
        var configService = new ConfigurationService();
        var config = configService.LoadConfig();
        var downloadService = new DownloadService(config);

        var (fileCount, totalSize, corruptCount) = downloadService.GetCacheStatus();

        // Get oldest file info
        var oldestFileAge = GetOldestFileAge(config.CachePath);

        Console.WriteLine("Cimian Cache Status");
        Console.WriteLine("═══════════════════════");
        Console.WriteLine($"Cache Path: {config.CachePath}");
        Console.WriteLine($"Total Files: {fileCount}");
        Console.WriteLine($"Total Size: {totalSize / (1024.0 * 1024.0 * 1024.0):F2} GB");
        Console.WriteLine($"Oldest File: {FormatTimeAgo(oldestFileAge)}");

        if (corruptCount > 0)
        {
            ConsoleLogger.Warn($"Corrupt Files: {corruptCount} (0-byte files detected)");
            ConsoleLogger.Info("Run with --validate-cache to clean up corrupt files");
        }
        else
        {
            Console.WriteLine("No corruption detected");
        }

        // Cache configuration section (matches Go output)
        Console.WriteLine();
        Console.WriteLine("Cache Configuration:");
        Console.WriteLine($"  Use Cache: {config.UseCache}");
        Console.WriteLine($"  Retention: {config.CacheRetentionDays} days");

        return 0;
    }

    private static TimeSpan GetOldestFileAge(string cachePath)
    {
        if (!Directory.Exists(cachePath))
            return TimeSpan.Zero;

        try
        {
            var oldestFile = Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (oldestFile == null)
                return TimeSpan.Zero;

            return DateTime.UtcNow - oldestFile.LastWriteTimeUtc;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static string FormatTimeAgo(TimeSpan age)
    {
        if (age == TimeSpan.Zero)
            return "0s ago";

        if (age.TotalDays >= 1)
            return $"{(int)age.TotalDays}d ago";
        if (age.TotalHours >= 1)
            return $"{(int)age.TotalHours}h ago";
        if (age.TotalMinutes >= 1)
            return $"{(int)age.TotalMinutes}m ago";
        return $"{(int)age.TotalSeconds}s ago";
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
            ConsoleLogger.Info("To trigger the update:");
            Console.WriteLine("   managedsoftwareupdate --restart-service");
        }
        else
        {
            Console.WriteLine("[STATUS]: No self-update pending");
            Console.WriteLine("Cimian is up to date");
        }

        return 0;
    }

    private static int PerformSelfUpdate()
    {
        // This is an internal flag used by the self-update mechanism
        // When a new version of Cimian is downloaded, it may re-launch itself with this flag
        // to complete the update process
        ConsoleLogger.Info("Performing self-update...");

        try
        {
            // Check for pending self-update flag
            var flagPath = @"C:\ProgramData\ManagedInstalls\.selfupdate_pending";
            
            if (!File.Exists(flagPath))
            {
                ConsoleLogger.Info("No self-update pending. Nothing to do.");
                return 0;
            }

            // Read the self-update metadata
            var flagData = File.ReadAllText(flagPath);
            ConsoleLogger.Info("Self-update metadata found:");
            
            // Parse and display metadata
            var lines = flagData.Split('\n');
            string? localFile = null;
            string? itemName = null;
            string? version = null;
            string? installerType = null;

            foreach (var line in lines)
            {
                if (line.Contains(':') && !line.TrimStart().StartsWith("#"))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var key = parts[0].Trim();
                        var value = parts[1].Trim();
                        Console.WriteLine($"   {key}: {value}");

                        switch (key)
                        {
                            case "LocalFile": localFile = value; break;
                            case "Item": itemName = value; break;
                            case "Version": version = value; break;
                            case "InstallerType": installerType = value; break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(localFile))
            {
                ConsoleLogger.Error("Self-update metadata missing LocalFile information");
                return 1;
            }

            Console.WriteLine();
            ConsoleLogger.Info($"Would execute: {installerType} installer at {localFile}");
            ConsoleLogger.Warn("Full self-update execution is not yet implemented in C# version.");
            ConsoleLogger.Info("For now, please run the Go version or manually install the update.");

            return 0;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Self-update failed: {ex.Message}");
            return 1;
        }
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
        // Note: In async code, the continuation may run on a different thread than the one
        // that acquired the mutex. ReleaseMutex() is thread-affine and will throw
        // ApplicationException if called from a different thread.
        // 
        // Disposing the mutex is sufficient to release the kernel handle - we don't need
        // to explicitly call ReleaseMutex(). The OS will clean up when the handle is closed.
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Expected when async continuation runs on different thread - ignore
        }
        
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

    [Option("perform-selfupdate", Required = false, HelpText = "Perform pending self-update (internal use)")]
    public bool PerformSelfUpdate { get; set; }

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

    // Verbosity options (note: -v, -vv, -vvv handled by preprocessing)
    // Keep the Option for help text purposes but it won't be used for parsing
    [Option('q', "quiet", Required = false, HelpText = "Suppress output")]
    public bool Quiet { get; set; }

    // Legacy: kept for compatibility but verbosity now handled via preprocessing
    [Value(999, Hidden = true)] // Hidden from help, won't match anything
    public bool Verbose { get; set; }

    // Configuration paths
    [Option("config", Required = false, HelpText = "Path to configuration file")]
    public string? ConfigPath { get; set; }

    // Version flag handled separately in Main
    [Option('V', "version", Required = false, HelpText = "Print the version and exit")]
    public bool Version { get; set; }
}
