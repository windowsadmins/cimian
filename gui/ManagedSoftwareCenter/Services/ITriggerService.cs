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
    /// the full preflight pipeline and only touches the affected package.
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
    /// Event raised when operation status changes
    /// </summary>
    event EventHandler<bool>? OperationStatusChanged;
}
