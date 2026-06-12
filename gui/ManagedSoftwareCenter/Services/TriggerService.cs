// TriggerService.cs - Triggers managedsoftwareupdate via CimianWatcher service
// Uses flag-file IPC so the SYSTEM service launches the process — no UAC needed.

using System.IO;
using Cimian.Core.Services;
using Microsoft.Extensions.Logging;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for triggering managedsoftwareupdate operations via CimianWatcher.
/// Writes a flag file with custom arguments; the CimianWatcher service (running
/// as SYSTEM) picks it up and launches managedsoftwareupdate elevated — no UAC
/// prompt required, even for standard users.
/// </summary>
public class TriggerService : ITriggerService, IDisposable
{
    private const string BootstrapFlagFile = @"C:\ProgramData\ManagedInstalls\.cimian.bootstrap";
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<TriggerService>? _logger;
    private readonly SemaphoreSlim _flagFileLock = new(1, 1);
    private readonly HashSet<string> _pendingItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pendingItemOrder = new();

    // A single in-flight Task per targeted-install batch. Concurrent
    // TriggerInstallItemAsync calls that arrive while the flag file from a
    // prior click is still pending watcher consumption rewrite the file with
    // the merged --item list and await the SAME task — otherwise they would
    // start their own polling loops and could recreate the flag file moments
    // after CimianWatcher deleted it, kicking off a second MSU run that
    // races the first.
    private Task? _currentBatch;
    private bool _isOperationRunning;
    private string? _currentOperationLabel;

    public bool IsOperationRunning => _isOperationRunning;

    public string? CurrentOperationLabel => _currentOperationLabel;

    public event EventHandler<bool>? OperationStatusChanged;

    public TriggerService(ILogger<TriggerService>? logger = null)
    {
        _logger = logger;
        _logger?.LogInformation("TriggerService initialized — flag-file IPC via CimianWatcher");
    }

    /// <inheritdoc />
    public async Task TriggerCheckAsync()
    {
        _logger?.LogInformation("Triggering check via CimianWatcher");
        _currentOperationLabel = "Checking for updates...";
        try
        {
            await WriteFlagFileAndWaitAsync("--checkonly --show-status -vv").ConfigureAwait(false);
        }
        finally
        {
            _currentOperationLabel = null;
        }
    }

    /// <inheritdoc />
    public async Task TriggerInstallAsync()
    {
        _logger?.LogInformation("Triggering install via CimianWatcher");
        _currentOperationLabel = "Installing pending updates...";
        try
        {
            await WriteFlagFileAndWaitAsync("--installonly --show-status -vv").ConfigureAwait(false);
        }
        finally
        {
            _currentOperationLabel = null;
        }
    }

    /// <inheritdoc />
    public async Task TriggerInstallItemAsync(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            throw new ArgumentException("itemName must be provided", nameof(itemName));
        }

        // Validate the name up front — BuildSelfServeInstallArgs would throw on
        // control characters too, but catching it here gives a clearer caller
        // stack trace than something coming from inside the merge loop.
        _ = BootstrapArgsBuilder.QuoteArgument(itemName);

        Task batchTask;
        await _flagFileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pendingItems.Add(itemName))
            {
                _pendingItemOrder.Add(itemName);
            }
            var mergedArgs = BootstrapArgsBuilder.BuildSelfServeInstallArgs(_pendingItemOrder);
            _currentOperationLabel = BuildOperationLabel(_pendingItemOrder);

            if (_currentBatch != null && !_currentBatch.IsCompleted)
            {
                // A batch is already polling for consume — just rewrite the
                // flag file with the merged --item list under the same lock so
                // CimianWatcher reads the up-to-date set on its next 10s tick.
                // The existing batch's poll loop is what we await.
                await WriteFlagFileAsync(mergedArgs).ConfigureAwait(false);
                _logger?.LogInformation(
                    "Merged {Item} into in-flight self-serve batch ({Label})",
                    itemName, _currentOperationLabel);
                batchTask = _currentBatch;
            }
            else
            {
                _logger?.LogInformation(
                    "Triggering targeted install via CimianWatcher: {Label}",
                    _currentOperationLabel);
                _currentBatch = RunSelfServeBatchAsync(mergedArgs);
                batchTask = _currentBatch;
            }
        }
        finally
        {
            _flagFileLock.Release();
        }

        await batchTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Composes the human-readable progress label rendered in the shell overlay
    /// while a self-serve run is in flight. Long batches (4+ items) are
    /// summarized as a count so the message stays single-line.
    /// </summary>
    internal static string BuildOperationLabel(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return "Installing requested items...";
        if (items.Count == 1) return $"Installing {items[0]}...";
        if (items.Count <= 3) return $"Installing {string.Join(", ", items)}...";
        return $"Installing {items.Count} items ({items[0]}, {items[1]}, +{items.Count - 2} more)...";
    }

    /// <summary>
    /// Back-compat wrapper for the original internal helper. Existing callers
    /// in this assembly continue to use this name; the implementation lives in
    /// <see cref="BootstrapArgsBuilder.QuoteArgument"/>.
    /// </summary>
    internal static string QuoteArgument(string value) => BootstrapArgsBuilder.QuoteArgument(value);

    /// <inheritdoc />
    public Task TriggerStopAsync()
    {
        // With the flag-file approach we don't hold a process handle.
        // The best we can do is delete the flag file if the service hasn't
        // picked it up yet, or rely on managedsoftwareupdate's own
        // graceful-shutdown logic.
        try
        {
            if (File.Exists(BootstrapFlagFile))
            {
                File.Delete(BootstrapFlagFile);
                _logger?.LogInformation("Deleted bootstrap flag file (stop requested)");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not delete flag file during stop");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Self-serve batch poll loop. Writes the merged --item args, waits for
    /// CimianWatcher to consume the flag file, then clears the pending state
    /// so the next click starts a fresh batch. Runs once per batch; concurrent
    /// callers within the same batch reuse the returned Task.
    /// </summary>
    private async Task RunSelfServeBatchAsync(string initialArgs)
    {
        _isOperationRunning = true;
        RaiseOperationStatusChanged(true);
        try
        {
            await WriteFlagFileAsync(initialArgs).ConfigureAwait(false);
            await WaitForFlagFileConsumedAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            _isOperationRunning = false;
            RaiseOperationStatusChanged(false);
            await ClearBatchStateAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Self-serve batch failed");
            _isOperationRunning = false;
            RaiseOperationStatusChanged(false);
            await ClearBatchStateAsync().ConfigureAwait(false);
            return;
        }

        // Consumed cleanly — pending state cleared so the next click starts a
        // fresh batch. _isOperationRunning stays true; completion of the install
        // is signaled by the InstallInfo.yaml FileSystemWatcher or the
        // StatusReporter pipe, which is also responsible for flipping the
        // overlay back to idle.
        await ClearBatchStateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps a one-shot Check/Install flag-file flow that doesn't need batching.
    /// </summary>
    private async Task WriteFlagFileAndWaitAsync(string arguments)
    {
        _isOperationRunning = true;
        RaiseOperationStatusChanged(true);
        try
        {
            await WriteFlagFileAsync(arguments).ConfigureAwait(false);
            await WaitForFlagFileConsumedAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            _isOperationRunning = false;
            RaiseOperationStatusChanged(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Flag-file trigger failed");
            _isOperationRunning = false;
            RaiseOperationStatusChanged(false);
        }
    }

    /// <summary>
    /// Raises OperationStatusChanged on the UI thread. The poll loops run on
    /// threadpool continuations (ConfigureAwait(false)) and subscribers set
    /// XAML-bound observable properties.
    /// </summary>
    private void RaiseOperationStatusChanged(bool isRunning)
        => UiDispatcher.Post(() => OperationStatusChanged?.Invoke(this, isRunning));

    private async Task WriteFlagFileAsync(string arguments)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var content = $"Bootstrap triggered at: {timestamp}\nSource: ManagedSoftwareCenter\nArgs: {arguments}\n";
        var dir = Path.GetDirectoryName(BootstrapFlagFile);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(BootstrapFlagFile, content).ConfigureAwait(false);
        _logger?.LogInformation("Wrote bootstrap flag file with args: {Args}", arguments);
    }

    private async Task WaitForFlagFileConsumedAsync()
    {
        var elapsed = TimeSpan.Zero;
        while (File.Exists(BootstrapFlagFile) && elapsed < ServiceTimeout)
        {
            await Task.Delay(PollInterval).ConfigureAwait(false);

            if (IsUpdateProcessRunning())
            {
                // CimianWatcher defers flag consumption while a run is in
                // flight (a second launch would just lose the instance lock).
                // The flag file is our queued request, not a dead service —
                // don't count this wait against the timeout.
                elapsed = TimeSpan.Zero;
                continue;
            }

            elapsed += PollInterval;
        }

        if (File.Exists(BootstrapFlagFile))
        {
            _logger?.LogError("CimianWatcher did not consume flag file within {Timeout}s — is the service running?",
                ServiceTimeout.TotalSeconds);
            try { File.Delete(BootstrapFlagFile); } catch { }
            throw new InvalidOperationException(
                "CimianWatcher service did not respond. Ensure the service is running: sc query CimianWatcher");
        }

        _logger?.LogInformation("CimianWatcher consumed flag file — managedsoftwareupdate is running");
    }

    private static bool IsUpdateProcessRunning()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("managedsoftwareupdate");
            var running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        }
        catch
        {
            return false;
        }
    }

    private async Task ClearBatchStateAsync()
    {
        await _flagFileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _pendingItems.Clear();
            _pendingItemOrder.Clear();
            _currentOperationLabel = null;
            _currentBatch = null;
        }
        finally
        {
            _flagFileLock.Release();
        }
    }

    public void Dispose()
    {
        _flagFileLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
