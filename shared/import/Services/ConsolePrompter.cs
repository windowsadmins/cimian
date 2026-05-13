using Cimian.CLI.Cimiimport.Models;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// Default <see cref="IImportPrompter"/> implementation — reproduces the
/// original <c>cimiimport</c> console behaviour exactly. Used when the CLI runs
/// without <c>--nointeractive</c>.
/// </summary>
public sealed class ConsolePrompter : IImportPrompter
{
    public Task<bool> AskUseTemplateAsync(PkgsInfo existingPkg, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("This item has the same Name as an existing item in the repo:");
        Console.WriteLine($"    Name: {existingPkg.Name}");
        Console.WriteLine($"    Version: {existingPkg.Version}");
        Console.WriteLine($"    Description: {existingPkg.Description}");
        Console.Write("Use existing item as a template? [Y/n]: ");
        var ans = Console.ReadLine()?.Trim();
        return Task.FromResult(string.IsNullOrEmpty(ans) || ans.Equals("y", StringComparison.OrdinalIgnoreCase));
    }

    public Task<InstallerMetadata> EditMetadataAsync(
        InstallerMetadata m,
        ImportConfiguration config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(config);

        var defaultID = !string.IsNullOrEmpty(m.ID) ? m.ID : "package";
        var defaultVersion = !string.IsNullOrEmpty(m.Version) ? m.Version : "1.0.0";

        m.ID = ReadLineWithDefault("Name", defaultID);
        m.Version = MetadataExtractor.ParseVersion(ReadLineWithDefault("Version", defaultVersion));
        m.Developer = ReadLineWithDefault("Developer", m.Developer);
        m.Description = ReadLineWithDefault("Description", m.Description);
        m.Category = ReadLineWithDefault("Category", m.Category);

        // Architectures (comma/semicolon/whitespace separated).
        var archDefault = string.Join(",", m.SupportedArch);
        var archLine = ReadLineWithDefault("Architecture(s)", archDefault).Trim();
        if (!string.IsNullOrEmpty(archLine))
        {
            var parts = archLine.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            m.SupportedArch = parts.Select(p => p.Trim().ToLowerInvariant()).ToList();
            if (m.SupportedArch.Count > 0)
            {
                m.Architecture = m.SupportedArch[0];
            }
        }

        // Catalogs — fall back to the configured default if the user accepts the default prompt.
        var fallbackCatalogs = m.Catalogs.Count > 0 ? m.Catalogs : [config.DefaultCatalog];
        var catalogsStr = string.Join(", ", fallbackCatalogs);
        var typedCatalogs = ReadLineWithDefault("Catalogs", catalogsStr);
        if (typedCatalogs == catalogsStr)
        {
            m.Catalogs = fallbackCatalogs;
        }
        else
        {
            m.Catalogs = typedCatalogs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim())
                .ToList();
        }

        return Task.FromResult(m);
    }

    public Task<string> AskRepoSubdirAsync(string defaultPath, CancellationToken cancellationToken = default)
    {
        Console.Write($"Location in repo [{defaultPath}]: ");
        var path = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            path = defaultPath;
        }
        if (!path.StartsWith('\\'))
        {
            path = "\\" + path;
        }
        return Task.FromResult(path.TrimEnd('\\'));
    }

    public Task<bool> ConfirmImportAsync(PkgsInfo pkg, CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("Pkginfo details:");
        Console.WriteLine($"     Name: {pkg.Name}");
        Console.WriteLine($"     Display Name: {pkg.DisplayName}");
        Console.WriteLine($"     Version: {pkg.Version}");
        Console.WriteLine($"     Description: {pkg.Description}");
        Console.WriteLine($"     Category: {pkg.Category}");
        Console.WriteLine($"     Developer: {pkg.Developer}");
        Console.WriteLine($"     Architectures: {string.Join(", ", pkg.SupportedArch)}");
        Console.WriteLine($"     Catalogs: {string.Join(", ", pkg.Catalogs)}");
        Console.WriteLine($"     Installer Type: {pkg.Installer?.Type}");
        Console.WriteLine();
        Console.Write("Import this item? (y/n) [n]: ");
        var confirm = Console.ReadLine()?.Trim();
        var yes = !string.IsNullOrEmpty(confirm) && confirm.Equals("y", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(yes);
    }

    public void ReportInfo(string message) => Console.WriteLine(message);

    public void ReportWarning(string message) => Console.WriteLine($"[WARN] {message}");

    public void ReportError(string message) => Console.WriteLine($"[ERROR] {message}");

    /// <summary>
    /// Console prompt with default-on-blank semantics — matches the original
    /// helper in ImportService so existing CLI muscle memory is preserved.
    /// </summary>
    private static string ReadLineWithDefault(string prompt, string defaultVal)
    {
        Console.Write($"{prompt} [{defaultVal}]: ");
        var line = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(line) ? defaultVal : line;
    }
}
