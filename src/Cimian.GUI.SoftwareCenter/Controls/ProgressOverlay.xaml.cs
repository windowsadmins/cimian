// ProgressOverlay.xaml.cs - Code-behind for Progress Overlay control (WPF/ModernWpf)

using System.Windows;
using System.Windows.Controls;
using Cimian.GUI.SoftwareCenter.ViewModels;

namespace Cimian.GUI.SoftwareCenter.Controls;

/// <summary>
/// Progress overlay control - shows real-time installation progress
/// </summary>
public partial class ProgressOverlay : UserControl
{
    public ProgressViewModel ViewModel { get; }

    public ProgressOverlay()
    {
        ViewModel = App.GetService<ProgressViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        // Initialize button commands
        HideButton.Click += (s, e) => ViewModel.HideCommand?.Execute(null);
        StopButton.Click += (s, e) => ViewModel.StopCommand?.Execute(null);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModel.CurrentItemName):
                    CurrentItemNameText.Text = ViewModel.CurrentItemName ?? "Installing...";
                    break;
                case nameof(ViewModel.StatusMessage):
                    StatusMessageText.Text = ViewModel.StatusMessage ?? "";
                    break;
                case nameof(ViewModel.ProgressPercent):
                    MainProgress.Value = ViewModel.ProgressPercent;
                    PercentText.Text = $"{ViewModel.ProgressPercent:0}%";
                    break;
                case nameof(ViewModel.IsIndeterminate):
                    MainProgress.IsIndeterminate = ViewModel.IsIndeterminate;
                    PercentText.Visibility = ViewModel.IsIndeterminate ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case nameof(ViewModel.ProgressText):
                    ProgressText.Text = ViewModel.ProgressText ?? "";
                    break;
                case nameof(ViewModel.HasDetailMessage):
                    DetailMessageBorder.Visibility = ViewModel.HasDetailMessage ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.DetailMessage):
                    DetailMessageText.Text = ViewModel.DetailMessage ?? "";
                    break;
                case nameof(ViewModel.IsDownloading):
                    DownloadStatsPanel.Visibility = ViewModel.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.DownloadSpeed):
                    DownloadSpeedText.Text = ViewModel.DownloadSpeed ?? "";
                    break;
                case nameof(ViewModel.TimeRemaining):
                    TimeRemainingText.Text = ViewModel.TimeRemaining ?? "";
                    break;
                case nameof(ViewModel.IsMultipleItems):
                    MultiItemProgressPanel.Visibility = ViewModel.IsMultipleItems ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.OverallProgressPercent):
                    OverallProgress.Value = ViewModel.OverallProgressPercent;
                    break;
                case nameof(ViewModel.CompletedCount):
                case nameof(ViewModel.TotalCount):
                    MultiItemStatusText.Text = $"{ViewModel.CompletedCount} of {ViewModel.TotalCount} items complete";
                    break;
                case nameof(ViewModel.CanStop):
                    StopButton.IsEnabled = ViewModel.CanStop;
                    break;
            }
        });
    }
}
