using System.Net;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for the catalog download of CatalogService when the repository is in plist format.
/// </summary>
public class CatalogServiceRepoFormatTests : IDisposable
{
    private readonly string _tempDir;

    public CatalogServiceRepoFormatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CimianTests", "CatalogRepoFormat", Guid.NewGuid().ToString("N"));
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
    public async Task DownloadCatalogAsync_PlistFormat_AsksWithoutExtensionAndCachesYaml()
    {
        var catalog = PlistHeader + "<plist version=\"1.0\"><array><dict><key>name</key><string>A</string><key>version</key><string>1.10</string><key>installer</key><dict><key>type</key><string>msi</string><key>location</key><string>a/A-1.10.msi</string></dict></dict></array></plist>\n";
        var handler = new StubHandler(url => url.EndsWith("/catalogs/Production") ? (HttpStatusCode.OK, catalog) : (HttpStatusCode.NotFound, ""));
        var config = ClientConfig("plist");
        var service = new CatalogService(config, new HttpClient(handler));

        var items = await service.DownloadCatalogAsync("Production");

        Assert.Equal("https://repo.example.test/cimian/catalogs/Production", Assert.Single(handler.RequestedUrls));
        var item = Assert.Single(items);
        Assert.Equal("A", item.Name);
        Assert.Equal("1.10", item.Version);

        var cached = File.ReadAllText(Path.Combine(config.CatalogsPath, "Production.yaml"));
        Assert.False(PlistUtils.LooksLikePlist(cached));
        Assert.StartsWith("items:", cached);
        Assert.Single(service.LoadLocalCatalog(Path.Combine(config.CatalogsPath, "Production.yaml")));
    }

    // A catalog as a Munki repository server writes it with Python's plistlib (keys sorted):
    // installer_item_location is the server's download name and installer has no location.
    private const string MunkiServerCatalog = """
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<array>
	<dict>
		<key>display_name</key>
		<string>Example App</string>
		<key>icon_name</key>
		<string>icon.125.ExampleApp.png</string>
		<key>installer</key>
		<dict>
			<key>args</key>
			<array>
				<string>/qn</string>
			</array>
			<key>hash</key>
			<string>0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef</string>
			<key>size</key>
			<integer>1661239</integer>
			<key>type</key>
			<string>msi</string>
		</dict>
		<key>installer_item_location</key>
		<string>installer-item.125.ExampleApp-2-5-1.msi</string>
		<key>name</key>
		<string>ExampleApp</string>
		<key>unattended_install</key>
		<true/>
		<key>version</key>
		<string>2.5.1</string>
	</dict>
</array>
</plist>
""";

    [Fact]
    public async Task DownloadCatalogAsync_MunkiServerCatalogWithInstallerItemLocationOnly_FillsInstallerLocation()
    {
        var handler = new StubHandler(url => url.EndsWith("/catalogs/manifest-catalog.1.Production") ? (HttpStatusCode.OK, MunkiServerCatalog + "\n") : (HttpStatusCode.NotFound, ""));
        var config = ClientConfig("plist");
        var service = new CatalogService(config, new HttpClient(handler));

        var item = Assert.Single(await service.DownloadCatalogAsync("manifest-catalog.1.Production"));

        Assert.Equal("https://repo.example.test/cimian/catalogs/manifest-catalog.1.Production", Assert.Single(handler.RequestedUrls));
        Assert.Equal("installer-item.125.ExampleApp-2-5-1.msi", item.Installer.Location);
        Assert.Equal("msi", item.Installer.Type);
        Assert.Equal(1661239, item.Installer.Size);
        Assert.Equal(new[] { "/qn" }, item.Installer.Args);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", item.Installer.Hash);

        var cached = Assert.Single(service.LoadLocalCatalog(Path.Combine(config.CatalogsPath, "manifest-catalog.1.Production.yaml")));
        Assert.Equal("installer-item.125.ExampleApp-2-5-1.msi", cached.Installer.Location);
    }

    [Fact]
    public async Task DownloadCatalogAsync_NoRepoFormat_AsksForYaml()
    {
        var handler = new StubHandler(url => url.EndsWith("/catalogs/Production.yaml") ? (HttpStatusCode.OK, "items:\n- name: A\n  version: '1.0'\n") : (HttpStatusCode.NotFound, ""));
        var service = new CatalogService(ClientConfig(null), new HttpClient(handler));

        var items = await service.DownloadCatalogAsync("Production");

        Assert.Equal("https://repo.example.test/cimian/catalogs/Production.yaml", Assert.Single(handler.RequestedUrls));
        Assert.Equal("A", Assert.Single(items).Name);
    }
}
