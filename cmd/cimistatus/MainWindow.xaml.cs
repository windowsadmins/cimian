using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace CimianStatus
{
    public partial class MainWindow : Window
    {
        private readonly StatusViewModel _viewModel;
        private readonly StatusServer _statusServer;
        private readonly ILogger<MainWindow> _logger;
        private readonly Timer _uiUpdateTimer;

        public MainWindow(StatusViewModel viewModel, StatusServer statusServer, ILogger<MainWindow> logger)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _statusServer = statusServer;
            _logger = logger;
            
            DataContext = _viewModel;

            // Subscribe to status updates
            _statusServer.MessageReceived += OnStatusMessageReceived;
            
            // Load last run time
            LoadLastRunTime();

            // Set up periodic UI updates for indeterminate progress
            _uiUpdateTimer = new Timer(UpdateIndeterminateProgress, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            _logger.LogInformation("CimianStatus main window initialized");
        }

        private void OnStatusMessageReceived(StatusMessage message)
        {
            // Ensure UI updates happen on the UI thread
            Dispatcher.Invoke(() =>
            {
                _viewModel.UpdateFromMessage(message);
            });
        }

        private void UpdateIndeterminateProgress(object? state)
        {
            Dispatcher.Invoke(() =>
            {
                // If not showing specific progress, show indeterminate animation
                if (!_viewModel.ShowProgress && _viewModel.IsRunning)
                {
                    var pos = (int)(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond / 50) % 100;
                    _viewModel.ProgressValue = pos;
                }
            });
        }

        private void LoadLastRunTime()
        {
            try
            {
                var lastRunFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), 
                    "ManagedInstalls", "LastRunTime.txt");
                
                if (File.Exists(lastRunFile))
                {
                    var lastRunTime = File.ReadAllText(lastRunFile).Trim();
                    _viewModel.LastRunTime = lastRunTime;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load last run time");
            }
        }

        private void SaveLastRunTime()
        {
            try
            {
                var lastRunFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), 
                    "ManagedInstalls", "LastRunTime.txt");
                
                Directory.CreateDirectory(Path.GetDirectoryName(lastRunFile)!);
                File.WriteAllText(lastRunFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                
                LoadLastRunTime(); // Refresh the display
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save last run time");
            }
        }

        private async void RunNow_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.IsRunning) return;

            try
            {
                _viewModel.IsRunning = true;
                _viewModel.StatusText = "Starting Cimian update process...";
                _viewModel.DetailText = "Launching managedsoftwareupdate.exe...";
                _viewModel.HasError = false;
                _viewModel.ShowProgress = true;
                _viewModel.ProgressValue = 0;

                await Task.Run(async () =>
                {
                    await ExecuteUpdateAsync();
                });
            }
            finally
            {
                _viewModel.IsRunning = false;
            }
        }

        private async Task ExecuteUpdateAsync()
        {
            try
            {
                // Find the executable
                var execPath = FindExecutable();
                if (execPath == null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        _viewModel.StatusText = "Could not find managedsoftwareupdate.exe";
                        _viewModel.DetailText = "Please check installation";
                        _viewModel.HasError = true;
                        _viewModel.ShowProgress = false;
                    });
                    return;
                }

                // Start progress simulation
                var progressTask = SimulateProgressAsync();

                // Execute the process
                var processInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = "--auto",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _logger.LogInformation("Starting managedsoftwareupdate.exe: {ExecutablePath}", execPath);

                using var process = Process.Start(processInfo);
                if (process == null)
                {
                    throw new InvalidOperationException("Failed to start process");
                }

                Dispatcher.Invoke(() =>
                {
                    _viewModel.StatusText = "Cimian update process running...";
                    _viewModel.DetailText = "Checking for available updates...";
                });

                // Wait for completion with timeout
                var completed = await Task.Run(() => process.WaitForExit(300000)); // 5 minutes

                if (completed)
                {
                    var exitCode = process.ExitCode;
                    _logger.LogInformation("Process completed with exit code: {ExitCode}", exitCode);

                    Dispatcher.Invoke(() =>
                    {
                        if (exitCode == 0)
                        {
                            _viewModel.StatusText = "Update completed successfully";
                            _viewModel.HasError = false;
                            SaveLastRunTime();
                        }
                        else
                        {
                            _viewModel.StatusText = "Update completed with warnings";
                            _viewModel.DetailText = $"Process exit code: {exitCode}";
                            _viewModel.HasError = false; // Don't treat all non-zero as errors
                        }
                        
                        _viewModel.ShowProgress = true;
                        _viewModel.ProgressValue = 100;
                    });
                }
                else
                {
                    _logger.LogWarning("Process did not complete within timeout");
                    process.Kill();
                    
                    Dispatcher.Invoke(() =>
                    {
                        _viewModel.StatusText = "Update process timed out";
                        _viewModel.DetailText = "Process was taking too long and was terminated";
                        _viewModel.HasError = true;
                        _viewModel.ShowProgress = false;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing update process");
                
                Dispatcher.Invoke(() =>
                {
                    _viewModel.StatusText = "Failed to start update process";
                    _viewModel.DetailText = $"Error: {ex.Message}";
                    _viewModel.HasError = true;
                    _viewModel.ShowProgress = false;
                });
            }
        }

        private async Task SimulateProgressAsync()
        {
            var steps = new[]
            {
                (10, "Initializing update process...", 2000),
                (25, "Checking catalog for updates...", 3000),
                (40, "Downloading package information...", 4000),
                (55, "Evaluating installed packages...", 3000),
                (70, "Processing update requirements...", 4000),
                (85, "Finalizing update operations...", 3000),
                (95, "Completing update process...", 2000)
            };

            foreach (var (progress, message, delay) in steps)
            {
                if (!_viewModel.IsRunning) return;

                await Task.Delay(delay);
                
                Dispatcher.Invoke(() =>
                {
                    if (_viewModel.IsRunning)
                    {
                        _viewModel.ProgressValue = progress;
                        _viewModel.DetailText = message;
                    }
                });
            }
        }

        private string? FindExecutable()
        {
            var possiblePaths = new[]
            {
                // First check the installed location (production deployment)
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cimian", "managedsoftwareupdate.exe"),
                
                // Check local development/release directories - various possible locations
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "..", "..", "release", "x64", "managedsoftwareupdate.exe"),
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "release", "x64", "managedsoftwareupdate.exe"),
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "..", "release", "x64", "managedsoftwareupdate.exe"),
                
                // Check bin directory (build outputs)
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "..", "..", "bin", "x64", "managedsoftwareupdate.exe"),
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "bin", "x64", "managedsoftwareupdate.exe"),
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "..", "bin", "x64", "managedsoftwareupdate.exe"),
                
                // Check same directory as this executable
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "managedsoftwareupdate.exe"),
                
                // Check current working directory
                "managedsoftwareupdate.exe"
            };

            foreach (var path in possiblePaths)
            {
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (File.Exists(fullPath))
                    {
                        _logger.LogInformation("Found managedsoftwareupdate.exe at: {ExecutablePath}", fullPath);
                        return fullPath;
                    }
                    else
                    {
                        _logger.LogDebug("Checked path (not found): {Path}", fullPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error checking path: {Path}", path);
                }
            }

            _logger.LogError("managedsoftwareupdate.exe not found in any expected location. Searched paths: {Paths}", 
                string.Join(", ", possiblePaths));
            return null;
        }

        private void ShowLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logsBaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), 
                    "ManagedInstalls", "logs");

                if (!Directory.Exists(logsBaseDir))
                {
                    _logger.LogWarning("Logs directory does not exist: {LogsDirectory}", logsBaseDir);
                    return;
                }

                // Find the most recent timestamped session directory
                var directories = Directory.GetDirectories(logsBaseDir)
                    .Where(d => Path.GetFileName(d).Length == 17 && Path.GetFileName(d)[4] == '-' && Path.GetFileName(d)[7] == '-' && Path.GetFileName(d)[10] == '-')
                    .OrderByDescending(d => Path.GetFileName(d))
                    .ToArray();

                if (directories.Length > 0)
                {
                    Process.Start("explorer.exe", directories[0]);
                    _logger.LogInformation("Opened latest log session: {LogSession}", directories[0]);
                }
                else
                {
                    Process.Start("explorer.exe", logsBaseDir);
                    _logger.LogInformation("Opened logs directory: {LogsDirectory}", logsBaseDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error opening logs directory");
            }
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Allow dragging the window by clicking anywhere on the window
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            // Handle custom close button
            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _uiUpdateTimer?.Dispose();
            _statusServer.MessageReceived -= OnStatusMessageReceived;
            base.OnClosed(e);
        }
    }
}
