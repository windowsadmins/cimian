using Cimian.CLI.managedsoftwareupdate.Models;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// What --show-config prints for RepoFormat.
/// </summary>
public class ShowConfigRepoFormatTests
{
    [Theory]
    [InlineData(null, "yaml (not set)")]
    [InlineData("", "yaml (not set)")]
    [InlineData("  ", "yaml (not set)")]
    [InlineData("yaml", "yaml")]
    [InlineData(" Plist ", "plist")]
    public void DescribeRepoFormat_Value_ShowsTheFormInUse(string? value, string expected)
    {
        Assert.Equal(expected, new CimianConfig { RepoFormat = value }.DescribeRepoFormat());
    }
}
