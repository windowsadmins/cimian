using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

public class BootstrapArgsBuilderTests
{
    [Theory]
    [InlineData("Gimp", "Gimp")]
    [InlineData("VS Code", "\"VS Code\"")]
    [InlineData("Has\"Quote", "\"Has\\\"Quote\"")]
    [InlineData("trailing\\", "\"trailing\\\\\"")]
    [InlineData("a\\\\b", "a\\\\b")]
    public void QuoteArgument_FollowsWindowsCRuntimeRules(string input, string expected)
    {
        Assert.Equal(expected, BootstrapArgsBuilder.QuoteArgument(input));
    }

    [Theory]
    [InlineData("hi\nthere")]
    [InlineData("hi\rthere")]
    [InlineData("nul\0byte")]
    public void QuoteArgument_RejectsControlCharacters(string input)
    {
        Assert.Throws<ArgumentException>(() => BootstrapArgsBuilder.QuoteArgument(input));
    }

    [Fact]
    public void BuildSelfServeInstallArgs_SingleItem_AppendsTrailingArgs()
    {
        var args = BootstrapArgsBuilder.BuildSelfServeInstallArgs(new[] { "Gimp" });
        Assert.Equal("--item Gimp --no-preflight --show-status -vv", args);
    }

    [Fact]
    public void BuildSelfServeInstallArgs_MultipleItems_EmitsSingleItemFlag()
    {
        // One --item flag with all values — repeated flags crash the engine's
        // CommandLineParser sequence option ("defined multiple times" -> exit 1).
        var args = BootstrapArgsBuilder.BuildSelfServeInstallArgs(new[] { "Gimp", "Cyberduck" });
        Assert.Equal("--item Gimp Cyberduck --no-preflight --show-status -vv", args);
    }

    [Fact]
    public void BuildSelfServeInstallArgs_QuotesNamesWithSpaces()
    {
        var args = BootstrapArgsBuilder.BuildSelfServeInstallArgs(new[] { "VS Code", "Gimp" });
        Assert.Equal("--item \"VS Code\" Gimp --no-preflight --show-status -vv", args);
    }

    [Fact]
    public void BuildSelfServeInstallArgs_DropsDuplicatesCaseInsensitively()
    {
        var args = BootstrapArgsBuilder.BuildSelfServeInstallArgs(new[] { "Gimp", "gimp", "GIMP" });
        Assert.Equal("--item Gimp --no-preflight --show-status -vv", args);
    }

    [Fact]
    public void BuildSelfServeInstallArgs_SkipsWhitespaceOnlyEntries()
    {
        var args = BootstrapArgsBuilder.BuildSelfServeInstallArgs(new[] { "  ", "Gimp", "" });
        Assert.Equal("--item Gimp --no-preflight --show-status -vv", args);
    }

    [Fact]
    public void BuildSelfServeInstallArgs_EmptyInput_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            BootstrapArgsBuilder.BuildSelfServeInstallArgs(Array.Empty<string>()));
    }

    [Fact]
    public void ExtractItemNames_SingleItem_ReturnsName()
    {
        var names = BootstrapArgsBuilder.ExtractItemNames(
            "--item Gimp --no-preflight --show-status -vv");
        Assert.Equal(new[] { "Gimp" }, names);
    }

    [Fact]
    public void ExtractItemNames_MultipleItems_PreservesOrder()
    {
        var names = BootstrapArgsBuilder.ExtractItemNames(
            "--item Gimp --item Cyberduck --no-preflight --show-status -vv");
        Assert.Equal(new[] { "Gimp", "Cyberduck" }, names);
    }

    [Fact]
    public void ExtractItemNames_QuotedNames_UnquotesCorrectly()
    {
        var names = BootstrapArgsBuilder.ExtractItemNames(
            "--item \"VS Code\" --item Gimp --no-preflight");
        Assert.Equal(new[] { "VS Code", "Gimp" }, names);
    }

    [Fact]
    public void ExtractItemNames_IgnoresDanglingItemFlag()
    {
        var names = BootstrapArgsBuilder.ExtractItemNames("--item Gimp --item --no-preflight");
        Assert.Equal(new[] { "Gimp" }, names);
    }

    [Fact]
    public void ExtractItemNames_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(BootstrapArgsBuilder.ExtractItemNames(null));
        Assert.Empty(BootstrapArgsBuilder.ExtractItemNames(""));
        Assert.Empty(BootstrapArgsBuilder.ExtractItemNames("   "));
    }

    [Fact]
    public void ExtractItemNames_RoundTripsBuildSelfServeInstallArgs()
    {
        var original = new[] { "Gimp", "VS Code", "Has\"Quote", "trailing\\" };
        var args = BootstrapArgsBuilder.BuildSelfServeInstallArgs(original);
        var extracted = BootstrapArgsBuilder.ExtractItemNames(args);
        Assert.Equal(original, extracted);
    }
}
