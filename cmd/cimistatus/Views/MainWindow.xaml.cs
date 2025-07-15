using System;
using System.Threading.Tasks;
using System.Windows;
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

        public MainWindow(MainViewModel viewModel, IStatusServer statusServer, ILogger<MainWindow> logger)
        {
            InitializeComponent();
            
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _statusServer = statusServer ?? throw new ArgumentNullException(nameof(statusServer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            DataContext = _viewModel;

            // Subscribe to status server events
            _statusServer.MessageReceived += OnStatusMessageReceived;

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
                            if (message.Percent >= 0)
                            {
                                _viewModel.ProgressValue = message.Percent;
                                _viewModel.ShowProgress = true;
                            }
                            else
                            {
                                _viewModel.ShowProgress = false;
                            }
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

        private async void RunNow_Click(object sender, RoutedEventArgs e)
        {
            // The actual work is handled by the ViewModel's command
            // This is just a backup for direct button clicks
            if (_viewModel.RunNowCommand.CanExecute(null))
            {
                await _viewModel.RunNowAsync();
            }
        }

        private void ShowLogs_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ShowLogsCommand.Execute(null);
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
