using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Tests for RepoPaths: the file names of repository files in each format.
/// </summary>
public class RepoPathsTests
{
    // --- File names ----------------------------------------------------------------------
    [Fact]
    public void FileName_EachKindAndFormat_FollowsMunkisLayout()
    {
        Assert.Equal("Production.yaml", RepoPaths.CatalogFileName("Production", RepoFormat.Yaml));
        Assert.Equal("Production", RepoPaths.CatalogFileName("Production", RepoFormat.Plist));
        Assert.Equal("PC01.yaml", RepoPaths.ManifestFileName("PC01", RepoFormat.Yaml));
        Assert.Equal("PC01", RepoPaths.ManifestFileName("PC01", RepoFormat.Plist));
    }
}
