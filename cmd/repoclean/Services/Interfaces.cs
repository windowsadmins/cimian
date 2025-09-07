using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RepoClean.Services;

public interface IRepositoryCleaner
{
    Task CleanAsync(RepoCleanOptions options);
}

public interface IManifestAnalyzer
{
    Task<(HashSet<string> manifestItems, HashSet<(string name, string version)> manifestItemsWithVersions)> AnalyzeManifestsAsync(IFileRepository repository);
}

public interface IPkgInfoAnalyzer
{
    Task<(Dictionary<string, Dictionary<string, List<PackageInfo>>> pkgInfoDb, HashSet<(string name, string version)> requiredItems, HashSet<string> referencedPackages, int pkgInfoCount)> AnalyzePkgInfoAsync(IFileRepository repository, HashSet<string> manifestItems);
}

public interface IPackageAnalyzer
{
    Task<List<string>> FindOrphanedPackagesAsync(IFileRepository repository, HashSet<string> referencedPackages);
}

public interface IFileRepository
{
    Task<IEnumerable<string>> GetItemListAsync(string path);
    Task<string> GetContentAsync(string path);
    Task DeleteAsync(string path);
    bool Exists(string path);
}
