using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;

namespace Cimian.CLI.Cimistatus;

/// <summary>
/// CLI tool to display Cimian system status information.
/// </summary>
class Program
{
    private const string ServiceName = "CimianWatcher";
    private static readonly string ManagedInstallsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls");

    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Display Cimian system status information");

        // Service subcommand
        var serviceCommand = new Command("service", "Display CimianWatcher service status");
        serviceCommand.SetHandler(() => ShowServiceStatus());
        rootCommand.AddCommand(serviceCommand);

        // Logs subcommand
        var logsCommand = new Command("logs", "Display information about log files");
        var openOption = new Option<bool>(
            aliases: new[] { "--open", "-o" },
            description: "Open logs directory in Explorer");
        logsCommand.AddOption(openOption);
        logsCommand.SetHandler((bool open) => ShowLogsInfo(open), openOption);
        rootCommand.AddCommand(logsCommand);

        // Config subcommand
        var configCommand = new Command("config", "Display configuration status");
        configCommand.SetHandler(() => ShowConfigStatus());
        rootCommand.AddCommand(configCommand);

        // Diagnostics subcommand
        var diagCommand = new Command("diag", "Run diagnostics");
        diagCommand.AddAlias("diagnostics");
        diagCommand.SetHandler(() => RunDiagnostics());
        rootCommand.AddCommand(diagCommand);

        // Root handler - show all status by default
        rootCommand.SetHandler(() => ShowAllStatus());

        return await rootCommand.InvokeAsync(args);
    }

    private static void ShowAllStatus()
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("                    Cimian System Status                         ");
        Console.WriteLine("=================================================================");
        Console.WriteLine();

        ShowServiceStatus();
        Console.WriteLine();
        ShowLogsInfo(open: false);
        Console.WriteLine();
        ShowConfigStatus();
        Console.WriteLine();
        ShowEnvironmentInfo();
    }

    private static void ShowServiceStatus()
    {
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine(" CimianWatcher Service Status");
        Console.WriteLine("-----------------------------------------------------------------");

        try
        {
            using var service = new ServiceController(ServiceName);
            var status = service.Status;
            var statusIcon = status switch
            {
                ServiceControllerStatus.Running => "[OK]",
                ServiceControllerStatus.Stopped => "[X]",
                ServiceControllerStatus.StartPending => "[...]",
                ServiceControllerStatus.StopPending => "[...]",
                _ => "[?]"
            };
            var statusText = GetStatusDescription(status);

            Console.WriteLine("  Service Name:     " + ServiceName);
            Console.WriteLine("  Status:           " + statusIcon + " " + statusText);
            Console.WriteLine("  Installed:        Yes");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("  [!] CimianWatcher service is not installed");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [X] Error checking service: " + ex.Message);
        }
    }

    private static void ShowLogsInfo(bool open)
    {
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine(" Logs Information");
        Console.WriteLine("-----------------------------------------------------------------");

        var logsDir = Path.Combine(ManagedInstallsPath, "logs");
        var lastRunTimeFile = Path.Combine(ManagedInstallsPath, "LastRunTime.txt");

        // Last run time
        Console.Write("  Last Run:         ");
        if (File.Exists(lastRunTimeFile))
        {
            try
            {
                var lastRun = File.ReadAllText(lastRunTimeFile).Trim();
                Console.WriteLine(string.IsNullOrEmpty(lastRun) ? "Never" : lastRun);
            }
            catch
            {
                Console.WriteLine("Unable to read");
            }
        }
        else
        {
            Console.WriteLine("Never");
        }

        // Logs directory
        Console.Write("  Logs Directory:   ");
        if (Directory.Exists(logsDir))
        {
            Console.WriteLine("[OK] Exists");

            // Find latest session
            var latestSession = GetLatestLogDirectory(logsDir);
            if (!string.IsNullOrEmpty(latestSession))
            {
                Console.WriteLine("  Latest Session:   " + Path.GetFileName(latestSession));

                // Count log files
                var logFiles = Directory.GetFiles(latestSession, "*.log");
                Console.WriteLine("  Log Files:        " + logFiles.Length);
            }
        }
        else
        {
            Console.WriteLine("[!] Does not exist");
        }

        // Open if requested
        if (open && Directory.Exists(logsDir))
        {
            var targetDir = GetLatestLogDirectory(logsDir) ?? logsDir;
            Console.WriteLine("\n  Opening: " + targetDir);
            Process.Start("explorer.exe", targetDir);
        }
    }

    private static void ShowConfigStatus()
    {
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine(" Configuration Status");
        Console.WriteLine("-----------------------------------------------------------------");

        var prefsFile = Path.Combine(ManagedInstallsPath, "preferences.yaml");
        var manifestsDir = Path.Combine(ManagedInstallsPath, "manifests");
        var catalogsDir = Path.Combine(ManagedInstallsPath, "catalogs");

        // Preferences
        Console.Write("  Preferences:      ");
        if (File.Exists(prefsFile))
        {
            Console.WriteLine("[OK] Found");
        }
        else
        {
            Console.WriteLine("[!] Not found");
        }

        // Manifests
        Console.Write("  Manifests:        ");
        if (Directory.Exists(manifestsDir))
        {
            var manifests = Directory.GetFiles(manifestsDir, "*.yaml").Length +
                           Directory.GetFiles(manifestsDir, "*.plist").Length;
            Console.WriteLine("[OK] " + manifests + " file(s)");
        }
        else
        {
            Console.WriteLine("[!] Directory not found");
        }

        // Catalogs
        Console.Write("  Catalogs:         ");
        if (Directory.Exists(catalogsDir))
        {
            var catalogs = Directory.GetFiles(catalogsDir, "*.yaml").Length +
                          Directory.GetFiles(catalogsDir, "*.plist").Length;
            Console.WriteLine("[OK] " + catalogs + " file(s)");
        }
        else
        {
            Console.WriteLine("[!] Directory not found");
        }

        // Data directory
        Console.WriteLine("  Data Directory:   " + ManagedInstallsPath);
    }

    private static void ShowEnvironmentInfo()
    {
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine(" Environment");
        Console.WriteLine("-----------------------------------------------------------------");

        Console.WriteLine("  User:             " + Environment.UserDomainName + "\\" + Environment.UserName);
        Console.WriteLine("  Machine:          " + Environment.MachineName);
        Console.WriteLine("  OS Version:       " + Environment.OSVersion);

        var isAdmin = IsRunningAsAdmin();
        Console.Write("  Admin Privileges: ");
        Console.WriteLine(isAdmin ? "[OK] Yes" : "[!] No");
    }

    private static void RunDiagnostics()
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("                    Cimian Diagnostics                           ");
        Console.WriteLine("=================================================================");
        Console.WriteLine();

        // 1. Service Status
        Console.WriteLine("[*] Checking CimianWatcher service...");
        try
        {
            using var service = new ServiceController(ServiceName);
            var status = service.Status;
            if (status == ServiceControllerStatus.Running)
            {
                Console.WriteLine("   [OK] Service is running");
            }
            else
            {
                Console.WriteLine("   [X] Service is " + GetStatusDescription(status));
                Console.WriteLine("   -> Run 'sc start CimianWatcher' as admin to start");
            }
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("   [X] Service not installed");
            Console.WriteLine("   -> Install Cimian to enable the service");
        }
        Console.WriteLine();

        // 2. Bootstrap files
        Console.WriteLine("[*] Checking bootstrap trigger files...");
        var bootstrapFile = Path.Combine(ManagedInstallsPath, ".cimian.bootstrap");
        var headlessFile = Path.Combine(ManagedInstallsPath, ".cimian.headless");

        if (File.Exists(bootstrapFile))
        {
            Console.WriteLine("   [!] Bootstrap trigger exists (may be waiting for service)");
        }
        else
        {
            Console.WriteLine("   [OK] No pending bootstrap trigger");
        }

        if (File.Exists(headlessFile))
        {
            Console.WriteLine("   [!] Headless trigger exists (may be waiting for service)");
        }
        else
        {
            Console.WriteLine("   [OK] No pending headless trigger");
        }
        Console.WriteLine();

        // 3. Preferences
        Console.WriteLine("[*] Checking preferences...");
        var prefsFile = Path.Combine(ManagedInstallsPath, "preferences.yaml");
        if (File.Exists(prefsFile))
        {
            Console.WriteLine("   [OK] Preferences file found");
        }
        else
        {
            Console.WriteLine("   [X] Preferences file not found");
            Console.WriteLine("   -> Create " + prefsFile + " with your repository settings");
        }
        Console.WriteLine();

        // 4. Admin check
        Console.WriteLine("[*] Checking privileges...");
        if (IsRunningAsAdmin())
        {
            Console.WriteLine("   [OK] Running with admin privileges");
        }
        else
        {
            Console.WriteLine("   [!] Not running as admin (some operations may require elevation)");
        }
        Console.WriteLine();

        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine("Diagnostics complete.");
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

    private static string? GetLatestLogDirectory(string logsDir)
    {
        try
        {
            return Directory.GetDirectories(logsDir)
                .Where(d =>
                {
                    var dirName = Path.GetFileName(d);
                    // Match format: YYYY-MM-DD-HHMMss (17 chars)
                    return dirName.Length == 17 &&
                           dirName[4] == '-' &&
                           dirName[7] == '-' &&
                           dirName[10] == '-';
                })
                .OrderByDescending(d => Path.GetFileName(d))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
