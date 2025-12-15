// CategoriesPage.xaml.cs - Code-behind for Categories page (WPF/ModernWpf)

using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

/// <summary>
/// Categories page - browse software organized by category
/// </summary>
public partial class CategoriesPage : Page
{
    public CategoriesViewModel ViewModel { get; }

    public CategoriesPage()
    {
        ViewModel = App.GetService<CategoriesViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        Loaded += async (s, e) =>
        {
            CategoriesList.ItemsSource = ViewModel.Categories;
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
                    CategoriesList.Visibility = ViewModel.IsLoading ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case nameof(ViewModel.IsEmpty):
                    EmptyState.Visibility = ViewModel.IsEmpty && !ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.Categories):
                    CategoriesList.ItemsSource = ViewModel.Categories;
                    break;
            }
        });
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoriesList.SelectedItem is CategoryGroup category)
        {
            // Navigate to Software page with category filter
            NavigationService?.Navigate(new SoftwarePage(), category.Name);
            CategoriesList.SelectedItem = null; // Reset selection
        }
    }
}
