using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using Cimian.CLI.Cimipkg.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Cimian.CLI.Cimipkg;

/// <summary>
/// Custom console formatter that outputs clean messages like Go binaries.
/// </summary>
public sealed class CleanConsoleFormatter : ConsoleFormatter
{
    public CleanConsoleFormatter() : base("clean") { }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message))
            return;

        textWriter.WriteLine(message);
    }
}

class Program
{
    private const string Version = "2.0.0";

    static async Task<int> Main(string[] args)
    {
        // Root command
        var rootCommand = new RootCommand("cimipkg - Cimian Package Builder")
        {
            Description = "Build .pkg and .nupkg packages for Cimian deployment"
        };

        // Global options
        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose logging");

        rootCommand.AddGlobalOption(verboseOption);

        // Project directory argument
        var projectDirArg = new Argument<string>(
            name: "project-directory",
            description: "Path to the project directory containing build-info.yaml")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        projectDirArg.SetDefaultValue(".");

        // Build options
        var nupkgOption = new Option<bool>(
            aliases: ["--nupkg"],
            description: "Build legacy .nupkg format (default is .pkg)");

        var intunewinOption = new Option<bool>(
            aliases: ["--intunewin"],
            description: "Also generate .intunewin from .nupkg (only works with --nupkg)");

        var envOption = new Option<string?>(
            aliases: ["--env", "-e"],
            description: "Path to .env file containing environment variables");

        // Create command
        var createOption = new Option<string?>(
            aliases: ["--create", "-c"],
            description: "Create a new project structure at the specified path");

        // Resign command
        var resignOption = new Option<string?>(
            aliases: ["--resign"],
            description: "Re-sign an existing .pkg package without recompressing");

        var resignCertOption = new Option<string?>(
            aliases: ["--resign-cert"],
            description: "Certificate name for re-signing");

        var resignThumbprintOption = new Option<string?>(
            aliases: ["--resign-thumbprint"],
            description: "Certificate thumbprint for re-signing");

        rootCommand.AddArgument(projectDirArg);
        rootCommand.AddOption(nupkgOption);
        rootCommand.AddOption(intunewinOption);
        rootCommand.AddOption(envOption);
        rootCommand.AddOption(createOption);
        rootCommand.AddOption(resignOption);
        rootCommand.AddOption(resignCertOption);
        rootCommand.AddOption(resignThumbprintOption);

        rootCommand.SetHandler((context) =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var projectDir = context.ParseResult.GetValueForArgument(projectDirArg);
            var buildNupkg = context.ParseResult.GetValueForOption(nupkgOption);
            var buildIntunewin = context.ParseResult.GetValueForOption(intunewinOption);
            var envFile = context.ParseResult.GetValueForOption(envOption);
            var createPath = context.ParseResult.GetValueForOption(createOption);
            var resignPath = context.ParseResult.GetValueForOption(resignOption);
            var resignCert = context.ParseResult.GetValueForOption(resignCertOption);
            var resignThumbprint = context.ParseResult.GetValueForOption(resignThumbprintOption);

            // Set up logging with clean output (like Go binaries)
            var logLevel = verbose ? LogLevel.Debug : LogLevel.Information;
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(logLevel);
                builder.AddConsoleFormatter<CleanConsoleFormatter, ConsoleFormatterOptions>();
                builder.AddConsole(options =>
                {
                    options.FormatterName = "clean";
                });
            });

            var logger = loggerFactory.CreateLogger<Program>();
            var scriptProcessor = new ScriptProcessor(loggerFactory.CreateLogger<ScriptProcessor>());
            var chocolateyGenerator = new ChocolateyGenerator(
                loggerFactory.CreateLogger<ChocolateyGenerator>(), scriptProcessor);
            var codeSigner = new CodeSigner(loggerFactory.CreateLogger<CodeSigner>());
            var zipHelper = new ZipArchiveHelper(loggerFactory.CreateLogger<ZipArchiveHelper>());
            var packageBuilder = new PackageBuilder(
                loggerFactory.CreateLogger<PackageBuilder>(),
                scriptProcessor, chocolateyGenerator, codeSigner, zipHelper);

            try
            {
                // Handle --create
                if (!string.IsNullOrEmpty(createPath))
                {
                    logger.LogInformation("Creating new project at: {Path}", createPath);
                    packageBuilder.CreateNewProject(createPath);
                    WriteSuccess("New project created successfully!");
                    context.ExitCode = 0;
                    return;
                }

                // Handle --resign
                if (!string.IsNullOrEmpty(resignPath))
                {
                    logger.LogInformation("Re-signing package: {Path}", resignPath);
                    packageBuilder.ResignPackage(resignPath, resignCert, resignThumbprint);
                    WriteSuccess("Package re-signed successfully!");
                    context.ExitCode = 0;
                    return;
                }

                // Default: build package
                if (string.IsNullOrEmpty(projectDir))
                {
                    projectDir = ".";
                }

                projectDir = Path.GetFullPath(projectDir);

                if (!Directory.Exists(projectDir))
                {
                    WriteError($"Project directory not found: {projectDir}");
                    context.ExitCode = 1;
                    return;
                }

                var options = new PackageBuildOptions
                {
                    BuildNupkg = buildNupkg,
                    BuildIntunewin = buildIntunewin,
                    EnvFilePath = envFile,
                    Verbose = verbose
                };

                var packagePath = packageBuilder.Build(projectDir, options);
                WriteSuccess($"Package created: {packagePath}");
                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                WriteError($"Error: {ex.Message}");
                if (verbose)
                {
                    logger.LogError(ex, "Stack trace:");
                }
                context.ExitCode = 1;
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static void WriteSuccess(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✓ {message}");
        Console.ForegroundColor = originalColor;
    }

    private static void WriteError(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"✗ {message}");
        Console.ForegroundColor = originalColor;
    }
}
