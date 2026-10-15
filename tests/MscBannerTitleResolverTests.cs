// MscBannerTitleResolverTests.cs - Precedence for MSC Software hero banner title.

using Cimian.Core;
using Xunit;

namespace Cimian.Tests;

public class MscBannerTitleResolverTests
{
    [Fact]
    public void Resolve_NoBranding_ReturnsDefault()
    {
        var result = MscBannerTitleResolver.Resolve(
            appTitle: null,
            bannerTitle: null,
            bannerTitleExplicit: false);

        Assert.Equal(MscBannerTitleResolver.DefaultTitle, result);
        Assert.True(MscBannerTitleResolver.ShouldShow(result));
    }

    [Fact]
    public void Resolve_AppTitleOnly_UsesAppTitle()
    {
        var result = MscBannerTitleResolver.Resolve(
            appTitle: "  Unchained Software  ",
            bannerTitle: null,
            bannerTitleExplicit: false);

        Assert.Equal("Unchained Software", result);
        Assert.True(MscBannerTitleResolver.ShouldShow(result));
    }

    [Fact]
    public void Resolve_WhitespaceAppTitle_FallsBackToDefault()
    {
        var result = MscBannerTitleResolver.Resolve(
            appTitle: "   ",
            bannerTitle: null,
            bannerTitleExplicit: false);

        Assert.Equal(MscBannerTitleResolver.DefaultTitle, result);
    }

    [Fact]
    public void Resolve_EmptyBannerTitle_HidesEvenWhenAppTitleSet()
    {
        var result = MscBannerTitleResolver.Resolve(
            appTitle: "Unchained Software",
            bannerTitle: "",
            bannerTitleExplicit: true);

        Assert.Equal(string.Empty, result);
        Assert.False(MscBannerTitleResolver.ShouldShow(result));
    }

    [Fact]
    public void Resolve_WhitespaceBannerTitle_HidesOverlay()
    {
        var result = MscBannerTitleResolver.Resolve(
            appTitle: "Unchained Software",
            bannerTitle: "   ",
            bannerTitleExplicit: true);

        Assert.Equal(string.Empty, result);
        Assert.False(MscBannerTitleResolver.ShouldShow(result));
    }

    [Fact]
    public void Resolve_CustomBannerTitle_WinsOverAppTitle()
    {
        var result = MscBannerTitleResolver.Resolve(
            appTitle: "Unchained Software",
            bannerTitle: "  Self Service  ",
            bannerTitleExplicit: true);

        Assert.Equal("Self Service", result);
        Assert.True(MscBannerTitleResolver.ShouldShow(result));
    }

    [Fact]
    public void Resolve_BannerTitleNotExplicit_IgnoresBannerValue()
    {
        // Without explicit presence, a leftover banner string must not win.
        var result = MscBannerTitleResolver.Resolve(
            appTitle: "Unchained Software",
            bannerTitle: "Should Be Ignored",
            bannerTitleExplicit: false);

        Assert.Equal("Unchained Software", result);
    }

    [Fact]
    public void ShouldShow_Null_ReturnsFalse()
    {
        Assert.False(MscBannerTitleResolver.ShouldShow(null));
    }
}
