// ShellViewModel.cs - Main window ViewModel
// Manages navigation, progress overlay, and global state

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cimian.GUI.SoftwareCenter.Models;
using Cimian.GUI.SoftwareCenter.Services;

namespace Cimian.GUI.SoftwareCenter.ViewModels;

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

    public bool HasAvailableSoftware => AvailableCount > 0;
    public bool HasMyItems => MyItemsCount > 0;
    public bool HasUpdates => UpdatesCount > 0;
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

        // Notify property changes for visibility bindings
        OnPropertyChanged(nameof(HasAvailableSoftware));
        OnPropertyChanged(nameof(HasMyItems));
        OnPropertyChanged(nameof(HasUpdates));
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
        // Update progress UI
        ProgressMessage = message.Message;
        ProgressDetail = message.Detail ?? string.Empty;
        ProgressPercent = message.Percent;
        IsProgressIndeterminate = message.Percent < 0;
        CanStopInstall = message.StopButtonEnabled;

        OnPropertyChanged(nameof(ProgressPercentText));

        // Handle completion/error
        if (message.Type == ProgressMessageType.Complete)
        {
            IsInstalling = false;
            if (!string.IsNullOrEmpty(message.ItemName))
            {
                _notificationService.ShowInstallComplete(message.ItemName);
            }
        }
        else if (message.Type == ProgressMessageType.Error)
        {
            IsInstalling = false;
            if (!string.IsNullOrEmpty(message.ItemName))
            {
                _notificationService.ShowInstallFailed(message.ItemName, message.Detail);
            }
        }
        else if (message.Type == ProgressMessageType.RestartRequired)
        {
            _notificationService.ShowRestartRequired();
        }
        else if (message.Type == ProgressMessageType.LogoutRequired)
        {
            _notificationService.ShowLogoutRequired();
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
