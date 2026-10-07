using System.Net;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for the manifest download of ManifestService when the repository is in plist format.
/// </summary>
public class ManifestServiceRepoFormatTests : IDisposable
{
    private readonly string _tempDir;

    public ManifestServiceRepoFormatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CimianTests", "ManifestRepoFormat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    private const string PlistHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + PlistUtils.AppleDoctype + "\n";

    private static string ManifestPlist(string[] installs, string[]? includes = null, string[]? catalogs = null)
    {
        var text = PlistHeader + "<plist version=\"1.0\"><dict>";
        text += "<key>catalogs</key><array>" + string.Concat((catalogs ?? new[] { "Production" }).Select(c => $"<string>{c}</string>")) + "</array>";
        if (includes != null)
            text += "<key>included_manifests</key><array>" + string.Concat(includes.Select(i => $"<string>{i}</string>")) + "</array>";
        text += "<key>managed_installs</key><array>" + string.Concat(installs.Select(i => $"<string>{i}</string>")) + "</array>";
        return text + "</dict></plist>\n";
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode, string)> _responder;
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

    private CimianConfig ClientConfig(string? format) => new()
    {
        SoftwareRepoURL = "https://repo.example.test/cimian",
        ClientIdentifier = "PC01",
        RepoFormat = format,
        CatalogsPath = Path.Combine(_tempDir, "client-cache", "catalogs"),
        ManifestsPath = Path.Combine(_tempDir, "client-cache", "manifests"),
        CachePath = Path.Combine(_tempDir, "client-cache", "Cache"),
    };

    [Fact]
    public async Task GetManifestItemsAsync_PlistFormat_ReadsPlistManifestsAndTheirIncludes()
    {
        var pc = ManifestPlist(new[] { "A" }, includes: new[] { "Core", "groups/Lab", "sub-manifest.12.staff" });
        var core = ManifestPlist(new[] { "B" }, catalogs: new[] { "Production", "Testing" });
        var lab = "managed_installs:\n- C\n";   // a YAML answer on a plist address is read too
        var staff = ManifestPlist(new[] { "D" });
        var handler = new StubHandler(url =>
            url.EndsWith("/manifests/PC01") ? (HttpStatusCode.OK, pc)
            : url.EndsWith("/manifests/Core") ? (HttpStatusCode.OK, core)
            : url.EndsWith("/manifests/groups/Lab") ? (HttpStatusCode.OK, lab)
            : url.EndsWith("/manifests/sub-manifest.12.staff") ? (HttpStatusCode.OK, staff)
            : (HttpStatusCode.NotFound, ""));
        var config = ClientConfig("plist");
        var service = new ManifestService(config, new HttpClient(handler))
        {
            CreateSelfServeManifestService = () => new SelfServiceManifestService(Path.Combine(_tempDir, "SelfServeManifest.yaml")),
        };

        var items = await service.GetManifestItemsAsync();

        Assert.Equal(new[] { "A", "B", "C", "D" }, items.Select(i => i.Name).OrderBy(n => n));
        Assert.Contains("https://repo.example.test/cimian/manifests/PC01", handler.RequestedUrls);
        Assert.Contains("https://repo.example.test/cimian/manifests/Core", handler.RequestedUrls);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.EndsWith(".yaml") || u.EndsWith(".plist"));
        Assert.Contains("Testing", config.Catalogs);

        var cached = File.ReadAllText(Path.Combine(config.ManifestsPath, "PC01.yaml"));
        Assert.False(PlistUtils.LooksLikePlist(cached));
        Assert.Contains("managed_installs", cached);
    }

    private async Task<List<string>> RequestedUrlsAsync(string? format, string primaryName, string primaryBody)
    {
        var handler = new StubHandler(url =>
            url.EndsWith("/manifests/" + primaryName) ? (HttpStatusCode.OK, primaryBody) : (HttpStatusCode.NotFound, ""));
        var service = new ManifestService(ClientConfig(format), new HttpClient(handler))
        {
            CreateSelfServeManifestService = () => new SelfServiceManifestService(Path.Combine(_tempDir, "SelfServeManifest.yaml")),
        };
        await service.GetManifestItemsAsync();
        return handler.RequestedUrls;
    }

    [Fact]
    public async Task GetManifestItemsAsync_PlistFormat_RequestsIncludesByTheirExactName()
    {
        var urls = await RequestedUrlsAsync("plist", "PC01", ManifestPlist(new[] { "A" }, includes: new[] { "Lab.plist", "Core" }));

        Assert.Contains("https://repo.example.test/cimian/manifests/Lab.plist", urls);
        Assert.Contains("https://repo.example.test/cimian/manifests/Core", urls);
        Assert.DoesNotContain("https://repo.example.test/cimian/manifests/Lab", urls);
    }

    [Fact]
    public async Task GetManifestItemsAsync_YamlFormat_RemovesOneSuffixFromIncludesAndAddsYaml()
    {
        const string pc = "managed_installs:\n- A\nincluded_manifests:\n- Core.YAML\n- Lab.plist\n- groups\\Win.yaml\n";

        var urls = await RequestedUrlsAsync(null, "PC01.yaml", pc);

        Assert.Contains("https://repo.example.test/cimian/manifests/Core.yaml", urls);
        Assert.Contains("https://repo.example.test/cimian/manifests/Lab.yaml", urls);
        Assert.Contains("https://repo.example.test/cimian/manifests/groups/Win.yaml", urls);
    }

    [Fact]
    public async Task GetManifestItemsAsync_PlistFormat_SkipsEmptyIncludeNames()
    {
        var urls = await RequestedUrlsAsync("plist", "PC01", ManifestPlist(new[] { "A" }, includes: new[] { "", " ", "Core" }));

        Assert.Equal(
            new[] { "https://repo.example.test/cimian/manifests/PC01", "https://repo.example.test/cimian/manifests/Core" },
            urls);
    }

    [Fact]
    public async Task GetManifestItemsAsync_YamlFormat_SkipsEmptyIncludeNames()
    {
        const string pc = "managed_installs:\n- A\nincluded_manifests:\n- ''\n- ' '\n- Core\n";

        var urls = await RequestedUrlsAsync(null, "PC01.yaml", pc);

        Assert.Equal(
            new[] { "https://repo.example.test/cimian/manifests/PC01.yaml", "https://repo.example.test/cimian/manifests/Core.yaml" },
            urls);
    }
}
