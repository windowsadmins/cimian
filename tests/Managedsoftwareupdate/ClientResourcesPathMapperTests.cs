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
    [InlineData("branding.yaml", "client_resources/branding.yaml")]
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
    [InlineData("branding/C:/ProgramData/ManagedInstalls/Config.yaml")]
    [InlineData("client_resources/C:/ProgramData/ManagedInstalls/preflight.ps1")]
    [InlineData("branding/logo.png:stream")]
    [InlineData("/branding/logo.png")]
    [InlineData("branding/./logo.png")]
    [InlineData("branding/subdir./logo.png")]
    [InlineData("branding//logo.png")]
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

    [Theory]
    [InlineData("acme")]
    [InlineData("acme.zip")]
    [InlineData("  acme.ZIP ")]
    public void BuildZipNameCandidates_tries_ClientResourcesFilename_first(string fileName)
    {
        var config = new Cimian.CLI.managedsoftwareupdate.Models.CimianConfig
        {
            ClientIdentifier = "import",
            ClientResourcesFilename = fileName,
        };

        var names = ClientResourcesSyncService.BuildZipNameCandidates(config);

        names[0].Should().Be("acme");
        names[1].Should().Be("import");
        names[^1].Should().Be("site_default");
    }

    [Fact]
    public void BuildZipUrl_defaults_to_repo_client_resources()
    {
        var config = new Cimian.CLI.managedsoftwareupdate.Models.CimianConfig
        {
            SoftwareRepoURL = "https://repo.example.org/deployment/",
        };

        ClientResourcesSyncService.BuildZipUrl(config, "site default")
            .Should().Be("https://repo.example.org/deployment/client_resources/site%20default.zip");
    }

    [Fact]
    public void BuildZipUrl_uses_ClientResourceURL_when_set()
    {
        var config = new Cimian.CLI.managedsoftwareupdate.Models.CimianConfig
        {
            SoftwareRepoURL = "https://repo.example.org/deployment",
            ClientResourceURL = "https://cdn.example.org/branding/",
        };

        ClientResourcesSyncService.BuildZipUrl(config, "site_default")
            .Should().Be("https://cdn.example.org/branding/site_default.zip");
    }

    [Fact]
    public void RemoveExtractedFiles_never_touches_files_outside_ManagedInstalls()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cimian-cr-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [1]);
        try
        {
            var removed = ClientResourcesSyncService.RemoveExtractedFiles(
                [new ClientResourcesSyncService.ExtractedFile { Path = path, WrittenUtc = File.GetLastWriteTimeUtc(path) }],
                keep: null);

            removed.Should().Be(0);
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
