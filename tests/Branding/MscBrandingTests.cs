using Cimian.Core.Branding;
using Xunit;

namespace Cimian.Tests.Branding;

public sealed class MscBrandingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cimian-branding-" + Guid.NewGuid().ToString("N"));
    private readonly string _branding;
    private readonly string _clientResources;

    public MscBrandingTests()
    {
        _branding = Directory.CreateDirectory(Path.Combine(_root, "branding")).FullName;
        _clientResources = Directory.CreateDirectory(Path.Combine(_root, "client_resources")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private IReadOnlyList<string> Dirs => [_branding, _clientResources];

    private string Touch(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static BrandingPolicy Policy(params (string Name, string Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, string>(v.Name, v.Value)));

    [Fact]
    public void NothingConfigured_GivesDefaults()
    {
        var b = MscBrandingResolver.Resolve(null, BrandingPolicy.Empty, Dirs);

        Assert.Null(b.AppTitle);
        Assert.Equal(MscBranding.DefaultAppTitle, b.WindowTitle);
        Assert.Equal(MscBranding.DefaultAppTitle, b.BannerTitle);
        Assert.True(b.ShowBannerTitle);
        Assert.Null(b.Accent);
        Assert.Null(b.LogoPath);
        Assert.Null(b.AppIconPath);
        Assert.Empty(b.Banners);
        Assert.Empty(b.Warnings);
    }

    [Fact]
    public void Yaml_IsParsedWithEveryKey()
    {
        var doc = MscBrandingResolver.Parse("""
            app_title: Unchained Software
            banner_title: ""
            accent_color: "#0B5CAD"
            logo: logo.png
            app_icon: app.ico
            sidebar_header: header.jpg
            unknown_key: ignored
            banners:
              - image: branding-a.png
                href: munki://detail-Firefox
              - image: branding-b.jpg
            """)!;

        Assert.Equal("Unchained Software", doc.AppTitle);
        Assert.Equal("", doc.BannerTitle);
        Assert.Equal("#0B5CAD", doc.AccentColor);
        Assert.Equal("logo.png", doc.Logo);
        Assert.Equal("app.ico", doc.AppIcon);
        Assert.Equal("header.jpg", doc.SidebarHeader);
        Assert.Equal(2, doc.Banners!.Count);
        Assert.Equal("munki://detail-Firefox", doc.Banners[0].Href);
        Assert.Null(doc.Banners[1].Href);
    }

    [Fact]
    public void Policy_WinsOverYaml()
    {
        Touch(_branding, "yaml-logo.png");
        var policyLogo = Touch(_clientResources, "policy-logo.png");
        var doc = new BrandingDocument { AppTitle = "Yaml", AccentColor = "#111111", Logo = "yaml-logo.png" };

        var b = MscBrandingResolver.Resolve(doc, Policy(
            (BrandingPolicy.AppTitle, "Policy"),
            (BrandingPolicy.AccentColor, "#FF8800"),
            (BrandingPolicy.Logo, "policy-logo.png")), Dirs);

        Assert.Equal("Policy", b.AppTitle);
        Assert.Equal("Policy", b.WindowTitle);
        Assert.Equal("Policy", b.BannerTitle);
        Assert.Equal(new BrandingColor(0xFF, 0x88, 0x00), b.Accent);
        Assert.Equal(policyLogo, b.LogoPath);
    }

    [Fact]
    public void EmptyPolicyValue_IsUnset_ExceptBannerTitleWhichHides()
    {
        var doc = new BrandingDocument { AppTitle = "Yaml", BannerTitle = "Welcome" };

        var b = MscBrandingResolver.Resolve(doc, Policy(
            (BrandingPolicy.AppTitle, ""),
            (BrandingPolicy.BannerTitle, "")), Dirs);

        Assert.Equal("Yaml", b.AppTitle);
        Assert.Equal("", b.BannerTitle);
        Assert.False(b.ShowBannerTitle);
    }

    [Fact]
    public void BadPolicyAccent_FallsBackToYaml_WithWarning()
    {
        var b = MscBrandingResolver.Resolve(
            new BrandingDocument { AccentColor = "#00AA00" },
            Policy((BrandingPolicy.AccentColor, "blue")), Dirs);

        Assert.Equal(new BrandingColor(0, 0xAA, 0), b.Accent);
        Assert.Contains(b.Warnings, w => w.Contains("blue"));
    }

    [Theory]
    [InlineData(@"..\Config.yaml.png")]
    [InlineData(@"C:\Windows\logo.png")]
    [InlineData("sub/logo.png")]
    [InlineData("logo.exe")]
    [InlineData("missing.png")]
    public void LogoThatIsNotAPlainBrandingFile_IsRefused(string name)
    {
        var b = MscBrandingResolver.Resolve(new BrandingDocument { Logo = name }, BrandingPolicy.Empty, Dirs);

        Assert.Null(b.LogoPath);
        Assert.Single(b.Warnings);
    }

    [Fact]
    public void BrandingFolder_IsSearchedBeforeClientResources()
    {
        var first = Touch(_branding, "logo.png");
        Touch(_clientResources, "logo.png");

        var b = MscBrandingResolver.Resolve(new BrandingDocument { Logo = "logo.png" }, BrandingPolicy.Empty, Dirs);

        Assert.Equal(first, b.LogoPath);
    }

    [Fact]
    public void AppIcon_MustBeIco()
    {
        Touch(_branding, "icon.png");
        var ico = Touch(_branding, "icon.ico");

        Assert.Null(MscBrandingResolver.Resolve(new BrandingDocument { AppIcon = "icon.png" }, BrandingPolicy.Empty, Dirs).AppIconPath);
        Assert.Equal(ico, MscBrandingResolver.Resolve(new BrandingDocument { AppIcon = "icon.ico" }, BrandingPolicy.Empty, Dirs).AppIconPath);
    }

    [Fact]
    public void SidebarHeader_FallsBackToConventionalName()
    {
        var header = Touch(_clientResources, "sidebar_header.jpg");

        var b = MscBrandingResolver.Resolve(null, BrandingPolicy.Empty, Dirs);

        Assert.Equal(header, b.SidebarHeaderPath);
    }

    [Fact]
    public void Banners_DefaultToBrandingImagesInNameOrder()
    {
        Touch(_branding, "branding2.jpg");
        Touch(_branding, "branding.png");
        Touch(_branding, "branding1.jpeg");
        Touch(_branding, "branding.txt");
        Touch(_branding, "other.png");

        var b = MscBrandingResolver.Resolve(null, BrandingPolicy.Empty, Dirs);

        Assert.Equal(["branding.png", "branding1.jpeg", "branding2.jpg"], b.Banners.Select(x => Path.GetFileName(x.ImagePath)));
        Assert.All(b.Banners, x => Assert.Null(x.Link));
    }

    [Fact]
    public void Banners_FromYaml_KeepOrderAndLinks_AndSkipMissing()
    {
        Touch(_branding, "b.png");
        Touch(_clientResources, "a.jpg");
        var doc = new BrandingDocument
        {
            Banners =
            [
                new() { Image = "b.png", Href = "munki://category-Productivity%20Apps" },
                new() { Image = "missing.png", Href = "https://example.org" },
                new() { Image = "a.jpg", Href = "https://example.org/help" },
            ],
        };

        var b = MscBrandingResolver.Resolve(doc, BrandingPolicy.Empty, Dirs);

        Assert.Equal(2, b.Banners.Count);
        Assert.Equal("cimian://category/Productivity%20Apps", b.Banners[0].Link!.Uri.OriginalString);
        Assert.Equal(BrandingLinkKind.External, b.Banners[1].Link!.Kind);
        Assert.Contains(b.Warnings, w => w.Contains("missing.png"));
    }

    [Fact]
    public void DefaultTitle_IsNotDrawnOverBanners()
    {
        Touch(_branding, "branding.png");

        var b = MscBrandingResolver.Resolve(new BrandingDocument { AppTitle = "Acme" }, BrandingPolicy.Empty, Dirs);

        Assert.Equal("Acme", b.BannerTitle);
        Assert.False(b.ShowBannerTitle);
    }

    [Fact]
    public void ExplicitBannerTitle_IsDrawnOverBanners()
    {
        Touch(_branding, "branding.png");

        var b = MscBrandingResolver.Resolve(new BrandingDocument { BannerTitle = "Welcome" }, BrandingPolicy.Empty, Dirs);

        Assert.True(b.ShowBannerTitle);
    }

    [Fact]
    public void Banners_AreCapped()
    {
        var doc = new BrandingDocument { Banners = [] };
        for (var i = 0; i < MscBrandingResolver.MaxBanners + 3; i++)
        {
            Touch(_branding, $"slide{i}.png");
            doc.Banners.Add(new() { Image = $"slide{i}.png" });
        }

        var b = MscBrandingResolver.Resolve(doc, BrandingPolicy.Empty, Dirs);

        Assert.Equal(MscBrandingResolver.MaxBanners, b.Banners.Count);
    }

    [Fact]
    public void Load_ReadsYamlFromClientResourcesFirst()
    {
        File.WriteAllText(Path.Combine(_clientResources, "branding.yaml"), "app_title: From client_resources");
        File.WriteAllText(Path.Combine(_branding, "branding.yaml"), "app_title: From branding");

        var b = MscBrandingResolver.Load(BrandingPolicy.Empty, Dirs);

        Assert.Equal("From client_resources", b.AppTitle);
    }

    [Fact]
    public void Load_BrokenYaml_StillAppliesPolicy()
    {
        File.WriteAllText(Path.Combine(_clientResources, "branding.yaml"), "app_title: [unclosed");

        var b = MscBrandingResolver.Load(Policy((BrandingPolicy.AppTitle, "Policy")), Dirs);

        Assert.Equal("Policy", b.AppTitle);
        Assert.NotEmpty(b.Warnings);
    }
}

public class BrandingColorTests
{
    [Theory]
    [InlineData("#0B5CAD", 0x0B, 0x5C, 0xAD)]
    [InlineData("0b5cad", 0x0B, 0x5C, 0xAD)]
    [InlineData("#abc", 0xAA, 0xBB, 0xCC)]
    [InlineData("#800B5CAD", 0x0B, 0x5C, 0xAD)]
    [InlineData("  #FFFFFF ", 0xFF, 0xFF, 0xFF)]
    public void Parses(string text, byte r, byte g, byte b)
    {
        Assert.True(BrandingColor.TryParse(text, out var c));
        Assert.Equal(new BrandingColor(r, g, b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Rejects(string? text) => Assert.False(BrandingColor.TryParse(text, out _));

    [Fact]
    public void FormatsAsHex() => Assert.Equal("#0B5CAD", new BrandingColor(0x0B, 0x5C, 0xAD).ToString());

    [Fact]
    public void Shades_GetLighterAndDarker()
    {
        var accent = new BrandingColor(0x0B, 0x5C, 0xAD);
        var light = accent.LightShades();
        var dark = accent.DarkShades();

        Assert.Equal(3, light.Length);
        Assert.Equal(3, dark.Length);
        Assert.True(light[0].RelativeLuminance() > accent.RelativeLuminance());
        Assert.True(light[2].RelativeLuminance() > light[0].RelativeLuminance());
        Assert.True(dark[0].RelativeLuminance() < accent.RelativeLuminance());
        Assert.True(dark[2].RelativeLuminance() < dark[0].RelativeLuminance());
    }

    [Fact]
    public void ReadableForeground_PicksContrast()
    {
        Assert.Equal(BrandingColor.White, new BrandingColor(0x0B, 0x5C, 0xAD).ReadableForeground());
        Assert.Equal(BrandingColor.Black, new BrandingColor(0xFF, 0xD7, 0x00).ReadableForeground());
    }
}

public class BrandingLinkTests
{
    [Theory]
    [InlineData("munki://detail-Firefox", "cimian://item/Firefox")]
    [InlineData("munki://updatedetail-Firefox", "cimian://updatedetail/Firefox")]
    [InlineData("munki://category-Productivity%20Apps", "cimian://category/Productivity%20Apps")]
    [InlineData("munki://category-Web Browsers", "cimian://category/Web%20Browsers")]
    [InlineData("munki://updates", "cimian://updates")]
    [InlineData("munki://categories", "cimian://categories")]
    [InlineData("munki://myitems", "cimian://myitems")]
    [InlineData("munki://filter-chrome", "cimian://software")]
    [InlineData("munki://developer-Google", "cimian://software")]
    [InlineData("cimian://item/Firefox", "cimian://item/Firefox")]
    public void InternalLinks(string href, string expected)
    {
        var link = BrandingLink.Parse(href);

        Assert.NotNull(link);
        Assert.Equal(BrandingLinkKind.Internal, link!.Kind);
        Assert.Equal(expected, link.Uri.OriginalString);
    }

    [Theory]
    [InlineData("https://intranet.example.org/software")]
    [InlineData("http://example.org")]
    public void ExternalLinks(string href)
    {
        var link = BrandingLink.Parse(href);

        Assert.Equal(BrandingLinkKind.External, link!.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("relative/page.html")]
    [InlineData("munki://settings")]
    public void UnsupportedLinks_AreNull(string? href) => Assert.Null(BrandingLink.Parse(href));
}
