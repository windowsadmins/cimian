// ShellViewModel.cs - Main window ViewModel
// Manages navigation, progress overlay, and global state

using System.ComponentModel;
using System.Diagnostics;
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

    // True when optional_installs is empty — sidebar collapses and Updates becomes the only view.
    [ObservableProperty]
    public partial bool IsUpdatesOnlyMode { get; set; }

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
        _progressClient.ConnectionChanged += OnProgressConnectionChanged;
        _triggerService.OperationStatusChanged += OnOperationStatusChanged;
    }

    /// <summary>
    /// Initialize the shell - load data and start monitoring
    /// </summary>
    public async Task InitializeAsync()
    {
        // Start watching for changes
        _installInfoService.StartWatching();

        // Detect if managedsoftwareupdate is already running (e.g., launched by
        // cimiwatcher before MSC started).  If so, enter the progress overlay
        // immediately — the StatusReporter inside MSU will auto-reconnect to
        // our ProgressServer on its next SendMessage call.
        if (IsManagedSoftwareUpdateRunning())
        {
            IsInstalling = true;
            CanRefresh = false;
            ProgressMessage = "Update in progress...";
            ProgressDetail = "Waiting for status...";
            ProgressPercent = 0;
            IsProgressIndeterminate = true;
            CanStopInstall = false;
        }

        // Load initial data
        await RefreshDataAsync();
    }

    /// <summary>
    /// Returns true if a managedsoftwareupdate process is currently running.
    /// </summary>
    private static bool IsManagedSoftwareUpdateRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("managedsoftwareupdate");
            var running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        }
        catch
        {
            return false;
        }
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
        // Updates tab count = managed_installs (includes updates) + removals this session
        UpdatesCount = info.ManagedInstalls.Count + info.Removals.Count;

        // Collapse the sidebar when the catalog has no optional software to browse.
        IsUpdatesOnlyMode = AvailableCount == 0;

        // Get user's selections from self-service manifest
        var selfService = await _selfServiceService.GetAllRequestsAsync();
        MyItemsCount = selfService.ManagedInstalls.Count + selfService.ManagedUninstalls.Count;

        // Check for approaching forced install deadlines
        var allItems = info.ManagedInstalls.Concat(info.Removals);
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

        // Always fire SessionCompleted when InstallInfo.yaml changes — this ensures
        // the Updates page reloads with fresh data even when IsInstalling was already
        // false (which happens when the ShellExecute/UAC launcher exits before the
        // actual managedsoftwareupdate process finishes).
        SessionCompleted?.Invoke(this, EventArgs.Empty);

        // Show notification if updates are available (items needing update this
        // session). NeedsUpdate is the authoritative flag set by
        // managedsoftwareupdate after catalog comparison — version-string
        // comparison would miss case and format differences (e.g. "1.0" vs
        // "1.0.0", "RC1" vs "rc1").
        var updateRecordsCount = info.ManagedInstalls.Count(x => x.NeedsUpdate);
        if (updateRecordsCount > 0)
        {
            _notificationService.ShowUpdatesAvailable(updateRecordsCount);
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

    /// <summary>
    /// Called when managedsoftwareupdate connects to or disconnects from our
    /// ProgressServer.  Fires on a background thread, so only touch simple
    /// observable properties here — no async work or XAML interactions.
    /// </summary>
    private void OnProgressConnectionChanged(object? sender, bool connected)
    {
        if (connected && !IsInstalling)
        {
            // MSU just connected — enter progress mode
            IsInstalling = true;
            CanRefresh = false;
            ProgressMessage = "Update in progress...";
            ProgressDetail = string.Empty;
            ProgressPercent = 0;
            IsProgressIndeterminate = true;
            CanStopInstall = false;
        }
        // Disconnect is deliberately NOT handled here.  The "quit" message
        // already triggers ProgressMessageType.Complete → HandleSessionEndAsync.
        // Handling disconnect too would cause double-reloads and races.  If MSU
        // crashes without sending quit, the next InstallInfo.yaml change will
        // fire OnInstallInfoChanged which resets state.
    }

    private void OnOperationStatusChanged(object? sender, bool isRunning)
    {
        if (isRunning)
        {
            // Operation launched — show progress overlay
            IsInstalling = true;
            CanRefresh = false;
            ProgressMessage = "Checking for updates...";
            ProgressDetail = string.Empty;
            ProgressPercent = 0;
            IsProgressIndeterminate = true;
            CanStopInstall = false;
        }
        else
        {
            // isRunning=false only fires here when the launch itself failed (UAC denied,
            // exe not found). Normal completion is handled by OnInstallInfoChanged or
            // ProgressMessageType.Complete. Re-enable the button and hide overlay.
            IsInstalling = false;
            CanRefresh = true;
        }
    }
}
