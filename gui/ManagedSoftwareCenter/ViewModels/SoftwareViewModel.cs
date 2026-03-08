// SoftwareViewModel.cs - ViewModel for Software page (browse all optional installs)

using System.Collections.ObjectModel;
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

    private List<InstallableItem> _allItems = [];

    [ObservableProperty]
    private ObservableCollection<InstallableItem> _items = [];

    [ObservableProperty]
    private ObservableCollection<string> _categories = [];

    [ObservableProperty]
    private string? _selectedCategory;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyMessage = "No software available";

    [ObservableProperty]
    private InstallableItem? _selectedItem;

    [ObservableProperty]
    private ObservableCollection<InstallableItem> _featuredItems = [];

    [ObservableProperty]
    private bool _hasFeaturedItems;

    public SoftwareViewModel(
        IInstallInfoService installInfoService,
        ISelfServiceManifestService selfServiceService,
        ITriggerService triggerService,
        IIconService iconService)
    {
        _installInfoService = installInfoService;
        _selfServiceService = selfServiceService;
        _triggerService = triggerService;
        _iconService = iconService;

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
            // Load optional installs
            var optionalInstalls = await _installInfoService.GetOptionalInstallsAsync();
            _allItems = optionalInstalls.ToList();
            
            System.Diagnostics.Debug.WriteLine($"SoftwareVM: Loaded {_allItems.Count} optional installs");

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

    private async void OnInstallInfoChanged(object? sender, InstallInfo info)
    {
        // Reload data when catalog changes
        await LoadAsync();
    }
}
