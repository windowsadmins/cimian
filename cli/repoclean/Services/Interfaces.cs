using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cimian.CLI.Repoclean.Services;

public interface IRepositoryCleaner
{
    /// <returns>The process exit code: 0 on success, 1 on any failure.</returns>
    Task<int> CleanAsync(RepoCleanOptions options);
}

public interface IManifestAnalyzer
{
    Task<ManifestAnalysis> AnalyzeManifestsAsync(IFileRepository repository);
}

public interface IPkgInfoAnalyzer
{
    Task<PkgInfoAnalysis> AnalyzePkgInfoAsync(IFileRepository repository, HashSet<string> manifestItems);
}

public class ManifestAnalysis
{
    /// <summary>Every item name any manifest lists.</summary>
    public HashSet<string> Items { get; } = new();

    /// <summary>Items a manifest pins to a version (<c>name-1.2</c>).</summary>
    public HashSet<(string name, string version)> ItemsWithVersions { get; } = new();

    /// <summary>Manifests that could not be read. The items they name are unaccounted for.</summary>
    public List<string> ParseErrors { get; } = new();
}

public class PkgInfoAnalysis
{
    /// <summary>metakey → version → the pkgsinfo items at that version.</summary>
    public Dictionary<string, Dictionary<string, List<PackageInfo>>> PkgInfoDb { get; } = new();
    public HashSet<(string name, string version)> RequiredItems { get; } = new();

    /// <summary>Every payload any pkgsinfo points at, normalised with <see cref="YamlValues.NormalizeRepoPath"/>.</summary>
    public HashSet<string> ReferencedPackages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int PkgInfoCount { get; set; }

    /// <summary>pkgsinfo files that could not be read. Their payloads are unaccounted for.</summary>
    public List<string> ParseErrors { get; } = new();
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
