// UpdatesViewModel.cs - ViewModel for Updates page

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.System.Power;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;

namespace Cimian.GUI.ManagedSoftwareCenter.ViewModels;

/// <summary>
/// ViewModel for the Updates page - shows pending updates, installs, removals, and problem items
/// Matches Munki's Managed Software Center pattern
/// </summary>
public partial class UpdatesViewModel : ObservableObject
{
    private readonly IInstallInfoService _installInfoService;
    private readonly ITriggerService _triggerService;
    private readonly IIconService _iconService;
    private readonly IUpdateTrackingService _updateTrackingService;
    private readonly IAlertService _alertService;

    [ObservableProperty]
    public partial ObservableCollection<InstallableItem> Updates { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<InstallableItem> PendingInstalls { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<InstallableItem> PendingRemovals { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<ProblemItem> ProblemItems { get; set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasUpdates { get; set; }

    [ObservableProperty]
    public partial bool HasPendingInstalls { get; set; }

    [ObservableProperty]
    public partial bool HasPendingRemovals { get; set; }

    [ObservableProperty]
    public partial bool HasProblems { get; set; }

    [ObservableProperty]
    public partial int TotalUpdateCount { get; set; }

    [ObservableProperty]
    public partial bool RequiresRestart { get; set; }

    [ObservableProperty]
    public partial InstallableItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool HasForcedDeadlines { get; set; }

    /// <summary>
    /// True if there is any pending work (installs, updates, or removals)
    /// </summary>
    public bool HasPendingWork => HasUpdates || HasPendingInstalls || HasPendingRemovals;

    public UpdatesViewModel(
        IInstallInfoService installInfoService,
        ITriggerService triggerService,
        IIconService iconService,
        IUpdateTrackingService updateTrackingService,
        IAlertService alertService)
    {
        _installInfoService = installInfoService;
        _triggerService = triggerService;
        _iconService = iconService;
        _updateTrackingService = updateTrackingService;
        _alertService = alertService;

        _installInfoService.InstallInfoChanged += OnInstallInfoChanged;
    }

    /// <summary>
    /// Load updates data
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            // Load managed updates (updates to already installed software)
            var updates = await _installInfoService.GetManagedUpdatesAsync();
            // Sort: forced deadlines first (earliest deadline on top), then by name
            Updates = new ObservableCollection<InstallableItem>(
                updates.OrderBy(x => x.ForceInstallAfterDate.HasValue ? 0 : 1)
                       .ThenBy(x => x.ForceInstallAfterDate ?? DateTime.MaxValue)
                       .ThenBy(x => x.GetDisplayName()));

            // Load pending managed installs (from admin manifest)
            var managedInstalls = await _installInfoService.GetManagedInstallsAsync();
            var pending = managedInstalls.Where(x => x.WillBeInstalled || !x.Installed).ToList();
            PendingInstalls = new ObservableCollection<InstallableItem>(
                pending.OrderBy(x => x.ForceInstallAfterDate.HasValue ? 0 : 1)
                       .ThenBy(x => x.ForceInstallAfterDate ?? DateTime.MaxValue)
                       .ThenBy(x => x.GetDisplayName()));

            // Load pending removals
            var removals = await _installInfoService.GetRemovalsAsync();
            var pendingRemovals = removals.Where(x => x.WillBeRemoved || x.Installed).ToList();
            PendingRemovals = new ObservableCollection<InstallableItem>(pendingRemovals.OrderBy(x => x.GetDisplayName()));

            // Load problem items
            var problems = await _installInfoService.GetProblemItemsAsync();
            ProblemItems = new ObservableCollection<ProblemItem>(problems);

            // Update flags
            HasUpdates = Updates.Count > 0;
            HasPendingInstalls = PendingInstalls.Count > 0;
            HasPendingRemovals = PendingRemovals.Count > 0;
            HasProblems = ProblemItems.Count > 0;
            TotalUpdateCount = Updates.Count + PendingInstalls.Count + PendingRemovals.Count;
            IsEmpty = TotalUpdateCount == 0 && !HasProblems;
            HasForcedDeadlines = Updates.Concat(PendingInstalls).Any(x => x.HasDeadline);

            // Notify HasPendingWork changed
            OnPropertyChanged(nameof(HasPendingWork));

            // Check if any updates require restart
            RequiresRestart = Updates.Any(x => 
                x.RestartAction?.Equals("restart", StringComparison.OrdinalIgnoreCase) == true ||
                x.RestartAction?.Equals("RequireRestart", StringComparison.OrdinalIgnoreCase) == true) ||
                PendingInstalls.Any(x => 
                    x.RestartAction?.Equals("restart", StringComparison.OrdinalIgnoreCase) == true ||
                    x.RestartAction?.Equals("RequireRestart", StringComparison.OrdinalIgnoreCase) == true);

            // Load icons for all items
            foreach (var item in Updates.Concat(PendingInstalls).Concat(PendingRemovals))
            {
                item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);
            }

            // Track days pending for updates and pending installs
            var allPending = Updates.Concat(PendingInstalls).ToList();
            foreach (var item in allPending)
            {
                await _updateTrackingService.TrackItemAsync(item.Name);
                var daysPending = await _updateTrackingService.GetDaysPendingAsync(item.Name);
                if (daysPending is > 2)
                {
                    item.DaysPendingText = $"Pending for {daysPending} days";
                }
            }
            await _updateTrackingService.PruneAsync(allPending.Select(x => x.Name));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Trigger an update check
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        IsLoading = true;
        try
        {
            await _triggerService.TriggerCheckAsync();
            // Wait a moment for the check to start, then reload
            await Task.Delay(1000);
            await LoadAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task InstallAllUpdatesAsync()
    {
        if (!HasPendingWork) return;

        // Check battery status before installing
        if (!await CheckBatteryAsync()) return;

        // Trigger installation of all pending updates
        await _triggerService.TriggerInstallAsync();
    }

    [RelayCommand]
    private async Task InstallUpdateAsync(InstallableItem item)
    {
        // Check battery status before installing
        if (!await CheckBatteryAsync()) return;

        // For individual update installation
        await _triggerService.TriggerInstallAsync();
    }

    private async Task<bool> CheckBatteryAsync()
    {
        try
        {
            var status = PowerManager.BatteryStatus;
            if (status == BatteryStatus.Discharging)
            {
                return await _alertService.ShowWarningAsync(
                    "Running on Battery",
                    "Your computer is not plugged in. Installing updates on battery power may cause problems if the battery runs out during installation.",
                    "Install Anyway",
                    "Cancel");
            }
        }
        catch
        {
            // Desktop without battery — no warning needed
        }
        return true;
    }

    private async void OnInstallInfoChanged(object? sender, InstallInfo info)
    {
        await LoadAsync();
    }
}
