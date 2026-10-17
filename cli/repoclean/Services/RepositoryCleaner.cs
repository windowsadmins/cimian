using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Cimian.CLI.Makecatalogs.Services;

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

    public async Task<int> CleanAsync(RepoCleanOptions options)
    {
        if (string.IsNullOrEmpty(options.RepoUrl))
        {
            Console.WriteLine("Error: Repository URL is required");
            return 1;
        }

        if (options.Keep < 1)
        {
            Console.WriteLine("Error: --keep value must be a positive integer");
            return 1;
        }

        Console.WriteLine($"Using repository: {options.RepoUrl}");

        // A mistyped or unmounted path used to read as an empty repo and exit 0,
        // which under --auto looks exactly like a clean run.
        if (!_fileRepository.Exists(options.RepoUrl))
        {
            Console.WriteLine($"Error: Repository path does not exist: {options.RepoUrl}");
            return 1;
        }

        if (!_fileRepository.Exists("pkgsinfo"))
        {
            Console.WriteLine($"Error: No pkgsinfo directory in {options.RepoUrl}; is this a Cimian repository?");
            return 1;
        }

        try
        {
            var manifests = await _manifestAnalyzer.AnalyzeManifestsAsync(_fileRepository);
            var pkgInfo = await _pkgInfoAnalyzer.AnalyzePkgInfoAsync(_fileRepository, manifests.Items);
            var orphanedPackages = await _packageAnalyzer.FindOrphanedPackagesAsync(_fileRepository, pkgInfo.ReferencedPackages);

            var (itemsToDelete, packagesToKeep) = FindCleanupItems(
                pkgInfo.PkgInfoDb, manifests.Items, manifests.ItemsWithVersions, pkgInfo.RequiredItems, options);

            DisplayStatistics(itemsToDelete, orphanedPackages, pkgInfo.PkgInfoCount, pkgInfo.PkgInfoDb.Count, packagesToKeep);

            // An unreadable pkgsinfo still points at a payload, and an unreadable manifest
            // still names items, but neither is counted above. Deleting on that picture
            // removes things that are in use, so report and stop.
            var readErrors = manifests.ParseErrors.Concat(pkgInfo.ParseErrors).ToList();
            if (readErrors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Error: {readErrors.Count} file(s) could not be read, so what they reference is unknown:");
                foreach (var error in readErrors)
                {
                    Console.WriteLine($"\t{error}");
                }
                Console.WriteLine("Nothing was deleted. Fix or remove these files and run repoclean again.");
                return 1;
            }

            if (!itemsToDelete.Any() && !orphanedPackages.Any())
            {
                Console.WriteLine("No items found for deletion.");
                return 0;
            }

            if (!options.Remove)
            {
                Console.WriteLine("\nRun with --remove to actually delete these items.");
                return 0;
            }

            if (!await ShouldProceedWithDeletion(options))
            {
                return 0;
            }

            var failures = await DeleteItemsAsync(itemsToDelete, orphanedPackages, packagesToKeep);
            var rebuild = RebuildCatalogs(options);
            return failures == 0 && rebuild == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during repository cleanup");
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
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
        var packagesToKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    KeepPayloads(itemList, packagesToKeep);
                    lineInfo = "(REQUIRED by a manifest)";
                }
                else if (requiredItems.Contains((itemList[0].Name, version)))
                {
                    KeepPayloads(itemList, packagesToKeep);
                    lineInfo = "(REQUIRED by another pkginfo item)";
                }
                else if (index <= options.Keep)
                {
                    KeepPayloads(itemList, packagesToKeep);
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

    private static void KeepPayloads(IEnumerable<PackageInfo> items, HashSet<string> packagesToKeep)
    {
        foreach (var payload in items.SelectMany(i => i.PayloadPaths))
        {
            packagesToKeep.Add(payload);
        }
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
            
            var uninstallers = item.UninstallPackagePaths.Where(p => !packagesToKeep.Contains(p)).ToList();
            if (uninstallers.Count > 0)
            {
                packageCount += uninstallers.Count;
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

    /// <returns>The number of files that could not be deleted.</returns>
    private async Task<int> DeleteItemsAsync(List<PackageInfo> itemsToDelete, List<string> orphanedPackages, HashSet<string> packagesToKeep)
    {
        var failures = 0;
        var deletedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task Delete(string path)
        {
            Console.WriteLine($"Removing {path}");
            try
            {
                await _fileRepository.DeleteAsync(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error removing {path}: {ex.Message}");
                failures++;
            }
        }

        foreach (var item in itemsToDelete)
        {
            if (!string.IsNullOrEmpty(item.ResourceIdentifier))
            {
                await Delete(item.ResourceIdentifier);
            }

            foreach (var payload in item.PayloadPaths)
            {
                // A payload that any kept pkgsinfo points at stays, whatever else points at it.
                if (packagesToKeep.Contains(payload) || !deletedPackages.Add(payload))
                    continue;

                await Delete(Path.Combine("pkgs", payload));
            }
        }

        foreach (var package in orphanedPackages)
        {
            if (packagesToKeep.Contains(package) || !deletedPackages.Add(package))
                continue;

            await Delete(Path.Combine("pkgs", package));
        }

        return failures;
    }

    /// <summary>
    /// Rebuilds the catalogs with makecatalogs' own builder, as Munki's repoclean does,
    /// so they stop listing the pkgsinfo just removed.
    /// </summary>
    private static int RebuildCatalogs(RepoCleanOptions options)
    {
        Console.WriteLine($"Rebuilding catalogs at {options.RepoUrl}...");

        var builder = new CatalogBuilder(
            log: Console.WriteLine,
            warn: msg => Console.WriteLine($"WARNING: {msg}"),
            success: Console.WriteLine);

        return builder.Run(options.RepoUrl, silent: true);
    }
}
