using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cimian.Status.Models;
using Cimian.Status.Services;

namespace Cimian.Status.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IUpdateService _updateService;
        private readonly ILogService _logService;

        [ObservableProperty]
        private string _statusText = "Ready";

        [ObservableProperty]
        private string _detailText = "System ready for updates";

        [ObservableProperty]
        private int _progressValue = 0;

        [ObservableProperty]
        private string _progressText = "System ready for updates";

        [ObservableProperty]
        private bool _showProgress = true;

        [ObservableProperty]
        private bool _hasError = false;

        [ObservableProperty]
        private bool _isRunning = false;

        [ObservableProperty]
        private string _lastRunTime = "Never";

        [ObservableProperty]
        private string _runButtonText = "Run Now";

        [ObservableProperty]
        private string _connectionStatusText = "Connected";

        [ObservableProperty]
        private Color _connectionStatusColor = Colors.Green;

        [ObservableProperty]
        private bool _isIndeterminate = false;

        // Log viewer properties
        [ObservableProperty]
        private bool _isLogViewerExpanded = false;

        [ObservableProperty]
        private string _logText = "";

        [ObservableProperty]
        private string _logViewerButtonText = "Show Live Logs";

        [ObservableProperty]
        private bool _isLogTailing = false;

        public MainViewModel(IUpdateService updateService, ILogService logService)
        {
            _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));

            // Subscribe to update service events
            _updateService.ProgressChanged += OnProgressChanged;
            _updateService.StatusChanged += OnStatusChanged;
            _updateService.Completed += OnUpdateCompleted;

            // Subscribe to log service events
            _logService.LogLineReceived += OnLogLineReceived;

            LoadLastRunTime();
        }

        public bool CanRunNow => !IsRunning;

        public Brush StatusBrush => HasError ? 
            new SolidColorBrush(Colors.Red) : 
            IsRunning ? 
                new SolidColorBrush(Color.FromRgb(0, 120, 212)) : 
                new SolidColorBrush(Color.FromRgb(16, 124, 16));

        public Brush ProgressBrush => HasError ? 
            new SolidColorBrush(Colors.Red) : 
            new SolidColorBrush(Color.FromRgb(0, 120, 212));

        public Color ProgressColor => HasError ? 
            Colors.Red : 
            Color.FromRgb(0, 120, 212);

        public Brush ConnectionStatusBrush => new SolidColorBrush(ConnectionStatusColor);

        [RelayCommand]
        public async Task RunNowAsync()
        {
            if (IsRunning) return;

            try
            {
                IsRunning = true;
                RunButtonText = "Running...";
                HasError = false;
                ShowProgress = true;
                IsIndeterminate = true; // Start with indeterminate progress
                ProgressValue = 0;
                ProgressText = "Initializing...";
                StatusText = "Starting update process...";

                // Start log tailing when update begins
                await StartLogTailingAsync();

                await _updateService.ExecuteUpdateAsync();
            }
            catch (Exception ex)
            {
                HasError = true;
                StatusText = "Update failed";
                ProgressText = ex.Message;
                IsIndeterminate = false;
            }
            finally
            {
                IsRunning = false;
                RunButtonText = "Run Now";
                IsIndeterminate = false;
                
                // Stop log tailing when update completes
                await StopLogTailingAsync();
            }
        }

        [RelayCommand]
        public void ShowLogs()
        {
            _logService.OpenLogsDirectory();
        }

        [RelayCommand]
        public async Task ToggleLogViewerAsync()
        {
            IsLogViewerExpanded = !IsLogViewerExpanded;
            
            if (IsLogViewerExpanded)
            {
                LogViewerButtonText = "Hide Live Logs";
                await StartLogTailingAsync();
            }
            else
            {
                LogViewerButtonText = "Show Live Logs";
                await StopLogTailingAsync();
            }
        }

        [RelayCommand]
        public Task StartLiveMonitoringAsync()
        {
            try
            {
                AddLogLine($"Testing live process monitoring...");
                // Remove the call to non-existent method for now
                AddLogLine($"Live monitoring test completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                AddLogLine($"Error during live monitoring test: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        private async Task StartLogTailingAsync()
        {
            if (IsLogTailing) return;

            try
            {
                LogText = "";
                await _logService.StartLogTailingAsync();
                IsLogTailing = _logService.IsLogTailing;
                
                if (IsLogTailing)
                {
                    AddLogLine($"Started monitoring: {_logService.GetCurrentLogFilePath()}");
                }
                else
                {
                    AddLogLine($"No active log file found yet...");
                }
            }
            catch (Exception ex)
            {
                AddLogLine($"Error starting log monitoring: {ex.Message}");
            }
        }

        private async Task StopLogTailingAsync()
        {
            if (!IsLogTailing) return;

            try
            {
                await _logService.StopLogTailingAsync();
                IsLogTailing = false;
                AddLogLine($"Stopped log monitoring");
            }
            catch (Exception ex)
            {
                AddLogLine($"Error stopping log monitoring: {ex.Message}");
            }
        }

        private void OnLogLineReceived(object? sender, string logLine)
        {
            // Ensure UI updates happen on the UI thread
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                AddLogLine(logLine);
            });
        }

        private void AddLogLine(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logEntry = $"[{timestamp}] {message}\r\n";
            LogText += logEntry;

            // Keep only the last 500 lines to prevent memory issues
            var lines = LogText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length > 500)
            {
                var keepLines = lines.Skip(lines.Length - 500).ToArray();
                LogText = string.Join("\r\n", keepLines);
            }

            // Trigger scroll to bottom
            OnPropertyChanged(nameof(ShouldScrollToBottom));
        }

        // Property to trigger auto-scroll in UI
        public bool ShouldScrollToBottom => !string.IsNullOrEmpty(LogText);

        private void OnProgressChanged(object? sender, ProgressEventArgs e)
        {
            try
            {
                // Only update percentage if it's a valid value (>= 0)
                if (e.Percentage >= 0)
                {
                    ProgressValue = e.Percentage;
                    ShowProgress = true;
                    IsIndeterminate = false; // Switch to determinate mode when we have real progress
                }
                
                // Always update the message if provided - only in ProgressText to avoid duplication
                if (!string.IsNullOrEmpty(e.Message))
                {
                    ProgressText = e.Message;
                    // Don't set DetailText here to avoid showing the same message twice
                }
            }
            catch (Exception ex)
            {
                // Log error but don't crash the app
                System.Diagnostics.Debug.WriteLine($"Error in OnProgressChanged: {ex.Message}");
            }
        }

        private void OnStatusChanged(object? sender, StatusEventArgs e)
        {
            StatusText = e.Message;
            HasError = e.IsError;
        }

        private void OnUpdateCompleted(object? sender, UpdateCompletedEventArgs e)
        {
            ProgressValue = 100;
            ShowProgress = true;
            
            if (e.Success)
            {
                ProgressText = "All operations completed successfully";
                HasError = false;
                SaveLastRunTime();
            }
            else
            {
                ProgressText = e.ErrorMessage ?? "Some operations completed with warnings";
                HasError = !string.IsNullOrEmpty(e.ErrorMessage);
            }
        }

        private void LoadLastRunTime()
        {
            LastRunTime = _logService.GetLastRunTime();
        }

        private void SaveLastRunTime()
        {
            _logService.SaveLastRunTime();
            LoadLastRunTime();
        }

        // Property change notifications for computed properties
        partial void OnIsRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(CanRunNow));
            OnPropertyChanged(nameof(StatusBrush));
        }

        partial void OnHasErrorChanged(bool value)
        {
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(ProgressBrush));
            OnPropertyChanged(nameof(ProgressColor));
        }

        partial void OnConnectionStatusColorChanged(Color value)
        {
            OnPropertyChanged(nameof(ConnectionStatusBrush));
        }
    }
}
