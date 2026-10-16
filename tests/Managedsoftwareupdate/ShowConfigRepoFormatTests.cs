using Cimian.CLI.managedsoftwareupdate.Models;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// What --show-config prints for RepoFormat, and the warning a run logs for a value the client cannot use.
/// </summary>
public class ShowConfigRepoFormatTests
{
    [Theory]
    [InlineData(null, "yaml (not set)")]
    [InlineData("", "yaml (not set)")]
    [InlineData("  ", "yaml (not set)")]
    [InlineData("yaml", "yaml")]
    [InlineData(" Plist ", "plist")]
    [InlineData("plsit", "yaml ('plsit' is not yaml or plist)")]
    public void DescribeRepoFormat_Value_ShowsTheFormInUse(string? value, string expected)
    {
        Assert.Equal(expected, new CimianConfig { RepoFormat = value }.DescribeRepoFormat());
    }

    [Fact]
    public void RepoFormatWarning_UnusableValue_NamesItAndYaml()
    {
        Assert.Equal("RepoFormat 'plsit' is not yaml or plist: reading the repository as YAML",
            new CimianConfig { RepoFormat = "plsit" }.RepoFormatWarning);
        Assert.Null(new CimianConfig { RepoFormat = "plist" }.RepoFormatWarning);
        Assert.Null(new CimianConfig().RepoFormatWarning);
    }
}
