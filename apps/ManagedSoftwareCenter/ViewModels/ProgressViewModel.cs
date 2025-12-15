// ProgressViewModel.cs - ViewModel for progress display

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;

namespace Cimian.GUI.ManagedSoftwareCenter.ViewModels;

/// <summary>
/// ViewModel for the progress overlay - shows real-time installation progress
/// </summary>
public partial class ProgressViewModel : ObservableObject
{
    private readonly IProgressPipeClient _progressClient;
    private readonly ITriggerService _triggerService;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _currentItemName = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _detailMessage = string.Empty;

    [ObservableProperty]
    private bool _hasDetailMessage;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private string _downloadSpeed = string.Empty;

    [ObservableProperty]
    private string _timeRemaining = string.Empty;

    [ObservableProperty]
    private bool _isMultipleItems;

    [ObservableProperty]
    private double _overallProgressPercent;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _canStop;

    public ProgressViewModel(IProgressPipeClient progressClient, ITriggerService triggerService)
    {
        _progressClient = progressClient;
        _triggerService = triggerService;
        _progressClient.ProgressReceived += OnProgressReceived;
    }

    [RelayCommand]
    private void Hide()
    {
        IsVisible = false;
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        // Signal stop through trigger service
        await _triggerService.TriggerStopAsync();
        CanStop = false;
        StatusMessage = "Stopping...";
    }

    public void Show(string itemName = "")
    {
        IsVisible = true;
        CurrentItemName = string.IsNullOrEmpty(itemName) ? "Installing..." : itemName;
        StatusMessage = "Preparing...";
        ProgressPercent = 0;
        IsIndeterminate = true;
        CanStop = false;
        DetailMessage = string.Empty;
        HasDetailMessage = false;
        IsDownloading = false;
        IsMultipleItems = false;
    }

    private void OnProgressReceived(object? sender, ProgressMessage message)
    {
        // Update UI properties from progress message
        CurrentItemName = message.ItemName ?? CurrentItemName;
        StatusMessage = message.Message;
        ProgressPercent = Math.Max(0, Math.Min(100, message.Percent));
        IsIndeterminate = message.Percent < 0;
        CanStop = message.StopButtonEnabled;
        
        // Detail message
        DetailMessage = message.Detail ?? string.Empty;
        HasDetailMessage = !string.IsNullOrEmpty(DetailMessage);

        // Download-specific info
        IsDownloading = message.Type == ProgressMessageType.Downloading;
        if (message.BytesReceived > 0 && message.TotalBytes > 0)
        {
            var receivedMB = message.BytesReceived / (1024.0 * 1024.0);
            var totalMB = message.TotalBytes / (1024.0 * 1024.0);
            ProgressText = $"{receivedMB:F1} MB of {totalMB:F1} MB";
        }
        else
        {
            ProgressText = message.Message;
        }

        // Download speed calculation would need timing data
        // For now, just show what we have
        DownloadSpeed = message.DownloadSpeed ?? string.Empty;
        TimeRemaining = message.TimeRemaining ?? string.Empty;

        // Multi-item progress
        TotalCount = message.TotalItems;
        CompletedCount = message.CurrentItemIndex;
        IsMultipleItems = TotalCount > 1;
        if (IsMultipleItems && TotalCount > 0)
        {
            OverallProgressPercent = (CompletedCount * 100.0) / TotalCount;
        }

        // Auto-hide on completion
        if (message.Type == ProgressMessageType.Complete || message.Type == ProgressMessageType.Error)
        {
            StatusMessage = message.Type == ProgressMessageType.Complete ? "Complete!" : "Error occurred";
            IsIndeterminate = false;
            ProgressPercent = message.Type == ProgressMessageType.Complete ? 100 : ProgressPercent;
            
            // Give user a moment to see the final status
            Task.Delay(2000).ContinueWith(_ => 
            {
                IsVisible = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
