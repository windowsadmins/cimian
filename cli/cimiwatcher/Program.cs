using System.CommandLine;
using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Cimian.CLI.Cimiwatcher.Services;
using Cimian.Core;

namespace Cimian.CLI.Cimiwatcher;

class Program
{
    private const string ServiceName = "CimianWatcher";
    private static readonly string LogPath = CimianPaths.CimiwatcherLog;

    static async Task<int> Main(string[] args)
    {
        // Check if running as a Windows Service
        if (WindowsServiceHelpers.IsWindowsService())
        {
            return await RunAsServiceAsync(args);
        }

        // Otherwise, run as CLI
        return await RunCliAsync(args);
    }

    private static async Task<int> RunAsServiceAsync(string[] args)
    {
        ConfigureLogging(isService: true);

        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = ServiceName;
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<FileWatcherService>();
                    services.AddHostedService(sp => sp.GetRequiredService<FileWatcherService>());
                })
                .UseSerilog()
                .Build();

            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CimianWatcher service failed");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        var serviceManager = new WindowsServiceManager();

        // Create root command
        var rootCommand = new RootCommand("CimianWatcher - Monitors for bootstrap flag files and triggers managed software updates")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        // install command
        var installCommand = new Command("install", "Install the CimianWatcher Windows service");
        installCommand.SetHandler(() =>
        {
            var success = serviceManager.Install();
            Environment.ExitCode = success ? 0 : 1;
        });
        rootCommand.AddCommand(installCommand);

        // remove command
        var removeCommand = new Command("remove", "Remove the CimianWatcher Windows service");
        removeCommand.SetHandler(() =>
        {
            var success = serviceManager.Remove();
            Environment.ExitCode = success ? 0 : 1;
        });
        rootCommand.AddCommand(removeCommand);

        // start command
        var startCommand = new Command("start", "Start the CimianWatcher Windows service");
        startCommand.SetHandler(() =>
        {
            var success = serviceManager.Start();
            Environment.ExitCode = success ? 0 : 1;
        });
        rootCommand.AddCommand(startCommand);

        // stop command
        var stopCommand = new Command("stop", "Stop the CimianWatcher Windows service");
        stopCommand.SetHandler(() =>
        {
            var success = serviceManager.Stop();
            Environment.ExitCode = success ? 0 : 1;
        });
        rootCommand.AddCommand(stopCommand);

        // pause command
        var pauseCommand = new Command("pause", "Pause the CimianWatcher Windows service");
        pauseCommand.SetHandler(() =>
        {
            var success = serviceManager.Pause();
            Environment.ExitCode = success ? 0 : 1;
        });
        rootCommand.AddCommand(pauseCommand);

        // continue command
        var continueCommand = new Command("continue", "Continue the CimianWatcher Windows service after pause");
        continueCommand.SetHandler(() =>
        {
            var success = serviceManager.Continue();
            Environment.ExitCode = success ? 0 : 1;
        });
        rootCommand.AddCommand(continueCommand);

        // status command
        var statusCommand = new Command("status", "Show the status of the CimianWatcher Windows service");
        statusCommand.SetHandler(() =>
        {
            var status = serviceManager.GetStatus();
            if (status == null)
            {
                Console.WriteLine($"Service {ServiceName} is not installed");
                Environment.ExitCode = 1;
            }
            else
            {
                Console.WriteLine($"Service {ServiceName}: {status}");
                Environment.ExitCode = 0;
            }
        });
        rootCommand.AddCommand(statusCommand);

        // debug command - runs the file watcher in console mode
        var debugCommand = new Command("debug", "Run the file watcher in console debug mode (not as a service)");
        debugCommand.SetHandler(async () =>
        {
            Console.WriteLine("Running CimianWatcher in debug mode...");
            Console.WriteLine("Press Ctrl+C to stop");
            Console.WriteLine();

            ConfigureLogging(isService: false);

            try
            {
                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices((context, services) =>
                    {
                        services.AddSingleton<FileWatcherService>();
                        services.AddHostedService(sp => sp.GetRequiredService<FileWatcherService>());
                    })
                    .UseSerilog()
                    .Build();

                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        });
        rootCommand.AddCommand(debugCommand);

        // service command - internal use when running as Windows service
        var serviceCommand = new Command("service", "Run as Windows service (internal use)")
        {
            IsHidden = true
        };
        serviceCommand.SetHandler(async () =>
        {
            // This should not be reached in normal circumstances
            // as WindowsServiceHelpers.IsWindowsService() should catch this earlier
            await RunAsServiceAsync(Array.Empty<string>());
        });
        rootCommand.AddCommand(serviceCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static void ConfigureLogging(bool isService)
    {
        var logDir = Path.GetDirectoryName(LogPath);
        if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", ServiceName)
            .WriteTo.File(
                LogPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (!isService)
        {
            logConfig.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }
        else
        {
            // When running as a service, also log to Windows Event Log
            logConfig.WriteTo.EventLog(
                ServiceName,
                manageEventSource: false,
                restrictedToMinimumLevel: LogEventLevel.Information);
        }

        Log.Logger = logConfig.CreateLogger();
    }
}
