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
        return await CreateRootCommand().Parse(args).InvokeAsync();
    }

    /// <summary>
    /// The command line: every command, option and argument, and the action each runs.
    /// Built separately so tests can parse a line without running anything.
    /// </summary>
    internal static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("makecatalogs - Cimian Catalog Generator")
        {
            Description = "Scan pkgsinfo directory and generate catalog files"
        };

        var repoPathOption = new Option<string?>("--repo_path", "-repo_path", "-r")
        {
            Description = "Path to the Cimian repo. If empty, uses config."
        };

        var skipPayloadCheckOption = new Option<bool>("--skip_payload_check", "-s")
        {
            Description = "Disable checking for installer/uninstaller files"
        };

        var hashCheckOption = new Option<bool>("--hash_check")
        {
            Description = "Enable hash and size validation (slow - use when needed)"
        };

        var silentOption = new Option<bool>("--silent", "-q")
        {
            Description = "Minimize output"
        };

        var tolerateParseErrorsOption = new Option<bool>("--tolerate_parse_errors")
        {
            Description = "Write catalogs even if some pkgsinfo failed to parse (they are omitted)"
        };

        var versionOption = new Option<bool>("-V")
        {
            Description = "Print version and exit"
        };

        rootCommand.Options.Add(repoPathOption);
        rootCommand.Options.Add(skipPayloadCheckOption);
        rootCommand.Options.Add(hashCheckOption);
        rootCommand.Options.Add(silentOption);
        rootCommand.Options.Add(tolerateParseErrorsOption);
        rootCommand.Options.Add(versionOption);

        rootCommand.SetAction(parseResult =>
        {
            var exitCode = 0;
            var repoPath = parseResult.GetValue(repoPathOption);
            var skipPayloadCheck = parseResult.GetValue(skipPayloadCheckOption);
            var hashCheck = parseResult.GetValue(hashCheckOption);
            var silent = parseResult.GetValue(silentOption);
            var tolerateParseErrors = parseResult.GetValue(tolerateParseErrorsOption);
            var showVersion = parseResult.GetValue(versionOption);

            try
            {
                exitCode = Run(repoPath, skipPayloadCheck, hashCheck, silent, showVersion, tolerateParseErrors);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                exitCode = 1;
            }

            return exitCode;
        });

        return rootCommand;
    }

    private static int Run(string? repoPath, bool skipPayloadCheck, bool hashCheck, bool silent, bool showVersion, bool tolerateParseErrors = false)
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

        return builder.Run(repoPath, skipPayloadCheck, hashCheck, silent, tolerateParseErrors);
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
