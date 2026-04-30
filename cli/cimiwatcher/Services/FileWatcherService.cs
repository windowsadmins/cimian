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

        // Read flag file content for optional custom arguments
        // If the file contains an "Args:" line, use those arguments instead of defaults.
        // This allows the MSC GUI to request specific modes (--checkonly, --installonly)
        // without the service guessing.
        string? customArgs = null;
        bool suppressCimistatus = false;
        try
        {
            var content = await File.ReadAllTextAsync(flagFile, cancellationToken);
            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Args:", StringComparison.OrdinalIgnoreCase))
                {
                    customArgs = trimmed.Substring("Args:".Length).Trim();
                    suppressCimistatus = true; // caller manages its own UI
                    _logger.LogInformation("Custom args from flag file: {Args}", customArgs);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read flag file content, using defaults");
        }

        // LoginWindow-only gate: a true bootstrap (no explicit Args: from MSC) on the
        // GUI flag file must wait for an unattended secure desktop, mirroring Munki's
        // loginwindow.py behaviour. MSC's in-session triggers (Args: present) and the
        // headless channel bypass this gate by design.
        if (withGUI && customArgs == null && SessionProbe.IsInteractiveUserLoggedOn())
        {
            _logger.LogInformation(
                "{UpdateType} bootstrap deferred — interactive user signed in; will re-check after logout",
                updateType);
            // Leave the flag file in place and reset lastSeen so the next poll re-detects it.
            _lastSeenGUI = DateTime.MinValue;
            return;
        }

        // Delete the flag file immediately after reading it, BEFORE launching MSU.
        // MSC's TriggerService polls for this deletion as the "acknowledged" signal.
        // If we wait until after MSU finishes, MSC's 30s timeout expires and throws.
        try
        {
            if (File.Exists(flagFile))
            {
                File.Delete(flagFile);
                _logger.LogInformation("Consumed {UpdateType} flag file (acknowledged)", updateType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete {UpdateType} flag file early", updateType);
        }

        try
        {
            var updateArgs = customArgs ?? (withGUI ? "--auto --show-status -vv" : "--auto --show-status");
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

            // If GUI mode and caller didn't suppress cimistatus, decide between:
            //   - logged-in user → launch cimistatus.exe in their session (existing behaviour)
            //   - pre-logon → leave the UI to the CimianStatusProvider PLAP loaded
            //     by LogonUI.exe, which is already listening on 127.0.0.1:19847.
            if (withGUI && !suppressCimistatus)
            {
                if (SessionProbe.IsInteractiveUserLoggedOn())
                {
                    LaunchCimianStatus();
                }
                else
                {
                    _logger.LogInformation(
                        "No interactive user — leaving UI to CimianStatusProvider PLAP on logon screen");
                }
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

            // Flag file already deleted above (before launching MSU)
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
    /// Checks for pending self-updates and, if one is found, launches the installer as a
    /// detached process then exits immediately so the installer can replace CimianWatcher's
    /// own binary without contending with the running service.  Windows SCM will restart the
    /// service after the installer (and postinstall.ps1) complete.
    /// </summary>
    private void CheckAndPerformSelfUpdate()
    {
        try
        {
            if (!SelfUpdateService.IsSelfUpdatePending())
            {
                _logger.LogInformation("No self-update pending");
                SelfUpdateService.CleanupStaleBackup();
                return;
            }

            _logger.LogInformation("Self-update pending — launching detached installer and exiting");

            bool launched = SelfUpdateService.LaunchDetachedSelfUpdate(msg => _logger.LogInformation("{Msg}", msg));

            if (launched)
            {
                // Exit now so the installer can replace our binary.
                // Windows SCM will restart CimianWatcher once the new binary is in place.
                _logger.LogInformation("Exiting CimianWatcher to allow self-update to proceed");
                Environment.Exit(0);
            }
            else
            {
                _logger.LogError("Failed to launch detached self-update installer");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during self-update check");
        }
    }
}
