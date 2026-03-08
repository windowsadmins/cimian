// AlertService.cs - ContentDialog-based alert service for WinUI 3

using Microsoft.UI.Xaml.Controls;
using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Shows pre-install/uninstall/upgrade alert dialogs using WinUI 3 ContentDialog
/// </summary>
public class AlertService : IAlertService
{
    public async Task<bool> ShowAlertAsync(AlertInfo alert, string defaultTitle = "Alert")
    {
        var title = !string.IsNullOrEmpty(alert.AlertTitle) ? alert.AlertTitle : defaultTitle;
        var detail = alert.AlertDetail ?? string.Empty;
        var okLabel = !string.IsNullOrEmpty(alert.OkLabel) ? alert.OkLabel : "OK";
        var cancelLabel = !string.IsNullOrEmpty(alert.CancelLabel) ? alert.CancelLabel : "Cancel";

        return await ShowWarningAsync(title, detail, okLabel, cancelLabel);
    }

    public async Task<bool> ShowWarningAsync(string title, string message, string okLabel = "OK", string cancelLabel = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = okLabel,
            CloseButtonText = cancelLabel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = App.MainWindow.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = App.MainWindow.Content.XamlRoot
        };

        await dialog.ShowAsync();
    }
}
