using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace CimianStatus
{
    public class StatusViewModel : INotifyPropertyChanged
    {
        private string _statusText = "Cimian Status Ready";
        private string _detailText = "Last run: Never";
        private int _progressValue = 0;
        private bool _showProgress = false;
        private bool _hasError = false;
        private bool _isRunning = false;
        private string _lastRunTime = "Never";
        private string _logText = "Waiting for software update process...\nSystem ready for management operations.";

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string DetailText
        {
            get => _detailText;
            set => SetProperty(ref _detailText, value);
        }

        public int ProgressValue
        {
            get => _progressValue;
            set => SetProperty(ref _progressValue, value);
        }

        public bool ShowProgress
        {
            get => _showProgress;
            set => SetProperty(ref _showProgress, value);
        }

        public bool HasError
        {
            get => _hasError;
            set 
            { 
                SetProperty(ref _hasError, value);
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(ProgressBrush));
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set 
            { 
                SetProperty(ref _isRunning, value);
                OnPropertyChanged(nameof(RunButtonText));
                OnPropertyChanged(nameof(CanRunNow));
            }
        }

        public string LastRunTime
        {
            get => _lastRunTime;
            set 
            { 
                SetProperty(ref _lastRunTime, value);
                UpdateDetailText();
            }
        }

        public string LogText
        {
            get => _logText;
            set => SetProperty(ref _logText, value);
        }

        public string RunButtonText => IsRunning ? "🔄 Running..." : "🔄 Run Now";
        
        public bool CanRunNow => !IsRunning;

        public Brush StatusBrush => HasError ? 
            new SolidColorBrush(Color.FromRgb(220, 53, 69)) : 
            new SolidColorBrush(Color.FromRgb(0, 51, 102));

        public Brush ProgressBrush
        {
            get
            {
                if (HasError)
                    return new SolidColorBrush(Color.FromRgb(255, 64, 64)); // Red
                else if (ProgressValue == 100)
                    return new SolidColorBrush(Color.FromRgb(64, 255, 64)); // Green
                else
                    return new SolidColorBrush(Color.FromRgb(53, 107, 255)); // Blue
            }
        }

        private void UpdateDetailText()
        {
            DetailText = LastRunTime == "Never" ? "Last run: Never" : $"Last run: {LastRunTime}";
        }

        public void UpdateFromMessage(StatusMessage message)
        {
            switch (message.Type)
            {
                case "statusMessage":
                    StatusText = message.Data ?? string.Empty;
                    HasError = message.Error;
                    break;

                case "detailMessage":
                    DetailText = message.Data ?? string.Empty;
                    break;

                case "percentProgress":
                    if (message.Percent >= 0)
                    {
                        ProgressValue = message.Percent;
                        ShowProgress = true;
                    }
                    else
                    {
                        ShowProgress = false;
                    }
                    OnPropertyChanged(nameof(ProgressBrush));
                    break;

                case "displayLog":
                    // Handle log display request
                    break;

                case "logMessage":
                    // Append to log text
                    var timestamp = DateTime.Now.ToString("HH:mm:ss");
                    var logEntry = $"[{timestamp}] {message.Data ?? ""}";
                    LogText = string.IsNullOrEmpty(LogText) ? logEntry : LogText + "\n" + logEntry;
                    break;

                case "quit":
                    // Handle quit request
                    break;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
