using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Cimian.Status.Views
{
    public partial class LoginScreenWindow : Window
    {
        private readonly string _bootstrapFile = @"C:\ProgramData\ManagedInstalls\.cimian.bootstrap";
        private readonly string _logsPath = @"C:\ProgramData\ManagedInstalls\logs";
        private CancellationTokenSource? _cts;
        private bool _isMonitoring = false;

        public LoginScreenWindow()
        {
            InitializeComponent();
            
            // Start monitoring when loaded
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Start monitoring bootstrap progress
            _cts = new CancellationTokenSource();
            _isMonitoring = true;
            Task.Run(() => MonitorBootstrapProgress(_cts.Token));
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _isMonitoring = false;
            _cts?.Cancel();
            _cts?.Dispose();
        }

        private async Task MonitorBootstrapProgress(CancellationToken cancellationToken)
        {
            int lastProgress = 0;
            string lastMessage = "Initializing...";

            while (_isMonitoring && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Check if bootstrap file still exists
                    if (!File.Exists(_bootstrapFile))
                    {
                        // Bootstrap complete - show completion and close
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ProgressBar.Value = 100;
                            StatusText.Text = "Bootstrap complete";
                            DetailText.Text = "System configuration finished successfully";
                        });
                        
                        await Task.Delay(2000, cancellationToken); // Show completion for 2 seconds
                        
                        await Dispatcher.InvokeAsync(() => Close());
                        break;
                    }

                    // Find latest session directory
                    if (Directory.Exists(_logsPath))
                    {
                        var latestSession = Directory.GetDirectories(_logsPath)
                            .Where(d => Path.GetFileName(d).Length == 19) // Session format: YYYY-MM-DD-HHMMSS
                            .OrderByDescending(d => d)
                            .FirstOrDefault();

                        if (latestSession != null)
                        {
                            string eventsFile = Path.Combine(latestSession, "events.jsonl");
                            if (File.Exists(eventsFile))
                            {
                                // Read the last few lines to get latest progress
                                var lines = File.ReadAllLines(eventsFile);
                                
                                // Parse from end to find latest progress/status
                                for (int i = lines.Length - 1; i >= Math.Max(0, lines.Length - 20); i--)
                                {
                                    try
                                    {
                                        var json = JsonDocument.Parse(lines[i]);
                                        var root = json.RootElement;

                                        // Check for progress
                                        if (root.TryGetProperty("progress", out var progressProp))
                                        {
                                            int progress = progressProp.GetInt32();
                                            if (progress > lastProgress)
                                            {
                                                lastProgress = progress;
                                                
                                                // Get message if available
                                                if (root.TryGetProperty("message", out var messageProp))
                                                {
                                                    lastMessage = messageProp.GetString() ?? lastMessage;
                                                }
                                                else if (root.TryGetProperty("event_type", out var eventTypeProp))
                                                {
                                                    lastMessage = eventTypeProp.GetString() ?? lastMessage;
                                                }

                                                // Update UI
                                                await Dispatcher.InvokeAsync(() =>
                                                {
                                                    ProgressBar.Value = progress;
                                                    StatusText.Text = lastMessage;
                                                    
                                                    // Show package name if available
                                                    if (root.TryGetProperty("package_name", out var packageProp))
                                                    {
                                                        DetailText.Text = $"Processing: {packageProp.GetString()}";
                                                    }
                                                });
                                                
                                                break; // Found latest progress, stop parsing
                                            }
                                        }
                                        // Check for status messages
                                        else if (root.TryGetProperty("message", out var messageProp))
                                        {
                                            string message = messageProp.GetString() ?? "";
                                            if (!string.IsNullOrEmpty(message))
                                            {
                                                lastMessage = message;
                                                
                                                await Dispatcher.InvokeAsync(() =>
                                                {
                                                    StatusText.Text = lastMessage;
                                                });
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        // Skip invalid JSON lines
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue monitoring
                    await Dispatcher.InvokeAsync(() =>
                    {
                        DetailText.Text = $"Monitoring error: {ex.Message}";
                    });
                }

                // Wait before next check
                await Task.Delay(1000, cancellationToken);
            }
        }
    }
}
