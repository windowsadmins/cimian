// INotificationService.cs - Interface for Windows toast notifications

namespace Cimian.GUI.SoftwareCenter.Services;

/// <summary>
/// Service for Windows toast notifications
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Initialize the notification service
    /// </summary>
    void Initialize();

    /// <summary>
    /// Show notification for available updates
    /// </summary>
    void ShowUpdatesAvailable(int updateCount);

    /// <summary>
    /// Show notification for successful installation
    /// </summary>
    void ShowInstallComplete(string itemName);

    /// <summary>
    /// Show notification for failed installation
    /// </summary>
    void ShowInstallFailed(string itemName, string? errorMessage = null);

    /// <summary>
    /// Show notification for required restart
    /// </summary>
    void ShowRestartRequired();

    /// <summary>
    /// Show notification for required logout
    /// </summary>
    void ShowLogoutRequired();

    /// <summary>
    /// Clear all notifications from this app
    /// </summary>
    void ClearAllNotifications();
}
