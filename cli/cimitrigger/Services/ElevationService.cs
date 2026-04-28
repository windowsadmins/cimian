using System.Diagnostics;
using Cimian.Core;
using CimianTools.CimiTrigger.Models;

namespace CimianTools.CimiTrigger.Services;

/// <summary>
/// Handles elevation of processes using various methods.
/// </summary>
public class ElevationService
{
    /// <summary>
    /// Common installation paths for managedsoftwareupdate.exe.
    /// </summary>
    private static readonly string[] ExecutablePaths =
    [
        CimianPaths.ManagedSoftwareUpdateExe,
        @"C:\Program Files (x86)\Cimian\managedsoftwareupdate.exe",
        @".\managedsoftwareupdate.exe",
        @"..\release\x64\managedsoftwareupdate.exe",
        @"..\..\release\x64\managedsoftwareupdate.exe",
        @"..\..\..\release\x64\managedsoftwareupdate.exe"
    ];

    /// <summary>
    /// Common installation paths for cimistatus.exe.
    /// </summary>
    private static readonly string[] StatusPaths =
    [
        CimianPaths.CimiStatusExe,
        @"C:\Program Files (x86)\Cimian\cimistatus.exe",
        @".\cimistatus.exe",
        @"..\release\x64\cimistatus.exe",
        @"..\..\release\x64\cimistatus.exe",
        @"..\..\..\release\x64\cimistatus.exe"
    ];

    /// <summary>
    /// Runs an update with direct elevation, trying multiple methods.
    /// </summary>
    /// <param name="mode">The trigger mode (gui or headless).</param>
    /// <returns>Elevation result.</returns>
    public async Task<ElevationResult> RunDirectUpdateAsync(TriggerMode mode)
    {
        var execPath = FindExecutable();
        if (execPath == null)
        {
            return new ElevationResult
            {
                Success = false,
                Error = "Could not find managedsoftwareupdate.exe"
            };
        }

        var args = mode switch
        {
            TriggerMode.Gui => "--auto --show-status -vv",
            TriggerMode.Headless => "--auto",
            _ => throw new ArgumentException($"Invalid mode: {mode}")
        };

        var message = mode switch
        {
            TriggerMode.Gui => "🚀 Initiating update with administrative privileges...",
            TriggerMode.Headless => "🚀 Initiating headless update with administrative privileges...",
            _ => "🚀 Initiating update..."
        };
        Console.WriteLine(message);

        // Try elevation methods in order
        var methods = new (string Name, Func<string, string, Task<bool>> Method)[]
        {
            ("PowerShell RunAs", RunWithPowerShellAsync),
            ("Scheduled Task", RunWithScheduledTaskAsync)
        };

        string? lastError = null;
        for (int i = 0; i < methods.Length; i++)
        {
            Console.WriteLine($"⚡ Using elevation method {i + 1} ({methods[i].Name})...");
            try
            {
                if (await methods[i].Method(execPath, args))
                {
                    Console.WriteLine("✅ Update process started successfully!");

                    // Give time for the process to fully start
                    Console.WriteLine("⏳ Giving process time to initialize logging...");
                    await Task.Delay(5000);

                    if (IsProcessRunning("managedsoftwareupdate"))
                    {
                        Console.WriteLine("✅ Update process confirmed running - CimianStatus should now show live progress");
                    }
                    else
                    {
                        Console.WriteLine("📋 Update process completed quickly");
                        Console.WriteLine($"💡 Check CimianStatus GUI for results, or view logs in {CimianPaths.LogsDir}");
                    }

                    return new ElevationResult
                    {
                        Success = true,
                        Method = methods[i].Name
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"📋 Method {i + 1} unavailable, trying next: {ex.Message}");
                lastError = ex.Message;
            }
        }

        return new ElevationResult
        {
            Success = false,
            Error = $"All elevation methods failed, last error: {lastError}"
        };
    }

    /// <summary>
    /// Elevates using PowerShell Start-Process with -Verb RunAs.
    /// </summary>
    private async Task<bool> RunWithPowerShellAsync(string execPath, string args)
    {
        var psCommand = $"Start-Process -FilePath '{execPath}' -ArgumentList '{args}' -Verb RunAs";
        
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -Command \"{psCommand}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return false;
        
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    /// <summary>
    /// Elevates using a scheduled task with SYSTEM account.
    /// </summary>
    private async Task<bool> RunWithScheduledTaskAsync(string execPath, string args)
    {
        var taskName = $"CimianDirect_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        // Create the task
        var createArgs = $"/Create /TN \"{taskName}\" /TR \"\\\"{execPath}\\\" {args}\" /SC ONCE /ST 23:59 /RU SYSTEM /F";
        
        var createPsi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = createArgs,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var createProcess = Process.Start(createPsi))
        {
            if (createProcess == null) return false;
            await createProcess.WaitForExitAsync();
            if (createProcess.ExitCode != 0)
            {
                throw new InvalidOperationException("Failed to create scheduled task");
            }
        }

        // Run the task
        var runPsi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Run /TN \"{taskName}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        bool success = false;
        using (var runProcess = Process.Start(runPsi))
        {
            if (runProcess != null)
            {
                await runProcess.WaitForExitAsync();
                success = runProcess.ExitCode == 0;
            }
        }

        // Clean up the task in background
        _ = Task.Run(async () =>
        {
            await Task.Delay(10000);
            var deletePsi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{taskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(deletePsi)?.WaitForExit();
        });

        if (!success)
        {
            throw new InvalidOperationException("Failed to run scheduled task");
        }

        return true;
    }

    /// <summary>
    /// Finds the managedsoftwareupdate.exe executable.
    /// </summary>
    public string? FindExecutable()
    {
        foreach (var path in ExecutablePaths)
        {
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the cimistatus.exe executable.
    /// </summary>
    public string? FindCimistatusExecutable()
    {
        // First try to find it relative to managedsoftwareupdate.exe
        var msuPath = FindExecutable();
        if (msuPath != null)
        {
            var statusPath = Path.Combine(Path.GetDirectoryName(msuPath)!, "cimistatus.exe");
            if (File.Exists(statusPath))
            {
                return statusPath;
            }
        }

        // Fall back to common paths
        foreach (var path in StatusPaths)
        {
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }
        return null;
    }

    /// <summary>
    /// Checks if a process with the given name is currently running.
    /// </summary>
    public static bool IsProcessRunning(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if cimistatus.exe is running in Session 0 (services session).
    /// </summary>
    public bool IsGUIRunningInSession0()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tasklist",
                Arguments = "/fi \"imagename eq cimistatus.exe\" /fo csv",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output.Contains(",\"Services\",\"0\",");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Terminates cimistatus processes running in Session 0.
    /// </summary>
    public void KillSession0GUI()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/f /im cimistatus.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi)?.WaitForExit();
        }
        catch
        {
            // Ignore errors - process might not exist
        }
    }

    /// <summary>
    /// Checks if running in SYSTEM session.
    /// </summary>
    public static bool IsSystemSession()
    {
        var username = Environment.GetEnvironmentVariable("USERNAME");
        var sessionName = Environment.GetEnvironmentVariable("SESSIONNAME");
        return username == "SYSTEM" || string.IsNullOrEmpty(username) || sessionName == "Services";
    }

    /// <summary>
    /// Checks if cimistatus.exe is running in the current user session.
    /// </summary>
    public bool IsGUIRunningInUserSession()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tasklist",
                Arguments = "/fi \"imagename eq cimistatus.exe\" /fo csv",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output.Contains("cimistatus.exe") && !output.Contains(",\"Services\",\"0\",");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches cimistatus.exe in the current user session.
    /// </summary>
    public bool LaunchGUIInUserSession()
    {
        var cimistatusPath = FindCimistatusExecutable();
        if (cimistatusPath == null)
        {
            Console.WriteLine("⚠️  Could not find cimistatus.exe");
            return false;
        }

        Console.WriteLine($"🚀 Launching CimianStatus GUI: {cimistatusPath}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cimistatusPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(cimistatusPath)
            };

            var process = Process.Start(psi);
            if (process != null)
            {
                Console.WriteLine($"✅ CimianStatus GUI launched successfully (PID: {process.Id})");
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to launch cimistatus.exe: {ex.Message}");
        }

        return false;
    }
}
