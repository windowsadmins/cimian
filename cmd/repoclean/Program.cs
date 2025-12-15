using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoClean.Services;

namespace RepoClean;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = ParseArguments(args);
        
        if (options.Version)
        {
            Console.WriteLine($"RepoClean v{typeof(Program).Assembly.GetName().Version}");
            return 0;
        }

        if (options.Help)
        {
            PrintHelp();
            return 0;
        }

        if (string.IsNullOrEmpty(options.RepoUrl))
        {
            Console.WriteLine("Error: Repository URL is required. Use --repo-url or -r to specify the repository path.");
            PrintHelp();
            return 1;
        }

        var host = CreateHostBuilder(args).Build();
        
        // Initialize the file repository with the repository root
        var fileRepository = host.Services.GetRequiredService<IFileRepository>();
        if (fileRepository is FileRepository fileRepo)
        {
            fileRepo.SetRepositoryRoot(options.RepoUrl);
        }

        var repoCleaner = host.Services.GetRequiredService<IRepositoryCleaner>();
        
        try
        {
            await repoCleaner.CleanAsync(options);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static RepoCleanOptions ParseArguments(string[] args)
    {
        var options = new RepoCleanOptions();
        
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--repo-url":
                case "-r":
                    if (i + 1 < args.Length)
                        options.RepoUrl = args[++i];
                    break;
                case "--keep":
                case "-k":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int keep))
                        options.Keep = keep;
                    break;
                case "--show-all":
                    options.ShowAll = true;
                    break;
                case "--auto":
                case "-a":
                    options.Auto = true;
                    break;
                case "--remove":
                    options.Remove = true;
                    break;
                case "--version":
                case "-v":
                    options.Version = true;
                    break;
                case "--plugin":
                    if (i + 1 < args.Length)
                        options.Plugin = args[++i];
                    break;
                case "--help":
                case "-h":
                    options.Help = true;
                    break;
                default:
                    // If it doesn't start with -, treat it as repo URL if not already set
                    if (!args[i].StartsWith("-") && string.IsNullOrEmpty(options.RepoUrl))
                        options.RepoUrl = args[i];
                    break;
            }
        }
        
        return options;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("RepoClean - A tool to remove older, unused software items from a repository");
        Console.WriteLine();
        Console.WriteLine("Usage: repoclean [OPTIONS] [REPOSITORY_PATH]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -r, --repo-url <path>     Repository URL or path to the repository root");
        Console.WriteLine("  -k, --keep <number>       Keep this many versions of each package (default: 2)");
        Console.WriteLine("      --show-all            Show all items even if none will be deleted");
        Console.WriteLine("      --remove              Actually perform deletions (default is to show what would be deleted)");
        Console.WriteLine("  -a, --auto                Do not prompt for confirmation when using --remove");
        Console.WriteLine("  -v, --version             Print the version and exit");
        Console.WriteLine("      --plugin <name>       Plugin to connect to repo (default: FileRepo)");
        Console.WriteLine("  -h, --help                Show this help message");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  repoclean --repo-url \"C:\\CimianRepo\"                          # Show what would be deleted");
        Console.WriteLine("  repoclean --repo-url \"C:\\CimianRepo\" --remove                # Actually delete items");
        Console.WriteLine("  repoclean --repo-url \"C:\\CimianRepo\" --remove --auto         # Delete without prompting");
        Console.WriteLine("  repoclean --repo-url \"C:\\CimianRepo\" --keep 3                # Keep 3 versions instead of 2");
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IRepositoryCleaner, RepositoryCleaner>();
                services.AddSingleton<IManifestAnalyzer, ManifestAnalyzer>();
                services.AddSingleton<IPkgInfoAnalyzer, PkgInfoAnalyzer>();
                services.AddSingleton<IPackageAnalyzer, PackageAnalyzer>();
                services.AddSingleton<IFileRepository, FileRepository>();
            });
}
