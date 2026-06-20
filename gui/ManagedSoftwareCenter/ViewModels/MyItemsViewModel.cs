// MyItemsViewModel.cs - ViewModel for My Items page (user's selections)

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;

namespace Cimian.GUI.ManagedSoftwareCenter.ViewModels;

/// <summary>
/// Represents a user's item selection with current status
/// </summary>
public class MyItem
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Category { get; set; }
    public string? Icon { get; set; }
    public string Status { get; set; } = ItemStatus.NotInstalled;
    public bool IsInstallRequest { get; set; }
    public bool IsRemovalRequest { get; set; }
    public bool CanCancel { get; set; }
    public InstallableItem? CatalogItem { get; set; }
    public BitmapImage? IconImage { get; set; }

    /// <summary>True once a standing install request is actually installed.</summary>
    public bool IsInstalled => Status == ItemStatus.Installed;

    /// <summary>
    /// Show "Remove" (immediate uninstall) instead of "Cancel" once an installed,
    /// uninstallable item is on the list — there's no pending request to cancel,
    /// the meaningful action is to uninstall it. Pending requests still cancel.
    /// </summary>
    public bool ShowRemove => IsInstallRequest && IsInstalled
        && CatalogItem?.Uninstallable == true;

    /// <summary>Cancel is the action whenever Remove isn't (pending requests).</summary>
    public bool ShowCancel => !ShowRemove;

    /// <summary>Friendly status label for the badge (raw enum is not user-facing).</summary>
    public string StatusText => Status switch
    {
        ItemStatus.Installed => "Installed",
        ItemStatus.NotInstalled => "Not installed",
        ItemStatus.InstallRequested => "Install pending",
        ItemStatus.WillBeInstalled => "Will be installed",
        ItemStatus.RemovalRequested => "Removal pending",
        ItemStatus.WillBeRemoved => "Will be removed",
        ItemStatus.Installing => "Installing...",
        _ => Status
    };
}

/// <summary>
/// ViewModel for the My Items page - shows user's software selections
/// </summary>
public partial class MyItemsViewModel : ObservableObject
{
    private readonly IInstallInfoService _installInfoService;
    private readonly ISelfServiceManifestService _selfServiceService;
    private readonly ITriggerService _triggerService;
    private readonly IIconService _iconService;

    [ObservableProperty]
    public partial ObservableCollection<MyItem> Items { get; set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasPendingActions { get; set; }

    [ObservableProperty]
    public partial MyItem? SelectedItem { get; set; }

    public MyItemsViewModel(
        IInstallInfoService installInfoService,
        ISelfServiceManifestService selfServiceService,
        ITriggerService triggerService,
        IIconService iconService)
    {
        _installInfoService = installInfoService;
        _selfServiceService = selfServiceService;
        _triggerService = triggerService;
        _iconService = iconService;

        _installInfoService.InstallInfoChanged += OnInstallInfoChanged;
    }

    /// <summary>
    /// Load user's items
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var myItems = new List<MyItem>();
            
            // Get self-service selections
            var selfService = await _selfServiceService.GetAllRequestsAsync();

            // Process install requests
            foreach (var itemName in selfService.ManagedInstalls)
            {
                var catalogItem = await _installInfoService.GetItemByNameAsync(itemName);
                
                myItems.Add(new MyItem
                {
                    Name = itemName,
                    DisplayName = catalogItem?.GetDisplayName() ?? itemName,
                    Version = catalogItem?.Version,
                    Category = catalogItem?.Category,
                    Icon = catalogItem?.Icon,
                    // The install request persists after the install completes
                    // (standing subscription) — show the real state, not a
                    // perpetual pending badge.
                    Status = catalogItem?.Installed == true && catalogItem.NeedsUpdate != true
                        ? ItemStatus.Installed
                        : (catalogItem?.WillBeInstalled == true
                            ? ItemStatus.WillBeInstalled
                            : ItemStatus.InstallRequested),
                    IsInstallRequest = true,
                    CanCancel = catalogItem?.Status != ItemStatus.Installing,
                    CatalogItem = catalogItem
                });
            }

            // Process removal requests
            foreach (var itemName in selfService.ManagedUninstalls)
            {
                var catalogItem = await _installInfoService.GetItemByNameAsync(itemName);

                myItems.Add(new MyItem
                {
                    Name = itemName,
                    DisplayName = catalogItem?.GetDisplayName() ?? itemName,
                    Version = catalogItem?.InstalledVersion ?? catalogItem?.Version,
                    Category = catalogItem?.Category,
                    Icon = catalogItem?.Icon,
                    // Removal is only pending while the item is still installed.
                    Status = catalogItem?.Installed != true
                        ? ItemStatus.NotInstalled
                        : (catalogItem.WillBeRemoved
                            ? ItemStatus.WillBeRemoved
                            : ItemStatus.RemovalRequested),
                    IsRemovalRequest = true,
                    CanCancel = catalogItem?.Status != ItemStatus.Installing,
                    CatalogItem = catalogItem
                });
            }

            var ordered = myItems.OrderBy(x => x.DisplayName).ToList();

            // Load icons BEFORE assigning the bound collection — IconImage has no
            // change notification, so a later assignment would not update rows.
            foreach (var item in ordered)
            {
                item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);
            }

            Items = new ObservableCollection<MyItem>(ordered);
            IsEmpty = Items.Count == 0;
            HasPendingActions = Items.Any(x =>
                x.Status == ItemStatus.InstallRequested ||
                x.Status == ItemStatus.RemovalRequested);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CancelItemAsync(MyItem item)
    {
        // Remove from self-service manifest
        await _selfServiceService.RemoveRequestAsync(item.Name);

        // Refresh list
        await LoadAsync();
    }

    /// <summary>
    /// Uninstall an installed item directly from My Items: flip it to a removal
    /// request (drops it from managed_installs, adds to managed_uninstalls) and
    /// kick off a targeted run — same proven path as the Software page Remove.
    /// </summary>
    [RelayCommand]
    private async Task RemoveItemAsync(MyItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Name)) return;

        if (item.CatalogItem != null)
        {
            item.CatalogItem.LiveStage = "pending";
        }

        await _selfServiceService.AddRemovalRequestAsync(item.Name);
        await _triggerService.TriggerInstallItemAsync(item.Name, asRemoval: true);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ProcessAllAsync()
    {
        if (!HasPendingActions) return;

        // Trigger install/removal of pending items
        await _triggerService.TriggerInstallAsync();
    }

    private async void OnInstallInfoChanged(object? sender, InstallInfo info)
    {
        await LoadAsync();
    }
}
