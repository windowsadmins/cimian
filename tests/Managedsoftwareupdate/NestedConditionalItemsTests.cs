using System.Net;
using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// A conditional_items entry nested in another is evaluated when its parent matches,
/// as Munki does, instead of being dropped on load (#150).
/// </summary>
public class NestedConditionalItemsTests
{
    private const string Manifest = """
        catalogs:
          - Production
        conditional_items:
          - condition: ANY catalogs == "Production"
            managed_installs:
              - OuterApp
            conditional_items:
              - condition: ANY catalogs == "Production"
                managed_installs:
                  - InnerMatchedApp
                conditional_items:
                  - condition: ANY catalogs == "Production"
                    optional_installs:
                      - DeepApp
              - condition: ANY catalogs == "Testing"
                managed_installs:
                  - InnerUnmatchedApp
          - condition: ANY catalogs == "Testing"
            conditional_items:
              - condition: ANY catalogs == "Production"
                managed_installs:
                  - UnderUnmatchedParentApp
        """;

    [Fact]
    public async Task NestedItems_EvaluateUnderAMatchingParentOnly()
    {
        var config = new CimianConfig
        {
            SoftwareRepoURL = "https://repo.example.test",
            ClientIdentifier = "nested-pc",
            ManifestsPath = Directory.CreateTempSubdirectory().FullName,
        };
        var handler = new StubHandler(url =>
            url.EndsWith("/manifests/nested-pc.yaml", StringComparison.OrdinalIgnoreCase)
                ? (HttpStatusCode.OK, Manifest)
                : (HttpStatusCode.NotFound, string.Empty));

        var items = await new ManifestService(config, new HttpClient(handler)).GetManifestItemsAsync();
        var names = items.Select(i => i.Name).ToList();

        Assert.Contains("OuterApp", names);
        Assert.Contains("InnerMatchedApp", names);
        Assert.Equal("optional", Assert.Single(items, i => i.Name == "DeepApp").Action);
        Assert.DoesNotContain("InnerUnmatchedApp", names);
        Assert.DoesNotContain("UnderUnmatchedParentApp", names);
    }

    private sealed class StubHandler(Func<string, (HttpStatusCode Status, string Body)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = responder(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
