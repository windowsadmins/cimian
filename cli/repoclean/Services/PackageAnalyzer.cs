using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cimian.CLI.Repoclean.Services;

public class PackageAnalyzer : IPackageAnalyzer
{
    private readonly ILogger<PackageAnalyzer> _logger;

    public PackageAnalyzer(ILogger<PackageAnalyzer> logger)
    {
        _logger = logger;
    }

    public async Task<List<string>> FindOrphanedPackagesAsync(IFileRepository repository, HashSet<string> referencedPackages)
    {
        Console.WriteLine("Analyzing installer items...");
        
        var referenced = new HashSet<string>(referencedPackages.Select(YamlValues.NormalizeRepoPath), StringComparer.OrdinalIgnoreCase);
        var packagesList = await repository.GetItemListAsync("pkgs");

        return packagesList
            .Select(YamlValues.NormalizeRepoPath)
            .Where(package => !referenced.Contains(package))
            .ToList();
    }
}
