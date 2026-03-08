// ShellViewModel.cs - Main window ViewModel
// Manages navigation, progress overlay, and global state

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;

namespace Cimian.GUI.ManagedSoftwareCenter.ViewModels;

/// <summary>
/// ViewModel for the main application shell
/// Manages navigation state, badges, progress overlay, and global operations
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly IInstallInfoService _installInfoService;
    private readonly ISelfServiceManifestService _selfServiceService;
    private readonly IProgressPipeClient _progressClient;
    private readonly ITriggerService _triggerService;
    private readonly INotificationService _notificationService;
    private readonly ICatalogCacheService _cacheService;

    [ObservableProperty]
    private int _availableCount;

    [ObservableProperty]
    private int _myItemsCount;

    [ObservableProperty]
    private int _updatesCount;

    [ObservableProperty]
    private string _lastCheckedText = "Never checked";

    [ObservableProperty]
    private bool _canRefresh = true;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    private string _progressDetail = string.Empty;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private bool _canStopInstall;

    [ObservableProperty]
    private string? _navigateToPage;

    [ObservableProperty]
    private object? _navigationParameter;

    [ObservableProperty]
    private string? _deadlineWarningText;

    public bool HasAvailableSoftware => AvailableCount > 0;
    public bool HasMyItems => MyItemsCount > 0;
    public bool HasUpdates => UpdatesCount > 0;
    public bool HasDeadlineWarning => !string.IsNullOrEmpty(DeadlineWarningText);
    public string ProgressPercentText => IsProgressIndeterminate ? "" : $"{ProgressPercent}%";

    public ShellViewModel(
        IInstallInfoService installInfoService,
        ISelfServiceManifestService selfServiceService,
        IProgressPipeClient progressClient,
        ITriggerService triggerService,
        INotificationService notificationService,
        ICatalogCacheService cacheService)
    {
        _installInfoService = installInfoService;
        _selfServiceService = selfServiceService;
        _progressClient = progressClient;
        _triggerService = triggerService;
        _notificationService = notificationService;
        _cacheService = cacheService;

        // Subscribe to events
        _installInfoService.InstallInfoChanged += OnInstallInfoChanged;
        _progressClient.ProgressReceived += OnProgressReceived;
        _triggerService.OperationStatusChanged += OnOperationStatusChanged;
    }

    /// <summary>
    /// Initialize the shell - load data and start monitoring
    /// </summary>
    public async Task InitializeAsync()
    {
        // Start watching for changes
        _installInfoService.StartWatching();

        // Load initial data
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!CanRefresh) return;

        CanRefresh = false;
        
        // Initialize progress state BEFORE setting IsInstalling
        ProgressMessage = "Checking for updates...";
        ProgressDetail = "Preparing...";
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        CanStopInstall = false;
        
        // Now set installing - this will trigger UI updates
        IsInstalling = true;
        
        // Navigate to Updates page to show progress
        NavigateToPage = "updates";
        
        try
        {
            // Trigger an update check via cimiwatcher
            await _triggerService.TriggerCheckAsync();

            // The InstallInfoChanged event will fire when the check completes
            // and InstallInfo.yaml is updated
        }
        catch (Exception)
        {
            // Trigger failed - might not have permission
            CanRefresh = true;
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task StopInstallAsync()
    {
        if (!CanStopInstall) return;

        await _progressClient.SendCommandAsync(new CommandMessage { Type = CommandType.Stop });
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            var info = await _installInfoService.LoadAsync();
            await UpdateBadgesAsync(info);

            // Update last checked time
            var lastCheck = await _installInfoService.GetLastCheckTimeAsync();
            LastCheckedText = _cacheService.GetLastUpdatedText(lastCheck);

            // Cache the data for offline access
            await _cacheService.CacheInstallInfoAsync(info);
        }
        catch (Exception)
        {
            // Try to load from cache
            var cached = await _cacheService.GetCachedInstallInfoAsync();
            if (cached != null)
            {
                await UpdateBadgesAsync(cached);
                var timestamp = await _cacheService.GetCacheTimestampAsync();
                LastCheckedText = _cacheService.GetLastUpdatedText(timestamp) + " (cached)";
            }
            else
            {
                LastCheckedText = "Unable to load catalog";
            }
        }
    }

    private async Task UpdateBadgesAsync(InstallInfo info)
    {
        AvailableCount = info.OptionalInstalls.Count;
        UpdatesCount = info.ManagedUpdates.Count + info.ManagedInstalls.Count(x => x.WillBeInstalled);

        // Get user's selections from self-service manifest
        var selfService = await _selfServiceService.GetAllRequestsAsync();
        MyItemsCount = selfService.ManagedInstalls.Count + selfService.ManagedUninstalls.Count;

        // Check for approaching forced install deadlines
        var allItems = info.ManagedUpdates.Concat(info.ManagedInstalls);
        var nextDeadline = allItems
            .Where(x => x.ForceInstallAfterDate.HasValue)
            .OrderBy(x => x.ForceInstallAfterDate!.Value)
            .FirstOrDefault();
        
        if (nextDeadline?.ForceInstallAfterDate != null)
        {
            var remaining = nextDeadline.ForceInstallAfterDate.Value - DateTime.Now;
            if (remaining.TotalDays < 1)
                DeadlineWarningText = $"Urgent: {nextDeadline.GetDisplayName()} must be installed soon!";
            else if (remaining.TotalDays < 3)
                DeadlineWarningText = $"{nextDeadline.GetDisplayName()} must be installed by {nextDeadline.ForceInstallAfterDate.Value:g}";
            else
                DeadlineWarningText = null;
        }
        else
        {
            DeadlineWarningText = null;
        }

        // Notify property changes for visibility bindings
        OnPropertyChanged(nameof(HasAvailableSoftware));
        OnPropertyChanged(nameof(HasMyItems));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(HasDeadlineWarning));
    }

    private async void OnInstallInfoChanged(object? sender, InstallInfo info)
    {
        // Update badges on UI thread
        await UpdateBadgesAsync(info);

        // Update last checked time
        var lastCheck = await _installInfoService.GetLastCheckTimeAsync();
        LastCheckedText = _cacheService.GetLastUpdatedText(lastCheck);

        // Re-enable refresh and end installing state
        CanRefresh = true;
        IsInstalling = false;

        // Show notification if updates are available
        if (info.ManagedUpdates.Count > 0)
        {
            _notificationService.ShowUpdatesAvailable(info.ManagedUpdates.Count);
        }
    }

    private void OnProgressReceived(object? sender, ProgressMessage message)
    {
        // The Go reporter sends separate messages for status, detail, and percent
        // Only update fields that are actually set in this message
        
        switch (message.Type)
        {
            case ProgressMessageType.Status:
                ProgressMessage = message.Message;
                if (message.Error)
                {
                    IsInstalling = false;
                    _notificationService.ShowInstallFailed(message.ItemName ?? "Update", message.Message);
                }
                break;
                
            case ProgressMessageType.Detail:
                ProgressDetail = message.Detail ?? string.Empty;
                break;
                
            case ProgressMessageType.Progress:
                ProgressPercent = message.Percent;
                IsProgressIndeterminate = message.Percent < 0;
                OnPropertyChanged(nameof(ProgressPercentText));
                break;
                
            case ProgressMessageType.Complete:
                IsInstalling = false;
                CanRefresh = true;
                ProgressMessage = "Complete";
                ProgressDetail = string.Empty;
                ProgressPercent = 100;
                IsProgressIndeterminate = false;
                OnPropertyChanged(nameof(ProgressPercentText));
                // Refresh data to show updated state
                _ = RefreshDataAsync();
                break;
                
            case ProgressMessageType.Error:
                IsInstalling = false;
                CanRefresh = true;
                if (!string.IsNullOrEmpty(message.ItemName))
                {
                    _notificationService.ShowInstallFailed(message.ItemName, message.Detail);
                }
                break;
                
            case ProgressMessageType.RestartRequired:
                _notificationService.ShowRestartRequired();
                break;
                
            case ProgressMessageType.LogoutRequired:
                _notificationService.ShowLogoutRequired();
                break;
        }

        // Legacy handling for bundled messages (if Message field is set directly)
        if (message.Type != ProgressMessageType.Status && 
            message.Type != ProgressMessageType.Detail && 
            message.Type != ProgressMessageType.Progress &&
            !string.IsNullOrEmpty(message.Message))
        {
            ProgressMessage = message.Message;
        }
        if (!string.IsNullOrEmpty(message.Detail) && message.Type != ProgressMessageType.Detail)
        {
            ProgressDetail = message.Detail;
        }
        
        CanStopInstall = message.StopButtonEnabled;
    }

    private void OnOperationStatusChanged(object? sender, bool isRunning)
    {
        IsInstalling = isRunning;
        CanRefresh = !isRunning;

        if (isRunning)
        {
            // Reset progress state
            ProgressMessage = "Checking for updates...";
            ProgressDetail = string.Empty;
            ProgressPercent = 0;
            IsProgressIndeterminate = true;
            CanStopInstall = false;
        }
    }
}
