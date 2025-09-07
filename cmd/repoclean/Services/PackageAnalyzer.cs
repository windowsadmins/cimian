using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RepoClean.Services;

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
        
        var orphanedPackages = new List<string>();

        try
        {
            var packagesList = await repository.GetItemListAsync("pkgs");
            
            foreach (var package in packagesList)
            {
                if (!referencedPackages.Contains(package))
                {
                    orphanedPackages.Add(package);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting packages list");
            Console.WriteLine($"Error getting packages list: {ex.Message}");
        }

        return orphanedPackages;
    }
}
