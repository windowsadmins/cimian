// UpdatesPage.xaml.cs - Code-behind for Updates page (WPF/ModernWpf)
// Munki-style layout with sections for Pending Installs, Updates, Removals, and Problems

using System.Windows;
using System.Windows.Controls;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

/// <summary>
/// Updates page - shows pending installs, updates, removals, and problems
/// Matches Munki's Managed Software Center pattern
/// </summary>
public partial class UpdatesPage : Page
{
    public UpdatesViewModel ViewModel { get; }
    private ShellViewModel? _shellViewModel;

    public UpdatesPage()
    {
        ViewModel = App.GetService<UpdatesViewModel>();
        _shellViewModel = App.GetService<ShellViewModel>();
        
        InitializeComponent();
        DataContext = ViewModel;
        
        // Subscribe to property changes for UI updates
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        // Subscribe to shell progress changes
        if (_shellViewModel != null)
        {
            _shellViewModel.PropertyChanged += ShellViewModel_PropertyChanged;
        }
        
        Loaded += async (s, e) =>
        {
            // Check progress state on load - must be done after UI is ready
            if (_shellViewModel?.IsInstalling == true)
            {
                ShowProgressOverlay();
                UpdateProgressUI();
            }
            else
            {
                await LoadDataAsync();
            }
        };
        
        Unloaded += (s, e) =>
        {
            // Unsubscribe from shell events
            if (_shellViewModel != null)
            {
                _shellViewModel.PropertyChanged -= ShellViewModel_PropertyChanged;
            }
        };
    }

    private void ShowProgressOverlay()
    {
        ProgressOverlay.Visibility = Visibility.Visible;
        ProgressSpinner.IsActive = true;
        MainContent.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingIndicator.Visibility = Visibility.Collapsed;
        LoadingIndicator.IsActive = false;
        InstallNowButton.Visibility = Visibility.Collapsed;
    }

    private void HideProgressOverlay()
    {
        ProgressOverlay.Visibility = Visibility.Collapsed;
        ProgressSpinner.IsActive = false;
        InstallNowButton.Visibility = Visibility.Visible;
    }

    private void UpdateProgressUI()
    {
        if (_shellViewModel == null) return;
        
        ProgressMessageText.Text = _shellViewModel.ProgressMessage;
        ProgressDetailText.Text = _shellViewModel.ProgressDetail;
        
        if (_shellViewModel.IsProgressIndeterminate)
        {
            ProgressBar.IsIndeterminate = true;
            ProgressPercentText.Text = "";
        }
        else
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = _shellViewModel.ProgressPercent;
            ProgressPercentText.Text = $"{_shellViewModel.ProgressPercent}%";
        }
        
        StopButton.Visibility = _shellViewModel.CanStopInstall ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShellViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ShellViewModel.IsInstalling):
                    if (_shellViewModel?.IsInstalling == true)
                    {
                        ShowProgressOverlay();
                    }
                    else
                    {
                        HideProgressOverlay();
                        // Reload data after operation completes
                        _ = LoadDataAsync();
                    }
                    break;
                case nameof(ShellViewModel.ProgressMessage):
                case nameof(ShellViewModel.ProgressDetail):
                case nameof(ShellViewModel.ProgressPercent):
                case nameof(ShellViewModel.IsProgressIndeterminate):
                case nameof(ShellViewModel.CanStopInstall):
                    UpdateProgressUI();
                    break;
            }
        });
    }

    private async Task LoadDataAsync()
    {
        try
        {
            LoadingIndicator.IsActive = true;
            LoadingIndicator.Visibility = Visibility.Visible;
            MainContent.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            StatusText.Text = "Checking for updates...";

            await ViewModel.LoadAsync();
            
            UpdateUI();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            LoadingIndicator.IsActive = false;
            LoadingIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateUI()
    {
        // Update ItemsControl sources
        PendingInstallsList.ItemsSource = ViewModel.PendingInstalls;
        UpdatesList.ItemsSource = ViewModel.Updates;
        RemovalsList.ItemsSource = ViewModel.PendingRemovals;
        ProblemsList.ItemsSource = ViewModel.ProblemItems;

        // Update section visibility
        PendingInstallsSection.Visibility = ViewModel.HasPendingInstalls ? Visibility.Visible : Visibility.Collapsed;
        UpdatesSection.Visibility = ViewModel.HasUpdates ? Visibility.Visible : Visibility.Collapsed;
        RemovalsSection.Visibility = ViewModel.HasPendingRemovals ? Visibility.Visible : Visibility.Collapsed;
        ProblemsSection.Visibility = ViewModel.HasProblems ? Visibility.Visible : Visibility.Collapsed;

        // Update restart warning
        RestartWarning.Visibility = ViewModel.RequiresRestart ? Visibility.Visible : Visibility.Collapsed;

        // Show empty state or main content
        bool hasAnyContent = ViewModel.HasPendingWork || ViewModel.HasProblems;
        MainContent.Visibility = hasAnyContent ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasAnyContent ? Visibility.Collapsed : Visibility.Visible;

        // Update status text
        if (ViewModel.TotalUpdateCount > 0)
        {
            StatusText.Text = $"{ViewModel.TotalUpdateCount} item(s) pending";
        }
        else if (ViewModel.HasProblems)
        {
            StatusText.Text = $"{ViewModel.ProblemItems.Count} problem item(s)";
        }
        else
        {
            StatusText.Text = "All software is up to date";
        }

        // Update Install button state
        InstallNowButton.IsEnabled = ViewModel.HasPendingWork;
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
                default:
                    UpdateUI();
                    break;
            }
        });
    }

    private async void InstallNow_Click(object sender, RoutedEventArgs e)
    {
        // Disable button during install
        if (sender is Button btn)
            btn.IsEnabled = false;

        try
        {
            await ViewModel.InstallAllUpdatesCommand.ExecuteAsync(null);
        }
        finally
        {
            // Re-enable after command completes
            if (sender is Button btn2)
                btn2.IsEnabled = ViewModel.HasPendingWork;
        }
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        // Trigger an update check
        await ViewModel.CheckForUpdatesAsync();
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        // Navigate to item detail when clicked
        if (sender is FrameworkElement fe && fe.DataContext is InstallableItem item)
        {
            if (Application.Current is App app && app.MainWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToItemDetail(item.Name);
            }
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        // Stop the current operation via shell
        _shellViewModel?.StopInstallCommand.Execute(null);
    }
}
