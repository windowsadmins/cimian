using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core;
using FluentAssertions;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

public class ClientResourcesPathMapperTests
{
    [Theory]
    [InlineData("branding/branding.png", "branding.png")]
    [InlineData("client_resources/branding.yaml", "branding.yaml")]
    [InlineData("resources/branding1.jpg", "branding1.jpg")]
    [InlineData("preferences.yaml", "preferences.yaml")]
    public void MapEntryToLocalPath_maps_known_layouts(string entry, string expectedLeaf)
    {
        var result = ClientResourcesPathMapper.MapEntryToLocalPath(entry);
        result.Should().NotBeNull();
        result!.Should().EndWith(expectedLeaf.Replace('/', Path.DirectorySeparatorChar));
        ClientResourcesPathMapper.IsUnderManagedInstalls(result!).Should().BeTrue();
    }

    [Theory]
    [InlineData("templates/showcase_template.html")]
    [InlineData("templates/sidebar_template.html")]
    [InlineData("../secrets.txt")]
    [InlineData("branding/../../evil.txt")]
    public void MapEntryToLocalPath_skips_unsafe_or_unused_entries(string entry)
    {
        ClientResourcesPathMapper.MapEntryToLocalPath(entry).Should().BeNull();
    }

    [Fact]
    public void BuildZipNameCandidates_includes_client_identifier_and_site_default()
    {
        var config = new Cimian.CLI.managedsoftwareupdate.Models.CimianConfig
        {
            ClientIdentifier = "import",
        };
        var names = ClientResourcesSyncService.BuildZipNameCandidates(config);
        names.Should().Contain("import");
        names.Should().Contain("site_default");
        names[0].Should().Be("import");
    }
}
