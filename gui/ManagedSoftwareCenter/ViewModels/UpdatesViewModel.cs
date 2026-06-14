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
    private readonly IProgressPipeClient _progressClient;
    private readonly ISelfServiceManifestService _selfServiceService;

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
        IAlertService alertService,
        IProgressPipeClient progressClient,
        ISelfServiceManifestService selfServiceService)
    {
        _installInfoService = installInfoService;
        _triggerService = triggerService;
        _iconService = iconService;
        _updateTrackingService = updateTrackingService;
        _alertService = alertService;
        _progressClient = progressClient;
        _selfServiceService = selfServiceService;

        _installInfoService.InstallInfoChanged += OnInstallInfoChanged;
        _progressClient.ProgressReceived += OnProgressReceived;
        _selfServiceService.RequestsChanged += OnSelfServiceRequestsChanged;
    }

    /// <summary>
    /// A self-serve request was added or cancelled elsewhere (e.g. My Items
    /// "Cancel"). Reload so a cancelled install-requested item drops off the
    /// Updates tab immediately instead of lingering until the next InstallInfo
    /// rewrite. Marshalled to the UI thread — the manifest service may raise
    /// this from a background continuation.
    /// </summary>
    private void OnSelfServiceRequestsChanged(object? sender, EventArgs e)
    {
        UiDispatcher.Post(() => _ = LoadAsync());
    }

    /// <summary>
    /// Routes per-item lifecycle stages from the running managedsoftwareupdate
    /// onto the matching row. Items the engine reports that aren't in the
    /// current list yet (e.g. a fresh self-serve click whose InstallInfo
    /// hasn't been rewritten) get a synthetic row so the user sees progress
    /// immediately. Raised on the UI thread by the progress server.
    /// </summary>
    private async void OnProgressReceived(object? sender, ProgressMessage message)
    {
        if (message.Type != ProgressMessageType.ItemStatus) return;
        if (string.IsNullOrEmpty(message.ItemName)) return;

        var stage = message.Detail;
        var existing = PendingInstalls.Concat(Updates).Concat(PendingRemovals)
            .FirstOrDefault(x => string.Equals(x.Name, message.ItemName, StringComparison.OrdinalIgnoreCase));

        // For "failed", Message carries the reason (e.g. "Exit code 1603") so the
        // user sees the exact code on the row instead of an unchanged "Will be
        // installed". Other stages clear any prior detail.
        var detail = string.Equals(stage, "failed", StringComparison.OrdinalIgnoreCase)
            ? message.Message
            : null;

        if (existing != null)
        {
            existing.LiveStageDetail = detail;
            existing.LiveStage = stage;
            return;
        }

        // Unknown item: synthesize a row. Removal stages go to the removals
        // section, everything else to pending installs.
        var item = new InstallableItem { Name = message.ItemName, LiveStage = stage, LiveStageDetail = detail };
        item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);

        if (stage is "removing" or "removed")
        {
            PendingRemovals.Add(item);
            HasPendingRemovals = true;
        }
        else
        {
            PendingInstalls.Add(item);
            HasPendingInstalls = true;
        }
        IsEmpty = false;
        TotalUpdateCount = Updates.Count + PendingInstalls.Count + PendingRemovals.Count;
        OnPropertyChanged(nameof(HasPendingWork));
    }

    /// <summary>
    /// Load updates data
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            // Updates tab is derived from managed_installs (records for items needing
            // install or update this session). Split by the needs_update flag set by
            // managedsoftwareupdate during the catalog comparison:
            //  - needs_update == true  -> shown under Updates
            //  - needs_update == false -> shown under Pending installs
            // Status checks below are a defensive fallback for any item whose
            // schedule was rewritten by the self-service flow (which doesn't
            // re-evaluate needs_update). The previous version-string comparison
            // was case- and format-sensitive — use NeedsUpdate as the source of
            // truth.
            var managedInstalls = await _installInfoService.GetManagedInstallsAsync();

            var updatesList = managedInstalls
                .Where(x => x.NeedsUpdate ||
                            x.Status == ItemStatus.UpdateAvailable ||
                            x.Status == ItemStatus.UpdateWillBeInstalled)
                .ToList();
            var installsList = managedInstalls.Except(updatesList).ToList();

            // Load pending removals
            var removals = await _installInfoService.GetRemovalsAsync();
            var pendingRemovals = removals.Where(x => x.WillBeRemoved || x.Installed).ToList();

            // Surface self-serve install requests the engine hasn't written into
            // managed_installs yet — a fresh click, or a run that hasn't finished
            // (InstallInfo is only rewritten at the end). Without this an item the
            // user just asked to install ("install-requested") is invisible on the
            // Updates tab until the next full InstallInfo rewrite.
            var selfServe = await _selfServiceService.GetAllRequestsAsync();
            var known = new HashSet<string>(
                updatesList.Concat(installsList).Concat(pendingRemovals).Select(x => x.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var name in selfServe.ManagedInstalls)
            {
                if (string.IsNullOrWhiteSpace(name) || known.Contains(name)) continue;
                var requested = await _installInfoService.GetItemByNameAsync(name);
                if (requested == null) continue;
                if (requested.Installed && !requested.NeedsUpdate) continue; // already satisfied
                requested.Status = ItemStatus.InstallRequested;
                requested.WillBeInstalled = true;
                installsList.Add(requested);
                known.Add(name);
            }

            // Load icons BEFORE assigning the bound collections — IconImage is a
            // plain property without change notification, so a later assignment
            // would not update an already-rendered row.
            foreach (var item in updatesList.Concat(installsList).Concat(pendingRemovals))
            {
                item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);
            }

            Updates = new ObservableCollection<InstallableItem>(
                updatesList.OrderBy(x => x.ForceInstallAfterDate.HasValue ? 0 : 1)
                       .ThenBy(x => x.ForceInstallAfterDate ?? DateTime.MaxValue)
                       .ThenBy(x => x.GetDisplayName()));

            PendingInstalls = new ObservableCollection<InstallableItem>(
                installsList.OrderBy(x => x.ForceInstallAfterDate.HasValue ? 0 : 1)
                       .ThenBy(x => x.ForceInstallAfterDate ?? DateTime.MaxValue)
                       .ThenBy(x => x.GetDisplayName()));

            PendingRemovals = new ObservableCollection<InstallableItem>(pendingRemovals.OrderBy(x => x.GetDisplayName()));

            // Load problem items and surface each error on its own row rather
            // than in a separate section: match a problem to the pending install,
            // update, or removal it belongs to and stamp the row's LiveStageDetail
            // (the red inline reason). Only problems with no matching row — an
            // item that isn't otherwise listed — remain in the standalone section
            // so nothing is silently dropped.
            var problems = await _installInfoService.GetProblemItemsAsync();
            var rowsByName = Updates.Concat(PendingInstalls).Concat(PendingRemovals)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var orphanProblems = new List<ProblemItem>();
            foreach (var problem in problems)
            {
                if (!string.IsNullOrWhiteSpace(problem.Name) &&
                    rowsByName.TryGetValue(problem.Name, out var row))
                {
                    row.LiveStageDetail = problem.ErrorMessage;
                }
                else
                {
                    orphanProblems.Add(problem);
                }
            }
            ProblemItems = new ObservableCollection<ProblemItem>(orphanProblems);

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
        }
        catch (InvalidOperationException ex)
        {
            await _alertService.ShowInfoAsync("Check Error", ex.Message);
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

        // Install exactly the items shown as pending (installs, updates, and
        // removals) via one targeted `--item ... --no-preflight` run. This is
        // the same fast path as a single self-serve click — it skips preflight
        // and the full ~85-item catalog re-evaluation that --installonly forced,
        // and it streams per-item lifecycle stages onto each row. Removals are
        // already classified as uninstalls in the self-serve manifest, so naming
        // them with --item triggers the removal.
        var targets = Updates.Concat(PendingInstalls).Concat(PendingRemovals)
            .Select(x => x.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var removalTargets = PendingRemovals
            .Select(x => x.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        try
        {
            if (targets.Count > 0)
            {
                await _triggerService.TriggerInstallItemsAsync(targets, removalTargets);
            }
            else
            {
                // No named targets (shouldn't happen when HasPendingWork) — fall
                // back to the full install pass.
                await _triggerService.TriggerInstallAsync();
            }
        }
        catch (InvalidOperationException ex)
        {
            await _alertService.ShowInfoAsync("Install Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync(InstallableItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Name)) return;

        // Check battery status before installing
        if (!await CheckBatteryAsync()) return;

        // Install just this row via the fast targeted path (was previously a
        // full --installonly run that ignored the item entirely).
        try
        {
            await _triggerService.TriggerInstallItemAsync(item.Name);
        }
        catch (InvalidOperationException ex)
        {
            await _alertService.ShowInfoAsync("Install Error", ex.Message);
        }
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
