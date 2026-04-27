using Cimian.Core;
using CimianTools.CimiTrigger.Models;

namespace CimianTools.CimiTrigger.Services;

/// <summary>
/// Handles trigger file creation and monitoring for service-based updates.
/// </summary>
public class TriggerService
{
    /// <summary>
    /// Bootstrap flag file paths.
    /// </summary>
    public static readonly string GuiBootstrapFile = CimianPaths.BootstrapFlagFile;
    public static readonly string HeadlessBootstrapFile = CimianPaths.HeadlessFlagFile;

    private readonly ElevationService _elevationService;

    public TriggerService(ElevationService? elevationService = null)
    {
        _elevationService = elevationService ?? new ElevationService();
    }

    /// <summary>
    /// Creates a trigger file to signal the service to start an update.
    /// </summary>
    /// <param name="flagPath">Path to the trigger file.</param>
    /// <param name="mode">The update mode.</param>
    /// <returns>True if created successfully.</returns>
    public bool CreateTriggerFile(string flagPath, string mode)
    {
        try
        {
            // Ensure the directory exists
            var dir = Path.GetDirectoryName(flagPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Create the trigger file with metadata
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var content = $"""
                Bootstrap triggered at: {timestamp}
                Mode: {mode}
                Triggered by: cimitrigger CLI
                """;

            File.WriteAllText(flagPath, content);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to create trigger file: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Waits for the service to process (delete) the trigger file.
    /// </summary>
    /// <param name="filePath">Path to the trigger file.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <returns>True if the file was processed (deleted).</returns>
    public async Task<bool> WaitForFileProcessingAsync(string filePath, TimeSpan timeout)
    {
        var start = DateTime.Now;
        while (DateTime.Now - start < timeout)
        {
            if (!File.Exists(filePath))
            {
                return true; // File was deleted - service processed it
            }
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>
    /// Runs a smart GUI update - tries service method first, falls back to direct elevation.
    /// </summary>
    public async Task<bool> RunSmartGUIUpdateAsync()
    {
        Console.WriteLine("🚀 Starting software update process...");

        // Check if managedsoftwareupdate is already running
        if (ElevationService.IsProcessRunning("managedsoftwareupdate"))
        {
            Console.WriteLine("⚠️  managedsoftwareupdate.exe is already running");
            Console.WriteLine("✅ CimianStatus GUI will monitor the existing process");
            Console.WriteLine("🔄 No need to start another process - waiting for current one to complete...");
            return true;
        }

        // Check for recent completed sessions
        var recentSession = CheckRecentSession();
        if (!string.IsNullOrEmpty(recentSession))
        {
            Console.WriteLine($"📋 Recent update session found: {recentSession}");
            Console.WriteLine("💡 CimianStatus GUI will show the latest results");
        }

        // Step 1: Try service method first
        Console.WriteLine("📡 Trying service-based update method...");
        if (!CreateTriggerFile(GuiBootstrapFile, "GUI"))
        {
            Console.WriteLine("📋 Service method unavailable (trigger file creation failed)");
            Console.WriteLine("🔄 Using direct elevation method...");
            var result = await _elevationService.RunDirectUpdateAsync(TriggerMode.Gui);
            return result.Success;
        }

        Console.WriteLine("✅ Service trigger created successfully");
        Console.WriteLine($"📁 Trigger file: {GuiBootstrapFile}");
        Console.WriteLine("⏳ Waiting for CimianWatcher service response...");

        // Wait and check if file gets processed
        if (await WaitForFileProcessingAsync(GuiBootstrapFile, TimeSpan.FromSeconds(15)))
        {
            Console.WriteLine("✅ Service-based update initiated successfully!");

            // Give the service a moment to start the process
            await Task.Delay(2000);

            // Check for Session 0 isolation issue
            if (_elevationService.IsGUIRunningInSession0())
            {
                Console.WriteLine("⚠️  Detected GUI running in Session 0 (service session)");
                Console.WriteLine("🔄 Switching to direct elevation for proper user session...");
                _elevationService.KillSession0GUI();
                var result = await _elevationService.RunDirectUpdateAsync(TriggerMode.Gui);
                return result.Success;
            }

            // Ensure GUI is visible in user session
            if (!_elevationService.IsGUIRunningInUserSession())
            {
                Console.WriteLine("🔄 Service completed - ensuring GUI remains visible in user session...");
                _elevationService.LaunchGUIInUserSession();
            }

            return true;
        }
        else
        {
            Console.WriteLine("📋 Service method timed out - using direct elevation method...");
            Console.WriteLine("🔄 This is normal and ensures the update completes successfully");

            // Clean up the trigger file
            try { File.Delete(GuiBootstrapFile); } catch { }

            var result = await _elevationService.RunDirectUpdateAsync(TriggerMode.Gui);
            return result.Success;
        }
    }

    /// <summary>
    /// Runs a smart headless update - tries service method first, falls back to direct elevation.
    /// </summary>
    public async Task<bool> RunSmartHeadlessUpdateAsync()
    {
        Console.WriteLine("🚀 Starting smart headless update...");

        // Step 1: Try service method first
        Console.WriteLine("📡 Attempting service method first...");
        if (!CreateTriggerFile(HeadlessBootstrapFile, "headless"))
        {
            Console.WriteLine($"⚠️  Service method failed (trigger file creation)");
            Console.WriteLine("🔄 Falling back to direct elevation...");
            var result = await _elevationService.RunDirectUpdateAsync(TriggerMode.Headless);
            return result.Success;
        }

        Console.WriteLine("✅ Headless update trigger file created successfully");
        Console.WriteLine($"📁 Trigger file location: {HeadlessBootstrapFile}");
        Console.WriteLine("⏳ Waiting for CimianWatcher service to process the request...");

        // Wait and check if file gets processed
        if (await WaitForFileProcessingAsync(HeadlessBootstrapFile, TimeSpan.FromSeconds(15)))
        {
            Console.WriteLine("✅ Service method successful - update should be starting!");
            return true;
        }
        else
        {
            Console.WriteLine("⚠️  Service method failed (not processed within 15 seconds)");
            Console.WriteLine("🔄 Automatically falling back to direct elevation...");

            // Clean up the trigger file
            try { File.Delete(HeadlessBootstrapFile); } catch { }

            var result = await _elevationService.RunDirectUpdateAsync(TriggerMode.Headless);
            return result.Success;
        }
    }

    /// <summary>
    /// Ensures the GUI is visible in the current user session.
    /// </summary>
    public bool EnsureGUIVisible()
    {
        // Check if we're in a SYSTEM session
        if (ElevationService.IsSystemSession())
        {
            Console.WriteLine("⚠️  Running in SYSTEM session, GUI not appropriate");
            return false;
        }

        // Check if cimistatus is already running in user session
        if (_elevationService.IsGUIRunningInUserSession())
        {
            Console.WriteLine("✅ CimianStatus GUI already running in user session");
            return true;
        }

        // Kill any Session 0 instances first
        _elevationService.KillSession0GUI();

        // Launch cimistatus in current user session
        return _elevationService.LaunchGUIInUserSession();
    }

    /// <summary>
    /// Checks for recent completed update sessions.
    /// </summary>
    private static string? CheckRecentSession()
    {
        try
        {
            var logsDir = CimianPaths.LogsDir;
            if (!Directory.Exists(logsDir)) return null;

            var directories = Directory.GetDirectories(logsDir)
                .OrderByDescending(d => Directory.GetLastWriteTime(d))
                .FirstOrDefault();

            if (directories == null) return null;

            var sessionFile = Path.Combine(directories, "session.json");
            if (File.Exists(sessionFile))
            {
                return Path.GetFileName(directories);
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }
}
