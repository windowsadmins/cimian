using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Cimian.Status.ViewModels;
using Cimian.Status.Services;

namespace Cimian.Status.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly IStatusServer _statusServer;
        private readonly ILogger<MainWindow> _logger;
        private readonly double _baseHeight = 450;
        private readonly double _expandedHeight = 700;

        public MainWindow(MainViewModel viewModel, IStatusServer statusServer, ILogger<MainWindow> logger)
        {
            InitializeComponent();
            
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _statusServer = statusServer ?? throw new ArgumentNullException(nameof(statusServer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            DataContext = _viewModel;

            // Subscribe to status server events
            _statusServer.MessageReceived += OnStatusMessageReceived;

            // Subscribe to log viewer expansion changes for window resizing
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Subscribe to Loaded event
            Loaded += OnLoaded;

            _logger.LogInformation("Cimian Status main window initialized");
        }

        private void OnStatusMessageReceived(object? sender, Models.StatusMessage message)
        {
            // Ensure UI updates happen on the UI thread
            Dispatcher.Invoke(() =>
            {
                try
                {
                    switch (message.Type?.ToLowerInvariant())
                    {
                        case "statusmessage":
                            _viewModel.StatusText = message.Data;
                            _viewModel.HasError = message.Error;
                            break;

                        case "detailmessage":
                            _viewModel.DetailText = message.Data;
                            break;

                        case "percentprogress":
                        case "percentProgress": // Handle both case variations
                            if (message.Percent >= 0)
                            {
                                _viewModel.ProgressValue = message.Percent;
                            }
                            // Note: ShowProgress is now always true, so no need to toggle visibility
                            break;

                        case "displaylog":
                            // Log path received - could be used for direct log access
                            _logger.LogInformation("Log path received: {LogPath}", message.Data);
                            break;

                        case "quit":
                            _logger.LogInformation("Quit message received from managedsoftwareupdate");
                            Application.Current.Shutdown();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing status message: {MessageType}", message.Type);
                }
            });
        }

        private async void ToggleLogViewer_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.ToggleLogViewerAsync();
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_viewModel.LogText))
                {
                    Clipboard.SetText(_viewModel.LogText);
                    _logger.LogInformation("Log text copied to clipboard");
                    
                    // Show a brief feedback by changing button text
                    if (sender is Button button)
                    {
                        var originalContent = button.Content;
                        button.Content = "Copied!";
                        
                        // Reset button text after 1 second
                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(1)
                        };
                        timer.Tick += (s, args) =>
                        {
                            button.Content = originalContent;
                            timer.Stop();
                        };
                        timer.Start();
                    }
                }
                else
                {
                    _logger.LogWarning("No log text to copy");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy log text to clipboard");
            }
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Terminate any running managedsoftwareupdate.exe processes
                var processes = Process.GetProcessesByName("managedsoftwareupdate");
                foreach (var process in processes)
                {
                    try
                    {
                        _logger.LogInformation("Terminating managedsoftwareupdate.exe process (PID: {ProcessId})", process.Id);
                        process.Kill();
                        process.WaitForExit(5000); // Wait up to 5 seconds for clean exit
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to terminate managedsoftwareupdate.exe process (PID: {ProcessId}): {Error}", 
                            process.Id, ex.Message);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error while terminating processes: {Error}", ex.Message);
            }

            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                DragMove();
            }
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_viewModel.IsLogViewerExpanded))
            {
                // Animate window height change
                var targetHeight = _viewModel.IsLogViewerExpanded ? _expandedHeight : _baseHeight;
                AnimateWindowHeight(targetHeight);
            }
            else if (e.PropertyName == nameof(_viewModel.ShouldScrollToBottom))
            {
                // Auto-scroll to bottom when log text changes
                Dispatcher.BeginInvoke(() =>
                {
                    var scrollViewer = FindName("LogScrollViewer") as ScrollViewer;
                    if (scrollViewer != null && _viewModel.IsLogViewerExpanded)
                    {
                        scrollViewer.ScrollToEnd();
                    }
                });
            }
        }

        private void AnimateWindowHeight(double targetHeight)
        {
            var animation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = Height,
                To = targetHeight,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new System.Windows.Media.Animation.CubicEase 
                { 
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut 
                }
            };

            BeginAnimation(HeightProperty, animation);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Start the status server if not already running
                if (!_statusServer.IsRunning)
                {
                    await _statusServer.StartAsync();
                    _logger.LogInformation("Status server started");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start status server");
                _viewModel.ConnectionStatusText = "Disconnected";
                _viewModel.ConnectionStatusColor = System.Windows.Media.Colors.Red;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                // Unsubscribe from events
                _statusServer.MessageReceived -= OnStatusMessageReceived;
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

                // Stop the status server (fire and forget)
                if (_statusServer.IsRunning)
                {
                    _ = Task.Run(async () => 
                    {
                        try
                        {
                            await _statusServer.StopAsync();
                            _logger.LogInformation("Status server stopped");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error stopping status server");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during window cleanup");
            }

            base.OnClosed(e);
        }
    }
}
