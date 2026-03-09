// SoftwareViewModel.cs - ViewModel for Software page (browse all optional installs)

using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;

namespace Cimian.GUI.ManagedSoftwareCenter.ViewModels;

/// <summary>
/// ViewModel for the Software page - browse all available optional software
/// Implements search and category filtering like Munki MSC
/// </summary>
public partial class SoftwareViewModel : ObservableObject
{
    private readonly IInstallInfoService _installInfoService;
    private readonly ISelfServiceManifestService _selfServiceService;
    private readonly ITriggerService _triggerService;
    private readonly IIconService _iconService;
    private readonly IAlertService _alertService;

    private List<InstallableItem> _allItems = [];

    [ObservableProperty]
    public partial ObservableCollection<InstallableItem> Items { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<string> Categories { get; set; } = [];

    [ObservableProperty]
    public partial string? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = "No software available";

    [ObservableProperty]
    public partial InstallableItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<InstallableItem> FeaturedItems { get; set; } = [];

    [ObservableProperty]
    public partial bool HasFeaturedItems { get; set; }

    public SoftwareViewModel(
        IInstallInfoService installInfoService,
        ISelfServiceManifestService selfServiceService,
        ITriggerService triggerService,
        IIconService iconService,
        IAlertService alertService)
    {
        _installInfoService = installInfoService;
        _selfServiceService = selfServiceService;
        _triggerService = triggerService;
        _iconService = iconService;
        _alertService = alertService;

        // Subscribe to changes
        _installInfoService.InstallInfoChanged += OnInstallInfoChanged;
    }

    /// <summary>
    /// Load initial data
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            // Load all managed items (optional + processed + managed + updates)
            var allItems = await _installInfoService.GetAllItemsAsync();
            _allItems = allItems.ToList();
            
            System.Diagnostics.Debug.WriteLine($"SoftwareVM: Loaded {_allItems.Count} items");

            // Update item statuses based on self-service selections
            await UpdateItemStatusesAsync();

            // Load icons for all items
            await LoadIconsAsync(_allItems);

            // Load featured items
            var installInfo = await _installInfoService.LoadAsync();
            var featured = installInfo.FeaturedItems
                .Select(name => _allItems.FirstOrDefault(i => 
                    string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                .Where(item => item != null)
                .Cast<InstallableItem>()
                .ToList();
            FeaturedItems = new ObservableCollection<InstallableItem>(featured);
            HasFeaturedItems = FeaturedItems.Count > 0;

            // Load categories
            var categories = await _installInfoService.GetCategoriesAsync();
            Categories = new ObservableCollection<string>(new[] { "All" }.Concat(categories));
            SelectedCategory = "All";

            ApplyFilters();
            
            System.Diagnostics.Debug.WriteLine($"SoftwareVM: After filters, Items.Count = {Items.Count}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedCategoryChanged(string? value)
    {
        ApplyFilters();
    }

    private async Task LoadIconsAsync(IEnumerable<InstallableItem> items)
    {
        foreach (var item in items)
        {
            item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);
        }
    }

    private void ApplyFilters()
    {
        var filtered = _allItems.AsEnumerable();

        // Apply category filter
        if (!string.IsNullOrEmpty(SelectedCategory) && SelectedCategory != "All")
        {
            filtered = filtered.Where(x => 
                string.Equals(x.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            filtered = filtered.Where(x =>
                x.GetDisplayName().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (x.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.Developer?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.Category?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Order by display name
        var items = filtered.OrderBy(x => x.GetDisplayName()).ToList();

        Items = new ObservableCollection<InstallableItem>(items);
        IsEmpty = Items.Count == 0;
        EmptyMessage = string.IsNullOrWhiteSpace(SearchText) 
            ? "No software available" 
            : "No results found";
    }

    private async Task UpdateItemStatusesAsync()
    {
        var selfService = await _selfServiceService.GetAllRequestsAsync();

        foreach (var item in _allItems)
        {
            // Check if user has requested install
            if (selfService.ManagedInstalls.Any(x => x.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                item.Status = item.WillBeInstalled ? ItemStatus.WillBeInstalled : ItemStatus.InstallRequested;
                item.UserRequested = true;
            }
            // Check if user has requested removal
            else if (selfService.ManagedUninstalls.Any(x => x.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                item.Status = item.WillBeRemoved ? ItemStatus.WillBeRemoved : ItemStatus.RemovalRequested;
            }
            // Set appropriate status based on installed state
            else if (item.Installed)
            {
                item.Status = !string.IsNullOrEmpty(item.InstalledVersion) && item.InstalledVersion != item.Version
                    ? ItemStatus.UpdateAvailable
                    : ItemStatus.Installed;
            }
            else
            {
                item.Status = ItemStatus.NotInstalled;
            }
        }
    }

    [RelayCommand]
    private async Task InstallItemAsync(InstallableItem item)
    {
        // Check for pre-install or pre-upgrade alert
        var alert = item.Installed ? item.PreupgradeAlert : item.PreinstallAlert;
        if (alert != null)
        {
            var defaultTitle = item.Installed ? "Upgrade Alert" : "Install Alert";
            if (!await _alertService.ShowAlertAsync(alert, defaultTitle))
                return;
        }

        // Check for blocking applications
        if (!await CheckBlockingAppsAsync(item))
            return;

        // Add to self-service manifest
        await _selfServiceService.AddInstallRequestAsync(item.Name);
        
        // Update item status
        item.Status = ItemStatus.InstallRequested;
        item.UserRequested = true;

        // Trigger immediate check/install
        await _triggerService.TriggerInstallAsync();

        // Refresh to show updated status
        OnPropertyChanged(nameof(Items));
    }

    [RelayCommand]
    private async Task RemoveItemAsync(InstallableItem item)
    {
        // Check for pre-uninstall alert
        if (item.PreuninstallAlert != null)
        {
            if (!await _alertService.ShowAlertAsync(item.PreuninstallAlert, "Uninstall Alert"))
                return;
        }

        // Add to self-service manifest for removal
        await _selfServiceService.AddRemovalRequestAsync(item.Name);

        // Update item status
        item.Status = ItemStatus.RemovalRequested;

        // Trigger immediate check/install
        await _triggerService.TriggerInstallAsync();

        // Refresh to show updated status
        OnPropertyChanged(nameof(Items));
    }

    [RelayCommand]
    private async Task CancelRequestAsync(InstallableItem item)
    {
        // Remove from self-service manifest
        await _selfServiceService.RemoveRequestAsync(item.Name);

        // Reset status
        item.UserRequested = false;
        item.Status = item.Installed ? ItemStatus.Installed : ItemStatus.NotInstalled;

        // Refresh to show updated status
        OnPropertyChanged(nameof(Items));
    }

    private async Task<bool> CheckBlockingAppsAsync(InstallableItem item)
    {
        if (item.BlockingApplications is not { Count: > 0 }) return true;

        var running = new List<string>();
        foreach (var app in item.BlockingApplications)
        {
            var processName = Path.GetFileNameWithoutExtension(app);
            if (Process.GetProcessesByName(processName).Length > 0)
                running.Add(app);
        }

        if (running.Count == 0) return true;

        var appList = string.Join(", ", running);
        return await _alertService.ShowWarningAsync(
            "Blocking Applications Running",
            $"Please quit the following applications before installing:\n\n{appList}",
            "Install Anyway",
            "Cancel");
    }

    private async void OnInstallInfoChanged(object? sender, InstallInfo info)
    {
        // Reload data when catalog changes
        await LoadAsync();
    }
}
