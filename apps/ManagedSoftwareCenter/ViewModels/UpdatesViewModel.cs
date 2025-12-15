// UpdatesViewModel.cs - ViewModel for Updates page

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty]
    private ObservableCollection<InstallableItem> _updates = [];

    [ObservableProperty]
    private ObservableCollection<InstallableItem> _pendingInstalls = [];

    [ObservableProperty]
    private ObservableCollection<InstallableItem> _pendingRemovals = [];

    [ObservableProperty]
    private ObservableCollection<ProblemItem> _problemItems = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _hasUpdates;

    [ObservableProperty]
    private bool _hasPendingInstalls;

    [ObservableProperty]
    private bool _hasPendingRemovals;

    [ObservableProperty]
    private bool _hasProblems;

    [ObservableProperty]
    private int _totalUpdateCount;

    [ObservableProperty]
    private bool _requiresRestart;

    [ObservableProperty]
    private InstallableItem? _selectedItem;

    /// <summary>
    /// True if there is any pending work (installs, updates, or removals)
    /// </summary>
    public bool HasPendingWork => HasUpdates || HasPendingInstalls || HasPendingRemovals;

    public UpdatesViewModel(
        IInstallInfoService installInfoService,
        ITriggerService triggerService)
    {
        _installInfoService = installInfoService;
        _triggerService = triggerService;

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
            Updates = new ObservableCollection<InstallableItem>(updates.OrderBy(x => x.GetDisplayName()));

            // Load pending managed installs (from admin manifest)
            var managedInstalls = await _installInfoService.GetManagedInstallsAsync();
            var pending = managedInstalls.Where(x => x.WillBeInstalled || !x.Installed).ToList();
            PendingInstalls = new ObservableCollection<InstallableItem>(pending.OrderBy(x => x.GetDisplayName()));

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

            // Notify HasPendingWork changed
            OnPropertyChanged(nameof(HasPendingWork));

            // Check if any updates require restart
            RequiresRestart = Updates.Any(x => 
                x.RestartAction?.Equals("restart", StringComparison.OrdinalIgnoreCase) == true ||
                x.RestartAction?.Equals("RequireRestart", StringComparison.OrdinalIgnoreCase) == true) ||
                PendingInstalls.Any(x => 
                    x.RestartAction?.Equals("restart", StringComparison.OrdinalIgnoreCase) == true ||
                    x.RestartAction?.Equals("RequireRestart", StringComparison.OrdinalIgnoreCase) == true);
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

        // Trigger installation of all pending updates
        await _triggerService.TriggerInstallAsync();
    }

    [RelayCommand]
    private async Task InstallUpdateAsync(InstallableItem item)
    {
        // For individual update installation
        // Note: In Munki-style workflow, individual updates are installed by triggering
        // the full install process, which will install all pending items
        await _triggerService.TriggerInstallAsync();
    }

    private async void OnInstallInfoChanged(object? sender, InstallInfo info)
    {
        await LoadAsync();
    }
}
