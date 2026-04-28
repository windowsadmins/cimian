using System.CommandLine;
using System.Reflection;
using Cimian.CLI.Cimiimport.Models;
using Cimian.CLI.Cimiimport.Services;
using Cimian.Core;

namespace Cimian.CLI.Cimiimport;

/// <summary>
/// Cimian installer import utility.
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Cimian installer import utility - Import installers into the Cimian repository");

        // Arguments
        var packagePathArg = new Argument<string?>("installerPath", () => null, 
            "Path to the installer file to import");
        rootCommand.AddArgument(packagePathArg);

        // Options
        var installsArrayOption = new Option<string[]>(
            ["-i", "--installs-array"],
            "Add a path to final 'installs' array (can be used multiple times)")
        { AllowMultipleArgumentsPerToken = true };

        var repoPathOption = new Option<string?>(
            "--repo_path",
            "Override the Cimian repo path");

        var archOption = new Option<string?>(
            "--arch",
            "Override architecture (e.g. x64,arm64)");

        var uninstallerOption = new Option<string?>(
            "--uninstaller",
            "Specify an optional uninstaller path");

        var minOSVersionOption = new Option<string?>(
            "--minimum_os_version",
            "Minimum Windows version required (e.g. 10.0.19041)");

        var maxOSVersionOption = new Option<string?>(
            "--maximum_os_version",
            "Maximum Windows version supported (e.g. 11.0.22000)");

        var preinstallScriptOption = new Option<string?>(
            "--preinstall-script",
            "Path to preinstall script");

        var postinstallScriptOption = new Option<string?>(
            "--postinstall-script",
            "Path to postinstall script");

        var preuninstallScriptOption = new Option<string?>(
            "--preuninstall-script",
            "Path to preuninstall script");

        var postuninstallScriptOption = new Option<string?>(
            "--postuninstall-script",
            "Path to postuninstall script");

        var installCheckScriptOption = new Option<string?>(
            "--install-check-script",
            "Path to install check script");

        var uninstallCheckScriptOption = new Option<string?>(
            "--uninstall-check-script",
            "Path to uninstall check script");

        var configOption = new Option<bool>(
            "--config",
            "Run interactive configuration setup and exit");

        var configAutoOption = new Option<bool>(
            "--config-auto",
            "Auto-configure with defaults and exit");

        var noInteractiveOption = new Option<bool>(
            "--nointeractive",
            "Run with no prompts (use defaults or fail)");

        var extractIconOption = new Option<bool>(
            "--extract-icon",
            "Enable icon extraction from installer (EXPERIMENTAL)");

        var iconOutputOption = new Option<string?>(
            "--icon",
            "Custom icon output path when extraction is enabled");

        var skipIconOption = new Option<bool>(
            "--skip-icon",
            "Deprecated: icon extraction is now disabled by default");

        rootCommand.AddOption(installsArrayOption);
        rootCommand.AddOption(repoPathOption);
        rootCommand.AddOption(archOption);
        rootCommand.AddOption(uninstallerOption);
        rootCommand.AddOption(minOSVersionOption);
        rootCommand.AddOption(maxOSVersionOption);
        rootCommand.AddOption(preinstallScriptOption);
        rootCommand.AddOption(postinstallScriptOption);
        rootCommand.AddOption(preuninstallScriptOption);
        rootCommand.AddOption(postuninstallScriptOption);
        rootCommand.AddOption(installCheckScriptOption);
        rootCommand.AddOption(uninstallCheckScriptOption);
        rootCommand.AddOption(configOption);
        rootCommand.AddOption(configAutoOption);
        rootCommand.AddOption(noInteractiveOption);
        rootCommand.AddOption(extractIconOption);
        rootCommand.AddOption(iconOutputOption);
        rootCommand.AddOption(skipIconOption);

        rootCommand.SetHandler(async (context) =>
        {
            var packagePath = context.ParseResult.GetValueForArgument(packagePathArg);
            var installsArray = context.ParseResult.GetValueForOption(installsArrayOption) ?? [];
            var repoPath = context.ParseResult.GetValueForOption(repoPathOption);
            var arch = context.ParseResult.GetValueForOption(archOption);
            var uninstaller = context.ParseResult.GetValueForOption(uninstallerOption);
            var minOSVersion = context.ParseResult.GetValueForOption(minOSVersionOption);
            var maxOSVersion = context.ParseResult.GetValueForOption(maxOSVersionOption);
            var preinstallScript = context.ParseResult.GetValueForOption(preinstallScriptOption);
            var postinstallScript = context.ParseResult.GetValueForOption(postinstallScriptOption);
            var preuninstallScript = context.ParseResult.GetValueForOption(preuninstallScriptOption);
            var postuninstallScript = context.ParseResult.GetValueForOption(postuninstallScriptOption);
            var installCheckScript = context.ParseResult.GetValueForOption(installCheckScriptOption);
            var uninstallCheckScript = context.ParseResult.GetValueForOption(uninstallCheckScriptOption);
            var configRequested = context.ParseResult.GetValueForOption(configOption);
            var configAuto = context.ParseResult.GetValueForOption(configAutoOption);
            var noInteractive = context.ParseResult.GetValueForOption(noInteractiveOption);
            var extractIcon = context.ParseResult.GetValueForOption(extractIconOption);
            var iconOutput = context.ParseResult.GetValueForOption(iconOutputOption);
            var skipIcon = context.ParseResult.GetValueForOption(skipIconOption);

            // Handle deprecated --skip-icon (warn but ignore)
            if (skipIcon)
            {
                Console.WriteLine("⚠️ --skip-icon is deprecated: icon extraction is now disabled by default. Use --extract-icon to enable.");
            }

            var configService = new ConfigurationService();
            var config = configService.LoadOrCreateConfig();

            // Handle --config or --config-auto
            if (configRequested || configAuto)
            {
                try
                {
                    if (configAuto && !configRequested)
                    {
                        configService.ConfigureNonInteractive(config);
                    }
                    else
                    {
                        configService.ConfigureInteractive(config);
                    }
                    context.ExitCode = 0;
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine($"❌ {ex.Message}");
                    context.ExitCode = 1;
                }
                return;
            }

            // Prompt for package path if not provided
            if (string.IsNullOrEmpty(packagePath))
            {
                Console.Write("Path to the installer file: ");
                packagePath = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(packagePath))
                {
                    Console.WriteLine("❌ No installer path provided; exiting.");
                    context.ExitCode = 1;
                    return;
                }
            }

            // Apply command-line overrides
            if (!string.IsNullOrEmpty(arch))
            {
                config.DefaultArch = arch;
            }
            if (!string.IsNullOrEmpty(repoPath))
            {
                config.RepoPath = repoPath;
            }

            // Build script paths
            var scripts = new ScriptPaths
            {
                Preinstall = preinstallScript,
                Postinstall = postinstallScript,
                Preuninstall = preuninstallScript,
                Postuninstall = postuninstallScript,
                InstallCheck = installCheckScript,
                UninstallCheck = uninstallCheckScript
            };

            // Check for git repo and pull
            var importService = new ImportService();
            if (ImportService.IsGitRepository(config.RepoPath))
            {
                Console.WriteLine("Git repository detected, pulling latest changes...");
                importService.RunGitPull(config.RepoPath);
            }

            // Run the import
            try
            {
                var success = await importService.ImportAsync(
                    packagePath,
                    config,
                    scripts,
                    uninstaller,
                    installsArray.ToList(),
                    minOSVersion,
                    maxOSVersion,
                    extractIcon,
                    iconOutput,
                    noInteractive
                );

                if (success)
                {
                    // Run makecatalogs
                    Console.WriteLine("Running makecatalogs...");
                    RunMakeCatalogs(config.RepoPath);

                    Console.WriteLine("Import completed successfully.");
                    context.ExitCode = 0;
                }
                else
                {
                    context.ExitCode = 0; // User canceled
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error in import: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static void PrintVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version ?? new Version(1, 0, 0);
        Console.WriteLine($"cimiimport v{version.Major}.{version.Minor}.{version.Build}");
    }

    private static void RunMakeCatalogs(string repoPath)
    {
        try
        {
            var makeCatalogsBinary = CimianPaths.MakeCatalogsExe;
            if (!File.Exists(makeCatalogsBinary))
            {
                Console.WriteLine("⚠️ makecatalogs not found");
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = makeCatalogsBinary,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(repoPath))
            {
                psi.ArgumentList.Add("--repo_path");
                psi.ArgumentList.Add(repoPath);
            }
            psi.ArgumentList.Add("--silent");

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ makecatalogs error: {ex.Message}");
        }
    }
}
