using System;
using System.ServiceProcess;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cimian.Status.Services
{
    public interface IServiceStatusService
    {
        Task<ServiceStatus> GetCimianWatcherStatusAsync();
        Task<bool> IsCimianWatcherRunningAsync();
        Task<(bool Success, string Message)> TryStartCimianWatcherAsync();
        Task<string> GetServiceDiagnosticsAsync();
    }

    public class ServiceStatus
    {
        public bool IsInstalled { get; set; }
        public bool IsRunning { get; set; }
        public ServiceControllerStatus Status { get; set; }
        public string StatusDescription { get; set; } = string.Empty;
        public DateTime LastChecked { get; set; }
    }

    public class ServiceStatusService : IServiceStatusService
    {
        private readonly ILogger<ServiceStatusService> _logger;
        private const string ServiceName = "CimianWatcher";

        public ServiceStatusService(ILogger<ServiceStatusService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ServiceStatus> GetCimianWatcherStatusAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var service = new ServiceController(ServiceName);
                    
                    // Check if service is installed by trying to access its status
                    var status = service.Status;
                    
                    return new ServiceStatus
                    {
                        IsInstalled = true,
                        IsRunning = status == ServiceControllerStatus.Running,
                        Status = status,
                        StatusDescription = GetStatusDescription(status),
                        LastChecked = DateTime.Now
                    };
                }
                catch (InvalidOperationException)
                {
                    // Service is not installed
                    return new ServiceStatus
                    {
                        IsInstalled = false,
                        IsRunning = false,
                        Status = ServiceControllerStatus.Stopped,
                        StatusDescription = "Service not installed",
                        LastChecked = DateTime.Now
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking CimianWatcher service status");
                    return new ServiceStatus
                    {
                        IsInstalled = false,
                        IsRunning = false,
                        Status = ServiceControllerStatus.Stopped,
                        StatusDescription = $"Error: {ex.Message}",
                        LastChecked = DateTime.Now
                    };
                }
            });
        }

        public async Task<bool> IsCimianWatcherRunningAsync()
        {
            var status = await GetCimianWatcherStatusAsync();
            return status.IsInstalled && status.IsRunning;
        }

        public async Task<(bool Success, string Message)> TryStartCimianWatcherAsync()
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                
                if (service.Status == ServiceControllerStatus.Running)
                {
                    return (true, "Service is already running");
                }

                if (service.Status == ServiceControllerStatus.StartPending)
                {
                    return (true, "Service is already starting");
                }

                _logger.LogInformation("Attempting to start CimianWatcher service");
                service.Start();
                
                // Wait for the service to start (with timeout)
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)));
                
                return (true, "Service started successfully");
            }
            catch (InvalidOperationException ex)
            {
                var message = "CimianWatcher service is not installed";
                _logger.LogError(ex, message);
                return (false, message);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                var message = $"Access denied starting service (admin privileges required): {ex.Message}";
                _logger.LogError(ex, message);
                return (false, message);
            }
            catch (Exception ex)
            {
                var message = $"Failed to start service: {ex.Message}";
                _logger.LogError(ex, message);
                return (false, message);
            }
        }

        public async Task<string> GetServiceDiagnosticsAsync()
        {
            var status = await GetCimianWatcherStatusAsync();
            var diagnostics = new System.Text.StringBuilder();
            
            diagnostics.AppendLine("=== CimianWatcher Service Diagnostics ===");
            diagnostics.AppendLine($"Service Name: {ServiceName}");
            diagnostics.AppendLine($"Installed: {status.IsInstalled}");
            diagnostics.AppendLine($"Running: {status.IsRunning}");
            diagnostics.AppendLine($"Status: {status.StatusDescription}");
            diagnostics.AppendLine($"Last Checked: {status.LastChecked:yyyy-MM-dd HH:mm:ss}");
            
            if (!status.IsInstalled)
            {
                diagnostics.AppendLine();
                diagnostics.AppendLine("ISSUE: CimianWatcher service is not installed!");
                diagnostics.AppendLine("SOLUTION: Install Cimian properly or run the installer.");
                diagnostics.AppendLine("WORKAROUND: Use manual elevation methods in the meantime.");
            }
            else if (!status.IsRunning)
            {
                diagnostics.AppendLine();
                diagnostics.AppendLine("ISSUE: CimianWatcher service is installed but not running!");
                diagnostics.AppendLine("SOLUTION: Start the service manually or check event logs for errors.");
                diagnostics.AppendLine("WORKAROUND: Use direct execution with elevation.");
            }
            else
            {
                diagnostics.AppendLine();
                diagnostics.AppendLine("✓ Service appears to be running normally.");
                diagnostics.AppendLine("If updates aren't working, check the bootstrap trigger files:");
                diagnostics.AppendLine("  - C:\\ProgramData\\ManagedInstalls\\.cimian.bootstrap");
                diagnostics.AppendLine("  - C:\\ProgramData\\ManagedInstalls\\.cimian.headless");
            }

            // Check current user context
            diagnostics.AppendLine();
            diagnostics.AppendLine("=== Environment Information ===");
            diagnostics.AppendLine($"Current User: {Environment.UserName}");
            diagnostics.AppendLine($"User Domain: {Environment.UserDomainName}");
            diagnostics.AppendLine($"Machine Name: {Environment.MachineName}");
            diagnostics.AppendLine($"Is Interactive: {Environment.UserInteractive}");
            
            // Check if running as admin
            var isAdmin = System.Security.Principal.WindowsIdentity.GetCurrent().Owner
                ?.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) ?? false;
            diagnostics.AppendLine($"Running as Admin: {isAdmin}");

            return diagnostics.ToString();
        }

        private static string GetStatusDescription(ServiceControllerStatus status)
        {
            return status switch
            {
                ServiceControllerStatus.Running => "Running",
                ServiceControllerStatus.Stopped => "Stopped",
                ServiceControllerStatus.Paused => "Paused",
                ServiceControllerStatus.StartPending => "Starting...",
                ServiceControllerStatus.StopPending => "Stopping...",
                ServiceControllerStatus.ContinuePending => "Resuming...",
                ServiceControllerStatus.PausePending => "Pausing...",
                _ => status.ToString()
            };
        }
    }
}
