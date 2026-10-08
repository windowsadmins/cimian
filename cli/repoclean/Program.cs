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
        return await CreateRootCommand().Parse(args).InvokeAsync();
    }

    /// <summary>
    /// The command line: every command, option and argument, and the action each runs.
    /// Built separately so tests can parse a line without running anything.
    /// </summary>
    internal static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("repoclean - Cimian Repository Cleaner")
        {
            Description = "Remove older, unused software items from a Cimian repository"
        };

        var repoUrlOption = new Option<string?>("--repo-url", "-r")
        {
            Description = "Path to the Cimian repository"
        };

        var keepOption = new Option<int>("--keep", "-k")
        {
            Description = "Number of versions to keep for each package",
            DefaultValueFactory = _ => 2
        };

        var showAllOption = new Option<bool>("--show-all", "-a")
        {
            Description = "Show all packages, not just those to be deleted"
        };

        var autoOption = new Option<bool>("--auto", "-y")
        {
            Description = "Automatically delete without prompting"
        };

        var removeOption = new Option<bool>("--remove", "--delete")
        {
            Description = "Actually perform deletions (default is dry-run)"
        };

        var versionOption = new Option<bool>("-V")
        {
            Description = "Print version and exit"
        };

        rootCommand.Options.Add(repoUrlOption);
        rootCommand.Options.Add(keepOption);
        rootCommand.Options.Add(showAllOption);
        rootCommand.Options.Add(autoOption);
        rootCommand.Options.Add(removeOption);
        rootCommand.Options.Add(versionOption);

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var exitCode = 0;
            var repoUrl = parseResult.GetValue(repoUrlOption);
            var keep = parseResult.GetValue(keepOption);
            var showAll = parseResult.GetValue(showAllOption);
            var auto = parseResult.GetValue(autoOption);
            var remove = parseResult.GetValue(removeOption);
            var showVersion = parseResult.GetValue(versionOption);

            if (showVersion)
            {
                Console.WriteLine($"repoclean version {Version}");
                exitCode = 0;
                return exitCode;
            }

            exitCode = await RunCleanAsync(repoUrl, keep, showAll, auto, remove);

            return exitCode;
        });

        return rootCommand;
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
