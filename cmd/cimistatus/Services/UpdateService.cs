using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Cimian.Status.Models;

namespace Cimian.Status.Services
{
    public class UpdateService : IUpdateService
    {
        private readonly ILogger<UpdateService> _logger;
        private readonly IStatusServer _statusServer;

        public event EventHandler<ProgressEventArgs>? ProgressChanged;
        public event EventHandler<StatusEventArgs>? StatusChanged;
        public event EventHandler<UpdateCompletedEventArgs>? Completed;

        private volatile bool _isExecutingUpdate = false;
        private volatile bool _updateCompleted = false;

        public UpdateService(ILogger<UpdateService> logger, IStatusServer statusServer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _statusServer = statusServer ?? throw new ArgumentNullException(nameof(statusServer));
            
            // Subscribe to status server messages to get real progress from managedsoftwareupdate.exe
            _statusServer.MessageReceived += OnStatusMessageReceived;
        }

        public async Task ExecuteUpdateAsync()
        {
            try
            {
                _isExecutingUpdate = true;
                _updateCompleted = false;

                // Immediately show that we're starting
                StatusChanged?.Invoke(this, new StatusEventArgs 
                { 
                    Message = "Initializing update process..." 
                });
                ProgressChanged?.Invoke(this, new ProgressEventArgs 
                { 
                    Percentage = 5, 
                    Message = "Starting update..." 
                });

                // First try to trigger via CimianWatcher service (preferred method)
                if (await TryTriggerViaBootstrapAsync())
                {
                    _logger.LogInformation("Successfully triggered update via CimianWatcher service");
                    return;
                }

                // Fallback to direct execution with elevation
                await ExecuteDirectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing update process");
                StatusChanged?.Invoke(this, new StatusEventArgs 
                { 
                    Message = "Update process failed", 
                    IsError = true 
                });
                Completed?.Invoke(this, new UpdateCompletedEventArgs 
                { 
                    Success = false, 
                    ErrorMessage = ex.Message 
                });
            }
            finally
            {
                _isExecutingUpdate = false;
            }
        }

        private async Task<bool> TryTriggerViaBootstrapAsync()
        {
            try
            {
                ProgressChanged?.Invoke(this, new ProgressEventArgs 
                { 
                    Percentage = 20, 
                    Message = "Checking for system service..." 
                });

                // Create the bootstrap flag file to trigger CimianWatcher service
                var bootstrapFlagPath = @"C:\ProgramData\ManagedInstalls\.cimian.bootstrap";
                var managedInstallsPath = Path.GetDirectoryName(bootstrapFlagPath);

                // Ensure the directory exists
                if (!Directory.Exists(managedInstallsPath))
                {
                    Directory.CreateDirectory(managedInstallsPath);
                }

                ProgressChanged?.Invoke(this, new ProgressEventArgs 
                { 
                    Percentage = 40, 
                    Message = "Creating bootstrap trigger..." 
                });

                // Create the bootstrap flag file
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var content = $"Bootstrap triggered at: {timestamp}\nTriggered by: CimianStatus UI";
                
                await File.WriteAllTextAsync(bootstrapFlagPath, content);
                
                _logger.LogInformation("Created bootstrap flag file: {FilePath}", bootstrapFlagPath);
                
                StatusChanged?.Invoke(this, new StatusEventArgs 
                { 
                    Message = "Triggered software update via system service..." 
                });

                ProgressChanged?.Invoke(this, new ProgressEventArgs 
                { 
                    Percentage = 60, 
                    Message = "Waiting for service response..." 
                });

                // Wait a moment for the service to pick up the file
                await Task.Delay(2000);

                ProgressChanged?.Invoke(this, new ProgressEventArgs 
                { 
                    Percentage = 80, 
                    Message = "Verifying trigger..." 
                });

                // Check if the file was processed (should be deleted by CimianWatcher)
                var fileProcessed = !File.Exists(bootstrapFlagPath);
                
                if (fileProcessed)
                {
                    StatusChanged?.Invoke(this, new StatusEventArgs 
                    { 
                        Message = "Update process initiated by system service" 
                    });

                    ProgressChanged?.Invoke(this, new ProgressEventArgs 
                    { 
                        Percentage = 100, 
                        Message = "Update process started" 
                    });
                    
                    Completed?.Invoke(this, new UpdateCompletedEventArgs 
                    { 
                        Success = true, 
                        ErrorMessage = null 
                    });
                    
                    return true;
                }
                else
                {
                    _logger.LogWarning("Bootstrap flag file was not processed by CimianWatcher service");
                    ProgressChanged?.Invoke(this, new ProgressEventArgs 
                    { 
                        Percentage = 50, 
                        Message = "Service not available, trying direct approach..." 
                    });
                    // Clean up the file we created
                    try { File.Delete(bootstrapFlagPath); } catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to trigger via CimianWatcher service, falling back to direct execution");
                ProgressChanged?.Invoke(this, new ProgressEventArgs 
                { 
                    Percentage = 30, 
                    Message = "Service trigger failed, trying direct approach..." 
                });
                return false;
            }
        }

        private async Task ExecuteDirectAsync()
        {
            // Find the executable
            var execPath = FindExecutable();
            if (execPath == null)
            {
                var errorMsg = "Could not find managedsoftwareupdate.exe";
                _logger.LogError(errorMsg);
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = errorMsg, IsError = true });
                Completed?.Invoke(this, new UpdateCompletedEventArgs { Success = false, ErrorMessage = errorMsg });
                return;
            }

            _logger.LogInformation("Starting update process with elevation: {ExecutablePath}", execPath);

            ProgressChanged?.Invoke(this, new ProgressEventArgs 
            { 
                Percentage = 10, 
                Message = "Preparing to request elevation..." 
            });

            StatusChanged?.Invoke(this, new StatusEventArgs 
            { 
                Message = "Requesting administrator privileges..." 
            });

            // Execute the process with elevation and status reporting enabled
            var processInfo = new ProcessStartInfo
            {
                FileName = execPath,
                Arguments = "--auto --show-status",  // Enable status reporting to our TCP server
                UseShellExecute = true,              // Required for elevation
                Verb = "runas",                      // Request elevation
                CreateNoWindow = false,              // Show window for elevated process
                WindowStyle = ProcessWindowStyle.Hidden  // Hide the console window
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start process or user denied elevation");
            }

            ProgressChanged?.Invoke(this, new ProgressEventArgs 
            { 
                Percentage = 30, 
                Message = "Process running with administrator privileges..." 
            });

            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Update process running with administrator privileges..." });

            // Wait for the real progress from TCP messages instead of simulating
            // The OnStatusMessageReceived method will handle progress updates from managedsoftwareupdate.exe
            ProgressChanged?.Invoke(this, new ProgressEventArgs 
            { 
                Percentage = 35, 
                Message = "Connected to update process, waiting for progress..." 
            });

            // Wait for completion with timeout (15 minutes for real operations)
            var completed = await Task.Run(() => process.WaitForExit(900000));

            // If we didn't receive a "quit" message but the process completed, handle it
            if (completed && !_updateCompleted)
            {
                var exitCode = process.ExitCode;
                _logger.LogInformation("Process completed with exit code: {ExitCode}", exitCode);

                ProgressChanged?.Invoke(this, new ProgressEventArgs 
                { 
                    Percentage = 100, 
                    Message = "Process completed" 
                });

                var success = exitCode == 0;
                var errorMessage = success ? null : $"Process exit code: {exitCode}";

                StatusChanged?.Invoke(this, new StatusEventArgs 
                { 
                    Message = success ? "Update completed successfully" : $"Update failed with exit code {exitCode}",
                    IsError = !success
                });

                Completed?.Invoke(this, new UpdateCompletedEventArgs 
                { 
                    Success = success, 
                    ErrorMessage = errorMessage,
                    ExitCode = exitCode
                });
            }
            else
            {
                _logger.LogWarning("Process timed out and was terminated");
                process.Kill();
                
                StatusChanged?.Invoke(this, new StatusEventArgs 
                { 
                    Message = "Update process timed out", 
                    IsError = true 
                });
                
                Completed?.Invoke(this, new UpdateCompletedEventArgs 
                { 
                    Success = false, 
                    ErrorMessage = "Process timed out after 10 minutes" 
                });
            }
        }

        public bool IsExecutableFound()
        {
            return FindExecutable() != null;
        }

        private string? FindExecutable()
        {
            var possiblePaths = new[]
            {
                // Installed location
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cimian", "managedsoftwareupdate.exe"),
                
                // Development locations - relative to current executable
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "..", "..", "..", "release", "x64", "managedsoftwareupdate.exe"),
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "..", "..", "release", "x64", "managedsoftwareupdate.exe"),
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "..", "release", "x64", "managedsoftwareupdate.exe"),
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "release", "x64", "managedsoftwareupdate.exe"),
                
                // Same directory
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "managedsoftwareupdate.exe"),
                
                // Current working directory
                "managedsoftwareupdate.exe"
            };

            foreach (var path in possiblePaths)
            {
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (File.Exists(fullPath))
                    {
                        _logger.LogDebug("Found executable at: {Path}", fullPath);
                        return fullPath;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error checking path: {Path}", path);
                }
            }

            _logger.LogWarning("managedsoftwareupdate.exe not found in any expected location");
            return null;
        }

        private void OnStatusMessageReceived(object? sender, StatusMessage message)
        {
            // Only process messages when we're actively running an update
            if (!_isExecutingUpdate) return;

            try
            {
                switch (message.Type?.ToLowerInvariant())
                {
                    case "statusmessage":
                        StatusChanged?.Invoke(this, new StatusEventArgs 
                        { 
                            Message = message.Data, 
                            IsError = message.Error 
                        });
                        break;

                    case "detailmessage":
                        ProgressChanged?.Invoke(this, new ProgressEventArgs 
                        { 
                            Percentage = -1, // Keep current percentage
                            Message = message.Data 
                        });
                        break;

                    case "percentprogress":
                    case "percentProgress": // Handle both case variations
                        if (message.Percent >= 0)
                        {
                            ProgressChanged?.Invoke(this, new ProgressEventArgs 
                            { 
                                Percentage = message.Percent,
                                Message = $"Progress: {message.Percent}%"
                            });
                        }
                        break;

                    case "quit":
                        _logger.LogInformation("Received quit message from managedsoftwareupdate");
                        _updateCompleted = true;
                        
                        // Assume success if we got a quit message (process completed normally)
                        ProgressChanged?.Invoke(this, new ProgressEventArgs 
                        { 
                            Percentage = 100,
                            Message = "Update completed"
                        });
                        
                        StatusChanged?.Invoke(this, new StatusEventArgs 
                        { 
                            Message = "Update process completed successfully"
                        });
                        
                        Completed?.Invoke(this, new UpdateCompletedEventArgs 
                        { 
                            Success = true,
                            ExitCode = 0
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing status message in UpdateService: {MessageType}", message.Type);
            }
        }
    }
}
