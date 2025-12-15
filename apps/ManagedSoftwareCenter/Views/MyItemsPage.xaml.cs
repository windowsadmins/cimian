// MyItemsPage.xaml.cs - Code-behind for My Items page (WPF/ModernWpf)

using System.Windows;
using System.Windows.Controls;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

/// <summary>
/// My Items page - shows user's software selections
/// </summary>
public partial class MyItemsPage : Page
{
    public MyItemsViewModel ViewModel { get; }

    public MyItemsPage()
    {
        ViewModel = App.GetService<MyItemsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        Loaded += async (s, e) =>
        {
            ItemsList.ItemsSource = ViewModel.Items;
            await ViewModel.LoadAsync();
        };
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModel.IsLoading):
                    LoadingIndicator.IsActive = ViewModel.IsLoading;
                    LoadingIndicator.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    ItemsList.Visibility = ViewModel.IsLoading ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case nameof(ViewModel.IsEmpty):
                    EmptyState.Visibility = ViewModel.IsEmpty && !ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.HasPendingActions):
                    FooterPanel.Visibility = ViewModel.HasPendingActions ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.Items):
                    ItemsList.ItemsSource = ViewModel.Items;
                    break;
            }
        });
    }

    private void OnItemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is MyItem item)
        {
            if (Application.Current is App app && app.MainWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToItemDetail(item.Name);
            }
            ItemsList.SelectedItem = null; // Reset selection
        }
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MyItem item)
        {
            await ViewModel.CancelItemCommand.ExecuteAsync(item);
        }
    }

    private void ProcessAll_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.ProcessAllCommand.ExecuteAsync(null);
    }
}
