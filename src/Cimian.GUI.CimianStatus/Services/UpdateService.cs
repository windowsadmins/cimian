using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        public async Task MonitorExistingProcessesAsync()
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
            // Default to GUI mode when called from the UI
            return await TryTriggerViaBootstrapAsync(true);
        }

        private async Task<bool> TryTriggerViaBootstrapAsync(bool withGui)
        {
            try
            {
                ProgressChanged?.Invoke(this, new ProgressEventArgs 
                { 
                    Percentage = 20, 
                    Message = "Checking for system service..." 
                });

                // Choose the appropriate bootstrap flag file
                var bootstrapFlagPath = withGui 
                    ? @"C:\ProgramData\ManagedInstalls\.cimian.bootstrap"
                    : @"C:\ProgramData\ManagedInstalls\.cimian.headless";
                
                var managedInstallsPath = Path.GetDirectoryName(bootstrapFlagPath);

                // Ensure the directory exists
                if (!string.IsNullOrEmpty(managedInstallsPath) && !Directory.Exists(managedInstallsPath))
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
                var triggerType = withGui ? "GUI" : "headless";
                var content = $"Bootstrap triggered at: {timestamp}\nMode: {triggerType}\nTriggered by: CimianStatus UI";
                
                await File.WriteAllTextAsync(bootstrapFlagPath, content);
                
                _logger.LogInformation("Created {Mode} bootstrap flag file: {FilePath}", triggerType, bootstrapFlagPath);
                
                StatusChanged?.Invoke(this, new StatusEventArgs 
                { 
                    Message = $"Triggered {triggerType} software update via system service..." 
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
                        Message = $"Update process initiated by system service ({triggerType} mode)" 
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
                    _logger.LogWarning("{Mode} bootstrap flag file was not processed by CimianWatcher service", triggerType);
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

            // Try multiple elevation methods for compatibility with different domain environments
            Process? process = null;
            Exception? lastException = null;

            // Method 1: Standard UAC elevation (works well on Entra Joined devices)
            try
            {
                var processInfo = new ProcessStartInfo
                {
                FileName = execPath,
                Arguments = "--auto --show-status -vv",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden
                };

                process = Process.Start(processInfo);
                if (process != null)
                {
                    _logger.LogInformation("Successfully started with UAC elevation (Method 1)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("UAC elevation method failed: {Error}", ex.Message);
                lastException = ex;
                process = null;
            }

            // Method 2: Try PowerShell Start-Process with -Verb RunAs (better for domain environments)
            if (process == null)
            {
                try
                {
                    ProgressChanged?.Invoke(this, new ProgressEventArgs 
                    { 
                        Percentage = 15, 
                        Message = "Trying PowerShell elevation method..." 
                    });

                    var psArgs = $"-Command \"Start-Process -FilePath '{execPath}' -ArgumentList '--auto','--show-status','-vv' -Verb RunAs -WindowStyle Hidden\"";
                    var processInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = psArgs,
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    var psProcess = Process.Start(processInfo);
                    if (psProcess != null)
                    {
                        await Task.Run(() => psProcess.WaitForExit(10000)); // Wait up to 10 seconds for PowerShell to launch the process
                        
                        // Look for the actual managedsoftwareupdate process
                        await Task.Delay(2000); // Give it time to start
                        var managedProcesses = Process.GetProcessesByName("managedsoftwareupdate");
                        if (managedProcesses.Length > 0)
                        {
                            process = managedProcesses.OrderByDescending(p => p.StartTime).First();
                            _logger.LogInformation("Successfully started with PowerShell elevation (Method 2), PID: {ProcessId}", process.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("PowerShell elevation method failed: {Error}", ex.Message);
                    lastException = ex;
                }
            }

            // Method 3: Try with SYSTEM account via scheduled task (for difficult domain environments)
            if (process == null)
            {
                try
                {
                    ProgressChanged?.Invoke(this, new ProgressEventArgs 
                    { 
                        Percentage = 20, 
                        Message = "Trying scheduled task elevation method..." 
                    });

                    // Create a temporary scheduled task to run with SYSTEM privileges
                    var taskName = $"CimianElevated_{Guid.NewGuid():N}";
                    var createTaskArgs = $"/Create /TN \"{taskName}\" /TR \"\\\"{execPath}\\\" --auto --show-status -vv\" /SC ONCE /ST 23:59 /RU SYSTEM /F";
                    
                    var createResult = await RunCommandAsync("schtasks.exe", createTaskArgs);
                    if (createResult.ExitCode == 0)
                    {
                        // Run the task immediately
                        var runTaskArgs = $"/Run /TN \"{taskName}\"";
                        var runResult = await RunCommandAsync("schtasks.exe", runTaskArgs);
                        
                        if (runResult.ExitCode == 0)
                        {
                            await Task.Delay(3000); // Give it time to start
                            var managedProcesses = Process.GetProcessesByName("managedsoftwareupdate");
                            if (managedProcesses.Length > 0)
                            {
                                process = managedProcesses.OrderByDescending(p => p.StartTime).First();
                                _logger.LogInformation("Successfully started with scheduled task elevation (Method 3), PID: {ProcessId}", process.Id);
                            }
                        }
                        
                        // Clean up the task
                        var deleteTaskArgs = $"/Delete /TN \"{taskName}\" /F";
                        await RunCommandAsync("schtasks.exe", deleteTaskArgs);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Scheduled task elevation method failed: {Error}", ex.Message);
                    lastException = ex;
                }
            }

            if (process == null)
            {
                var errorMessage = lastException != null 
                    ? $"All elevation methods failed. Last error: {lastException.Message}" 
                    : "Failed to start process with any elevation method";
                throw new InvalidOperationException(errorMessage);
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
            // Get the ProgramW6432 environment variable for 64-bit Program Files path
            var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? 
                              Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            
            var possiblePaths = new[]
            {
                // Installed location using ProgramW6432 (64-bit Program Files)
                Path.Combine(programFiles, "Cimian", "managedsoftwareupdate.exe"),
                
                // Fallback to standard Program Files location
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

        /// <summary>
        /// Launches managedsoftwareupdate.exe with output capture for live logging
        /// This method is specifically designed to work with LogService for live output tailing
        /// </summary>
        public Process? LaunchWithOutputCapture(Action<string> onOutputReceived, Action<string> onErrorReceived)
        {
            try
            {
                var execPath = FindExecutable();
                if (execPath == null)
                {
                    _logger.LogError("Could not find managedsoftwareupdate.exe for output capture");
                    return null;
                }

                _logger.LogInformation("Launching managedsoftwareupdate.exe with output capture: {ExecutablePath}", execPath);

                var processInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = "--auto --show-status -vv",  // Max verbosity for detailed logging (READ-ONLY monitoring)
                    UseShellExecute = false,             // Required for output capture
                    RedirectStandardOutput = true,       // Capture stdout
                    RedirectStandardError = true,        // Capture stderr
                    CreateNoWindow = true,               // No window needed
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var process = Process.Start(processInfo);
                if (process == null)
                {
                    _logger.LogError("Failed to start process for output capture");
                    return null;
                }

                // Set up output capture
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        onOutputReceived?.Invoke(e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        onErrorReceived?.Invoke(e.Data);
                    }
                };

                // Start async reading
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _logger.LogInformation("Started process with output capture (PID: {ProcessId})", process.Id);
                return process;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch process with output capture");
                return null;
            }
        }

        /// <summary>
        /// Triggers a headless update via the CimianWatcher service (no GUI will be shown)
        /// </summary>
        /// <returns>True if the headless update was successfully triggered</returns>
        public async Task<bool> TriggerHeadlessUpdateAsync()
        {
            try
            {
                _logger.LogInformation("Triggering headless update via CimianWatcher service");
                return await TryTriggerViaBootstrapAsync(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to trigger headless update");
                return false;
            }
        }

        /// <summary>
        /// Helper method to run command-line tools asynchronously
        /// </summary>
        private async Task<(int ExitCode, string Output)> RunCommandAsync(string fileName, string arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            
            await process.WaitForExitAsync();
            
            var fullOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";
            return (process.ExitCode, fullOutput);
        }
    }
}
