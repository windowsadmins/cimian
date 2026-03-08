// MainWindow.xaml.cs - Main application window with NavigationView shell
// Implements Munki MSC-style navigation: Software, Categories, My Items, Updates

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using Cimian.GUI.ManagedSoftwareCenter.Services;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;
using Cimian.GUI.ManagedSoftwareCenter.Views;

namespace Cimian.GUI.ManagedSoftwareCenter;

/// <summary>
/// Main application window with NavigationView shell
/// </summary>
public partial class MainWindow : Window
{
    public ShellViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();

        // Set window size and center on screen
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        CenterOnScreen();

        // Get ViewModel from DI
        ViewModel = App.GetService<ShellViewModel>();
        RootGrid.DataContext = ViewModel;

        // Subscribe to ViewModel property changes
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void CenterOnScreen()
    {
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var x = (displayArea.WorkArea.Width - 1400) / 2;
        var y = (displayArea.WorkArea.Height - 900) / 2;
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Update UI based on property changes
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModel.LastCheckedText):
                    LastCheckedText.Text = ViewModel.LastCheckedText;
                    break;
                case nameof(ViewModel.CanRefresh):
                    CheckNowButton.IsEnabled = ViewModel.CanRefresh;
                    break;
                case nameof(ViewModel.UpdatesCount):
                    UpdatesBadge.Value = ViewModel.UpdatesCount;
                    UpdatesBadge.Visibility = ViewModel.UpdatesCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.NavigateToPage):
                    NavigateToPage(ViewModel.NavigateToPage);
                    break;
                case nameof(ViewModel.IsObnoxiousMode):
                    ApplyObnoxiousMode(ViewModel.IsObnoxiousMode);
                    break;
            }
        });
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Apply custom sidebar configuration from preferences
        ApplySidebarConfiguration();

        // Apply custom branding
        _ = ApplyBrandingAsync();

        // Navigate to first page by default
        if (NavView.MenuItems.Count > 0)
        {
            NavView.SelectedItem = NavView.MenuItems[0];
            if (NavView.MenuItems[0] is NavigationViewItem firstItem)
                NavigateToPage(firstItem.Tag?.ToString());
        }

        // Load initial data
        _ = ViewModel.InitializeAsync();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem selectedItem)
        {
            var tag = selectedItem.Tag?.ToString();
            if (!string.IsNullOrEmpty(tag))
            {
                NavigateToPage(tag);
            }
        }
    }

    private void NavView_PaneOpening(NavigationView sender, object args)
    {
        // Show footer content when pane opens
        PaneFooterContent.Visibility = Visibility.Visible;
    }

    private void NavView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        // Hide footer content when pane closes
        PaneFooterContent.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Applies custom sidebar configuration from preferences.
    /// If sidebar_items is set, only shows the specified items in the specified order.
    /// </summary>
    private void ApplySidebarConfiguration()
    {
        var prefs = App.GetService<IPreferencesService>();
        var sidebarItems = prefs.SidebarItems;
        if (sidebarItems == null) return;

        // Build a lookup of the existing XAML-defined nav items by tag
        var existingItems = new Dictionary<string, NavigationViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            var tag = item.Tag?.ToString();
            if (!string.IsNullOrEmpty(tag))
                existingItems[tag] = item;
        }

        NavView.MenuItems.Clear();
        foreach (var tag in sidebarItems)
        {
            if (existingItems.TryGetValue(tag, out var navItem))
                NavView.MenuItems.Add(navItem);
        }
    }

    /// <summary>
    /// Loads and applies custom client branding (app title, sidebar header image).
    /// </summary>
    private async Task ApplyBrandingAsync()
    {
        var branding = App.GetService<IBrandingService>();
        await branding.LoadAsync();

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!string.IsNullOrEmpty(branding.AppTitle))
                Title = branding.AppTitle;

            if (branding.SidebarHeaderImage != null)
            {
                SidebarHeaderImage.Source = branding.SidebarHeaderImage;
                SidebarHeaderImage.Visibility = Visibility.Visible;
            }
        });
    }

    private void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshCommand.Execute(null);
    }



    public void NavigateToPage(string? pageTag)
    {
        if (string.IsNullOrEmpty(pageTag)) return;

        Type? pageType = pageTag switch
        {
            "software" => typeof(SoftwarePage),
            "categories" => typeof(CategoriesPage),
            "myitems" => typeof(MyItemsPage),
            "updates" => typeof(UpdatesPage),
            "detail" => typeof(ItemDetailPage),
            _ => null
        };

        if (pageType != null)
        {
            var transition = pageTag == "detail"
                ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight }
                : (NavigationTransitionInfo)new EntranceNavigationTransitionInfo();
            ContentFrame.Navigate(pageType, ViewModel.NavigationParameter, transition);
            
            // Select the corresponding nav item (for non-detail pages)
            if (pageTag != "detail")
            {
                foreach (var item in NavView.MenuItems)
                {
                    if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == pageTag)
                    {
                        NavView.SelectedItem = navItem;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Navigate to item detail page
    /// </summary>
    public void NavigateToItemDetail(string itemName)
    {
        ViewModel.NavigationParameter = itemName;
        ContentFrame.Navigate(typeof(ItemDetailPage), itemName,
            new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
    }

    /// <summary>
    /// Navigate back to previous page
    /// </summary>
    public void NavigateBack()
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    /// <summary>
    /// Apply or remove obnoxious mode (always-on-top, prevent close/minimize)
    /// </summary>
    private void ApplyObnoxiousMode(bool obnoxious)
    {
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter == null) return;

        if (obnoxious)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMinimizable = false;
            // Move to foreground
            this.Activate();
        }
        else
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsMinimizable = true;
        }
    }

    private void Refresh_Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.CanRefresh)
            ViewModel.RefreshCommand.Execute(null);
    }

    private void Back_Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        NavigateBack();
    }
}
