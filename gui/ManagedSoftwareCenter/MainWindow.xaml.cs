// MainWindow.xaml.cs - Main application window with NavigationView shell
// Implements Munki MSC-style navigation: Software, Categories, My Items, Updates

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
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
        var x = (displayArea.WorkArea.Width - 1200) / 2;
        var y = (displayArea.WorkArea.Height - 800) / 2;
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
            }
        });
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Navigate to Software page by default
        NavView.SelectedItem = NavView.MenuItems[0];
        NavigateToPage("software");

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
            ContentFrame.Navigate(pageType, ViewModel.NavigationParameter);
            
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
        ContentFrame.Navigate(typeof(ItemDetailPage), itemName);
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
}
