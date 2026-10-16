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
    // Where user-context tools (Managed Software Center, cimitrigger, CimianStatus) put
    // one file per request; the root files above are writable only by administrators.
    private static readonly string TriggersDir = CimianPaths.TriggersDir;
    private static readonly string CimianExePath = CimianPaths.ManagedSoftwareUpdateExe;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnremovableRequestRetry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RepairRetryInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<FileWatcherService> _logger;
    private readonly object _lock = new();
    
    private DateTime _lastSeenGUI = DateTime.MinValue;
    private DateTime _lastSeenHeadless = DateTime.MinValue;
    // Requests that could not be removed (held open by their writer, say). Skipped for a
    // while so one stuck file cannot hold up everyone else's.
    private readonly Dictionary<string, DateTime> _skipUntil = new(StringComparer.OrdinalIgnoreCase);
    // When this watcher first saw each pending request; the queue order.
    private readonly Dictionary<string, DateTime> _firstSeen = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _lastUserRunStart;
    private bool _isPaused;

    // 1 while a triggered managedsoftwareupdate process is running. New flag
    // files are NOT consumed during that window — managedsoftwareupdate holds
    // an instance lock, so a second launch would just exit with code 1. The
    // flag stays on disk (MSC keeps merging additional --item requests into
    // it) and is consumed on the first poll after the current run exits.
    private int _updateRunning;

    // Run requests are not acted on until a permissions repair has succeeded in this
    // service's lifetime, whichever installer (or none) put this build here.
    private readonly Func<Action<string>, bool> _repair;
    private bool _permissionsSecured;
    private DateTime _nextRepairAttempt = DateTime.MinValue;

    public FileWatcherService(ILogger<FileWatcherService> logger)
        : this(logger, log => ManagedInstallsSecurity.Repair(log, deep: false))
    {
    }

    internal FileWatcherService(ILogger<FileWatcherService> logger, Func<Action<string>, bool> repair)
    {
        _logger = logger;
        _repair = repair;
    }

    /// <summary>
    /// True once ManagedInstalls has been secured. Until then a repair is tried at most
    /// every few minutes, and run requests wait.
    /// </summary>
    internal bool EnsurePermissions(DateTime utcNow)
    {
        if (_permissionsSecured) return true;
        if (utcNow < _nextRepairAttempt) return false;

        _permissionsSecured = _repair(message => _logger.LogInformation("Permissions: {Message}", message));
        if (_permissionsSecured)
        {
            _logger.LogInformation("ManagedInstalls permissions verified; accepting run requests");
        }
        else
        {
            _nextRepairAttempt = utcNow + RepairRetryInterval;
            _logger.LogError(
                "ManagedInstalls permissions could not be secured; holding run requests and retrying in {Minutes} minutes",
                RepairRetryInterval.TotalMinutes);
        }
        return _permissionsSecured;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CimianWatcher file monitoring service started");
        
        // Check for pending self-updates on service start
        CheckAndPerformSelfUpdate();
        
        _logger.LogInformation("Monitoring bootstrap files:");
        _logger.LogInformation("  GUI: {BootstrapFile}", BootstrapFlagFile);
        _logger.LogInformation("  Headless: {HeadlessFile}", HeadlessFlagFile);
        _logger.LogInformation("  User requests: {TriggersDir}", TriggersDir);
        _logger.LogInformation("Poll interval: {Interval} seconds", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_isPaused)
                {
                    if (EnsurePermissions(DateTime.UtcNow))
                    {
                        CheckBootstrapFiles(stoppingToken);
                    }
                    // A staged self-update has its own trust checks, and is how a fixed
                    // build arrives, so it is not held back.
                    ApplyStagedSelfUpdateIfIdle();
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

    /// <summary>
    /// Applies a self-update that an earlier run staged, without waiting for a
    /// service restart or for a flag-file-triggered run.
    ///
    /// The routine hourly run is a scheduled task that invokes
    /// managedsoftwareupdate.exe directly, so it never passes through
    /// RunBootstrapUpdateAsync and the post-run check that lives there. A staged
    /// update therefore sat on disk until the machine rebooted or the nightly
    /// bootstrap fired, which put up to a day between publishing a client and
    /// the fleet running it.
    ///
    /// The installer stops this service and replaces managedsoftwareupdate.exe,
    /// so it must never start while a run is in flight - that truncates the
    /// session mid-install. Hence both guards: the flag we set for our own
    /// triggered runs, and a process check that also covers runs launched by the
    /// scheduled task or by hand.
    /// </summary>
    private void ApplyStagedSelfUpdateIfIdle()
    {
        if (!SelfUpdateService.IsSelfUpdatePending())
            return;

        if (Interlocked.CompareExchange(ref _updateRunning, 1, 0) != 0)
        {
            _logger.LogDebug("Self-update staged, but a triggered update is running - deferring");
            return;
        }

        try
        {
            if (IsManagedSoftwareUpdateRunning())
            {
                _logger.LogDebug(
                    "Self-update staged, but managedsoftwareupdate is running - deferring");
                return;
            }

            _logger.LogInformation("Self-update staged by an earlier run - applying now");
            CheckAndPerformSelfUpdate();
        }
        finally
        {
            // Not reached on the success path: CheckAndPerformSelfUpdate exits
            // the process once the detached installer is running.
            Interlocked.Exchange(ref _updateRunning, 0);
        }
    }

    /// <summary>
    /// True when any managedsoftwareupdate process is running, including one this
    /// service did not launch.
    /// </summary>
    private bool IsManagedSoftwareUpdateRunning()
    {
        try
        {
            return Process.GetProcessesByName("managedsoftwareupdate").Length > 0;
        }
        catch (Exception ex)
        {
            // An unreadable process list counts as busy: deferring costs one poll
            // interval, guessing wrong truncates an install session.
            _logger.LogDebug(ex, "Could not enumerate processes - assuming a run is active");
            return true;
        }
    }

    private void CheckBootstrapFiles(CancellationToken cancellationToken)
    {
        // Check GUI bootstrap file
        CheckFlagFile(BootstrapFlagFile, "GUI", withGUI: true, 
            ref _lastSeenGUI, cancellationToken);
        
        // Check headless bootstrap file
        CheckFlagFile(HeadlessFlagFile, "Headless", withGUI: false, 
            ref _lastSeenHeadless, cancellationToken);

        CheckUserRequests(cancellationToken);
    }

    /// <summary>
    /// Consumes the oldest request in the triggers folder. Each request is its own file,
    /// acted on once and then removed, so the root flags' change-time tracking does not apply.
    /// </summary>
    private void CheckUserRequests(CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(TriggersDir)) return;

            var now = DateTime.UtcNow;
            PendingRequest? next;
            List<PendingRequest> superseded;
            lock (_lock)
            {
                var present = new HashSet<string>(
                    Directory.EnumerateFiles(TriggersDir).Where(TriggerRequests.IsRequestFile),
                    StringComparer.OrdinalIgnoreCase);
                PruneTracking(_firstSeen, _skipUntil, present, now);
                foreach (var path in present)
                {
                    _firstSeen.TryAdd(path, now);
                }

                if (!IsUserRunDue(_lastUserRunStart, now)) return;

                var pending = BuildPending(
                    present.Where(p => !_skipUntil.ContainsKey(p)),
                    p => ManagedInstallsSecurity.TryGetOwner(p)?.Value,
                    p => _firstSeen[p]);
                (next, superseded) = SelectNext(pending);
            }

            foreach (var old in superseded)
            {
                try
                {
                    File.Delete(old.Path);
                    _logger.LogInformation("Dropped an older request from {Owner}; a newer one replaces it", old.Owner);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not drop an older request from {Owner}: {Error}", old.Owner, ex.Message);
                    lock (_lock) { _skipUntil[old.Path] = now + UnremovableRequestRetry; }
                }
            }

            if (next == null) return;

            if (Interlocked.CompareExchange(ref _updateRunning, 1, 0) != 0)
            {
                _logger.LogDebug("User request waiting for the current update to finish");
                return;
            }

            lock (_lock) { _lastUserRunStart = now; }
            var request = next.Value;
            var withGUI = TriggerRequests.IsGuiRequest(request.Path);
            var updateType = withGUI ? "GUI" : "Headless";
            _logger.LogInformation("{UpdateType} user request detected - triggering update", updateType);
            _ = Task.Run(() => TriggerBootstrapUpdateAsync(request.Path, updateType, withGUI,
                requireTrustedOwner: false, cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking user requests");
        }
    }

    /// <summary>A request waiting in triggers\, with when this watcher first saw it.</summary>
    internal readonly record struct PendingRequest(string Path, string Owner, DateTime FirstSeen);

    /// <summary>
    /// Queue entries for request files. A file whose owner cannot be read is left out until
    /// it can be: giving it a place of its own would let anyone who can make an owner
    /// unreadable hold more than one place.
    /// </summary>
    internal static List<PendingRequest> BuildPending(IEnumerable<string> paths, Func<string, string?> ownerOf,
        Func<string, DateTime> firstSeen) =>
        paths.Select(p => (Path: p, Owner: ownerOf(p)))
            .Where(x => x.Owner != null)
            .Select(x => new PendingRequest(x.Path, x.Owner!, firstSeen(x.Path)))
            .ToList();

    /// <summary>Shortest gap between the starts of two user-requested runs.</summary>
    internal static readonly TimeSpan MinUserRunInterval = TimeSpan.FromSeconds(20);

    internal static bool IsUserRunDue(DateTime? lastStart, DateTime now) =>
        lastStart == null || now - lastStart.Value >= MinUserRunInterval;

    /// <summary>
    /// Picks the next request fairly. Each owner has one place in the queue, set by when
    /// the watcher first saw any of their requests (file times belong to the file's owner
    /// and can be backdated). Their newest request is the one that runs; older ones are
    /// returned to be dropped, so no user can hold more than one place.
    /// </summary>
    internal static (PendingRequest? Next, List<PendingRequest> Superseded) SelectNext(IEnumerable<PendingRequest> pending)
    {
        var owners = pending
            .GroupBy(r => r.Owner, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.FirstSeen).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList())
            .OrderBy(g => g[0].FirstSeen)
            .ToList();

        var superseded = owners.SelectMany(g => g.Take(g.Count - 1)).ToList();
        return owners.Count == 0 ? (null, superseded) : (owners[0][^1], superseded);
    }

    /// <summary>Forgets files that are gone and skips that have expired, so tracking stays bounded.</summary>
    internal static void PruneTracking(Dictionary<string, DateTime> firstSeen, Dictionary<string, DateTime> skipUntil,
        ISet<string> present, DateTime now)
    {
        foreach (var gone in firstSeen.Keys.Where(k => !present.Contains(k)).ToList())
        {
            firstSeen.Remove(gone);
        }
        foreach (var done in skipUntil.Where(kv => kv.Value <= now || !present.Contains(kv.Key)).Select(kv => kv.Key).ToList())
        {
            skipUntil.Remove(done);
        }
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
                _ = Task.Run(() => TriggerBootstrapUpdateAsync(flagFile, updateType, withGUI,
                    requireTrustedOwner: true, cancellationToken), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking {UpdateType} flag file", updateType);
        }
    }

    private async Task TriggerBootstrapUpdateAsync(string flagFile, string updateType, bool withGUI,
        bool requireTrustedOwner, CancellationToken cancellationToken)
    {
        // The caller claimed _updateRunning; release it on every exit path so
        // the next poll can consume a queued flag file.
        try
        {
            await RunBootstrapUpdateAsync(flagFile, updateType, withGUI, requireTrustedOwner, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _updateRunning, 0);
        }
    }

    private async Task RunBootstrapUpdateAsync(string flagFile, string updateType, bool withGUI,
        bool requireTrustedOwner, CancellationToken cancellationToken)
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

        // Read flag file content for optional custom arguments. An "Args:" line lets the
        // MSC GUI request specific modes (--checkonly, --installonly) without the service
        // guessing. Requests are written by the signed-in user and the run below is SYSTEM:
        // the file is read once through a checked handle, and only allow-listed arguments
        // get through. A request that cannot be read or is refused does not run at all.
        string? customArgs = null;
        bool suppressCimistatus = false;
        bool rejected = false;
        if (ManagedInstallsSecurity.TryReadRequest(flagFile, requireTrustedOwner, out var content, out var readProblem))
        {
            switch (TriggerRequests.Parse(content!, out customArgs, out var refusal))
            {
                case TriggerRequestKind.Custom:
                    suppressCimistatus = true; // caller manages its own UI
                    _logger.LogInformation("Custom args from flag file: {Args}", customArgs);
                    break;
                case TriggerRequestKind.Refused:
                    rejected = true;
                    _logger.LogWarning("Refusing {UpdateType} request: {Reason}", updateType, refusal);
                    break;
            }
        }
        else
        {
            rejected = true;
            _logger.LogWarning("Refusing {UpdateType} request: {Reason}", updateType, readProblem);
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
            _logger.LogWarning(ex, "Could not delete {UpdateType} flag file early; skipping it for now", updateType);
            lock (_lock)
            {
                _skipUntil[flagFile] = DateTime.UtcNow + UnremovableRequestRetry;
            }
            rejected = true;
        }

        if (rejected)
        {
            return;
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

        // The run that just finished may have scheduled a self-update. Checking
        // only at service start meant a host that never rebooted kept downloading
        // the new CimianTools MSI every night without ever installing it.
        CheckAndPerformSelfUpdate();
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
