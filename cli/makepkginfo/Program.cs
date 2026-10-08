using System.CommandLine;
using Cimian.CLI.Makepkginfo.Services;

namespace Cimian.CLI.Makepkginfo;

class Program
{
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
        var builder = new PkgInfoBuilder();

        // Create root command
        var rootCommand = new RootCommand("makepkginfo - Create pkgsinfo files from installer packages")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        // Define options
        var installCheckScriptOption = new Option<string?>("--installcheck_script")
        {
            Description = "Path to install check script"
        };

        var uninstallCheckScriptOption = new Option<string?>("--uninstallcheck_script")
        {
            Description = "Path to uninstall check script"
        };

        var preinstallScriptOption = new Option<string?>("--preinstall_script")
        {
            Description = "Path to preinstall script"
        };

        var postinstallScriptOption = new Option<string?>("--postinstall_script")
        {
            Description = "Path to postinstall script"
        };

        var preuninstallScriptOption = new Option<string?>("--preuninstall_script")
        {
            Description = "Path to preuninstall script"
        };

        var postuninstallScriptOption = new Option<string?>("--postuninstall_script")
        {
            Description = "Path to postuninstall script"
        };

        var uninstallerOption = new Option<string?>("--uninstaller")
        {
            Description = "Path to uninstaller executable; emitted as a one-element uninstaller: list"
        };

        var catalogsOption = new Option<string>("--catalogs")
        {
            Description = "Comma-separated list of catalogs",
            DefaultValueFactory = _ => "Development"
        };

        var categoryOption = new Option<string?>("--category")
        {
            Description = "Category"
        };

        var developerOption = new Option<string?>("--developer")
        {
            Description = "Developer"
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Name override for the package"
        };

        var identifierOption = new Option<string?>("--identifier")
        {
            Description = "Optional pkg identifier (nuspec id)"
        };

        var displayNameOption = new Option<string?>("--displayname")
        {
            Description = "Display name override"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Description"
        };

        var versionOption = new Option<string?>("--pkg-version")
        {
            Description = "Version override"
        };

        var minOSVersionOption = new Option<string?>("--minimum_os_version")
        {
            Description = "Minimum OS version required"
        };

        var maxOSVersionOption = new Option<string?>("--maximum_os_version")
        {
            Description = "Maximum OS version supported"
        };

        var minCimianVersionOption = new Option<string?>("--minimum_cimian_version")
        {
            Description = "Minimum Cimian agent version required"
        };

        var unattendedInstallOption = new Option<bool>("--unattended_install")
        {
            Description = "Set 'unattended_install: true'"
        };

        var unattendedUninstallOption = new Option<bool>("--unattended_uninstall")
        {
            Description = "Set 'unattended_uninstall: true'"
        };

        var onDemandOption = new Option<bool>("--OnDemand")
        {
            Description = "Set 'OnDemand: true' - items that can be run multiple times"
        };

        var daysUntouchedOption = new Option<int?>("--unused_removal_days")
        {
            Description = "Emit unused_software_removal_info.removal_days: uninstall when no tracked executable has been used for this many days (requires --unattended_uninstall and usage data)"
        };

        var usageTrackedPathOption = new Option<string[]>("--unused_path")
        {
            Description = "Emit unused_software_removal_info.paths entry: executable whose usage gates removal (can be specified multiple times; defaults to .exe entries in installs)",
            AllowMultipleArgumentsPerToken = true
        };

        var minUsageHistoryOption = new Option<int?>("--unused_minimum_history_days")
        {
            Description = "Emit unused_software_removal_info.minimum_history_days: minimum days of usage history required on a device before removal may act"
        };

        var newPkgOption = new Option<bool>("--new")
        {
            Description = "Create a new pkginfo stub"
        };

        var additionalFilesOption = new Option<string[]>("--file", "-f")
        {
            Description = "Add extra files to 'installs' array (can be specified multiple times)",
            AllowMultipleArgumentsPerToken = true
        };

        var installerArgument = new Argument<string?>("installer")
        {
            Description = "Path to the installer file (MSI, EXE, NUPKG)",
            Arity = ArgumentArity.ZeroOrOne
        };

        // Add all options to root command
        rootCommand.Options.Add(installCheckScriptOption);
        rootCommand.Options.Add(uninstallCheckScriptOption);
        rootCommand.Options.Add(preinstallScriptOption);
        rootCommand.Options.Add(postinstallScriptOption);
        rootCommand.Options.Add(preuninstallScriptOption);
        rootCommand.Options.Add(postuninstallScriptOption);
        rootCommand.Options.Add(uninstallerOption);
        rootCommand.Options.Add(catalogsOption);
        rootCommand.Options.Add(categoryOption);
        rootCommand.Options.Add(developerOption);
        rootCommand.Options.Add(nameOption);
        rootCommand.Options.Add(identifierOption);
        rootCommand.Options.Add(displayNameOption);
        rootCommand.Options.Add(descriptionOption);
        rootCommand.Options.Add(versionOption);
        rootCommand.Options.Add(minOSVersionOption);
        rootCommand.Options.Add(maxOSVersionOption);
        rootCommand.Options.Add(minCimianVersionOption);
        rootCommand.Options.Add(unattendedInstallOption);
        rootCommand.Options.Add(unattendedUninstallOption);
        rootCommand.Options.Add(onDemandOption);
        rootCommand.Options.Add(daysUntouchedOption);
        rootCommand.Options.Add(usageTrackedPathOption);
        rootCommand.Options.Add(minUsageHistoryOption);
        rootCommand.Options.Add(newPkgOption);
        rootCommand.Options.Add(additionalFilesOption);
        rootCommand.Arguments.Add(installerArgument);

        rootCommand.SetAction(parseResult =>
        {
            var exitCode = 0;
            var installCheckScript = parseResult.GetValue(installCheckScriptOption);
            var uninstallCheckScript = parseResult.GetValue(uninstallCheckScriptOption);
            var preinstallScript = parseResult.GetValue(preinstallScriptOption);
            var postinstallScript = parseResult.GetValue(postinstallScriptOption);
            var preuninstallScript = parseResult.GetValue(preuninstallScriptOption);
            var postuninstallScript = parseResult.GetValue(postuninstallScriptOption);
            var uninstaller = parseResult.GetValue(uninstallerOption);
            var catalogs = parseResult.GetValue(catalogsOption)!;
            var category = parseResult.GetValue(categoryOption);
            var developer = parseResult.GetValue(developerOption);
            var name = parseResult.GetValue(nameOption);
            var identifier = parseResult.GetValue(identifierOption);
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var version = parseResult.GetValue(versionOption);
            var minOSVersion = parseResult.GetValue(minOSVersionOption);
            var maxOSVersion = parseResult.GetValue(maxOSVersionOption);
            var minCimianVersion = parseResult.GetValue(minCimianVersionOption);
            var unattendedInstall = parseResult.GetValue(unattendedInstallOption);
            var unattendedUninstall = parseResult.GetValue(unattendedUninstallOption);
            var onDemand = parseResult.GetValue(onDemandOption);
            var daysUntouched = parseResult.GetValue(daysUntouchedOption);
            var usageTrackedPaths = parseResult.GetValue(usageTrackedPathOption);
            var minUsageHistory = parseResult.GetValue(minUsageHistoryOption);
            var newPkg = parseResult.GetValue(newPkgOption);
            var additionalFiles = parseResult.GetValue(additionalFilesOption);
            var installerPath = parseResult.GetValue(installerArgument);

            try
            {
                // Load config
                if (!File.Exists(PkgInfoBuilder.DefaultConfigPath))
                {
                    Console.Error.WriteLine($"Error: Config file not found at {PkgInfoBuilder.DefaultConfigPath}");
                    exitCode = 1;
                    return exitCode;
                }

                var config = builder.LoadConfig(PkgInfoBuilder.DefaultConfigPath);

                // Handle --new mode
                if (newPkg)
                {
                    if (string.IsNullOrEmpty(installerPath))
                    {
                        Console.Error.WriteLine("Usage: makepkginfo --new PkginfoName");
                        exitCode = 1;
                        return exitCode;
                    }

                    if (string.IsNullOrEmpty(config.RepoPath))
                    {
                        Console.Error.WriteLine("Error: RepoPath not configured. Set 'RepoPath' in Config.yaml or use a repo directory.");
                        exitCode = 1;
                        return exitCode;
                    }

                    var pkgsinfoDir = Path.Combine(config.RepoPath, "pkgsinfo");
                    if (!Directory.Exists(pkgsinfoDir))
                    {
                        Directory.CreateDirectory(pkgsinfoDir);
                    }

                    var pkgsinfoPath = Path.Combine(pkgsinfoDir, installerPath + ".yaml");
                    builder.CreateNewPkgsInfo(pkgsinfoPath, installerPath);
                    Console.WriteLine($"New pkgsinfo created: {pkgsinfoPath}");
                    return exitCode;
                }

                // Validate input
                if (string.IsNullOrEmpty(installerPath) && (additionalFiles == null || additionalFiles.Length == 0))
                {
                    Console.Error.WriteLine("Usage: makepkginfo [options] /path/to/installer.msi -f path1 -f path2 ...");
                    exitCode = 1;
                    return exitCode;
                }

                // Build options
                var options = new PkgsInfoOptions
                {
                    Name = name,
                    DisplayName = displayName,
                    Version = version,
                    Catalogs = catalogs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    Category = category,
                    Developer = developer,
                    Description = description,
                    UnattendedInstall = unattendedInstall,
                    UnattendedUninstall = unattendedUninstall,
                    OnDemand = onDemand,
                    UnusedRemovalDays = daysUntouched,
                    UnusedPaths = usageTrackedPaths?.Length > 0 ? usageTrackedPaths.ToList() : null,
                    UnusedMinimumHistoryDays = minUsageHistory,
                    MinOSVersion = minOSVersion,
                    MaxOSVersion = maxOSVersion,
                    MinCimianVersion = minCimianVersion,
                    InstallCheckScriptPath = installCheckScript,
                    UninstallCheckScriptPath = uninstallCheckScript,
                    PreinstallScriptPath = preinstallScript,
                    PostinstallScriptPath = postinstallScript,
                    PreuninstallScriptPath = preuninstallScript,
                    PostuninstallScriptPath = postuninstallScript,
                    UninstallerPath = uninstaller,
                    AdditionalFiles = additionalFiles?.ToList()
                };

                // Build and output pkgsinfo
                if (!string.IsNullOrEmpty(installerPath))
                {
                    installerPath = installerPath.TrimEnd('/');
                    
                    if (!File.Exists(installerPath))
                    {
                        Console.Error.WriteLine($"Error: Installer file not found: {installerPath}");
                        exitCode = 1;
                        return exitCode;
                    }

                    var pkgsinfo = builder.BuildFromInstaller(installerPath, options);
                    var yaml = builder.SerializePkgsInfo(pkgsinfo);
                    Console.WriteLine(yaml);
                }
                else if (additionalFiles?.Length > 0)
                {
                    // No installer, just -f files - create minimal pkgsinfo
                    var pkgsinfo = new Models.PkgsInfo
                    {
                        Name = name ?? "unknown",
                        Version = version ?? DateTime.Now.ToString("yyyy.MM.dd"),
                        Catalogs = options.Catalogs ?? new List<string> { "Development" },
                        Category = category,
                        Developer = developer,
                        Description = description,
                        UnattendedInstall = unattendedInstall,
                        OnDemand = onDemand,
                        MinOSVersion = minOSVersion,
                        MaxOSVersion = maxOSVersion,
                        MinCimianVersion = minCimianVersion,
                        Installs = builder.BuildInstallsArray(additionalFiles.ToList())
                    };

                    var yaml = builder.SerializePkgsInfo(pkgsinfo);
                    Console.WriteLine(yaml);
                }
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
}
