using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for ManifestService - in particular the action-precedence rules
/// applied during deduplication, which decide the outcome when the same item
/// name is listed in multiple manifest sections.
/// </summary>
public class ManifestServiceTests
{
    private static ManifestService CreateService() => new(new CimianConfig());

    [Fact]
    public void DeduplicateItems_ManagedInstallSupersedesOptional_RegardlessOfOrder()
    {
        var service = CreateService();

        // Optional encountered first (e.g. via a leaf manifest), install
        // encountered later (via a shared core manifest). Install must win.
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
    public void DeduplicateItems_InstallSupersedesDefault()
    {
        var service = CreateService();

        // default_installs is "install once, don't re-enforce". An explicit
        // managed_install for the same name must take over so the item stays
        // enforced if removed.
        var items = new List<ManifestItem>
        {
            new() { Name = "Browser", Action = "default", SourceManifest = "Provisioning" },
            new() { Name = "Browser", Action = "install", SourceManifest = "CoreApps" },
        };

        var result = service.DeduplicateItems(items);

        var entry = Assert.Single(result);
        Assert.Equal("install", entry.Action);
        Assert.Equal("CoreApps", entry.SourceManifest);
    }

    [Fact]
    public void DeduplicateItems_DefaultSupersedesOptional()
    {
        var service = CreateService();

        // default_installs is installed automatically; optional_installs is
        // opt-in. If both are listed, default wins.
        var items = new List<ManifestItem>
        {
            new() { Name = "Editor", Action = "optional", SourceManifest = "Staff" },
            new() { Name = "Editor", Action = "default", SourceManifest = "Provisioning" },
        };

        var result = service.DeduplicateItems(items);

        var entry = Assert.Single(result);
        Assert.Equal("default", entry.Action);
        Assert.Equal("Provisioning", entry.SourceManifest);
    }

    [Fact]
    public void DeduplicateItems_UninstallSupersedesDefault()
    {
        var service = CreateService();

        // Explicit removal beats "install by default".
        var items = new List<ManifestItem>
        {
            new() { Name = "OldTool", Action = "default", SourceManifest = "Provisioning" },
            new() { Name = "OldTool", Action = "uninstall", SourceManifest = "Cleanup" },
        };

        var result = service.DeduplicateItems(items);

        var entry = Assert.Single(result);
        Assert.Equal("uninstall", entry.Action);
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

    // --- Primary-manifest 404 fallback chain -------------------------------
    // configured identifier -> hostname -> serial -> Orphaned -> site_default.
    // Only a 404 advances the chain; a non-404 aborts without degrading to a
    // catch-all so a transient server error stays visible.

    [Fact]
    public async Task GetManifestItems_PrimaryManifest404_FallsBackToOrphaned()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "https://repo.example.test",
            ClientIdentifier = "configured-pc",
            ManifestsPath = Directory.CreateTempSubdirectory().FullName,
        };

        const string orphanedYaml = "catalogs:\n  - Production\nmanaged_installs:\n  - FallbackApp\n";
        var handler = new StubHandler(url =>
            url.EndsWith("/manifests/Orphaned.yaml", StringComparison.OrdinalIgnoreCase)
                ? (HttpStatusCode.OK, orphanedYaml)
                : (HttpStatusCode.NotFound, string.Empty));

        var service = new ManifestService(config, new HttpClient(handler));

        var items = await service.GetManifestItemsAsync();

        var fallback = Assert.Single(items, i => i.Name == "FallbackApp");
        Assert.Equal("install", fallback.Action);
        Assert.Equal("Orphaned", fallback.SourceManifest);
        Assert.Contains("Production", config.Catalogs);
    }

    [Fact]
    public async Task GetManifestItems_PrimaryManifestNon404_AbortsWithoutCatchAll()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "https://repo.example.test",
            ClientIdentifier = "configured-pc",
            ManifestsPath = Directory.CreateTempSubdirectory().FullName,
        };

        // The configured identifier returns 500; everything else would 404.
        var handler = new StubHandler(url =>
            url.EndsWith("/manifests/configured-pc.yaml", StringComparison.OrdinalIgnoreCase)
                ? (HttpStatusCode.InternalServerError, string.Empty)
                : (HttpStatusCode.NotFound, string.Empty));

        var service = new ManifestService(config, new HttpClient(handler));

        await service.GetManifestItemsAsync();

        // A non-404 must abort the chain: the catch-all manifests are never requested.
        Assert.DoesNotContain(handler.RequestedUrls,
            u => u.Contains("/manifests/Orphaned.yaml", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.RequestedUrls,
            u => u.Contains("/manifests/site_default.yaml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Minimal HttpMessageHandler that answers each request from a URL-driven
    /// responder and records every requested URL for assertions.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode Status, string Body)> _responder;
        public List<string> RequestedUrls { get; } = new();

        public StubHandler(Func<string, (HttpStatusCode, string)> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            var (status, body) = _responder(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
