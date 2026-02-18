using CommandLine;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Services;
using System.Diagnostics;
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

        if (options.CleanCache)
        {
            return CleanCache();
        }

        if (options.SelfUpdateStatus)
        {
            return ShowSelfUpdateStatus();
        }

        if (options.PerformSelfUpdate)
        {
            return PerformSelfUpdate();
        }

        if (options.CheckSelfUpdate)
        {
            return CheckSelfUpdate();
        }

        if (options.ClearSelfUpdate)
        {
            return ClearSelfUpdate();
        }

        if (options.RestartService)
        {
            return RestartCimianWatcherService();
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
            // If checkonly, provide interactive options
            if (options.CheckOnly)
            {
                var action = HandleCheckOnlyConflict();
                if (action == "exit")
                {
                    return 1;
                }
                // action == "retry" - try to acquire mutex again
                if (!TryAcquireSingleInstance())
                {
                    Console.Error.WriteLine("Failed to acquire single instance after retry. Exiting.");
                    return 1;
                }
            }
            else
            {
                Console.Error.WriteLine("Another instance of managedsoftwareupdate is running. Exiting.");
                return 1;
            }
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
                showStatus: options.ShowStatus,
                itemFilter: options.Items);

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

        var (pending, metadata, error) = SelfUpdateService.GetSelfUpdateStatus();
        
        if (error != null)
        {
            ConsoleLogger.Error($"Error checking self-update status: {error}");
            return 1;
        }
        
        if (pending && metadata != null)
        {
            Console.WriteLine("[STATUS]: Self-update pending");
            Console.WriteLine();
            Console.WriteLine($"   Item: {metadata.Item}");
            Console.WriteLine($"   Version: {metadata.Version}");
            Console.WriteLine($"   Installer: {metadata.InstallerType}");
            Console.WriteLine($"   Scheduled: {metadata.ScheduledAt}");
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
        // Use the SelfUpdateService to perform the actual update
        return SelfUpdateService.PerformSelfUpdate() ? 0 : 1;
    }

    private static int CleanCache()
    {
        Console.WriteLine("Cleaning Cimian Cache");
        Console.WriteLine("════════════════════════════");

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Cimian", "Cache");

        if (!Directory.Exists(cacheDir))
        {
            Console.WriteLine("Cache directory does not exist. Nothing to clean.");
            return 0;
        }

        try
        {
            var files = Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories);
            var totalSize = files.Sum(f => new FileInfo(f).Length);

            Console.WriteLine($"Found {files.Length} cached files ({totalSize / 1024 / 1024:N0} MB)");

            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Warn($"Could not delete {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            // Clean empty directories
            foreach (var dir in Directory.GetDirectories(cacheDir, "*", SearchOption.AllDirectories).Reverse())
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch { /* Ignore directory cleanup errors */ }
            }

            ConsoleLogger.Success("Cache cleaned successfully");
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Failed to clean cache: {ex.Message}");
            return 1;
        }
    }

    private static int CheckSelfUpdate()
    {
        Console.WriteLine("Checking for Cimian Self-Update");
        Console.WriteLine("════════════════════════════════");

        var (pending, metadata, error) = SelfUpdateService.GetSelfUpdateStatus();

        if (error != null)
        {
            ConsoleLogger.Error($"Error: {error}");
            return 1;
        }

        if (pending && metadata != null)
        {
            Console.WriteLine();
            ConsoleLogger.Info($"Update available: {metadata.Item} v{metadata.Version}");
            Console.WriteLine($"   Scheduled: {metadata.ScheduledAt}");
            Console.WriteLine();
            Console.WriteLine("Run with --restart-service to apply the update.");
            return 0;
        }

        Console.WriteLine("No updates pending. Cimian is up to date.");
        return 0;
    }

    private static int ClearSelfUpdate()
    {
        Console.WriteLine("Clearing Pending Self-Update");
        Console.WriteLine("════════════════════════════════");

        var (pending, metadata, _) = SelfUpdateService.GetSelfUpdateStatus();

        if (!pending || metadata == null)
        {
            Console.WriteLine("No pending self-update to clear.");
            return 0;
        }

        if (SelfUpdateService.ClearSelfUpdateFlag())
        {
            ConsoleLogger.Success($"Cleared pending update: {metadata.Item} v{metadata.Version}");
            return 0;
        }

        ConsoleLogger.Error("Failed to clear self-update flag");
        return 1;
    }

    private static int RestartCimianWatcherService()
    {
        Console.WriteLine("Restarting Cimian Watcher Service");
        Console.WriteLine("══════════════════════════════════");

        const string serviceName = "CimianWatcher";

        try
        {
            // Stop the service
            Console.WriteLine($"Stopping {serviceName}...");
            var stopProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"stop {serviceName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            stopProcess?.WaitForExit(30000);

            // Wait for service to stop
            Thread.Sleep(2000);

            // Start the service
            Console.WriteLine($"Starting {serviceName}...");
            var startProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"start {serviceName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            startProcess?.WaitForExit(30000);

            if (startProcess?.ExitCode == 0)
            {
                ConsoleLogger.Success($"{serviceName} restarted successfully");
                Console.WriteLine();
                Console.WriteLine("Note: If a self-update was pending, it will be applied now.");
                return 0;
            }
            else
            {
                ConsoleLogger.Error($"Failed to start {serviceName}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Error restarting service: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Handles conflict when --checkonly is used but another instance is running.
    /// Matches Go: handleCheckOnlyConflict()
    /// </summary>
    private static string HandleCheckOnlyConflict()
    {
        Console.Error.WriteLine("\nAnother managedsoftwareupdate process is currently running.\n");

        // Try to determine what the running process is doing
        var runningProcessInfo = GetRunningProcessInfo();
        if (!string.IsNullOrEmpty(runningProcessInfo))
        {
            Console.Error.WriteLine($"Current process status: {runningProcessInfo}\n");
        }

        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  [K] Kill the existing process and continue with --checkonly");
        Console.Error.WriteLine("  [W] Wait for the existing process to complete");
        Console.Error.WriteLine("  [Q] Quit (default)\n");
        Console.Error.Write("Choice [k/w/q]: ");

        var choice = Console.ReadLine()?.ToLower().Trim() ?? "";

        switch (choice)
        {
            case "k":
            case "kill":
                Console.Error.WriteLine("\nAttempting to terminate existing process...");
                if (!KillExistingProcess())
                {
                    Console.Error.WriteLine("ERROR: Failed to kill existing process.");
                    Console.Error.WriteLine("The existing process may be in a critical state. Exiting.");
                    return "exit";
                }
                Console.Error.WriteLine("Existing process terminated. Continuing with --checkonly...\n");
                Thread.Sleep(500); // Give system time to release mutex
                return "retry";

            case "w":
            case "wait":
                Console.Error.WriteLine("\nWaiting for existing process to complete...");
                WaitForProcessCompletion();
                Console.Error.WriteLine("Process completed. Continuing with --checkonly...\n");
                return "retry";

            case "q":
            case "quit":
            case "":
                Console.Error.WriteLine("\nExiting. The existing process will continue running.");
                return "exit";

            default:
                Console.Error.WriteLine("\nInvalid choice. Exiting.");
                return "exit";
        }
    }

    /// <summary>
    /// Gets information about the running managedsoftwareupdate process.
    /// Matches Go: getRunningProcessInfo()
    /// </summary>
    private static string GetRunningProcessInfo()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            var processes = Process.GetProcessesByName("managedsoftwareupdate");
            
            foreach (var proc in processes)
            {
                if (proc.Id == currentPid) continue;
                
                try
                {
                    var cmdLine = GetProcessCommandLine(proc.Id);
                    
                    if (string.IsNullOrEmpty(cmdLine))
                        return "Unable to determine process status";
                    
                    if (cmdLine.Contains("--checkonly"))
                        return "Running check-only operation (safe to terminate)";
                    else if (cmdLine.Contains("--show-config") || cmdLine.Contains("--version"))
                        return "Running information display (safe to terminate)";
                    else if (cmdLine.Contains("--auto"))
                        return "WARNING: Running automatic installation (RISKY to terminate)";
                    else if (cmdLine.Contains("--installonly"))
                        return "WARNING: Running install-only operation (RISKY to terminate)";
                    else
                        return "WARNING: Running software management operation (POTENTIALLY RISKY to terminate)";
                }
                catch
                {
                    return "Unable to determine process status";
                }
            }

            return "Process no longer running";
        }
        catch
        {
            return "Unable to determine process status";
        }
    }

    /// <summary>
    /// Gets the command line of a process by PID using WMI.
    /// </summary>
    private static string? GetProcessCommandLine(int pid)
    {
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddScript($"Get-WmiObject Win32_Process -Filter \"ProcessId = {pid}\" | Select-Object -ExpandProperty CommandLine");
            var result = ps.Invoke();
            return result.FirstOrDefault()?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Kills existing managedsoftwareupdate processes.
    /// Matches Go: killExistingProcess()
    /// </summary>
    private static bool KillExistingProcess()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            var processes = Process.GetProcessesByName("managedsoftwareupdate");

            foreach (var proc in processes)
            {
                if (proc.Id == currentPid) continue;
                
                try
                {
                    proc.Kill(true); // Kill entire process tree
                    proc.WaitForExit(5000);
                }
                catch
                {
                    // Continue trying to kill other instances
                }
            }
            
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for existing managedsoftwareupdate processes to complete.
    /// Matches Go: waitForProcessCompletion()
    /// </summary>
    private static void WaitForProcessCompletion()
    {
        var currentPid = Environment.ProcessId;
        Console.Error.Write("Monitoring process completion");

        while (true)
        {
            var processes = Process.GetProcessesByName("managedsoftwareupdate")
                .Where(p => p.Id != currentPid)
                .ToList();

            if (processes.Count == 0)
                break;

            Console.Error.Write(".");
            Thread.Sleep(2000);
        }

        Console.Error.WriteLine();
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
            // Enable UTF-8 output for proper Unicode character rendering
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
            
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

    [Option("clean-cache", Required = false, HelpText = "Perform comprehensive cache cleanup and exit")]
    public bool CleanCache { get; set; }

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
