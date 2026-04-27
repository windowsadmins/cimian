using System.CommandLine;
using Cimian.CLI.Makecatalogs.Services;
using Cimian.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Makecatalogs;

class Program
{
    private const string Version = "2.0.0";
    private static readonly string DefaultConfigPath = CimianPaths.ConfigYaml;

    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("makecatalogs - Cimian Catalog Generator")
        {
            Description = "Scan pkgsinfo directory and generate catalog files"
        };

        var repoPathOption = new Option<string?>(
            aliases: ["--repo_path", "-repo_path", "-r"],
            description: "Path to the Cimian repo. If empty, uses config.");

        var skipPayloadCheckOption = new Option<bool>(
            aliases: ["--skip_payload_check", "-s"],
            description: "Disable checking for installer/uninstaller files");

        var hashCheckOption = new Option<bool>(
            aliases: ["--hash_check"],
            description: "Enable hash and size validation (slow - use when needed)");

        var silentOption = new Option<bool>(
            aliases: ["--silent", "-q"],
            description: "Minimize output");

        var versionOption = new Option<bool>(
            aliases: ["-V"],
            description: "Print version and exit");

        rootCommand.AddOption(repoPathOption);
        rootCommand.AddOption(skipPayloadCheckOption);
        rootCommand.AddOption(hashCheckOption);
        rootCommand.AddOption(silentOption);
        rootCommand.AddOption(versionOption);

        rootCommand.SetHandler((context) =>
        {
            var repoPath = context.ParseResult.GetValueForOption(repoPathOption);
            var skipPayloadCheck = context.ParseResult.GetValueForOption(skipPayloadCheckOption);
            var hashCheck = context.ParseResult.GetValueForOption(hashCheckOption);
            var silent = context.ParseResult.GetValueForOption(silentOption);
            var showVersion = context.ParseResult.GetValueForOption(versionOption);

            try
            {
                context.ExitCode = Run(repoPath, skipPayloadCheck, hashCheck, silent, showVersion);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static int Run(string? repoPath, bool skipPayloadCheck, bool hashCheck, bool silent, bool showVersion)
    {
        if (showVersion)
        {
            Console.WriteLine($"makecatalogs version {Version}");
            return 0;
        }

        // Resolve repo path
        if (string.IsNullOrEmpty(repoPath))
        {
            repoPath = LoadRepoPathFromConfig();
            if (string.IsNullOrEmpty(repoPath))
            {
                Console.Error.WriteLine("Error: No repo_path found in config or via --repo_path.");
                return 1;
            }
        }

        // Run catalog builder
        var builder = new CatalogBuilder(
            log: silent ? null : Console.WriteLine,
            warn: msg => Console.Error.WriteLine($"WARNING: {msg}"),
            success: msg => Console.WriteLine(msg)
        );

        return builder.Run(repoPath, skipPayloadCheck, hashCheck, silent);
    }

    private static string? LoadRepoPathFromConfig()
    {
        if (!File.Exists(DefaultConfigPath))
        {
            Console.Error.WriteLine($"Error: Config file not found: {DefaultConfigPath}");
            return null;
        }

        try
        {
            var yaml = File.ReadAllText(DefaultConfigPath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var config = deserializer.Deserialize<ConfigFile>(yaml);
            return config?.RepoPath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error loading config: {ex.Message}");
            return null;
        }
    }

    private class ConfigFile
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "repo_path")]
        public string? RepoPath { get; set; }
    }
}
