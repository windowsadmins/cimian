using System;
using System.ComponentModel;
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
        private string _progressText = "";

        [ObservableProperty]
        private bool _showProgress = false;

        [ObservableProperty]
        private bool _hasError = false;

        [ObservableProperty]
        private bool _isRunning = false;

        [ObservableProperty]
        private string _lastRunTime = "Never";

        [ObservableProperty]
        private string _runButtonText = "🚀 Run Now";

        [ObservableProperty]
        private string _connectionStatusText = "Connected";

        [ObservableProperty]
        private Color _connectionStatusColor = Colors.Green;

        public MainViewModel(IUpdateService updateService, ILogService logService)
        {
            _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));

            // Subscribe to update service events
            _updateService.ProgressChanged += OnProgressChanged;
            _updateService.StatusChanged += OnStatusChanged;
            _updateService.Completed += OnUpdateCompleted;

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
                RunButtonText = "⏳ Running...";
                HasError = false;
                ShowProgress = true;
                ProgressValue = 0;
                ProgressText = "Initializing...";
                StatusText = "Starting update process...";
                DetailText = "Initializing Cimian update...";

                await _updateService.ExecuteUpdateAsync();
            }
            catch (Exception ex)
            {
                HasError = true;
                StatusText = "Update failed";
                DetailText = ex.Message;
                ShowProgress = false;
            }
            finally
            {
                IsRunning = false;
                RunButtonText = "🚀 Run Now";
            }
        }

        [RelayCommand]
        public void ShowLogs()
        {
            _logService.OpenLogsDirectory();
        }

        private void OnProgressChanged(object? sender, ProgressEventArgs e)
        {
            ProgressValue = e.Percentage;
            ProgressText = e.Message;
            DetailText = e.Message;
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
                StatusText = "Update completed successfully";
                DetailText = "All operations completed";
                HasError = false;
                SaveLastRunTime();
            }
            else
            {
                StatusText = "Update completed with warnings";
                DetailText = e.ErrorMessage ?? "Some operations completed with warnings";
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
