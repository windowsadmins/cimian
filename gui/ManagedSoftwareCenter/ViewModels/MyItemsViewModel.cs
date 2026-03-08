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
                    Status = catalogItem?.WillBeInstalled == true 
                        ? ItemStatus.WillBeInstalled 
                        : ItemStatus.InstallRequested,
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
                    Status = catalogItem?.WillBeRemoved == true 
                        ? ItemStatus.WillBeRemoved 
                        : ItemStatus.RemovalRequested,
                    IsRemovalRequest = true,
                    CanCancel = catalogItem?.Status != ItemStatus.Installing,
                    CatalogItem = catalogItem
                });
            }

            Items = new ObservableCollection<MyItem>(myItems.OrderBy(x => x.DisplayName));
            IsEmpty = Items.Count == 0;

            // Load icons
            foreach (var item in Items)
            {
                item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);
            }
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
