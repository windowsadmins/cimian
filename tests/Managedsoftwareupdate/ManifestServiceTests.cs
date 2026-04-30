using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for ManifestService - in particular the action-precedence rules
/// applied during deduplication, which are how Cimian matches Munki's behavior
/// for items listed in multiple manifest sections.
/// </summary>
public class ManifestServiceTests
{
    private static ManifestService CreateService() => new(new CimianConfig());

    [Fact]
    public void DeduplicateItems_ManagedInstallSupersedesOptional_RegardlessOfOrder()
    {
        var service = CreateService();

        // Optional encountered first (e.g. via Staff.yaml), install encountered
        // later (via CoreApps). Install must win.
        var items = new List<ManifestItem>
        {
            new() { Name = "PowerShell", Action = "optional", SourceManifest = "Staff" },
            new() { Name = "PowerShell", Action = "install", SourceManifest = "CoreApps" },
        };

        var result = service.DeduplicateItems(items);

        var entry = Assert.Single(result);
        Assert.Equal("install", entry.Action);
        Assert.Equal("CoreApps", entry.SourceManifest);
    }

    [Fact]
    public void DeduplicateItems_ManagedInstallSupersedesOptional_WhenInstallEncounteredFirst()
    {
        var service = CreateService();

        var items = new List<ManifestItem>
        {
            new() { Name = "PowerShell", Action = "install", SourceManifest = "CoreApps" },
            new() { Name = "PowerShell", Action = "optional", SourceManifest = "Staff" },
        };

        var result = service.DeduplicateItems(items);

        var entry = Assert.Single(result);
        Assert.Equal("install", entry.Action);
        Assert.Equal("CoreApps", entry.SourceManifest);
    }

    [Fact]
    public void DeduplicateItems_UninstallSupersedesOptionalAndUpdate()
    {
        var service = CreateService();

        var items = new List<ManifestItem>
        {
            new() { Name = "LegacyApp", Action = "optional", SourceManifest = "OldOptional" },
            new() { Name = "LegacyApp", Action = "update", SourceManifest = "Updates" },
            new() { Name = "LegacyApp", Action = "uninstall", SourceManifest = "Cleanup" },
        };

        var result = service.DeduplicateItems(items);

        var entry = Assert.Single(result);
        Assert.Equal("uninstall", entry.Action);
        Assert.Equal("Cleanup", entry.SourceManifest);
    }

    [Fact]
    public void DeduplicateItems_InstallSupersedesUninstall()
    {
        var service = CreateService();

        // install outranks uninstall — admin's "this should be present" wins over
        // a stale "remove this" listing.
        var items = new List<ManifestItem>
        {
            new() { Name = "FleetTool", Action = "uninstall", SourceManifest = "Cleanup" },
            new() { Name = "FleetTool", Action = "install", SourceManifest = "CoreApps" },
        };

        var result = service.DeduplicateItems(items);

        var entry = Assert.Single(result);
        Assert.Equal("install", entry.Action);
    }

    [Fact]
    public void DeduplicateItems_SameAction_PrefersHigherVersion()
    {
        var service = CreateService();

        var items = new List<ManifestItem>
        {
            new() { Name = "Tool", Action = "install", Version = "1.0.0", SourceManifest = "A" },
            new() { Name = "Tool", Action = "install", Version = "2.0.0", SourceManifest = "B" },
        };

        var result = service.DeduplicateItems(items);

        var entry = Assert.Single(result);
        Assert.Equal("2.0.0", entry.Version);
    }

    [Fact]
    public void DeduplicateItems_PreservesFirstOccurrenceOrdering()
    {
        var service = CreateService();

        var items = new List<ManifestItem>
        {
            new() { Name = "Alpha", Action = "install" },
            new() { Name = "Beta", Action = "install" },
            new() { Name = "Alpha", Action = "optional" }, // does not displace; keep position 0
            new() { Name = "Gamma", Action = "install" },
        };

        var result = service.DeduplicateItems(items);

        Assert.Equal(3, result.Count);
        Assert.Equal("Alpha", result[0].Name);
        Assert.Equal("install", result[0].Action);
        Assert.Equal("Beta", result[1].Name);
        Assert.Equal("Gamma", result[2].Name);
    }

    [Fact]
    public void DeduplicateItems_StrongerActionRetainsOriginalPosition()
    {
        var service = CreateService();

        // Optional is encountered first (claims position 0). When the install
        // later supersedes it, the result still occupies position 0.
        var items = new List<ManifestItem>
        {
            new() { Name = "PowerShell", Action = "optional", SourceManifest = "Staff" },
            new() { Name = "Teams", Action = "install", SourceManifest = "CoreApps" },
            new() { Name = "PowerShell", Action = "install", SourceManifest = "CoreApps" },
        };

        var result = service.DeduplicateItems(items);

        Assert.Equal(2, result.Count);
        Assert.Equal("PowerShell", result[0].Name);
        Assert.Equal("install", result[0].Action);
        Assert.Equal("CoreApps", result[0].SourceManifest);
        Assert.Equal("Teams", result[1].Name);
    }

    [Fact]
    public void DeduplicateItems_IgnoresEmptyNames()
    {
        var service = CreateService();

        var items = new List<ManifestItem>
        {
            new() { Name = "", Action = "install" },
            new() { Name = "Real", Action = "install" },
        };

        var result = service.DeduplicateItems(items);

        Assert.Single(result);
        Assert.Equal("Real", result[0].Name);
    }
}
