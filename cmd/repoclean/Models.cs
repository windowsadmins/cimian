using System.Collections.Generic;

namespace RepoClean;

public class RepoCleanOptions
{
    public string RepoUrl { get; set; } = string.Empty;
    public int Keep { get; set; } = 2;
    public bool ShowAll { get; set; }
    public bool Auto { get; set; }
    public bool Remove { get; set; }  // Actually perform deletions (default is dry-run)
    public string Plugin { get; set; } = "FileRepo";
    public bool Version { get; set; }
    public bool Help { get; set; }
}

public class PackageInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ResourceIdentifier { get; set; } = string.Empty;
    public long ItemSize { get; set; }
    public string PackagePath { get; set; } = string.Empty;
    public long PackageSize { get; set; }
    public string UninstallPackagePath { get; set; } = string.Empty;
    public long UninstallPackageSize { get; set; }
    public List<string> Catalogs { get; set; } = new();
    public List<string> Requires { get; set; } = new();
    public List<string> UpdateFor { get; set; } = new();
    public string MinimumMunkiVersion { get; set; } = string.Empty;
    public string MinimumOsVersion { get; set; } = string.Empty;
    public string MaximumOsVersion { get; set; } = string.Empty;
    public List<string> SupportedArchitectures { get; set; } = new();
    public string InstallableCondition { get; set; } = string.Empty;
    public string UninstallMethod { get; set; } = string.Empty;
    public List<Receipt> Receipts { get; set; } = new();
}

public class Receipt
{
    public string PackageId { get; set; } = string.Empty;
}

public class ManifestItem
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

public class DeleteStats
{
    public int PackageCount { get; set; }
    public string PkgInfoSize { get; set; } = string.Empty;
    public string PackageSize { get; set; } = string.Empty;
}
