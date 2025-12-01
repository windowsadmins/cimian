using System.CommandLine;
using Cimian.CLI.Manifestutil.Services;

namespace Cimian.CLI.Manifestutil;

class Program
{
    private const string Version = "2.0.0";
    private const string DefaultConfigPath = @"C:\ProgramData\ManagedInstalls\Config.yaml";

    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("manifestutil - Cimian Manifest Utility")
        {
            Description = "Manage package deployment manifests and self-service requests"
        };

        // Options
        var listManifestsOption = new Option<bool>(
            aliases: ["--list-manifests", "-l"],
            description: "List available manifests");

        var newManifestOption = new Option<string?>(
            aliases: ["--new-manifest", "-n"],
            description: "Create a new manifest with the specified name");

        var addPackageOption = new Option<string?>(
            aliases: ["--add-pkg", "-a"],
            description: "Package to add to manifest");

        var removePackageOption = new Option<string?>(
            aliases: ["--remove-pkg", "-r"],
            description: "Package to remove from manifest");

        var sectionOption = new Option<string>(
            aliases: ["--section", "-s"],
            description: "Manifest section (managed_installs, managed_uninstalls, managed_updates, optional_installs)",
            getDefaultValue: () => "managed_installs");

        var manifestOption = new Option<string?>(
            aliases: ["--manifest", "-m"],
            description: "Manifest name to operate on (without .yaml extension)");

        var selfServiceRequestOption = new Option<string?>(
            aliases: ["--selfservice-request"],
            description: "Add package to self-service manifest for installation");

        var selfServiceRemoveOption = new Option<string?>(
            aliases: ["--selfservice-remove"],
            description: "Remove package from self-service manifest");

        var configOption = new Option<string>(
            aliases: ["--config", "-c"],
            description: "Path to Cimian config file",
            getDefaultValue: () => DefaultConfigPath);

        var versionOption = new Option<bool>(
            aliases: ["-V"],
            description: "Print the version and exit");

        rootCommand.AddOption(listManifestsOption);
        rootCommand.AddOption(newManifestOption);
        rootCommand.AddOption(addPackageOption);
        rootCommand.AddOption(removePackageOption);
        rootCommand.AddOption(sectionOption);
        rootCommand.AddOption(manifestOption);
        rootCommand.AddOption(selfServiceRequestOption);
        rootCommand.AddOption(selfServiceRemoveOption);
        rootCommand.AddOption(configOption);
        rootCommand.AddOption(versionOption);

        rootCommand.SetHandler((context) =>
        {
            var showVersion = context.ParseResult.GetValueForOption(versionOption);
            var listManifests = context.ParseResult.GetValueForOption(listManifestsOption);
            var newManifest = context.ParseResult.GetValueForOption(newManifestOption);
            var addPackage = context.ParseResult.GetValueForOption(addPackageOption);
            var removePackage = context.ParseResult.GetValueForOption(removePackageOption);
            var section = context.ParseResult.GetValueForOption(sectionOption)!;
            var manifestName = context.ParseResult.GetValueForOption(manifestOption);
            var selfServiceRequest = context.ParseResult.GetValueForOption(selfServiceRequestOption);
            var selfServiceRemove = context.ParseResult.GetValueForOption(selfServiceRemoveOption);
            var configPath = context.ParseResult.GetValueForOption(configOption)!;

            try
            {
                context.ExitCode = Run(
                    showVersion, listManifests, newManifest, addPackage, removePackage,
                    section, manifestName, selfServiceRequest, selfServiceRemove, configPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static int Run(
        bool showVersion,
        bool listManifests,
        string? newManifest,
        string? addPackage,
        string? removePackage,
        string section,
        string? manifestName,
        string? selfServiceRequest,
        string? selfServiceRemove,
        string configPath)
    {
        // Handle --version
        if (showVersion)
        {
            Console.WriteLine($"manifestutil version {Version}");
            return 0;
        }

        // Handle self-service commands first
        if (!string.IsNullOrEmpty(selfServiceRequest))
        {
            return HandleSelfServiceRequest(selfServiceRequest);
        }

        if (!string.IsNullOrEmpty(selfServiceRemove))
        {
            return HandleSelfServiceRemove(selfServiceRemove);
        }

        // Load config for regular manifest operations
        var manifestService = new ManifestService();
        var config = manifestService.LoadConfig(configPath);

        if (string.IsNullOrEmpty(config.RepoPath))
        {
            Console.Error.WriteLine("Error: repo_path not configured in config file");
            return 1;
        }

        var manifestDir = Path.Combine(config.RepoPath, "manifests");

        // List manifests
        if (listManifests)
        {
            return ListManifests(manifestService, manifestDir);
        }

        // Create new manifest
        if (!string.IsNullOrEmpty(newManifest))
        {
            return CreateManifest(manifestService, manifestDir, newManifest);
        }

        // Validate section
        ManifestSection parsedSection;
        try
        {
            parsedSection = ManifestSectionExtensions.Parse(section);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        // Add or remove package from manifest
        if (!string.IsNullOrEmpty(manifestName))
        {
            var manifestFilePath = Path.Combine(manifestDir, manifestName + ".yaml");

            if (!string.IsNullOrEmpty(addPackage))
            {
                return AddPackage(manifestService, manifestFilePath, addPackage, parsedSection, manifestName);
            }

            if (!string.IsNullOrEmpty(removePackage))
            {
                return RemovePackage(manifestService, manifestFilePath, removePackage, parsedSection, manifestName);
            }
        }

        // No action specified - show help hint
        Console.WriteLine("manifestutil - Cimian Manifest Utility");
        Console.WriteLine("Use --help for usage information.");
        return 0;
    }

    private static int HandleSelfServiceRequest(string packageName)
    {
        Console.WriteLine($"Adding package '{packageName}' to self-service manifest...");

        var selfService = new SelfServiceManifestService();
        var added = selfService.AddToInstalls(packageName);

        if (added)
        {
            Console.WriteLine($"Successfully added '{packageName}' to self-service manifest");
        }
        else
        {
            Console.WriteLine($"Package '{packageName}' is already in self-service manifest");
        }

        Console.WriteLine("Package will be processed on next 'managedsoftwareupdate' run.");
        return 0;
    }

    private static int HandleSelfServiceRemove(string packageName)
    {
        Console.WriteLine($"Removing package '{packageName}' from self-service manifest...");

        var selfService = new SelfServiceManifestService();
        var removed = selfService.RemoveFromInstalls(packageName);

        if (removed)
        {
            Console.WriteLine($"Successfully removed '{packageName}' from self-service manifest");
        }
        else
        {
            Console.WriteLine($"Package '{packageName}' was not in self-service manifest");
        }

        return 0;
    }

    private static int ListManifests(ManifestService service, string manifestDir)
    {
        try
        {
            var manifests = service.ListManifests(manifestDir);
            Console.WriteLine("Available manifests:");
            foreach (var manifest in manifests)
            {
                Console.WriteLine(manifest);
            }
            return 0;
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int CreateManifest(ManifestService service, string manifestDir, string name)
    {
        var manifestFilePath = Path.Combine(manifestDir, name + ".yaml");

        if (File.Exists(manifestFilePath))
        {
            Console.Error.WriteLine($"Error: Manifest '{name}' already exists");
            return 1;
        }

        service.CreateNewManifest(manifestFilePath, name);
        Console.WriteLine($"New manifest created: {manifestFilePath}");
        return 0;
    }

    private static int AddPackage(ManifestService service, string manifestPath, string package, ManifestSection section, string manifestName)
    {
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Error: Manifest file not found: {manifestPath}");
            return 1;
        }

        var manifest = service.GetManifest(manifestPath);
        service.AddPackageToManifest(manifest, package, section);
        service.SaveManifest(manifestPath, manifest);

        Console.WriteLine($"Added {package} to {section.ToYamlName()} in {manifestName}");
        return 0;
    }

    private static int RemovePackage(ManifestService service, string manifestPath, string package, ManifestSection section, string manifestName)
    {
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Error: Manifest file not found: {manifestPath}");
            return 1;
        }

        var manifest = service.GetManifest(manifestPath);
        var removed = service.RemovePackageFromManifest(manifest, package, section);

        if (removed)
        {
            service.SaveManifest(manifestPath, manifest);
            Console.WriteLine($"Removed {package} from {section.ToYamlName()} in {manifestName}");
        }
        else
        {
            Console.WriteLine($"Package {package} was not in {section.ToYamlName()} in {manifestName}");
        }

        return 0;
    }
}
