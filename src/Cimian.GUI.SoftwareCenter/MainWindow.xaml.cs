// MainWindow.xaml.cs - Main application window with NavigationView shell
// Implements Munki MSC-style navigation: Software, Categories, My Items, Updates

using System.Windows;
using System.Windows.Controls;
using ModernWpf.Controls;
using Cimian.GUI.SoftwareCenter.ViewModels;
using Cimian.GUI.SoftwareCenter.Views;

namespace Cimian.GUI.SoftwareCenter;

/// <summary>
/// Main application window with NavigationView shell
/// </summary>
public partial class MainWindow : Window
{
    public ShellViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();

        // Get ViewModel from DI
        ViewModel = App.GetService<ShellViewModel>();
        DataContext = ViewModel;

        // Subscribe to ViewModel property changes
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Update UI based on property changes
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModel.LastCheckedText):
                    LastCheckedText.Text = ViewModel.LastCheckedText;
                    break;
                case nameof(ViewModel.CanRefresh):
                    CheckNowButton.IsEnabled = ViewModel.CanRefresh;
                    break;
                case nameof(ViewModel.IsInstalling):
                    ProgressOverlay.Visibility = ViewModel.IsInstalling ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.ProgressMessage):
                    ProgressMessageText.Text = ViewModel.ProgressMessage;
                    break;
                case nameof(ViewModel.ProgressDetail):
                    ProgressDetailText.Text = ViewModel.ProgressDetail;
                    break;
                case nameof(ViewModel.ProgressPercent):
                    InstallProgress.Value = ViewModel.ProgressPercent;
                    InstallProgress.IsIndeterminate = ViewModel.IsProgressIndeterminate;
                    break;
                case nameof(ViewModel.ProgressPercentText):
                    ProgressPercentText.Text = ViewModel.ProgressPercentText;
                    break;
                case nameof(ViewModel.CanStopInstall):
                    StopButton.Visibility = ViewModel.CanStopInstall ? Visibility.Visible : Visibility.Collapsed;
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

    private void StopInstall_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StopInstallCommand.Execute(null);
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
            var page = (System.Windows.Controls.Page)Activator.CreateInstance(pageType)!;
            ContentFrame.Navigate(page, ViewModel.NavigationParameter);
            
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
        var page = new ItemDetailPage();
        ContentFrame.Navigate(page, itemName);
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

