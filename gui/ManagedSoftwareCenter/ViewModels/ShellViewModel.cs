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
    private readonly IPreferencesService _preferencesService;
    private readonly IAlertService _alertService;

    [ObservableProperty]
    public partial int AvailableCount { get; set; }

    [ObservableProperty]
    public partial int MyItemsCount { get; set; }

    [ObservableProperty]
    public partial int UpdatesCount { get; set; }

    [ObservableProperty]
    public partial string LastCheckedText { get; set; } = "Never checked";

    [ObservableProperty]
    public partial bool CanRefresh { get; set; } = true;

    [ObservableProperty]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial string ProgressMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool CanStopInstall { get; set; }

    [ObservableProperty]
    public partial string? NavigateToPage { get; set; }

    [ObservableProperty]
    public partial object? NavigationParameter { get; set; }

    [ObservableProperty]
    public partial string? DeadlineWarningText { get; set; }

    [ObservableProperty]
    public partial bool IsObnoxiousMode { get; set; }

    /// <summary>
    /// Raised when a managedsoftwareupdate session completes so pages can reload
    /// </summary>
    public event EventHandler? SessionCompleted;

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
        ICatalogCacheService cacheService,
        IPreferencesService preferencesService,
        IAlertService alertService)
    {
        _installInfoService = installInfoService;
        _selfServiceService = selfServiceService;
        _progressClient = progressClient;
        _triggerService = triggerService;
        _notificationService = notificationService;
        _cacheService = cacheService;
        _preferencesService = preferencesService;
        _alertService = alertService;

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
            // Trigger failed - might not have permission or can't contact server
            CanRefresh = true;
            IsInstalling = false;
            try
            {
                await _alertService.ShowInfoAsync(
                    "Update Check Failed",
                    "Could not check for updates. The update server may be unavailable, or the update tool is not installed.\n\nPlease try again later. If this continues, contact your systems administrator.");
            }
            catch
            {
                // Dialog might fail during startup
            }
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

        // Check for obnoxious mode — enter when any item has been pending
        // longer than AggressiveNotificationDays
        var threshold = _preferencesService.AggressiveNotificationDays;
        var overdueItems = allItems.Where(x =>
            x.ForceInstallAfterDate.HasValue &&
            x.ForceInstallAfterDate.Value < DateTime.Now).ToList();

        if (overdueItems.Count > 0)
        {
            // Forced items are past their deadline — go obnoxious
            IsObnoxiousMode = true;
            NavigateToPage = "updates";
        }
        else if (UpdatesCount > 0 && threshold > 0)
        {
            // Non-forced items: check if pending for too long using DaysPendingText heuristic
            // The UpdateTrackingService handles the actual tracking; here we check deadlines only
            IsObnoxiousMode = false;
        }
        else
        {
            IsObnoxiousMode = false;
        }
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
                    _ = ShowErrorRecoveryDialogAsync(message);
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
                _ = HandleSessionEndAsync();
                break;
                
            case ProgressMessageType.Error:
                IsInstalling = false;
                CanRefresh = true;
                _ = ShowErrorRecoveryDialogAsync(message);
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

    private async Task ShowErrorRecoveryDialogAsync(ProgressMessage message)
    {
        var itemName = message.ItemName ?? "Unknown";
        var isRemoval = message.Message?.Contains("remov", StringComparison.OrdinalIgnoreCase) == true;

        var title = isRemoval ? "Removal Error" : "Installation Error";
        var detail = !string.IsNullOrEmpty(message.Detail) ? message.Detail : message.Message;
        var guidance = isRemoval
            ? $"The removal of \"{itemName}\" failed.\n\n{detail}\n\nRemoval will be attempted again. If this situation continues, contact your systems administrator."
            : $"The installation of \"{itemName}\" failed.\n\n{detail}\n\nInstallation will be attempted again. If this situation continues, contact your systems administrator.";

        try
        {
            await _alertService.ShowInfoAsync(title, guidance);
        }
        catch
        {
            // Dialog might fail if window is not ready
            _notificationService.ShowInstallFailed(itemName, detail);
        }
    }

    /// <summary>
    /// Full session-end handler: clear caches, reload data, notify pages, show completion
    /// </summary>
    private async Task HandleSessionEndAsync()
    {
        try
        {
            // Clear local cache so stale data isn't shown
            await _cacheService.ClearCacheAsync();

            // Reload preferences in case they changed during update
            await _preferencesService.ReloadAsync();

            // Full data refresh (loads InstallInfo, updates badges)
            await RefreshDataAsync();

            // Notify listening pages to reload their data
            SessionCompleted?.Invoke(this, EventArgs.Empty);

            // Exit obnoxious mode if no more overdue items
            if (IsObnoxiousMode && UpdatesCount == 0)
            {
                IsObnoxiousMode = false;
            }

            // Show completion notification
            _notificationService.ShowUpdatesAvailable(0); // "All up to date" if count is 0
        }
        catch
        {
            // Best-effort session end handling
        }
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
