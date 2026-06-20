// ITriggerService.cs - Interface for triggering managedsoftwareupdate

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for triggering managedsoftwareupdate via cimiwatcher
/// Uses trigger file mechanism (like Munki's launchd trigger files)
/// </summary>
public interface ITriggerService
{
    /// <summary>
    /// Trigger an update check
    /// </summary>
    Task TriggerCheckAsync();

    /// <summary>
    /// Trigger installation of pending items
    /// </summary>
    Task TriggerInstallAsync();

    /// <summary>
    /// Trigger a fast targeted install/uninstall for a single item just requested via
    /// the self-service manifest. Uses `--item <name> --no-preflight` so the run skips
    /// the full preflight pipeline and only touches the affected package. When called
    /// concurrently before CimianWatcher has consumed the flag file, the names are
    /// coalesced into a single `--item N1 N2 ...` run so rapid clicks do not
    /// lose entries to a clobbering race. Pass <paramref name="asRemoval"/> when the
    /// item was flipped to a removal request so the progress banner reads
    /// "Removing X..." instead of "Installing X...".
    /// </summary>
    Task TriggerInstallItemAsync(string itemName, bool asRemoval = false);

    /// <summary>
    /// Trigger a fast targeted run for a set of items at once — used by the Updates
    /// page "Install Now" so it processes exactly the pending installs/removals via a
    /// single `--item N1 N2 ... --no-preflight` run instead of a full
    /// `--installonly` pass (which re-runs preflight and re-evaluates the entire
    /// catalog from scratch). Names are coalesced with any already-pending self-serve
    /// clicks into one run. Names listed in <paramref name="removalNames"/> are
    /// flagged as removals so the progress banner verb reflects them.
    /// </summary>
    Task TriggerInstallItemsAsync(IEnumerable<string> itemNames, IEnumerable<string>? removalNames = null);

    /// <summary>
    /// Trigger stop of current operation (during download phase)
    /// </summary>
    Task TriggerStopAsync();

    /// <summary>
    /// Check if an operation is currently running
    /// </summary>
    bool IsOperationRunning { get; }

    /// <summary>
    /// True when the in-flight (or most recently launched) operation is targeted
    /// at a specific set of items via <c>--item</c> (a self-serve click or the
    /// Updates "Install Now" over the known pending set). False for broad runs —
    /// a check, an <c>--installonly</c> pass, or an externally launched session.
    /// The shell uses this to render progress inside each item's row for targeted
    /// runs and the global banner for broad ones.
    /// </summary>
    bool IsItemScopedOperation { get; }

    /// <summary>
    /// Human-readable description of the in-flight operation, e.g.
    /// "Installing Gimp, Cyberduck...". Set when a trigger writes the flag
    /// file and cleared once CimianWatcher consumes it (or the call fails).
    /// Read by the shell to populate the progress overlay so it reflects the
    /// actual work instead of a generic "Checking for updates...".
    /// </summary>
    string? CurrentOperationLabel { get; }

    /// <summary>
    /// Event raised when operation status changes
    /// </summary>
    event EventHandler<bool>? OperationStatusChanged;
}
