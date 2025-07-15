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

        public event EventHandler<ProgressEventArgs>? ProgressChanged;
        public event EventHandler<StatusEventArgs>? StatusChanged;
        public event EventHandler<UpdateCompletedEventArgs>? Completed;

        public UpdateService(ILogger<UpdateService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ExecuteUpdateAsync()
        {
            try
            {
                // Immediately show that we're starting
                StatusChanged?.Invoke(this, new StatusEventArgs 
                { 
                    Message = "Initializing update process..." 
                });
                ProgressChanged?.Invoke(this, new ProgressEventArgs 
                { 
                    Percentage = 10, 
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

            // Simulate progress during the wait period
            var progressTask = Task.Run(async () =>
            {
                for (int i = 30; i <= 90 && !process.HasExited; i += 10)
                {
                    await Task.Delay(30000); // Update every 30 seconds
                    if (!process.HasExited)
                    {
                        ProgressChanged?.Invoke(this, new ProgressEventArgs 
                        { 
                            Percentage = i, 
                            Message = $"Update in progress... ({i}%)" 
                        });
                    }
                }
            });

            // Wait for completion with timeout (10 minutes for real operations)
            var completed = await Task.Run(() => process.WaitForExit(600000));

            if (completed)
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
    }
}
