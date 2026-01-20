using System.Diagnostics;
using Cimian.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cimian.CLI.Cimiwatcher.Services;

/// <summary>
/// Background service that monitors bootstrap flag files and triggers updates when detected.
/// Also checks for pending self-updates on service start.
/// </summary>
public class FileWatcherService : BackgroundService
{
    private const string BootstrapFlagFile = @"C:\ProgramData\ManagedInstalls\.cimian.bootstrap";
    private const string HeadlessFlagFile = @"C:\ProgramData\ManagedInstalls\.cimian.headless";
    private const string CimianExePath = @"C:\Program Files\Cimian\managedsoftwareupdate.exe";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<FileWatcherService> _logger;
    private readonly object _lock = new();
    
    private DateTime _lastSeenGUI = DateTime.MinValue;
    private DateTime _lastSeenHeadless = DateTime.MinValue;
    private bool _isPaused;

    public FileWatcherService(ILogger<FileWatcherService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CimianWatcher file monitoring service started");
        
        // Check for pending self-updates on service start
        CheckAndPerformSelfUpdate();
        
        _logger.LogInformation("Monitoring bootstrap files:");
        _logger.LogInformation("  GUI: {BootstrapFile}", BootstrapFlagFile);
        _logger.LogInformation("  Headless: {HeadlessFile}", HeadlessFlagFile);
        _logger.LogInformation("Poll interval: {Interval} seconds", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_isPaused)
                {
                    CheckBootstrapFiles(stoppingToken);
                }
                
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during file monitoring");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("CimianWatcher file monitoring service stopped");
    }

    private void CheckBootstrapFiles(CancellationToken cancellationToken)
    {
        // Check GUI bootstrap file
        CheckFlagFile(BootstrapFlagFile, "GUI", withGUI: true, 
            ref _lastSeenGUI, cancellationToken);
        
        // Check headless bootstrap file
        CheckFlagFile(HeadlessFlagFile, "Headless", withGUI: false, 
            ref _lastSeenHeadless, cancellationToken);
    }

    private void CheckFlagFile(string flagFile, string updateType, bool withGUI,
        ref DateTime lastSeen, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(flagFile))
                return;

            var fileInfo = new FileInfo(flagFile);
            var modTime = fileInfo.LastWriteTime;

            // Check if this is a new file or if it was modified since last seen
            if (lastSeen == DateTime.MinValue || modTime > lastSeen)
            {
                _logger.LogInformation("{UpdateType} flag file detected - triggering update", updateType);
                lastSeen = modTime;
                
                // Trigger update in background
                _ = Task.Run(() => TriggerBootstrapUpdateAsync(flagFile, updateType, withGUI, cancellationToken), 
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking {UpdateType} flag file", updateType);
        }
    }

    private async Task TriggerBootstrapUpdateAsync(string flagFile, string updateType, bool withGUI, 
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            // Verify the file still exists (race condition protection)
            if (!File.Exists(flagFile))
            {
                _logger.LogInformation("{UpdateType} flag file no longer exists - skipping update", updateType);
                return;
            }
        }

        _logger.LogInformation("Starting {UpdateType} bootstrap update process", updateType);

        try
        {
            // Start managedsoftwareupdate
            // Always include --show-status so any listening GUI (ManagedSoftwareCenter or CimianStatus) 
            // receives progress updates. The StatusReporter only connects if a GUI is listening on port 19847.
            var updateArgs = withGUI ? "--auto --show-status -vv" : "--auto --show-status";
            var updateProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = CimianExePath,
                    Arguments = updateArgs,
                    UseShellExecute = false,
                    CreateNoWindow = !withGUI
                }
            };

            if (!updateProcess.Start())
            {
                _logger.LogError("Failed to start {UpdateType} bootstrap update", updateType);
                return;
            }

            _logger.LogInformation("Started managedsoftwareupdate process (PID: {Pid})", updateProcess.Id);

            // If GUI mode, also launch cimistatus to provide UI monitoring
            if (withGUI)
            {
                LaunchCimianStatus();
            }

            // Wait for the update process to complete
            await updateProcess.WaitForExitAsync(cancellationToken);

            if (updateProcess.ExitCode == 0)
            {
                _logger.LogInformation("{UpdateType} bootstrap update process completed successfully", updateType);
            }
            else
            {
                _logger.LogWarning("{UpdateType} bootstrap update process exited with code {ExitCode}", 
                    updateType, updateProcess.ExitCode);
            }

            // Clean up the flag file after completion
            try
            {
                if (File.Exists(flagFile))
                {
                    File.Delete(flagFile);
                    _logger.LogInformation("Removed {UpdateType} flag file after completion", updateType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove {UpdateType} flag file", updateType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{UpdateType} bootstrap update process failed", updateType);
        }
    }

    private void LaunchCimianStatus()
    {
        var cimianDir = Path.GetDirectoryName(CimianExePath);
        if (cimianDir == null) return;

        var cimistatus = Path.Combine(cimianDir, "cimistatus.exe");

        if (!File.Exists(cimistatus))
        {
            _logger.LogWarning("CimianStatus not found at: {Path}", cimistatus);
            return;
        }

        try
        {
            var guiProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cimistatus,
                    UseShellExecute = true  // Use shell execute for GUI app
                }
            };

            if (guiProcess.Start())
            {
                _logger.LogInformation("Started CimianStatus UI (PID: {Pid})", guiProcess.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start CimianStatus UI");
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _isPaused = true;
            _logger.LogInformation("Monitoring paused");
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _isPaused = false;
            _logger.LogInformation("Monitoring resumed");
        }
    }

    /// <summary>
    /// Checks for pending self-updates and executes them on service start.
    /// Port of Go cimiwatcher checkAndPerformSelfUpdate()
    /// </summary>
    private void CheckAndPerformSelfUpdate()
    {
        try
        {
            var (pending, metadata, error) = SelfUpdateService.GetSelfUpdateStatus();
            
            if (error != null)
            {
                _logger.LogError("Failed to check self-update status: {Error}", error);
                return;
            }

            if (!pending || metadata == null)
            {
                _logger.LogInformation("No self-update pending");
                return;
            }

            _logger.LogInformation("Self-update detected, executing update for {Item} v{Version}", 
                metadata.Item, metadata.Version);

            // Perform the self-update
            if (SelfUpdateService.PerformSelfUpdate())
            {
                _logger.LogInformation("Self-update completed successfully");
            }
            else
            {
                _logger.LogError("Self-update failed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during self-update check");
        }
    }
}
