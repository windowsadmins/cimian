using Cimian.CLI.Cimiimport.Models;
using Cimian.CLI.Cimiimport.Services;
using Xunit;

namespace Cimian.Tests.CLI.Cimiimport;

/// <summary>
/// Unit tests for the non-interactive prompter (the default for unattended
/// scripts and CI). The console prompter is exercised by manual + CLI tests;
/// here we lock down the non-interactive contract so future changes don't
/// silently regress the <c>--nointeractive</c> behaviour.
/// </summary>
public class NoInteractivePrompterTests
{
    private readonly NoInteractivePrompter _prompter = new();

    [Fact]
    public async Task AskUseTemplate_AlwaysReturnsTrue()
    {
        var existing = new PkgsInfo { Name = "Slack", Version = "4.49.81.0" };
        Assert.True(await _prompter.AskUseTemplateAsync(existing));
    }

    [Fact]
    public async Task EditMetadata_ReturnsSeedUnchanged()
    {
        var seed = new InstallerMetadata
        {
            ID = "Slack",
            Version = "4.49.81.0",
            Developer = "Slack Technologies",
            Catalogs = ["Productivity"],
        };
        var config = new ImportConfiguration { DefaultCatalog = "Test" };

        var result = await _prompter.EditMetadataAsync(seed, config);

        Assert.Same(seed, result);
        Assert.Equal("Slack", result.ID);
        Assert.Equal("4.49.81.0", result.Version);
        Assert.Single(result.Catalogs);
        Assert.Equal("Productivity", result.Catalogs[0]);
    }

    [Theory]
    [InlineData(@"\mgmt", @"\mgmt")]
    [InlineData("mgmt", @"\mgmt")]               // adds leading slash
    [InlineData(@"\mgmt\", @"\mgmt")]            // trims trailing slash
    [InlineData("apps/productivity", @"\apps/productivity")]
    public async Task AskRepoSubdir_NormalizesPath(string input, string expected)
    {
        Assert.Equal(expected, await _prompter.AskRepoSubdirAsync(input));
    }

    [Fact]
    public async Task ConfirmImport_AlwaysReturnsTrue()
    {
        var pkg = new PkgsInfo { Name = "Slack", Version = "4.49.81.0" };
        Assert.True(await _prompter.ConfirmImportAsync(pkg));
    }
}
