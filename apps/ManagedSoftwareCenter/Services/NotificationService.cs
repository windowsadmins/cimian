// NotificationService.cs - Windows toast notifications for Software Center
// Uses Microsoft.Toolkit.Uwp.Notifications for native Windows toasts

using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for Windows toast notifications
/// Mirrors Munki MSC notification patterns
/// </summary>
public class NotificationService : INotificationService
{
    private const string AppId = "WindowsAdmins.CimianSoftwareCenter";

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
            // Register for notification activation
            ToastNotificationManagerCompat.OnActivated += OnNotificationActivated;
            _initialized = true;
            _logger?.LogInformation("Notification service initialized");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize notification service");
        }
    }

    private void OnNotificationActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);

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

            new ToastContentBuilder()
                .AddArgument("action", "viewUpdates")
                .AddText(title)
                .AddText("Open Software Center to install updates.")
                .AddButton(new ToastButton()
                    .SetContent("View Updates")
                    .AddArgument("action", "viewUpdates"))
                .AddButton(new ToastButtonDismiss("Later"))
                .Show();

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
            new ToastContentBuilder()
                .AddArgument("action", "viewItem")
                .AddArgument("item", itemName)
                .AddText("Installation complete")
                .AddText($"{itemName} has been installed successfully.")
                .Show();

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
            var builder = new ToastContentBuilder()
                .AddArgument("action", "viewItem")
                .AddArgument("item", itemName)
                .AddText("Installation failed")
                .AddText($"Failed to install {itemName}.");

            if (!string.IsNullOrEmpty(errorMessage))
            {
                builder.AddText(errorMessage);
            }

            builder.Show();

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
            new ToastContentBuilder()
                .AddArgument("action", "restart")
                .AddText("Restart required")
                .AddText("A restart is required to complete software updates.")
                .AddButton(new ToastButton()
                    .SetContent("Restart Now")
                    .AddArgument("action", "restartNow"))
                .AddButton(new ToastButton()
                    .SetContent("Later")
                    .AddArgument("action", "restartLater"))
                .Show();

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
            new ToastContentBuilder()
                .AddArgument("action", "logout")
                .AddText("Logout required")
                .AddText("Please log out to complete software updates.")
                .AddButton(new ToastButton()
                    .SetContent("Log Out Now")
                    .AddArgument("action", "logoutNow"))
                .AddButton(new ToastButtonDismiss("Later"))
                .Show();

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
            ToastNotificationManagerCompat.History.Clear();
            _logger?.LogDebug("Cleared all notifications");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear notifications");
        }
    }
}
