using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Tests for RepoFormatParser.
/// </summary>
public class RepoFormatTests
{
    [Fact]
    public void Parse_Setting_ReadsWordsAndRefusesATypo()
    {
        Assert.Equal(RepoFormat.Yaml, RepoFormatParser.Parse(null));
        Assert.Equal(RepoFormat.Yaml, RepoFormatParser.Parse(" yaml "));
        Assert.Equal(RepoFormat.Yaml, RepoFormatParser.Parse("YAML"));
        Assert.Equal(RepoFormat.Plist, RepoFormatParser.Parse("Plist"));
        Assert.Throws<ArgumentException>(() => RepoFormatParser.Parse("plists"));
    }
}
