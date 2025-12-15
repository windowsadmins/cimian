// ItemDetailPage.xaml.cs - Code-behind for Item Detail page (WPF/ModernWpf)

using System.Windows;
using System.Windows.Controls;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;
using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

/// <summary>
/// Item Detail page - shows full details for a software item
/// </summary>
public partial class ItemDetailPage : Page
{
    public ItemDetailViewModel ViewModel { get; }
    private object? _navigationParameter;

    public ItemDetailPage()
    {
        ViewModel = App.GetService<ItemDetailViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        Loaded += async (s, e) =>
        {
            if (_navigationParameter is InstallableItem item)
            {
                await ViewModel.LoadAsync(item);
            }
            else if (_navigationParameter is string itemName)
            {
                await ViewModel.LoadAsync(itemName);
            }
        };
    }

    public ItemDetailPage(object? parameter) : this()
    {
        _navigationParameter = parameter;
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
                    break;
                case nameof(ViewModel.Item):
                    UpdateItemDisplay();
                    break;
                case nameof(ViewModel.ShowInstallButton):
                    InstallButton.Visibility = ViewModel.ShowInstallButton ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.ShowRemoveButton):
                    RemoveButton.Visibility = ViewModel.ShowRemoveButton ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.ShowCancelButton):
                    CancelButton.Visibility = ViewModel.ShowCancelButton ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.ShowStatusBadge):
                    StatusBadge.Visibility = ViewModel.ShowStatusBadge ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.StatusText):
                    StatusText.Text = ViewModel.StatusText;
                    break;
            }
        });
    }

    private void UpdateItemDisplay()
    {
        var item = ViewModel.Item;
        if (item == null) return;

        TitleText.Text = item.DisplayName ?? "Software Details";
        DisplayNameText.Text = item.DisplayName ?? "";
        DeveloperText.Text = item.Developer ?? "";
        DeveloperText.Visibility = string.IsNullOrEmpty(item.Developer) ? Visibility.Collapsed : Visibility.Visible;
        VersionText.Text = item.Version ?? "";
        SizeText.Text = FormatFileSize(item.InstallerSize);
        CategoryText.Text = item.Category ?? "";
        DescriptionText.Text = item.Description ?? "";
        
        // Info section
        InfoVersionText.Text = item.Version ?? "";
        InfoCategoryText.Text = item.Category ?? "";
        InfoSizeText.Text = FormatFileSize(item.InstallerSize);
        
        // Installed version
        if (!string.IsNullOrEmpty(item.InstalledVersion))
        {
            InstalledVersionPanel.Visibility = Visibility.Visible;
            InstalledVersionText.Text = item.InstalledVersion;
        }
        else
        {
            InstalledVersionPanel.Visibility = Visibility.Collapsed;
        }
        
        // Developer
        if (!string.IsNullOrEmpty(item.Developer))
        {
            DeveloperPanel.Visibility = Visibility.Visible;
            InfoDeveloperText.Text = item.Developer;
        }
        else
        {
            DeveloperPanel.Visibility = Visibility.Collapsed;
        }
        
        // Restart required
        RestartPanel.Visibility = item.RestartRequired ? Visibility.Visible : Visibility.Collapsed;
        
        // Release notes (from ReleaseNotes or Notes)
        var releaseNotes = item.ReleaseNotes ?? item.Notes;
        if (!string.IsNullOrEmpty(releaseNotes))
        {
            WhatsNewPanel.Visibility = Visibility.Visible;
            ReleaseNotesText.Text = releaseNotes;
        }
        else
        {
            WhatsNewPanel.Visibility = Visibility.Collapsed;
        }
        
        // Update action buttons
        InstallButton.Visibility = ViewModel.ShowInstallButton ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = ViewModel.ShowRemoveButton ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = ViewModel.ShowCancelButton ? Visibility.Visible : Visibility.Collapsed;
        StatusBadge.Visibility = ViewModel.ShowStatusBadge ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = ViewModel.StatusText;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "Unknown";
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (NavigationService?.CanGoBack == true)
        {
            NavigationService.GoBack();
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.InstallCommand.Execute(null);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveCommand.Execute(null);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCommand.Execute(null);
    }
}
