// UpdatesPage.xaml.cs - Code-behind for Updates page (WinUI 3)
// Munki-style layout with sections for Pending Installs, Updates, Removals, and Problems

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            _shellViewModel.SessionCompleted += OnSessionCompleted;
        }
        
        Loaded += async (s, e) =>
        {
            // Check progress state on load - must be done after UI is ready.
            // The banner and the item list coexist, so always load the list.
            ApplyRunState();
            await LoadDataAsync();
        };
        
        Unloaded += (s, e) =>
        {
            // The user has seen any finished rows (green check) — let the next
            // load drop them so returning to the page is clean.
            ViewModel.DismissCompletedRows();

            // Unsubscribe from shell events
            if (_shellViewModel != null)
            {
                _shellViewModel.PropertyChanged -= ShellViewModel_PropertyChanged;
                _shellViewModel.SessionCompleted -= OnSessionCompleted;
            }
        };
    }

    // Decides how an in-flight run is presented:
    //  - broad run (check / install-all / external)  -> global banner, with the
    //    item list below showing per-row live stages;
    //  - targeted per-item run                        -> NO banner; progress lives
    //    inside each item's row, and the header shows "Stop" so Cancel is still
    //    reachable without the banner;
    //  - idle                                          -> "Check Again" always
    //    available (re-scan any time), with "Install Now" added beside it only
    //    when there is pending work. Matches the reference MSC behavior.
    private void ApplyRunState()
    {
        bool running = _shellViewModel?.IsInstalling == true;
        bool broad = _shellViewModel?.ShowGlobalBanner == true;

        if (running && broad)
        {
            // Broad run: the banner conveys progress and carries its own Cancel.
            ShowProgressOverlay();
            UpdateProgressUI();
            InstallNowButton.Visibility = Visibility.Collapsed;
            HeaderCheckAgainButton.Visibility = Visibility.Collapsed;
            HeaderStopButton.Visibility = Visibility.Collapsed;
        }
        else if (running && !broad)
        {
            // Item-scoped: hide the banner, keep the list (rows carry the
            // progress), and present Stop in place of Install Now.
            HideBanner();
            MainContent.Visibility = Visibility.Visible;
            InstallNowButton.Visibility = Visibility.Collapsed;
            HeaderCheckAgainButton.Visibility = Visibility.Collapsed;
            HeaderStopButton.Visibility = Visibility.Visible;
        }
        else
        {
            // Idle: Check Again is always offered; Install Now appears beside it
            // only when there is outstanding work.
            HideBanner();
            HeaderStopButton.Visibility = Visibility.Collapsed;
            HeaderCheckAgainButton.Visibility = Visibility.Visible;
            bool hasWork = ViewModel.TotalUpdateCount > 0;
            InstallNowButton.Visibility = hasWork ? Visibility.Visible : Visibility.Collapsed;
            InstallNowButton.IsEnabled = hasWork;
        }
    }

    // The progress banner sits at the top of the content; the item list stays
    // visible underneath with per-row live stages, and View Log remains usable.
    private void ShowProgressOverlay()
    {
        ProgressOverlay.Visibility = Visibility.Visible;
        ProgressSpinner.IsActive = true;
        MainContent.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingIndicator.Visibility = Visibility.Collapsed;
        LoadingIndicator.IsActive = false;
        InstallNowButton.IsEnabled = false;
        StopButton.IsEnabled = true;
    }

    // Collapses the global banner without touching the header buttons — the
    // caller (ApplyRunState) owns Install Now / Stop visibility.
    private void HideBanner()
    {
        ProgressOverlay.Visibility = Visibility.Collapsed;
        ProgressSpinner.IsActive = false;
    }

    private void HideProgressOverlay()
    {
        HideBanner();
        ApplyRunState();
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
    }

    private void ShellViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ShellViewModel.IsInstalling):
                    ApplyRunState();
                    if (_shellViewModel?.IsInstalling != true)
                    {
                        // Reload data after operation completes
                        _ = LoadDataAsync();
                    }
                    break;
                case nameof(ShellViewModel.ShowGlobalBanner):
                    // Scope can flip after IsInstalling is already set (e.g. an
                    // external session connects mid-flight) — re-apply.
                    ApplyRunState();
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
            // Keep content (and the progress banner) on screen during a run.
            if (_shellViewModel?.IsInstalling != true)
            {
                MainContent.Visibility = Visibility.Collapsed;
            }
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

        // Show empty state or main content. While a run is in flight the
        // content (with the progress banner) always stays up, even if the
        // pending list hasn't been populated yet.
        bool hasAnyContent = ViewModel.HasPendingWork || ViewModel.HasProblems
            || _shellViewModel?.IsInstalling == true;
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
            StatusText.Text = "No pending updates";
        }



        // Header buttons (Install Now vs Stop, enablement) follow the run state —
        // ApplyRunState keeps Install Now disabled/hidden during a run and enabled
        // only for outstanding work when idle.
        ApplyRunState();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
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
            // Re-apply run state — if the run launched, Install Now is now hidden
            // behind Stop; if it didn't, it re-enables only for outstanding work.
            ApplyRunState();
        }
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        // Trigger an update check
        await ViewModel.CheckForUpdatesAsync();
    }

    private async void CheckAgain_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckForUpdatesAsync();
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        // Navigate to item detail when clicked
        if (sender is FrameworkElement fe && fe.DataContext is InstallableItem item)
        {
            if (App.MainWindow is MainWindow mainWindow)
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

    // The log opens as a flyout anchored to the button (set via Button.Flyout,
    // shown automatically on click). The flyout's pop-out button promotes it to
    // the standalone LogWindow for users who want a persistent view.
    private bool _logPopOutWired;

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        // Flyout opens itself; nothing to do here.
    }

    private void LogFlyout_Opening(object? sender, object e)
    {
        if (!_logPopOutWired)
        {
            _logPopOutWired = true;
            FlyoutLogViewer.ShowPopOutButton = true;
            FlyoutLogViewer.PopOutRequested += (_, _) =>
            {
                LogFlyout.Hide();
                LogWindow.GetOrActivate();
            };
        }

        FlyoutLogViewer.Start();
    }

    private void LogFlyout_Closed(object? sender, object e)
    {
        FlyoutLogViewer.Stop();
    }

    private void OnSessionCompleted(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => _ = LoadDataAsync());
    }
}
