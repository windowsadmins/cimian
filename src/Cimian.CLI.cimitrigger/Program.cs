using System.CommandLine;
using System.Reflection;
using CimianTools.CimiTrigger.Models;
using CimianTools.CimiTrigger.Services;

namespace CimianTools.CimiTrigger;

/// <summary>
/// Cimian software update trigger utility.
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Cimian software update trigger utility");

        // GUI command
        var guiCommand = new Command("gui", "Update with GUI - ALWAYS shows CimianStatus window when logged in");
        guiCommand.SetHandler(async () =>
        {
            var elevationService = new ElevationService();
            var triggerService = new TriggerService(elevationService);

            // Ensure GUI is shown when in GUI mode
            if (triggerService.EnsureGUIVisible())
            {
                Console.WriteLine("⏳ Allowing GUI to initialize...");
                await Task.Delay(3000);
            }
            else
            {
                Console.WriteLine("⚠️  Warning: Could not ensure GUI visibility");
            }

            if (!await triggerService.RunSmartGUIUpdateAsync())
            {
                Environment.Exit(1);
            }
        });
        rootCommand.AddCommand(guiCommand);

        // Headless command
        var headlessCommand = new Command("headless", "Smart headless update (tries service, falls back to direct)");
        headlessCommand.SetHandler(async () =>
        {
            var elevationService = new ElevationService();
            var triggerService = new TriggerService(elevationService);

            if (!await triggerService.RunSmartHeadlessUpdateAsync())
            {
                Environment.Exit(1);
            }
        });
        rootCommand.AddCommand(headlessCommand);

        // Debug command
        var debugCommand = new Command("debug", "Run diagnostics to troubleshoot issues");
        debugCommand.SetHandler(() =>
        {
            Console.WriteLine("🔍 Running diagnostic mode...");
            var diagnosticService = new DiagnosticService();
            diagnosticService.RunDiagnostics();
        });
        rootCommand.AddCommand(debugCommand);

        // Force option with subcommand
        var forceCommand = new Command("--force", "Force direct elevation (skip service attempt)");
        var forceModeArgument = new Argument<string>("mode", "The mode to use (gui or headless)");
        forceCommand.AddArgument(forceModeArgument);
        forceCommand.SetHandler(async (string mode) =>
        {
            var triggerMode = mode.ToLowerInvariant() switch
            {
                "gui" => TriggerMode.Gui,
                "headless" => TriggerMode.Headless,
                _ => throw new ArgumentException($"Invalid mode: {mode} (must be 'gui' or 'headless')")
            };

            var elevationService = new ElevationService();
            var result = await elevationService.RunDirectUpdateAsync(triggerMode);
            if (!result.Success)
            {
                Console.Error.WriteLine($"Error running forced update: {result.Error}");
                Environment.Exit(1);
            }
        }, forceModeArgument);
        rootCommand.AddCommand(forceCommand);

        // Version option
        var versionOption = new Option<bool>("--version", "Show version information");
        rootCommand.AddGlobalOption(versionOption);
        rootCommand.SetHandler((bool version) =>
        {
            if (version)
            {
                PrintVersion();
                return Task.FromResult(0);
            }
            // If no command is provided and --version is not set, show help
            return Task.FromResult(1);
        }, versionOption);

        return await rootCommand.InvokeAsync(args);
    }

    /// <summary>
    /// Prints version information.
    /// </summary>
    private static void PrintVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version ?? new Version(1, 0, 0);
        Console.WriteLine($"cimitrigger v{version.Major}.{version.Minor}.{version.Build}");
    }
}
