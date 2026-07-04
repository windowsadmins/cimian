using System.Diagnostics;
using Cimian.Core;
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
    private static readonly string BootstrapFlagFile = CimianPaths.BootstrapFlagFile;
    private static readonly string HeadlessFlagFile = CimianPaths.HeadlessFlagFile;
    private static readonly string CimianExePath = CimianPaths.ManagedSoftwareUpdateExe;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<FileWatcherService> _logger;
    private readonly object _lock = new();

    // Event-driven notification for flag files. This is ADDITIVE to the periodic
    // poll below, not a replacement: FileSystemWatcher can silently drop events on
    // internal-buffer overflow and never fires for files created while the service
    // was down, so the poll remains the reliability backstop.
    private FileSystemWatcher? _watcher;

    private DateTime _lastSeenGUI = DateTime.MinValue;
    private DateTime _lastSeenHeadless = DateTime.MinValue;
    private bool _isPaused;

    // 1 while a triggered managedsoftwareupdate process is running. New flag
    // files are NOT consumed during that window — managedsoftwareupdate holds
    // an instance lock, so a second launch would just exit with code 1. The
    // flag stays on disk (MSC keeps merging additional --item requests into
    // it) and is consumed on the first poll after the current run exits.
    private int _updateRunning;

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
        _logger.LogInformation("Poll interval (fallback): {Interval} seconds", PollInterval.TotalSeconds);

        // Start event-driven detection so flag files are picked up near-instantly
        // instead of waiting up to a full poll interval. The poll below still runs
        // as the reliability backstop.
        StartWatching(stoppingToken);

        try
        {
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
        }
        finally
        {
            _watcher?.Dispose();
            _watcher = null;
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

    /// <summary>
    /// Sets up a FileSystemWatcher on the ManagedInstalls root so flag files are
    /// detected the moment they're written instead of on the next poll. Additive to
    /// the periodic poll and startup scan — never a replacement. Failure to start
    /// the watcher is non-fatal; the poll continues to cover flag files.
    /// </summary>
    private void StartWatching(CancellationToken stoppingToken)
    {
        var directory = CimianPaths.ManagedInstallsRoot;
        try
        {
            // The directory normally exists (installer creates it), but ensure it so
            // the watcher can attach on a fresh machine before the first write.
            Directory.CreateDirectory(directory);

            _watcher = new FileSystemWatcher(directory)
            {
                // Matches .cimian.bootstrap / .cimian.headless (and .cimian.selfupdate,
                // which yields a harmless no-op CheckBootstrapFiles pass).
                Filter = ".cimian.*",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            // Created and Changed can both fire for a single write; the claim-slot /
            // lastSeen logic in CheckFlagFile absorbs the duplicate, so no extra
            // debounce is needed here.
            _watcher.Created += (_, e) => OnFlagFileEvent(e, stoppingToken);
            _watcher.Changed += (_, e) => OnFlagFileEvent(e, stoppingToken);
            _watcher.Error += OnWatcherError;

            _logger.LogInformation("Watching {Directory} for flag files (event-driven)", directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start FileSystemWatcher on {Directory} - relying on periodic poll", directory);
        }
    }

    private void OnFlagFileEvent(FileSystemEventArgs e, CancellationToken stoppingToken)
    {
        // FileSystemWatcher callbacks run on threadpool threads; never let one bubble
        // up and tear down the service. Concurrency with the poll is handled by the
        // same _updateRunning interlock in CheckFlagFile.
        try
        {
            if (_isPaused || stoppingToken.IsCancellationRequested)
                return;

            _logger.LogDebug("Flag file event: {ChangeType} {Name}", e.ChangeType, e.Name);
            CheckBootstrapFiles(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling flag file event");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // FileSystemWatcher can drop events on internal-buffer overflow. Log and lean
        // on the periodic poll, which stays the reliability backstop.
        _logger.LogWarning(e.GetException(), "FileSystemWatcher error - falling back to periodic poll");
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
                // Serialize runs: claim the slot before consuming. If a run is
                // active, leave the flag file untouched (and lastSeen unchanged)
                // so this poll re-fires once the current run completes.
                if (Interlocked.CompareExchange(ref _updateRunning, 1, 0) != 0)
                {
                    _logger.LogDebug(
                        "{UpdateType} flag file detected but an update is already running - deferring consumption",
                        updateType);
                    return;
                }

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
        // The caller claimed _updateRunning; release it on every exit path so
        // the next poll can consume a queued flag file.
        try
        {
            await RunBootstrapUpdateAsync(flagFile, updateType, withGUI, cancellationToken);
        }
        finally
        {
            // A flag file still on disk after the run was neither consumed as an
            // ack nor cleared by a converged UpdateEngine run — i.e. a bare
            // bootstrap marker left in place to retry. CheckFlagFile only fires
            // when the modification time advances past lastSeen, so clear lastSeen
            // to force the next poll to re-detect the unchanged marker and retry
            // (the converge loop). Reset before releasing the slot so a concurrent
            // poll can't observe the stale lastSeen and skip the retry. For files
            // consumed as an ack (MSC self-service, headless) this is a no-op —
            // they are already gone.
            if (File.Exists(flagFile))
            {
                if (withGUI) _lastSeenGUI = DateTime.MinValue;
                else _lastSeenHeadless = DateTime.MinValue;
            }
            Interlocked.Exchange(ref _updateRunning, 0);
        }
    }

    private async Task RunBootstrapUpdateAsync(string flagFile, string updateType, bool withGUI,
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

        // A bare GUI marker (.cimian.bootstrap with no Args: line) is written by
        // `managedsoftwareupdate --set-bootstrap-mode` and means "run a real
        // bootstrap and keep looping until converged". It is handled differently
        // from an MSC/self-service request (which always carries an Args: line):
        //   • Launch: --bootstrap (NOT --auto). Bootstrap installs everything;
        //     --auto would trip UpdateEngine's active-user gate
        //     (_auto && IsUserActive()) and restrict the run to non-disruptive
        //     items — the opposite of bootstrap intent.
        //   • Flag lifecycle: DO NOT delete the marker here. UpdateEngine owns it —
        //     it clears the flag via DisableBootstrapMode() on converged success
        //     and leaves it in place on failure so a later poll retries (the
        //     Munki-style converge loop). Deleting it before the agent starts
        //     would strip bootstrap mode (IsBootstrapMode() == File.Exists) and
        //     silently downgrade the run to a restricted --auto run. Nobody polls
        //     a bare marker for deletion, so leaving it is safe.
        bool isBareBootstrapMarker = withGUI && customArgs == null;

        // Delete the flag file immediately after reading it, BEFORE launching MSU.
        // MSC's TriggerService polls for this deletion as the "acknowledged" signal.
        // If we wait until after MSU finishes, MSC's 30s timeout expires and throws.
        // Skipped for a bare bootstrap marker (see above): UpdateEngine owns that flag.
        if (!isBareBootstrapMarker)
        {
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
        }

        try
        {
            var updateArgs = customArgs
                ?? (isBareBootstrapMarker
                    ? "--bootstrap --show-status -vv"
                    : (withGUI ? "--auto --show-status -vv" : "--auto --show-status"));
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

            // If GUI mode and caller didn't suppress cimistatus, launch the status UI
            if (withGUI && !suppressCimistatus)
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
