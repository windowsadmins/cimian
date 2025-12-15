using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cimian.CLI.Repoclean.Services;

namespace Cimian.CLI.Repoclean;

public class Program
{
    private const string Version = "2.0.0";

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("repoclean - Cimian Repository Cleaner")
        {
            Description = "Remove older, unused software items from a Cimian repository"
        };

        var repoUrlOption = new Option<string?>(
            aliases: ["--repo-url", "-r"],
            description: "Path to the Cimian repository");

        var keepOption = new Option<int>(
            aliases: ["--keep", "-k"],
            description: "Number of versions to keep for each package",
            getDefaultValue: () => 2);

        var showAllOption = new Option<bool>(
            aliases: ["--show-all", "-a"],
            description: "Show all packages, not just those to be deleted");

        var autoOption = new Option<bool>(
            aliases: ["--auto", "-y"],
            description: "Automatically delete without prompting");

        var removeOption = new Option<bool>(
            aliases: ["--remove", "--delete"],
            description: "Actually perform deletions (default is dry-run)");

        var versionOption = new Option<bool>(
            aliases: ["-V"],
            description: "Print version and exit");

        rootCommand.AddOption(repoUrlOption);
        rootCommand.AddOption(keepOption);
        rootCommand.AddOption(showAllOption);
        rootCommand.AddOption(autoOption);
        rootCommand.AddOption(removeOption);
        rootCommand.AddOption(versionOption);

        rootCommand.SetHandler(async (context) =>
        {
            var repoUrl = context.ParseResult.GetValueForOption(repoUrlOption);
            var keep = context.ParseResult.GetValueForOption(keepOption);
            var showAll = context.ParseResult.GetValueForOption(showAllOption);
            var auto = context.ParseResult.GetValueForOption(autoOption);
            var remove = context.ParseResult.GetValueForOption(removeOption);
            var showVersion = context.ParseResult.GetValueForOption(versionOption);

            if (showVersion)
            {
                Console.WriteLine($"repoclean version {Version}");
                context.ExitCode = 0;
                return;
            }

            context.ExitCode = await RunCleanAsync(repoUrl, keep, showAll, auto, remove);
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task<int> RunCleanAsync(
        string? repoUrl,
        int keep,
        bool showAll,
        bool auto,
        bool remove)
    {
        if (string.IsNullOrEmpty(repoUrl))
        {
            Console.Error.WriteLine("Error: --repo-url is required");
            return 1;
        }

        try
        {
            var host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.SetMinimumLevel(LogLevel.Warning);
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IFileRepository, FileRepository>();
                    services.AddSingleton<IManifestAnalyzer, ManifestAnalyzer>();
                    services.AddSingleton<IPkgInfoAnalyzer, PkgInfoAnalyzer>();
                    services.AddSingleton<IPackageAnalyzer, PackageAnalyzer>();
                    services.AddSingleton<IRepositoryCleaner, RepositoryCleaner>();
                })
                .Build();

            var fileRepo = host.Services.GetRequiredService<IFileRepository>() as FileRepository;
            fileRepo?.SetRepositoryRoot(repoUrl);

            var cleaner = host.Services.GetRequiredService<IRepositoryCleaner>();

            var options = new RepoCleanOptions
            {
                RepoUrl = repoUrl,
                Keep = keep,
                ShowAll = showAll,
                Auto = auto,
                Remove = remove
            };

            Console.WriteLine($"Repository Cleaner");
            Console.WriteLine($"==================");
            Console.WriteLine($"Repository: {repoUrl}");
            Console.WriteLine($"Mode: {(remove ? "LIVE (will delete files)" : "Dry run (no changes)")}");
            Console.WriteLine($"Keep versions: {keep}");
            Console.WriteLine();

            await cleaner.CleanAsync(options);

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
