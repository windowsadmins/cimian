using Microsoft.UI.Xaml.Controls;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

public partial class HistoryPage : Page
{
    public HistoryViewModel ViewModel { get; }

    public HistoryPage()
    {
        ViewModel = App.GetService<HistoryViewModel>();
        InitializeComponent();
        DataContext = ViewModel;

        Loaded += async (s, e) => await ViewModel.LoadAsync();
    }
}
