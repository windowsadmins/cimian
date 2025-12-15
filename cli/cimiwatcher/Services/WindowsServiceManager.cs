using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace Cimian.CLI.Cimiwatcher.Services;

/// <summary>
/// Windows Service management operations for CimianWatcher.
/// </summary>
public class WindowsServiceManager
{
    private const string ServiceName = "CimianWatcher";
    private const string DisplayName = "Cimian Watcher Service";
    private const string Description = "Monitors for Cimian bootstrap flag files and triggers managed software updates";

    /// <summary>
    /// Installs the CimianWatcher Windows service.
    /// </summary>
    public bool Install()
    {
        try
        {
            // Check if already installed
            if (IsInstalled())
            {
                Console.WriteLine($"Service {ServiceName} already exists, skipping installation");
                return true;
            }

            var exePath = GetExecutablePath();
            Console.WriteLine($"Installing service from: {exePath}");

            // Use sc.exe to create the service
            var result = RunScCommand($"create {ServiceName} binPath= \"\\\"{exePath}\\\" service\" " +
                $"DisplayName= \"{DisplayName}\" start= auto");

            if (!result)
            {
                Console.WriteLine("Failed to create service");
                return false;
            }

            // Set the description
            RunScCommand($"description {ServiceName} \"{Description}\"");

            // Configure recovery options (restart on failure)
            RunScCommand($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");

            Console.WriteLine($"Service {ServiceName} installed successfully");
            
            // Register event log source
            RegisterEventSource();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error installing service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes the CimianWatcher Windows service.
    /// </summary>
    public bool Remove()
    {
        try
        {
            if (!IsInstalled())
            {
                Console.WriteLine($"Service {ServiceName} is not installed");
                return false;
            }

            // Stop the service first if running
            Stop();

            // Delete the service
            var result = RunScCommand($"delete {ServiceName}");

            if (!result)
            {
                Console.WriteLine("Failed to remove service");
                return false;
            }

            Console.WriteLine($"Service {ServiceName} removed successfully");
            
            // Unregister event log source
            UnregisterEventSource();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error removing service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts the CimianWatcher Windows service.
    /// </summary>
    public bool Start()
    {
        try
        {
            if (!IsInstalled())
            {
                Console.WriteLine($"Service {ServiceName} is not installed");
                return false;
            }

            using var controller = new ServiceController(ServiceName);
            
            if (controller.Status == ServiceControllerStatus.Running)
            {
                Console.WriteLine($"Service {ServiceName} is already running");
                return true;
            }

            Console.WriteLine($"Starting service {ServiceName}...");
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Console.WriteLine($"Service {ServiceName} started successfully");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops the CimianWatcher Windows service.
    /// </summary>
    public bool Stop()
    {
        try
        {
            if (!IsInstalled())
            {
                Console.WriteLine($"Service {ServiceName} is not installed");
                return false;
            }

            using var controller = new ServiceController(ServiceName);
            
            if (controller.Status == ServiceControllerStatus.Stopped)
            {
                Console.WriteLine($"Service {ServiceName} is already stopped");
                return true;
            }

            Console.WriteLine($"Stopping service {ServiceName}...");
            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            Console.WriteLine($"Service {ServiceName} stopped successfully");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error stopping service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Pauses the CimianWatcher Windows service.
    /// </summary>
    public bool Pause()
    {
        try
        {
            if (!IsInstalled())
            {
                Console.WriteLine($"Service {ServiceName} is not installed");
                return false;
            }

            using var controller = new ServiceController(ServiceName);
            
            if (controller.Status != ServiceControllerStatus.Running)
            {
                Console.WriteLine($"Service {ServiceName} is not running");
                return false;
            }

            if (!controller.CanPauseAndContinue)
            {
                Console.WriteLine($"Service {ServiceName} does not support pause/continue");
                return false;
            }

            Console.WriteLine($"Pausing service {ServiceName}...");
            controller.Pause();
            controller.WaitForStatus(ServiceControllerStatus.Paused, TimeSpan.FromSeconds(30));
            Console.WriteLine($"Service {ServiceName} paused successfully");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error pausing service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Continues the CimianWatcher Windows service.
    /// </summary>
    public bool Continue()
    {
        try
        {
            if (!IsInstalled())
            {
                Console.WriteLine($"Service {ServiceName} is not installed");
                return false;
            }

            using var controller = new ServiceController(ServiceName);
            
            if (controller.Status != ServiceControllerStatus.Paused)
            {
                Console.WriteLine($"Service {ServiceName} is not paused");
                return false;
            }

            Console.WriteLine($"Continuing service {ServiceName}...");
            controller.Continue();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Console.WriteLine($"Service {ServiceName} continued successfully");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error continuing service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the status of the CimianWatcher Windows service.
    /// </summary>
    public ServiceControllerStatus? GetStatus()
    {
        try
        {
            if (!IsInstalled())
            {
                return null;
            }

            using var controller = new ServiceController(ServiceName);
            return controller.Status;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if the CimianWatcher Windows service is installed.
    /// </summary>
    public bool IsInstalled()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            _ = controller.Status; // This will throw if service doesn't exist
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string GetExecutablePath()
    {
        // For single-file apps, use Environment.ProcessPath
        // Fall back to AppContext.BaseDirectory for embedded assemblies
        var path = Environment.ProcessPath;
        
        if (string.IsNullOrEmpty(path))
        {
            // For single-file apps, Assembly.Location returns empty string
            // Use AppContext.BaseDirectory and the assembly name instead
            var assemblyName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
            path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.exe");
        }
        
        // If it's a dll, look for the exe
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.ChangeExtension(path, ".exe");
        }
        
        return Path.GetFullPath(path);
    }

    private static bool RunScCommand(string arguments)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrEmpty(error))
                    Console.WriteLine($"SC Error: {error}");
                else if (!string.IsNullOrEmpty(output))
                    Console.WriteLine($"SC Output: {output}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running sc command: {ex.Message}");
            return false;
        }
    }

    private static void RegisterEventSource()
    {
        try
        {
            if (!System.Diagnostics.EventLog.SourceExists(ServiceName))
            {
                System.Diagnostics.EventLog.CreateEventSource(ServiceName, "Application");
            }
        }
        catch
        {
            // Ignore event log registration errors
        }
    }

    private static void UnregisterEventSource()
    {
        try
        {
            if (System.Diagnostics.EventLog.SourceExists(ServiceName))
            {
                System.Diagnostics.EventLog.DeleteEventSource(ServiceName);
            }
        }
        catch
        {
            // Ignore event log unregistration errors
        }
    }
}
