// NotificationService.cs - Windows toast notifications for Software Center (WinUI 3)
// Uses Microsoft.Windows.AppNotifications from WindowsAppSDK

using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for Windows toast notifications
/// Mirrors Munki MSC notification patterns
/// </summary>
public class NotificationService : INotificationService
{
    private readonly ILogger<NotificationService>? _logger;
    private bool _initialized;

    public NotificationService(ILogger<NotificationService>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void Initialize()
    {
        if (_initialized) return;

        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _initialized = true;
            _logger?.LogInformation("Notification service initialized");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize notification service");
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs e)
    {
        var args = e.Arguments;

        // Handle different actions
        if (args.TryGetValue("action", out var action))
        {
            _logger?.LogDebug("Notification action: {Action}", action);

            switch (action)
            {
                case "viewUpdates":
                    // Navigate to Updates page - handled by App.xaml.cs
                    break;
                case "viewItem":
                    if (args.TryGetValue("item", out var itemName))
                    {
                        // Navigate to item detail - handled by App.xaml.cs
                    }
                    break;
                case "restartNow":
                    // Trigger restart - would need elevation
                    break;
                case "restartLater":
                case "dismiss":
                    // Dismiss notification
                    break;
            }
        }
    }

    /// <inheritdoc />
    public void ShowUpdatesAvailable(int updateCount)
    {
        try
        {
            var title = updateCount == 1 
                ? "1 update available" 
                : $"{updateCount} updates available";

            var builder = new AppNotificationBuilder()
                .AddArgument("action", "viewUpdates")
                .AddText(title)
                .AddText("Open Software Center to install updates.")
                .AddButton(new AppNotificationButton("View Updates")
                    .AddArgument("action", "viewUpdates"))
                .AddButton(new AppNotificationButton("Later")
                    .AddArgument("action", "dismiss"));

            AppNotificationManager.Default.Show(builder.BuildNotification());

            _logger?.LogDebug("Showed updates available notification: {Count}", updateCount);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to show updates available notification");
        }
    }

    /// <inheritdoc />
    public void ShowInstallComplete(string itemName)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "viewItem")
                .AddArgument("item", itemName)
                .AddText("Installation complete")
                .AddText($"{itemName} has been installed successfully.");

            AppNotificationManager.Default.Show(builder.BuildNotification());

            _logger?.LogDebug("Showed install complete notification: {Item}", itemName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to show install complete notification");
        }
    }

    /// <inheritdoc />
    public void ShowInstallFailed(string itemName, string? errorMessage = null)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "viewItem")
                .AddArgument("item", itemName)
                .AddText("Installation failed")
                .AddText($"Failed to install {itemName}.");

            if (!string.IsNullOrEmpty(errorMessage))
            {
                builder.AddText(errorMessage);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());

            _logger?.LogDebug("Showed install failed notification: {Item}", itemName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to show install failed notification");
        }
    }

    /// <inheritdoc />
    public void ShowRestartRequired()
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "restart")
                .AddText("Restart required")
                .AddText("A restart is required to complete software updates.")
                .AddButton(new AppNotificationButton("Restart Now")
                    .AddArgument("action", "restartNow"))
                .AddButton(new AppNotificationButton("Later")
                    .AddArgument("action", "restartLater"));

            AppNotificationManager.Default.Show(builder.BuildNotification());

            _logger?.LogDebug("Showed restart required notification");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to show restart required notification");
        }
    }

    /// <inheritdoc />
    public void ShowLogoutRequired()
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "logout")
                .AddText("Logout required")
                .AddText("Please log out to complete software updates.")
                .AddButton(new AppNotificationButton("Log Out Now")
                    .AddArgument("action", "logoutNow"))
                .AddButton(new AppNotificationButton("Later")
                    .AddArgument("action", "dismiss"));

            AppNotificationManager.Default.Show(builder.BuildNotification());

            _logger?.LogDebug("Showed logout required notification");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to show logout required notification");
        }
    }

    /// <inheritdoc />
    public void ClearAllNotifications()
    {
        try
        {
            AppNotificationManager.Default.RemoveAllAsync().GetAwaiter().GetResult();
            _logger?.LogDebug("Cleared all notifications");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear notifications");
        }
    }

    /// <summary>
    /// Unregister notification manager on shutdown
    /// </summary>
    public void Shutdown()
    {
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to unregister notification manager");
        }
    }
}
