using System.CommandLine;
using Cimian.CLI.Makepkginfo.Services;

namespace Cimian.CLI.Makepkginfo;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var builder = new PkgInfoBuilder();

        // Create root command
        var rootCommand = new RootCommand("makepkginfo - Create pkgsinfo files from installer packages")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        // Define options
        var installCheckScriptOption = new Option<string?>(
            "--installcheck_script",
            "Path to install check script");

        var uninstallCheckScriptOption = new Option<string?>(
            "--uninstallcheck_script",
            "Path to uninstall check script");

        var preinstallScriptOption = new Option<string?>(
            "--preinstall_script",
            "Path to preinstall script");

        var postinstallScriptOption = new Option<string?>(
            "--postinstall_script",
            "Path to postinstall script");

        var catalogsOption = new Option<string>(
            "--catalogs",
            getDefaultValue: () => "Development",
            "Comma-separated list of catalogs");

        var categoryOption = new Option<string?>(
            "--category",
            "Category");

        var developerOption = new Option<string?>(
            "--developer",
            "Developer");

        var nameOption = new Option<string?>(
            "--name",
            "Name override for the package");

        var identifierOption = new Option<string?>(
            "--identifier",
            "Optional pkg identifier (nuspec id)");

        var displayNameOption = new Option<string?>(
            "--displayname",
            "Display name override");

        var descriptionOption = new Option<string?>(
            "--description",
            "Description");

        var versionOption = new Option<string?>(
            "--pkg-version",
            "Version override");

        var minOSVersionOption = new Option<string?>(
            "--minimum_os_version",
            "Minimum OS version required");

        var maxOSVersionOption = new Option<string?>(
            "--maximum_os_version",
            "Maximum OS version supported");

        var unattendedInstallOption = new Option<bool>(
            "--unattended_install",
            "Set 'unattended_install: true'");

        var unattendedUninstallOption = new Option<bool>(
            "--unattended_uninstall",
            "Set 'unattended_uninstall: true'");

        var onDemandOption = new Option<bool>(
            "--OnDemand",
            "Set 'OnDemand: true' - items that can be run multiple times");

        var newPkgOption = new Option<bool>(
            "--new",
            "Create a new pkginfo stub");

        var additionalFilesOption = new Option<string[]>(
            new[] { "-f", "--file" },
            "Add extra files to 'installs' array (can be specified multiple times)")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var installerArgument = new Argument<string?>(
            "installer",
            "Path to the installer file (MSI, EXE, NUPKG)")
        {
            Arity = ArgumentArity.ZeroOrOne
        };

        // Add all options to root command
        rootCommand.AddOption(installCheckScriptOption);
        rootCommand.AddOption(uninstallCheckScriptOption);
        rootCommand.AddOption(preinstallScriptOption);
        rootCommand.AddOption(postinstallScriptOption);
        rootCommand.AddOption(catalogsOption);
        rootCommand.AddOption(categoryOption);
        rootCommand.AddOption(developerOption);
        rootCommand.AddOption(nameOption);
        rootCommand.AddOption(identifierOption);
        rootCommand.AddOption(displayNameOption);
        rootCommand.AddOption(descriptionOption);
        rootCommand.AddOption(versionOption);
        rootCommand.AddOption(minOSVersionOption);
        rootCommand.AddOption(maxOSVersionOption);
        rootCommand.AddOption(unattendedInstallOption);
        rootCommand.AddOption(unattendedUninstallOption);
        rootCommand.AddOption(onDemandOption);
        rootCommand.AddOption(newPkgOption);
        rootCommand.AddOption(additionalFilesOption);
        rootCommand.AddArgument(installerArgument);

        rootCommand.SetHandler((context) =>
        {
            var installCheckScript = context.ParseResult.GetValueForOption(installCheckScriptOption);
            var uninstallCheckScript = context.ParseResult.GetValueForOption(uninstallCheckScriptOption);
            var preinstallScript = context.ParseResult.GetValueForOption(preinstallScriptOption);
            var postinstallScript = context.ParseResult.GetValueForOption(postinstallScriptOption);
            var catalogs = context.ParseResult.GetValueForOption(catalogsOption)!;
            var category = context.ParseResult.GetValueForOption(categoryOption);
            var developer = context.ParseResult.GetValueForOption(developerOption);
            var name = context.ParseResult.GetValueForOption(nameOption);
            var identifier = context.ParseResult.GetValueForOption(identifierOption);
            var displayName = context.ParseResult.GetValueForOption(displayNameOption);
            var description = context.ParseResult.GetValueForOption(descriptionOption);
            var version = context.ParseResult.GetValueForOption(versionOption);
            var minOSVersion = context.ParseResult.GetValueForOption(minOSVersionOption);
            var maxOSVersion = context.ParseResult.GetValueForOption(maxOSVersionOption);
            var unattendedInstall = context.ParseResult.GetValueForOption(unattendedInstallOption);
            var unattendedUninstall = context.ParseResult.GetValueForOption(unattendedUninstallOption);
            var onDemand = context.ParseResult.GetValueForOption(onDemandOption);
            var newPkg = context.ParseResult.GetValueForOption(newPkgOption);
            var additionalFiles = context.ParseResult.GetValueForOption(additionalFilesOption);
            var installerPath = context.ParseResult.GetValueForArgument(installerArgument);

            try
            {
                // Load config
                if (!File.Exists(PkgInfoBuilder.DefaultConfigPath))
                {
                    Console.Error.WriteLine($"Error: Config file not found at {PkgInfoBuilder.DefaultConfigPath}");
                    context.ExitCode = 1;
                    return;
                }

                var config = builder.LoadConfig(PkgInfoBuilder.DefaultConfigPath);

                // Handle --new mode
                if (newPkg)
                {
                    if (string.IsNullOrEmpty(installerPath))
                    {
                        Console.Error.WriteLine("Usage: makepkginfo --new PkginfoName");
                        context.ExitCode = 1;
                        return;
                    }

                    var pkgsinfoPath = Path.Combine(config.RepoPath!, "pkgsinfo", installerPath + ".yaml");
                    builder.CreateNewPkgsInfo(pkgsinfoPath, installerPath);
                    Console.WriteLine($"New pkgsinfo created: {pkgsinfoPath}");
                    return;
                }

                // Validate input
                if (string.IsNullOrEmpty(installerPath) && (additionalFiles == null || additionalFiles.Length == 0))
                {
                    Console.Error.WriteLine("Usage: makepkginfo [options] /path/to/installer.msi -f path1 -f path2 ...");
                    context.ExitCode = 1;
                    return;
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
                    MinOSVersion = minOSVersion,
                    MaxOSVersion = maxOSVersion,
                    InstallCheckScriptPath = installCheckScript,
                    UninstallCheckScriptPath = uninstallCheckScript,
                    PreinstallScriptPath = preinstallScript,
                    PostinstallScriptPath = postinstallScript,
                    AdditionalFiles = additionalFiles?.ToList()
                };

                // Build and output pkgsinfo
                if (!string.IsNullOrEmpty(installerPath))
                {
                    installerPath = installerPath.TrimEnd('/');
                    
                    if (!File.Exists(installerPath))
                    {
                        Console.Error.WriteLine($"Error: Installer file not found: {installerPath}");
                        context.ExitCode = 1;
                        return;
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
                        Installs = builder.BuildInstallsArray(additionalFiles.ToList())
                    };

                    var yaml = builder.SerializePkgsInfo(pkgsinfo);
                    Console.WriteLine(yaml);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        return await rootCommand.InvokeAsync(args);
    }
}
