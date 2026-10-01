using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Munki-parity tests for orthogonal optional_installs + managed_updates and
/// InstallInfoAnalyzer.DeduplicatePreservingOrthogonalLists.
/// </summary>
public class InstallInfoAnalyzerTests
{
    [Fact]
    public void Deduplicate_KeepsBothUpdateAndOptional()
    {
        var items = new List<ManifestItem>
        {
            new() { Name = "BoxDrive", Action = "optional", SourceManifest = "release" },
            new() { Name = "BoxDrive", Action = "update", SourceManifest = "release" },
        };

        var result = InstallInfoAnalyzer.DeduplicatePreservingOrthogonalLists(items);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.Action == "update");
        Assert.Contains(result, i => i.Action == "optional");
    }

    [Fact]
    public void Deduplicate_UpdateAfterOptional_StillKeepsBoth()
    {
        var items = new List<ManifestItem>
        {
            new() { Name = "BoxDrive", Action = "update", SourceManifest = "release" },
            new() { Name = "BoxDrive", Action = "optional", SourceManifest = "release" },
        };

        var result = InstallInfoAnalyzer.DeduplicatePreservingOrthogonalLists(items);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.Action == "update");
        Assert.Contains(result, i => i.Action == "optional");
    }

    [Fact]
    public void Deduplicate_InstallDropsOptionalAndUpdate()
    {
        var items = new List<ManifestItem>
        {
            new() { Name = "Zoom", Action = "optional", SourceManifest = "Staff" },
            new() { Name = "Zoom", Action = "update", SourceManifest = "Updates" },
            new() { Name = "Zoom", Action = "install", SourceManifest = "CoreApps" },
        };

        var result = InstallInfoAnalyzer.DeduplicatePreservingOrthogonalLists(items);

        var entry = Assert.Single(result);
        Assert.Equal("install", entry.Action);
        Assert.Equal("CoreApps", entry.SourceManifest);
    }

    [Fact]
    public void Deduplicate_UninstallDropsOptional()
    {
        var items = new List<ManifestItem>
        {
            new() { Name = "LegacyApp", Action = "optional", SourceManifest = "Old" },
            new() { Name = "LegacyApp", Action = "update", SourceManifest = "Updates" },
            new() { Name = "LegacyApp", Action = "uninstall", SourceManifest = "Cleanup" },
        };

        var result = InstallInfoAnalyzer.DeduplicatePreservingOrthogonalLists(items);

        var entry = Assert.Single(result);
        Assert.Equal("uninstall", entry.Action);
    }

    [Fact]
    public void Deduplicate_KeepsDefaultAndOptionalTogether()
    {
        // Munki often lists the same title in both default_installs and
        // optional_installs (seed SelfServe + show in Self Service).
        var items = new List<ManifestItem>
        {
            new() { Name = "Editor", Action = "optional", SourceManifest = "Staff" },
            new() { Name = "Editor", Action = "default", SourceManifest = "Provisioning" },
        };

        var result = InstallInfoAnalyzer.DeduplicatePreservingOrthogonalLists(items);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.Action == "optional");
        Assert.Contains(result, i => i.Action == "default");
    }

    [Fact]
    public void ManifestService_DeduplicateItems_DelegatesToOrthogonalPreserving()
    {
        var service = new ManifestService(new CimianConfig
        {
            SoftwareRepoURL = "https://example.test",
            ManifestsPath = Path.GetTempPath(),
        });

        var items = new List<ManifestItem>
        {
            new() { Name = "Miro", Action = "optional" },
            new() { Name = "Miro", Action = "update" },
        };

        var result = service.DeduplicateItems(items);

        Assert.Equal(2, result.Count);
    }
}
