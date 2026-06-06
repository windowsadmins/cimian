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
    /// coalesced into a single `--item N1 --item N2 ...` run so rapid clicks do not
    /// lose entries to a clobbering race.
    /// </summary>
    Task TriggerInstallItemAsync(string itemName);

    /// <summary>
    /// Trigger stop of current operation (during download phase)
    /// </summary>
    Task TriggerStopAsync();

    /// <summary>
    /// Check if an operation is currently running
    /// </summary>
    bool IsOperationRunning { get; }

    /// <summary>
    /// Human-readable description of the in-flight operation, e.g.
    /// "Installing Gimp, Cyberduck...". Null when no operation is active.
    /// Read by the shell to populate the progress overlay so it reflects the
    /// actual work instead of a generic "Checking for updates...".
    /// </summary>
    string? CurrentOperationLabel { get; }

    /// <summary>
    /// Event raised when operation status changes
    /// </summary>
    event EventHandler<bool>? OperationStatusChanged;
}
