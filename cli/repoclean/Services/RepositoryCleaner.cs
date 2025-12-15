using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cimian.CLI.Repoclean.Services;

public class RepositoryCleaner : IRepositoryCleaner
{
    private readonly ILogger<RepositoryCleaner> _logger;
    private readonly IManifestAnalyzer _manifestAnalyzer;
    private readonly IPkgInfoAnalyzer _pkgInfoAnalyzer;
    private readonly IPackageAnalyzer _packageAnalyzer;
    private readonly IFileRepository _fileRepository;

    public RepositoryCleaner(
        ILogger<RepositoryCleaner> logger,
        IManifestAnalyzer manifestAnalyzer,
        IPkgInfoAnalyzer pkgInfoAnalyzer,
        IPackageAnalyzer packageAnalyzer,
        IFileRepository fileRepository)
    {
        _logger = logger;
        _manifestAnalyzer = manifestAnalyzer;
        _pkgInfoAnalyzer = pkgInfoAnalyzer;
        _packageAnalyzer = packageAnalyzer;
        _fileRepository = fileRepository;
    }

    public async Task CleanAsync(RepoCleanOptions options)
    {
        if (string.IsNullOrEmpty(options.RepoUrl))
        {
            Console.WriteLine("Error: Repository URL is required");
            return;
        }

        if (options.Keep < 1)
        {
            Console.WriteLine("Error: --keep value must be a positive integer");
            return;
        }

        Console.WriteLine($"Using repository: {options.RepoUrl}");

        try
        {
            // Initialize repository connection
            if (!_fileRepository.Exists(options.RepoUrl))
            {
                Console.WriteLine($"Error: Repository path does not exist: {options.RepoUrl}");
                return;
            }

            // Analyze manifests
            var (manifestItems, manifestItemsWithVersions) = await _manifestAnalyzer.AnalyzeManifestsAsync(_fileRepository);

            // Analyze pkginfo files
            var (pkgInfoDb, requiredItems, referencedPackages, pkgInfoCount) = 
                await _pkgInfoAnalyzer.AnalyzePkgInfoAsync(_fileRepository, manifestItems);

            // Find orphaned packages
            var orphanedPackages = await _packageAnalyzer.FindOrphanedPackagesAsync(_fileRepository, referencedPackages);

            // Find cleanup items
            var (itemsToDelete, packagesToKeep) = FindCleanupItems(
                pkgInfoDb, manifestItems, manifestItemsWithVersions, requiredItems, options);

            // Display statistics
            DisplayStatistics(itemsToDelete, orphanedPackages, pkgInfoCount, pkgInfoDb.Count, packagesToKeep);

            // Perform cleanup if requested
            if (itemsToDelete.Any() || orphanedPackages.Any())
            {
                if (options.Remove)
                {
                    if (await ShouldProceedWithDeletion(options))
                    {
                        await DeleteItemsAsync(itemsToDelete, orphanedPackages, packagesToKeep);
                        await RebuildCatalogsAsync(options);
                    }
                }
                else
                {
                    Console.WriteLine("\nRun with --remove to actually delete these items.");
                }
            }
            else
            {
                Console.WriteLine("No items found for deletion.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during repository cleanup");
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private (List<PackageInfo> itemsToDelete, HashSet<string> packagesToKeep) FindCleanupItems(
        Dictionary<string, Dictionary<string, List<PackageInfo>>> pkgInfoDb,
        HashSet<string> manifestItems,
        HashSet<(string name, string version)> manifestItemsWithVersions,
        HashSet<(string name, string version)> requiredItems,
        RepoCleanOptions options)
    {
        var itemsToDelete = new List<PackageInfo>();
        var packagesToKeep = new HashSet<string>();

        foreach (var kvp in pkgInfoDb.OrderBy(x => x.Key))
        {
            var metakey = kvp.Key;
            var versions = kvp.Value;
            
            var shouldPrint = options.ShowAll || versions.Count > options.Keep;
            var itemName = versions.Values.First().First().Name;

            if (shouldPrint)
            {
                Console.WriteLine(metakey);
                if (!manifestItems.Contains(itemName))
                {
                    Console.WriteLine("[not in any manifests]");
                }
                Console.WriteLine("versions:");
            }

            var index = 0;
            foreach (var versionKvp in versions.OrderByDescending(x => new Version(NormalizeVersion(x.Key))))
            {
                var version = versionKvp.Key;
                var itemList = versionKvp.Value;
                index++;

                var lineInfo = "";
                
                if (manifestItemsWithVersions.Contains((itemList[0].Name, version)))
                {
                    foreach (var item in itemList)
                    {
                        if (!string.IsNullOrEmpty(item.PackagePath))
                            packagesToKeep.Add(item.PackagePath);
                        if (!string.IsNullOrEmpty(item.UninstallPackagePath))
                            packagesToKeep.Add(item.UninstallPackagePath);
                    }
                    lineInfo = "(REQUIRED by a manifest)";
                }
                else if (requiredItems.Contains((itemList[0].Name, version)))
                {
                    foreach (var item in itemList)
                    {
                        if (!string.IsNullOrEmpty(item.PackagePath))
                            packagesToKeep.Add(item.PackagePath);
                        if (!string.IsNullOrEmpty(item.UninstallPackagePath))
                            packagesToKeep.Add(item.UninstallPackagePath);
                    }
                    lineInfo = "(REQUIRED by another pkginfo item)";
                }
                else if (index <= options.Keep)
                {
                    foreach (var item in itemList)
                    {
                        if (!string.IsNullOrEmpty(item.PackagePath))
                            packagesToKeep.Add(item.PackagePath);
                        if (!string.IsNullOrEmpty(item.UninstallPackagePath))
                            packagesToKeep.Add(item.UninstallPackagePath);
                    }
                }
                else
                {
                    itemsToDelete.AddRange(itemList);
                    lineInfo = "[to be DELETED]";
                }

                if (itemList.Count > 1)
                {
                    lineInfo = $"(multiple items share this version number) {lineInfo}";
                }
                else if (!string.IsNullOrEmpty(lineInfo))
                {
                    lineInfo = $"({itemList[0].ResourceIdentifier}) {lineInfo}";
                }

                if (shouldPrint)
                {
                    Console.WriteLine($"    {version} {lineInfo}");
                    if (itemList.Count > 1)
                    {
                        foreach (var item in itemList)
                        {
                            Console.WriteLine($"    {new string(' ', version.Length)} ({item.ResourceIdentifier})");
                        }
                    }
                }
            }

            if (shouldPrint)
            {
                Console.WriteLine();
            }
        }

        return (itemsToDelete, packagesToKeep);
    }

    private string NormalizeVersion(string version)
    {
        // Simple version normalization - replace non-numeric characters with dots
        var normalized = Regex.Replace(version, @"[^\d\.]", ".");
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        
        // Ensure we have at least 4 parts for Version constructor
        while (parts.Length < 4)
        {
            var list = parts.ToList();
            list.Add("0");
            parts = list.ToArray();
        }

        // Take only first 4 parts for Version constructor
        if (parts.Length > 4)
        {
            parts = parts.Take(4).ToArray();
        }

        try
        {
            return string.Join(".", parts);
        }
        catch
        {
            return "0.0.0.0";
        }
    }

    private void DisplayStatistics(
        List<PackageInfo> itemsToDelete,
        List<string> orphanedPackages,
        int pkgInfoCount,
        int itemVariants,
        HashSet<string> packagesToKeep)
    {
        if (orphanedPackages.Any())
        {
            Console.WriteLine("The following packages are not referred to by any pkginfo item:");
            foreach (var pkg in orphanedPackages)
            {
                Console.WriteLine($"\t{pkg}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"Total pkginfo items:     {pkgInfoCount}");
        Console.WriteLine($"Item variants:           {itemVariants}");
        Console.WriteLine($"pkginfo items to delete: {itemsToDelete.Count}");

        var stats = GetDeleteStats(itemsToDelete, packagesToKeep);
        Console.WriteLine($"pkgs to delete:          {stats.PackageCount}");
        Console.WriteLine($"pkginfo space savings:   {stats.PkgInfoSize}");
        Console.WriteLine($"pkg space savings:       {stats.PackageSize}");

        if (orphanedPackages.Any())
        {
            Console.WriteLine($"                         (Unknown additional pkg space savings from {orphanedPackages.Count} orphaned pkgs)");
        }
    }

    private DeleteStats GetDeleteStats(List<PackageInfo> itemsToDelete, HashSet<string> packagesToKeep)
    {
        var packageCount = 0;
        var pkgInfoTotalSize = 0L;
        var packageTotalSize = 0L;

        foreach (var item in itemsToDelete)
        {
            pkgInfoTotalSize += item.ItemSize;
            
            if (!string.IsNullOrEmpty(item.PackagePath) && !packagesToKeep.Contains(item.PackagePath))
            {
                packageCount++;
                packageTotalSize += item.PackageSize;
            }
            
            if (!string.IsNullOrEmpty(item.UninstallPackagePath) && !packagesToKeep.Contains(item.UninstallPackagePath))
            {
                packageCount++;
                packageTotalSize += item.UninstallPackageSize;
            }
        }

        return new DeleteStats
        {
            PackageCount = packageCount,
            PkgInfoSize = FormatBytes(pkgInfoTotalSize),
            PackageSize = FormatBytes(packageTotalSize)
        };
    }

    private string FormatBytes(long bytes)
    {
        string[] units = { " bytes", " KB", " MB", " GB", " TB", " PB" };
        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        if (unitIndex == 0)
            return $"{bytes} bytes";

        return $"{size:F1}{units[unitIndex]}";
    }

    private async Task<bool> ShouldProceedWithDeletion(RepoCleanOptions options)
    {
        if (options.Auto)
        {
            Console.WriteLine("Auto mode selected, deleting pkginfo and pkg items marked as [to be DELETED]");
            return true;
        }

        Console.Write("Delete pkginfo and pkg items marked as [to be DELETED]? WARNING: This action cannot be undone. [y/N] ");
        
        var response = await ReadLineWithTimeoutAsync(30); // 30 second timeout
        if (string.IsNullOrEmpty(response))
        {
            Console.WriteLine("\nNo response received within 30 seconds. Aborting.");
            return false;
        }
        
        if (response.Trim().ToLowerInvariant().StartsWith("y"))
        {
            Console.Write("Are you sure? This action cannot be undone. [y/N] ");
            response = await ReadLineWithTimeoutAsync(30);
            if (string.IsNullOrEmpty(response))
            {
                Console.WriteLine("\nNo response received within 30 seconds. Aborting.");
                return false;
            }
            return response.Trim().ToLowerInvariant().StartsWith("y");
        }

        return false;
    }

    private async Task<string?> ReadLineWithTimeoutAsync(int timeoutSeconds)
    {
        var timeoutTask = Task.Delay(timeoutSeconds * 1000);
        var readTask = Task.Run(() => Console.ReadLine());
        
        var completedTask = await Task.WhenAny(timeoutTask, readTask);
        
        if (completedTask == timeoutTask)
        {
            return null; // Timeout occurred
        }
        
        return await readTask;
    }

    private async Task DeleteItemsAsync(List<PackageInfo> itemsToDelete, List<string> orphanedPackages, HashSet<string> packagesToKeep)
    {
        // Delete pkginfo items and referenced packages
        foreach (var item in itemsToDelete)
        {
            if (!string.IsNullOrEmpty(item.ResourceIdentifier))
            {
                Console.WriteLine($"Removing {item.ResourceIdentifier}");
                try
                {
                    await _fileRepository.DeleteAsync(item.ResourceIdentifier);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error removing {item.ResourceIdentifier}: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(item.PackagePath) && !packagesToKeep.Contains(item.PackagePath))
            {
                var packagePath = Path.Combine("pkgs", item.PackagePath);
                Console.WriteLine($"Removing {packagePath}");
                try
                {
                    await _fileRepository.DeleteAsync(packagePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error removing {packagePath}: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(item.UninstallPackagePath) && !packagesToKeep.Contains(item.UninstallPackagePath))
            {
                var packagePath = Path.Combine("pkgs", item.UninstallPackagePath);
                Console.WriteLine($"Removing {packagePath}");
                try
                {
                    await _fileRepository.DeleteAsync(packagePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error removing {packagePath}: {ex.Message}");
                }
            }
        }

        // Delete orphaned packages
        foreach (var package in orphanedPackages)
        {
            var packagePath = Path.Combine("pkgs", package);
            Console.WriteLine($"Removing {packagePath}");
            try
            {
                await _fileRepository.DeleteAsync(packagePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error removing {packagePath}: {ex.Message}");
            }
        }
    }

    private Task RebuildCatalogsAsync(RepoCleanOptions options)
    {
        Console.WriteLine($"Rebuilding catalogs at {options.RepoUrl}...");
        
        // In a real implementation, this would call the makecatalogs equivalent
        // For now, we'll just print a message
        Console.WriteLine("Catalog rebuild would be performed here...");
        
        // TODO: Implement catalog rebuilding logic
        // This would involve calling the equivalent of makecatalogs
        return Task.CompletedTask;
    }
}
