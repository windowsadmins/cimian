// ItemDetailViewModel.cs - ViewModel for item detail page

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.UI;
using System.Diagnostics;
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
    private readonly IAlertService _alertService;

    private string _itemName = string.Empty;

    [ObservableProperty]
    public partial InstallableItem? Item { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool NotFound { get; set; }

    // Action button visibility
    [ObservableProperty]
    public partial bool ShowInstallButton { get; set; }

    [ObservableProperty]
    public partial bool ShowRemoveButton { get; set; }

    [ObservableProperty]
    public partial bool ShowCancelButton { get; set; }

    // Install button label/glyph — switches between "Install" and "Update" based on item state
    [ObservableProperty]
    public partial string InstallButtonLabel { get; set; } = "Install";

    [ObservableProperty]
    public partial string InstallButtonGlyph { get; set; } = "";

    // Status display
    [ObservableProperty]
    public partial bool ShowStatusBadge { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Brush StatusBackground { get; set; } = new SolidColorBrush(Colors.Gray);

    [ObservableProperty]
    public partial Brush StatusForeground { get; set; } = new SolidColorBrush(Colors.White);

    // Screenshots and release notes
    [ObservableProperty]
    public partial bool HasScreenshots { get; set; }

    [ObservableProperty]
    public partial bool HasReleaseNotes { get; set; }

    public ItemDetailViewModel(
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

        _installInfoService.InstallInfoChanged += OnInstallInfoChanged;
    }

    /// <summary>
    /// Load item details from InstallableItem
    /// </summary>
    public async Task LoadAsync(InstallableItem item)
    {
        _itemName = item.Name;
        IsLoading = true;
        NotFound = false;

        try
        {
            // Load icon BEFORE assigning Item so the binding picks it up on first evaluation.
            // (IconImage is a plain property without change notification, so a later assignment
            // would not update the bound Image.)
            item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);
            Item = item;

            await UpdateStatusAsync();

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

            item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);
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
                // Update available: show "Update" button, suppress redundant status badge
                InstallButtonLabel = "Update";
                InstallButtonGlyph = ""; // Sync
                ShowInstallButton = true;
                ShowStatusBadge = false;
                StatusText = string.Empty;
            }
            else
            {
                StatusText = "Installed";
                InstallButtonLabel = "Install";
                InstallButtonGlyph = "";
                ShowInstallButton = false;
                ShowStatusBadge = true;
                StatusBackground = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16)); // Green
                StatusForeground = new SolidColorBrush(Colors.White);
            }

            // Prevent removal if other items depend on this one
            ShowRemoveButton = Item.Uninstallable && (Item.DependentItems == null || Item.DependentItems.Count == 0);
            ShowCancelButton = false;
        }
        else if (Item.Status == ItemStatus.Unavailable)
        {
            StatusText = "Unavailable";
            ShowStatusBadge = true;
            ShowInstallButton = false;
            ShowRemoveButton = false;
            ShowCancelButton = false;
            StatusBackground = new SolidColorBrush(Color.FromArgb(255, 128, 128, 128)); // Gray
            StatusForeground = new SolidColorBrush(Colors.White);
        }
        else
        {
            StatusText = string.Empty;
            ShowStatusBadge = false;
            InstallButtonLabel = "Install";
            InstallButtonGlyph = "";
            ShowInstallButton = true;
            ShowRemoveButton = false;
            ShowCancelButton = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Item == null || !ShowInstallButton) return;

        // Check for pre-install or pre-upgrade alert
        var alert = Item.Installed ? Item.PreupgradeAlert : Item.PreinstallAlert;
        if (alert != null)
        {
            var defaultTitle = Item.Installed ? "Upgrade Alert" : "Install Alert";
            if (!await _alertService.ShowAlertAsync(alert, defaultTitle))
                return;
        }

        // Check for blocking applications
        if (!await CheckBlockingAppsAsync(Item))
            return;

        await _selfServiceService.AddInstallRequestAsync(Item.Name);
        await _triggerService.TriggerInstallAsync();
        await UpdateStatusAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Item == null || !ShowRemoveButton) return;

        // Check for pre-uninstall alert
        if (Item.PreuninstallAlert != null)
        {
            if (!await _alertService.ShowAlertAsync(Item.PreuninstallAlert, "Uninstall Alert"))
                return;
        }

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
        if (!string.IsNullOrEmpty(_itemName))
        {
            await LoadAsync(_itemName);
        }
    }
}
