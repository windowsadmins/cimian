// IAlertService.cs - Interface for showing alert dialogs

using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for showing pre-install/uninstall/upgrade alert dialogs
/// </summary>
public interface IAlertService
{
    /// <summary>
    /// Shows an alert dialog and returns true if the user chose to proceed
    /// </summary>
    Task<bool> ShowAlertAsync(AlertInfo alert, string defaultTitle = "Alert");

    /// <summary>
    /// Shows a warning dialog with a message and returns true if the user chose to proceed
    /// </summary>
    Task<bool> ShowWarningAsync(string title, string message, string okLabel = "OK", string cancelLabel = "Cancel");

    /// <summary>
    /// Shows an informational dialog (OK only)
    /// </summary>
    Task ShowInfoAsync(string title, string message);
}
