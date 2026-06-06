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
        await TriggerViaFlagFileAsync("--checkonly --show-status -vv");
    }

    /// <inheritdoc />
    public async Task TriggerInstallAsync()
    {
        _logger?.LogInformation("Triggering install via CimianWatcher");
        _currentOperationLabel = "Installing pending updates...";
        await TriggerViaFlagFileAsync("--installonly --show-status -vv");
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

        string mergedArgs;
        string label;
        await _flagFileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pendingItems.Add(itemName))
            {
                _pendingItemOrder.Add(itemName);
            }
            mergedArgs = BootstrapArgsBuilder.BuildSelfServeInstallArgs(_pendingItemOrder);
            label = BuildOperationLabel(_pendingItemOrder);
            _currentOperationLabel = label;
        }
        finally
        {
            _flagFileLock.Release();
        }

        _logger?.LogInformation(
            "Triggering targeted install via CimianWatcher: {Label}", label);
        await TriggerViaFlagFileAsync(mergedArgs).ConfigureAwait(false);
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
    /// Writes the bootstrap flag file with custom arguments and waits for
    /// CimianWatcher to consume it (indicating it launched MSU). Concurrent
    /// callers polling the same file all exit together once it disappears.
    /// </summary>
    private async Task TriggerViaFlagFileAsync(string arguments)
    {
        _isOperationRunning = true;
        OperationStatusChanged?.Invoke(this, true);

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var content = $"Bootstrap triggered at: {timestamp}\nSource: ManagedSoftwareCenter\nArgs: {arguments}\n";

            // Ensure the directory exists (it should, but be safe)
            var dir = Path.GetDirectoryName(BootstrapFlagFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(BootstrapFlagFile, content).ConfigureAwait(false);
            _logger?.LogInformation("Wrote bootstrap flag file with args: {Args}", arguments);

            // Wait for CimianWatcher to consume the flag file
            var elapsed = TimeSpan.Zero;
            while (File.Exists(BootstrapFlagFile) && elapsed < ServiceTimeout)
            {
                await Task.Delay(PollInterval).ConfigureAwait(false);
                elapsed += PollInterval;
            }

            if (File.Exists(BootstrapFlagFile))
            {
                _logger?.LogError("CimianWatcher did not consume flag file within {Timeout}s — is the service running?",
                    ServiceTimeout.TotalSeconds);

                // Clean up the stale flag file
                try { File.Delete(BootstrapFlagFile); } catch { }

                await ClearPendingItemsAsync().ConfigureAwait(false);
                _isOperationRunning = false;
                _currentOperationLabel = null;
                OperationStatusChanged?.Invoke(this, false);
                throw new InvalidOperationException(
                    "CimianWatcher service did not respond. Ensure the service is running: sc query CimianWatcher");
            }

            _logger?.LogInformation("CimianWatcher consumed flag file — managedsoftwareupdate is running");

            // The service has picked up the flag file and launched MSU.
            // Clear the pending set so any subsequent click (after this point)
            // starts a fresh batch instead of re-issuing items already handed
            // off to the currently-running MSU. Completion of the install run
            // itself is still signaled by the InstallInfo.yaml FileSystemWatcher
            // and the StatusReporter pipe — same as before.
            await ClearPendingItemsAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw; // re-throw the service-not-running error
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to trigger via flag file");
            await ClearPendingItemsAsync().ConfigureAwait(false);
            _isOperationRunning = false;
            _currentOperationLabel = null;
            OperationStatusChanged?.Invoke(this, false);
        }
    }

    private async Task ClearPendingItemsAsync()
    {
        await _flagFileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _pendingItems.Clear();
            _pendingItemOrder.Clear();
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
