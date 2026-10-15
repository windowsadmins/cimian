// MscCategoryIconResolverTests.cs - Built-in glyphs and preferences.yaml category_icons overrides.

using Cimian.Core;
using Xunit;

namespace Cimian.Tests;

public class MscCategoryIconResolverTests
{
    [Fact]
    public void Resolve_BuiltInCategory_ReturnsKnownGlyph()
    {
        var glyph = MscCategoryIconResolver.Resolve("Browsers");

        Assert.Equal("\uE774", glyph);
    }

    [Fact]
    public void Resolve_BuiltInAlias_ReturnsSameGlyph()
    {
        var browsers = MscCategoryIconResolver.Resolve("browsers");
        var browser = MscCategoryIconResolver.Resolve("browser");

        Assert.Equal(browsers, browser);
    }

    [Fact]
    public void Resolve_UnknownCategory_ReturnsDefaultGlyph()
    {
        var glyph = MscCategoryIconResolver.Resolve("PDF Tools");

        Assert.Equal(MscCategoryIconResolver.DefaultGlyph, glyph);
    }

    [Fact]
    public void Resolve_Override_WinsOverBuiltIn()
    {
        var overrides = MscCategoryIconResolver.BuildOverrideGlyphs(
            new Dictionary<string, string> { ["browsers"] = "gaming" });

        var glyph = MscCategoryIconResolver.Resolve("Browsers", overrides);

        Assert.Equal("\uE7FC", glyph);
    }

    [Fact]
    public void Resolve_CustomCategoryMappedToBuiltInKey()
    {
        var overrides = MscCategoryIconResolver.BuildOverrideGlyphs(
            new Dictionary<string, string> { ["PDF Tools"] = "utilities" });

        var glyph = MscCategoryIconResolver.Resolve("PDF Tools", overrides);

        Assert.Equal("\uE90F", glyph);
    }

    [Fact]
    public void BuildOverrideGlyphs_AcceptsHexCodepoint()
    {
        var overrides = MscCategoryIconResolver.BuildOverrideGlyphs(
            new Dictionary<string, string> { ["IT Tools"] = "E8FD" });

        Assert.True(overrides.TryGetValue("IT Tools", out var glyph));
        Assert.Equal("\uE8FD", glyph);
    }

    [Fact]
    public void BuildOverrideGlyphs_Accepts0xHex()
    {
        var overrides = MscCategoryIconResolver.BuildOverrideGlyphs(
            new Dictionary<string, string> { ["Custom"] = "0xE90F" });

        Assert.Equal("\uE90F", overrides["Custom"]);
    }

    [Fact]
    public void BuildOverrideGlyphs_SkipsInvalidSpec()
    {
        var overrides = MscCategoryIconResolver.BuildOverrideGlyphs(
            new Dictionary<string, string>
            {
                ["Good"] = "security",
                ["Bad"] = "not-a-real-icon-name",
            });

        Assert.True(overrides.ContainsKey("Good"));
        Assert.False(overrides.ContainsKey("Bad"));
    }

    [Fact]
    public void TryParseIconSpec_SingleGlyphCharacter()
    {
        Assert.True(MscCategoryIconResolver.TryParseIconSpec("\uE74C", out var glyph));
        Assert.Equal("\uE74C", glyph);
    }

    [Fact]
    public void ConfigureOverrides_AffectsParameterlessResolve()
    {
        try
        {
            var overrides = MscCategoryIconResolver.BuildOverrideGlyphs(
                new Dictionary<string, string> { ["PDF Tools"] = "docs" });
            MscCategoryIconResolver.ConfigureOverrides(overrides);

            Assert.Equal("\uE8A5", MscCategoryIconResolver.Resolve("PDF Tools"));
        }
        finally
        {
            MscCategoryIconResolver.ConfigureOverrides(null);
        }
    }

    [Fact]
    public void BuildOverrideGlyphsFromPolicyLines_ParsesEqualsAndColon()
    {
        var overrides = MscCategoryIconResolver.BuildOverrideGlyphsFromPolicyLines(
        [
            "PDF Tools=utilities",
            "browsers: gaming",
            "bad-line-without-sep",
            " =skipped",
        ]);

        Assert.Equal("\uE90F", overrides["PDF Tools"]);
        Assert.Equal("\uE7FC", overrides["browsers"]);
        Assert.Equal(2, overrides.Count);
    }

    [Fact]
    public void BuildOverrideGlyphsFromPolicyLines_EmptyInput_ReturnsEmptyMap()
    {
        var overrides = MscCategoryIconResolver.BuildOverrideGlyphsFromPolicyLines([]);
        Assert.Empty(overrides);
    }
}
