// ItemDetailViewModel.cs - ViewModel for item detail page

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.UI;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;

namespace Cimian.GUI.ManagedSoftwareCenter.ViewModels;

/// <summary>
/// ViewModel for the item detail page - shows full information about a software item
/// </summary>
public partial class ItemDetailViewModel : ObservableObject
{
    private readonly IInstallInfoService _installInfoService;
    private readonly ISelfServiceManifestService _selfServiceService;
    private readonly ITriggerService _triggerService;
    private readonly IIconService _iconService;

    private string _itemName = string.Empty;

    [ObservableProperty]
    private InstallableItem? _item;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _notFound;

    // Action button visibility
    [ObservableProperty]
    private bool _showInstallButton;

    [ObservableProperty]
    private bool _showRemoveButton;

    [ObservableProperty]
    private bool _showCancelButton;

    // Status display
    [ObservableProperty]
    private bool _showStatusBadge;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private Brush _statusBackground = new SolidColorBrush(Colors.Gray);

    [ObservableProperty]
    private Brush _statusForeground = new SolidColorBrush(Colors.White);

    // Screenshots and release notes
    [ObservableProperty]
    private bool _hasScreenshots;

    [ObservableProperty]
    private bool _hasReleaseNotes;

    public ItemDetailViewModel(
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
    /// Load item details from InstallableItem
    /// </summary>
    public async Task LoadAsync(InstallableItem item)
    {
        _itemName = item.Name;
        Item = item;
        IsLoading = true;
        NotFound = false;

        try
        {
            // Load icon
            item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);

            await UpdateStatusAsync();
            
            // Check for screenshots/release notes
            HasScreenshots = false; // TODO: Add screenshot support
            HasReleaseNotes = !string.IsNullOrEmpty(item.Notes);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Load item details by name
    /// </summary>
    public async Task LoadAsync(string itemName)
    {
        _itemName = itemName;
        IsLoading = true;
        NotFound = false;

        try
        {
            var item = await _installInfoService.GetItemByNameAsync(itemName);
            
            if (item == null)
            {
                NotFound = true;
                return;
            }

            Item = item;
            await UpdateStatusAsync();
            
            HasScreenshots = false;
            HasReleaseNotes = !string.IsNullOrEmpty(item.Notes);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task UpdateStatusAsync()
    {
        if (Item == null) return;

        // Check self-service manifest for pending requests
        var isInstallRequested = await _selfServiceService.IsInstallRequestedAsync(Item.Name);
        var isRemovalRequested = await _selfServiceService.IsRemovalRequestedAsync(Item.Name);

        // Determine status and available actions
        if (isInstallRequested)
        {
            StatusText = Item.WillBeInstalled ? "Will be installed" : "Installation requested";
            ShowStatusBadge = true;
            ShowInstallButton = false;
            ShowRemoveButton = false;
            ShowCancelButton = true;
            StatusBackground = new SolidColorBrush(Color.FromArgb(255, 255, 185, 0)); // Yellow
            StatusForeground = new SolidColorBrush(Colors.Black);
        }
        else if (isRemovalRequested)
        {
            StatusText = Item.WillBeRemoved ? "Will be removed" : "Removal requested";
            ShowStatusBadge = true;
            ShowInstallButton = false;
            ShowRemoveButton = false;
            ShowCancelButton = true;
            StatusBackground = new SolidColorBrush(Color.FromArgb(255, 209, 52, 56)); // Red
            StatusForeground = new SolidColorBrush(Colors.White);
        }
        else if (Item.Installed)
        {
            if (!string.IsNullOrEmpty(Item.InstalledVersion) && Item.InstalledVersion != Item.Version)
            {
                StatusText = "Update available";
                ShowInstallButton = true; // Show as "Update"
                ShowStatusBadge = true;
                StatusBackground = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)); // Blue
                StatusForeground = new SolidColorBrush(Colors.White);
            }
            else
            {
                StatusText = "Installed";
                ShowInstallButton = false;
                ShowStatusBadge = true;
                StatusBackground = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16)); // Green
                StatusForeground = new SolidColorBrush(Colors.White);
            }
            
            ShowRemoveButton = Item.Uninstallable;
            ShowCancelButton = false;
        }
        else
        {
            StatusText = string.Empty;
            ShowStatusBadge = false;
            ShowInstallButton = true;
            ShowRemoveButton = false;
            ShowCancelButton = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Item == null || !ShowInstallButton) return;

        await _selfServiceService.AddInstallRequestAsync(Item.Name);
        await _triggerService.TriggerInstallAsync();
        await UpdateStatusAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Item == null || !ShowRemoveButton) return;

        await _selfServiceService.AddRemovalRequestAsync(Item.Name);
        await _triggerService.TriggerInstallAsync();
        await UpdateStatusAsync();
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (Item == null || !ShowCancelButton) return;

        await _selfServiceService.RemoveRequestAsync(Item.Name);
        await UpdateStatusAsync();
    }

    private async void OnInstallInfoChanged(object? sender, InstallInfo info)
    {
        if (!string.IsNullOrEmpty(_itemName))
        {
            await LoadAsync(_itemName);
        }
    }
}
