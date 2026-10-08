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
        return await CreateRootCommand().Parse(args).InvokeAsync();
    }

    /// <summary>
    /// The command line: every command, option and argument, and the action each runs.
    /// Built separately so tests can parse a line without running anything.
    /// </summary>
    internal static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("Cimian installer import utility - Import installers into the Cimian repository");

        // Arguments
        var packagePathArg = new Argument<string?>("installerPath")
        {
            Description = "Path to the installer file to import",
            DefaultValueFactory = _ => null
        };
        rootCommand.Arguments.Add(packagePathArg);

        // Options
        var installsArrayOption = new Option<string[]>("--installs-array", "-i")
        {
            Description = "Add a path to final 'installs' array (can be used multiple times)",
            AllowMultipleArgumentsPerToken = true
        };

        var repoPathOption = new Option<string?>("--repo_path")
        {
            Description = "Override the Cimian repo path"
        };

        var archOption = new Option<string?>("--arch")
        {
            Description = "Override architecture (e.g. x64,arm64)"
        };

        var uninstallerOption = new Option<string?>("--uninstaller")
        {
            Description = "Specify an optional uninstaller path"
        };

        var minOSVersionOption = new Option<string?>("--minimum_os_version")
        {
            Description = "Minimum Windows version required (e.g. 10.0.19041)"
        };

        var maxOSVersionOption = new Option<string?>("--maximum_os_version")
        {
            Description = "Maximum Windows version supported (e.g. 11.0.22000)"
        };

        var minCimianVersionOption = new Option<string?>("--minimum_cimian_version")
        {
            Description = "Minimum Cimian agent version required (e.g. 2026.05.01.0000)"
        };

        var preinstallScriptOption = new Option<string?>("--preinstall-script")
        {
            Description = "Path to preinstall script"
        };

        var postinstallScriptOption = new Option<string?>("--postinstall-script")
        {
            Description = "Path to postinstall script"
        };

        var preuninstallScriptOption = new Option<string?>("--preuninstall-script")
        {
            Description = "Path to preuninstall script"
        };

        var postuninstallScriptOption = new Option<string?>("--postuninstall-script")
        {
            Description = "Path to postuninstall script"
        };

        var installCheckScriptOption = new Option<string?>("--install-check-script")
        {
            Description = "Path to install check script"
        };

        var uninstallCheckScriptOption = new Option<string?>("--uninstall-check-script")
        {
            Description = "Path to uninstall check script"
        };

        var configOption = new Option<bool>("--config")
        {
            Description = "Run interactive configuration setup and exit"
        };

        var configAutoOption = new Option<bool>("--config-auto")
        {
            Description = "Auto-configure with defaults and exit"
        };

        var noInteractiveOption = new Option<bool>("--nointeractive")
        {
            Description = "Run with no prompts (use defaults or fail)"
        };

        var emitInstallsOption = new Option<bool>("--emit-installs")
        {
            Description = "Print the auto-generated 'installs' array for the installer as YAML to stdout and exit (no repo import)"
        };

        var extractIconOption = new Option<bool>("--extract-icon")
        {
            Description = "Enable icon extraction from installer (EXPERIMENTAL)"
        };

        var iconOutputOption = new Option<string?>("--icon")
        {
            Description = "Custom icon output path when extraction is enabled"
        };

        var skipIconOption = new Option<bool>("--skip-icon")
        {
            Description = "Deprecated: icon extraction is now disabled by default"
        };

        rootCommand.Options.Add(installsArrayOption);
        rootCommand.Options.Add(repoPathOption);
        rootCommand.Options.Add(archOption);
        rootCommand.Options.Add(uninstallerOption);
        rootCommand.Options.Add(minOSVersionOption);
        rootCommand.Options.Add(maxOSVersionOption);
        rootCommand.Options.Add(minCimianVersionOption);
        rootCommand.Options.Add(preinstallScriptOption);
        rootCommand.Options.Add(postinstallScriptOption);
        rootCommand.Options.Add(preuninstallScriptOption);
        rootCommand.Options.Add(postuninstallScriptOption);
        rootCommand.Options.Add(installCheckScriptOption);
        rootCommand.Options.Add(uninstallCheckScriptOption);
        rootCommand.Options.Add(configOption);
        rootCommand.Options.Add(configAutoOption);
        rootCommand.Options.Add(noInteractiveOption);
        rootCommand.Options.Add(emitInstallsOption);
        rootCommand.Options.Add(extractIconOption);
        rootCommand.Options.Add(iconOutputOption);
        rootCommand.Options.Add(skipIconOption);

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var exitCode = 0;
            var packagePath = parseResult.GetValue(packagePathArg);
            var installsArray = parseResult.GetValue(installsArrayOption) ?? [];
            var repoPath = parseResult.GetValue(repoPathOption);
            var arch = parseResult.GetValue(archOption);
            var uninstaller = parseResult.GetValue(uninstallerOption);
            var minOSVersion = parseResult.GetValue(minOSVersionOption);
            var maxOSVersion = parseResult.GetValue(maxOSVersionOption);
            var minCimianVersion = parseResult.GetValue(minCimianVersionOption);
            var preinstallScript = parseResult.GetValue(preinstallScriptOption);
            var postinstallScript = parseResult.GetValue(postinstallScriptOption);
            var preuninstallScript = parseResult.GetValue(preuninstallScriptOption);
            var postuninstallScript = parseResult.GetValue(postuninstallScriptOption);
            var installCheckScript = parseResult.GetValue(installCheckScriptOption);
            var uninstallCheckScript = parseResult.GetValue(uninstallCheckScriptOption);
            var configRequested = parseResult.GetValue(configOption);
            var configAuto = parseResult.GetValue(configAutoOption);
            var noInteractive = parseResult.GetValue(noInteractiveOption);
            var emitInstalls = parseResult.GetValue(emitInstallsOption);
            var extractIcon = parseResult.GetValue(extractIconOption);
            var iconOutput = parseResult.GetValue(iconOutputOption);
            var skipIcon = parseResult.GetValue(skipIconOption);

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
                    exitCode = 0;
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine($"❌ {ex.Message}");
                    exitCode = 1;
                }
                return exitCode;
            }

            // Prompt for package path if not provided
            if (string.IsNullOrEmpty(packagePath))
            {
                Console.Write("Path to the installer file: ");
                packagePath = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(packagePath))
                {
                    Console.WriteLine("❌ No installer path provided; exiting.");
                    exitCode = 1;
                    return exitCode;
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

            // Handle --emit-installs: print the auto-generated installs array
            // and exit without touching the repo (no git pull, no makecatalogs).
            if (emitInstalls)
            {
                var emitService = new ImportService();
                exitCode = emitService.EmitInstalls(packagePath, config, installsArray.ToList()) ? 0 : 1;
                return exitCode;
            }

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
                    minCimianVersion,
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
                    exitCode = 0;
                }
                else
                {
                    exitCode = 0; // User canceled
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error in import: {ex.Message}");
                exitCode = 1;
            }

            return exitCode;
        });

        return rootCommand;
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
