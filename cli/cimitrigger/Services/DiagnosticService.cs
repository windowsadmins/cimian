using System.Diagnostics;
using System.ServiceProcess;
using Cimian.Core;
using CimianTools.CimiTrigger.Models;

namespace CimianTools.CimiTrigger.Services;

/// <summary>
/// Diagnostic service for troubleshooting cimitrigger issues.
/// </summary>
public class DiagnosticService
{
    private readonly ElevationService _elevationService;
    private readonly TriggerService _triggerService;

    public DiagnosticService(ElevationService? elevationService = null, TriggerService? triggerService = null)
    {
        _elevationService = elevationService ?? new ElevationService();
        _triggerService = triggerService ?? new TriggerService(_elevationService);
    }

    /// <summary>
    /// Runs full diagnostics and reports results.
    /// </summary>
    public void RunDiagnostics()
    {
        Console.WriteLine("🔍 CIMITRIGGER DIAGNOSTIC MODE");
        Console.WriteLine(new string('=', 50));

        var result = new DiagnosticResult();

        // 1. Check admin privileges
        Console.WriteLine("\n1. Checking administrative privileges...");
        result.IsAdmin = CheckAdminPrivileges();
        if (result.IsAdmin)
        {
            Console.WriteLine("   ✅ Running with administrative privileges");
        }
        else
        {
            Console.WriteLine("   ⚠️  NOT running as administrator");
            result.Issues.Add("Not running with administrator privileges");
        }

        // 2. Check CimianWatcher service status
        Console.WriteLine("\n2. Checking CimianWatcher service...");
        result.ServiceRunning = CheckServiceStatus();
        if (!result.ServiceRunning)
        {
            result.Issues.Add("CimianWatcher service not found or not running");
        }

        // 3. Check directory access
        Console.WriteLine("\n3. Checking directory access...");
        result.DirectoryOK = CheckDirectoryAccess();
        if (!result.DirectoryOK)
        {
            result.Issues.Add("Cannot access ManagedInstalls directory");
        }

        // 4. Check executables
        Console.WriteLine("\n4. Checking executables...");
        result.ExecutablesOK = CheckExecutables();
        if (!result.ExecutablesOK)
        {
            result.Issues.Add("Missing required executables");
        }

        // 5. Test trigger file creation
        Console.WriteLine("\n5. Testing trigger file creation...");
        if (result.DirectoryOK)
        {
            TestTriggerFile();
        }
        else
        {
            Console.WriteLine("   ⏭️  Skipped due to directory access issues");
        }

        // 6. Monitor for service response
        if (result.ServiceRunning && result.DirectoryOK)
        {
            Console.WriteLine("\n6. Testing service response...");
            Console.WriteLine("   Testing trigger file monitoring for 30 seconds...");
            MonitorTriggerResponse();
        }
        else
        {
            Console.WriteLine("\n6. Service response test skipped (prerequisites not met)");
        }

        // 7. Environment information
        Console.WriteLine("\n7. Environment Information:");
        Console.WriteLine($"   Current User: {Environment.GetEnvironmentVariable("USERNAME")}");
        Console.WriteLine($"   User Domain: {Environment.GetEnvironmentVariable("USERDOMAIN")}");
        Console.WriteLine($"   Machine Name: {Environment.MachineName}");
        Console.WriteLine($"   OS: {Environment.OSVersion}");

        // 8. Summary and recommendations
        PrintSummary(result);
    }

    /// <summary>
    /// Checks if running with administrative privileges.
    /// </summary>
    public static bool CheckAdminPrivileges()
    {
        try
        {
            var testFile = @"C:\Windows\Temp\cimian_admin_test.txt";
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks the CimianWatcher service status.
    /// </summary>
    private bool CheckServiceStatus()
    {
        try
        {
            using var sc = new ServiceController("CimianWatcher");
            var status = sc.Status;
            Console.WriteLine($"   Service status: {status}");

            if (status == ServiceControllerStatus.Running)
            {
                Console.WriteLine("   ✅ CimianWatcher service is running");
                return true;
            }
            else
            {
                Console.WriteLine("   ⚠️  CimianWatcher service is not running");
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("   ❌ CimianWatcher service not found");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Error checking service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks directory access for ManagedInstalls.
    /// </summary>
    private bool CheckDirectoryAccess()
    {
        var dir = CimianPaths.ManagedInstallsRoot;

        if (!Directory.Exists(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
                Console.WriteLine($"   ✅ Created directory: {dir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Cannot create directory: {ex.Message}");
                return false;
            }
        }

        // Try to write a test file
        var testFile = Path.Combine(dir, ".cimian.test");
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            Console.WriteLine($"   ✅ Write access confirmed: {dir}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ No write access: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks for required executables.
    /// </summary>
    private bool CheckExecutables()
    {
        var allOK = true;

        // Check managedsoftwareupdate.exe
        var msuPath = _elevationService.FindExecutable();
        if (msuPath != null)
        {
            Console.WriteLine($"   ✅ Found managedsoftwareupdate.exe: {msuPath}");

            // Also check for cimiwatcher.exe in the same directory
            var cimiWatcherPath = Path.Combine(Path.GetDirectoryName(msuPath)!, "cimiwatcher.exe");
            if (File.Exists(cimiWatcherPath))
            {
                Console.WriteLine($"   ✅ Found cimiwatcher.exe: {cimiWatcherPath}");
            }
            else
            {
                Console.WriteLine($"   ⚠️  cimiwatcher.exe not found: {cimiWatcherPath}");
            }
        }
        else
        {
            Console.WriteLine("   ❌ managedsoftwareupdate.exe not found");
            allOK = false;
        }

        // Check cimistatus.exe
        var statusPath = _elevationService.FindCimistatusExecutable();
        if (statusPath != null)
        {
            Console.WriteLine($"   ✅ Found cimistatus.exe: {statusPath}");
        }
        else
        {
            Console.WriteLine("   ⚠️  cimistatus.exe not found");
        }

        return allOK;
    }

    /// <summary>
    /// Creates a test trigger file.
    /// </summary>
    private void TestTriggerFile()
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var content = $"""
            Bootstrap triggered at: {timestamp}
            Mode: GUI
            Triggered by: cimitrigger debug
            """;

        try
        {
            File.WriteAllText(TriggerService.GuiBootstrapFile, content);
            Console.WriteLine($"   ✅ Created test trigger file: {TriggerService.GuiBootstrapFile}");

            // Verify it's readable
            var data = File.ReadAllBytes(TriggerService.GuiBootstrapFile);
            Console.WriteLine($"   ✅ Trigger file contents verified ({data.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Failed to create trigger file: {ex.Message}");
        }
    }

    /// <summary>
    /// Monitors for service response to trigger file.
    /// </summary>
    private void MonitorTriggerResponse()
    {
        Console.WriteLine("   Monitoring for file deletion (indicates service processed it)...");

        var start = DateTime.Now;
        while (DateTime.Now - start < TimeSpan.FromSeconds(30))
        {
            if (!File.Exists(TriggerService.GuiBootstrapFile))
            {
                var elapsed = DateTime.Now - start;
                Console.WriteLine($"   ✅ File was deleted after {elapsed.TotalSeconds:F1}s - service is responding!");
                return;
            }

            Console.Write(".");
            Thread.Sleep(2000);
        }

        Console.WriteLine();
        Console.WriteLine("   ❌ File was not processed within 30 seconds");
        Console.WriteLine("   💡 Service may not be running or monitoring correctly");

        // Clean up
        if (File.Exists(TriggerService.GuiBootstrapFile))
        {
            Console.WriteLine("   🧹 Cleaning up test trigger file...");
            try { File.Delete(TriggerService.GuiBootstrapFile); } catch { }
        }
    }

    /// <summary>
    /// Prints the diagnostic summary and recommendations.
    /// </summary>
    private void PrintSummary(DiagnosticResult result)
    {
        Console.WriteLine("\n" + new string('=', 50));
        Console.WriteLine("📊 DIAGNOSTIC SUMMARY");
        Console.WriteLine(new string('=', 50));

        if (result.Issues.Count == 0)
        {
            Console.WriteLine("✅ All checks passed - cimitrigger should work properly!");
            Console.WriteLine("💡 If you're still having issues, try running with verbose output.");
        }
        else
        {
            Console.WriteLine($"❌ Found {result.Issues.Count} issue(s):");
            for (int i = 0; i < result.Issues.Count; i++)
            {
                Console.WriteLine($"   {i + 1}. {result.Issues[i]}");
            }

            Console.WriteLine("\n🔧 SOLUTIONS:");
            ProvideRecommendations(result);
        }

        Console.WriteLine("\n💡 Alternative methods to try:");
        Console.WriteLine("   1. cimitrigger --force gui        # Direct elevation (bypasses service)");
        Console.WriteLine("   2. cimitrigger --force headless   # Direct headless elevation");
        Console.WriteLine("   3. Manual PowerShell elevation:");
        Console.WriteLine($"      PowerShell -Command \"Start-Process -FilePath '{CimianPaths.ManagedSoftwareUpdateExe}' -ArgumentList '--auto','--show-status','-vv' -Verb RunAs\"");

        Console.WriteLine("\n📋 Troubleshooting commands:");
        Console.WriteLine("   sc query CimianWatcher              # Check service status");
        Console.WriteLine("   sc start CimianWatcher              # Start service");
        Console.WriteLine("   Get-WinEvent -LogName Application | Where-Object {$_.ProviderName -eq 'CimianWatcher'} # View service logs");
    }

    /// <summary>
    /// Provides detailed recommendations based on issues found.
    /// </summary>
    private void ProvideRecommendations(DiagnosticResult result)
    {
        foreach (var issue in result.Issues)
        {
            if (issue.Contains("administrator", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("🔴 PRIVILEGE ISSUE:");
                Console.WriteLine("   Solutions:");
                Console.WriteLine("   1. Right-click Command Prompt → 'Run as administrator'");
                Console.WriteLine("   2. Use PowerShell as administrator");
                Console.WriteLine("   3. Check UAC settings");
                Console.WriteLine();
            }
            else if (issue.Contains("service not found", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("🔴 SERVICE MISSING:");
                Console.WriteLine("   Solutions:");
                Console.WriteLine("   1. Reinstall Cimian completely");
                Console.WriteLine("   2. Register service manually:");
                Console.WriteLine($"      cd \"{CimianPaths.CimianInstallDir}\"");
                Console.WriteLine("      cimiwatcher.exe install");
                Console.WriteLine("      sc start CimianWatcher");
                Console.WriteLine("   3. Use direct method: cimitrigger --force gui");
                Console.WriteLine();
            }
            else if (issue.Contains("service", StringComparison.OrdinalIgnoreCase) && !result.ServiceRunning)
            {
                Console.WriteLine("🔴 SERVICE STOPPED:");
                Console.WriteLine("   Solutions:");
                Console.WriteLine("   1. sc start CimianWatcher");
                Console.WriteLine("   2. Check Windows Event Logs for service errors");
                Console.WriteLine("   3. Restart as administrator: net stop CimianWatcher && net start CimianWatcher");
                Console.WriteLine("   4. Use direct method: cimitrigger --force gui");
                Console.WriteLine();
            }
            else if (issue.Contains("directory", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("🔴 DIRECTORY ACCESS:");
                Console.WriteLine("   Solutions:");
                Console.WriteLine("   1. Run as administrator");
                Console.WriteLine($"   2. Check folder permissions on {CimianPaths.ManagedInstallsRoot}");
                Console.WriteLine("   3. Create directory manually with proper permissions");
                Console.WriteLine("   4. Use direct method: cimitrigger --force gui");
                Console.WriteLine();
            }
            else if (issue.Contains("executable", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("🔴 MISSING EXECUTABLES:");
                Console.WriteLine("   Solutions:");
                Console.WriteLine("   1. Reinstall Cimian");
                Console.WriteLine("   2. Check installation completed successfully");
                Console.WriteLine("   3. Verify installation path");
                Console.WriteLine("   4. Check if antivirus quarantined files");
                Console.WriteLine();
            }
        }
    }
}
