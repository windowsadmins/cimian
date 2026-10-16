using Cimian.CLI.Cimiimport.Models;
using Cimian.Core.Services;
using Xunit;
using ClientModels = Cimian.CLI.managedsoftwareupdate.Models;

namespace Cimian.Tests.Shared;

/// <summary>
/// Tests for PlistUtils: reading XML plists, and converting them to the YAML text the
/// typed models bind.
/// </summary>
public class PlistUtilsTests
{
    internal const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + PlistUtils.AppleDoctype + "\n";

    internal static string Plist(string body) => Header + "<plist version=\"1.0\">\n" + body + "\n</plist>\n";

    internal const string PkgInfoPlistBody = """
        <dict>
            <key>name</key><string>ExampleApp</string>
            <key>display_name</key><string>Example App</string>
            <key>version</key><string>1.10</string>
            <key>catalogs</key><array><string>Production</string></array>
            <key>unattended_install</key><true/>
            <key>unattended_uninstall</key><false/>
            <key>installer</key>
            <dict>
                <key>location</key><string>util/ExampleApp-1.10.msi</string>
                <key>type</key><string>msi</string>
                <key>size</key><integer>1661239</integer>
                <key>args</key><array><string>/qn</string></array>
            </dict>
            <key>installcheck_script</key><string>$x = 1
        if ($x -eq 1) { exit 0 }
        exit 1
        </string>
            <key>force_install_after_date</key><date>2026-12-02T02:00:00Z</date>
            <key>_metadata</key>
            <dict>
                <key>created_by</key><string>tester</string>
            </dict>
        </dict>
        """;

    // --- Parse --------------------------------------------------------------------------
    [Fact]
    public void Parse_EveryElementKind_ReadsTypedValues()
    {
        var tree = PlistUtils.Parse(Plist("""
            <dict>
                <key>s</key><string>text</string>
                <key>i</key><integer>-42</integer>
                <key>r</key><real>1.5</real>
                <key>t</key><true/>
                <key>f</key><false/>
                <key>d</key><date>2026-12-02T02:00:00Z</date>
                <key>a</key><array><string>x</string><integer>1</integer></array>
                <key>n</key><dict><key>k</key><string>v</string></dict>
            </dict>
            """));

        var dict = Assert.IsType<Dictionary<string, object?>>(tree);
        Assert.Equal("text", dict["s"]);
        Assert.Equal(-42L, dict["i"]);
        Assert.Equal(1.5, dict["r"]);
        Assert.Equal(true, dict["t"]);
        Assert.Equal(false, dict["f"]);
        var date = Assert.IsType<DateTime>(dict["d"]);
        Assert.Equal(DateTimeKind.Utc, date.Kind);
        Assert.Equal(new DateTime(2026, 12, 2, 2, 0, 0, DateTimeKind.Utc), date);
        var list = Assert.IsType<List<object?>>(dict["a"]);
        Assert.Equal(new object?[] { "x", 1L }, list);
        var nested = Assert.IsType<Dictionary<string, object?>>(dict["n"]);
        Assert.Equal("v", nested["k"]);
    }

    [Fact]
    public void Parse_RootElementOtherThanPlist_Throws()
    {
        Assert.Throws<PlistFormatException>(() => PlistUtils.Parse(Header + "<foo><dict/></foo>"));
    }

    [Fact]
    public void Parse_DateWithoutTime_ReadsAsMidnightUtc()
    {
        var dict = Assert.IsType<Dictionary<string, object?>>(PlistUtils.Parse(Plist("<dict><key>d</key><date>2026-12-02</date></dict>")));
        var date = Assert.IsType<DateTime>(dict["d"]);
        Assert.Equal(DateTimeKind.Utc, date.Kind);
        Assert.Equal(new DateTime(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc), date);
    }

    [Fact]
    public void Parse_DoctypeWithUnreachableAddress_IsNotFetched()
    {
        // A DOCTYPE that names an unreachable address: a reader that resolved it would fail.
        var text = "<?xml version=\"1.0\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://127.0.0.1:9/never.dtd\">\n"
                   + "<plist version=\"1.0\"><!-- a comment --><dict><key>a</key><string>b</string></dict></plist>";
        var dict = Assert.IsType<Dictionary<string, object?>>(PlistUtils.Parse(text));
        Assert.Equal("b", dict["a"]);
    }

    [Fact]
    public void Parse_DoctypeDeclaringAnEntity_Throws()
    {
        // The DTD is ignored, so an entity it declares is never expanded (no entity
        // bombs) and a reference to it is an error.
        var text = "<?xml version=\"1.0\"?>\n<!DOCTYPE plist [ <!ENTITY e \"expanded\"> ]>\n"
                   + "<plist version=\"1.0\"><dict><key>a</key><string>&e;</string></dict></plist>";
        Assert.Throws<PlistFormatException>(() => PlistUtils.Parse(text));
    }

    [Fact]
    public void Parse_DataBinaryOrMalformedPlist_Throws()
    {
        var ex = Assert.Throws<PlistFormatException>(() => PlistUtils.Parse(Plist("<dict><key>blob</key><data>AAEC</data></dict>")));
        Assert.Contains("<data>", ex.Message);
        Assert.Throws<PlistFormatException>(() => PlistUtils.Parse("bplist00\u0000\u0001"));
        Assert.Throws<PlistFormatException>(() => PlistUtils.Parse("name: not a plist\n"));
    }

    [Theory]
    [InlineData("<dict><key>a</key></dict>")]
    [InlineData("<dict><string>no key</string></dict>")]
    [InlineData("<dict><key>a</key><key>b</key><string>x</string></dict>")]
    [InlineData("<dict><key>a</key><unknown/></dict>")]
    [InlineData("<dict><key>a</key><integer>x</integer></dict>")]
    [InlineData("<dict/><dict/>")]
    [InlineData("")]
    [InlineData("<dict><key>a</key><string>unclosed</dict>")]
    public void Parse_MalformedPlistBody_Throws(string body)
    {
        Assert.Throws<PlistFormatException>(() => PlistUtils.Parse(Plist(body)));
    }

    [Theory]
    [InlineData("2026-12-02T02:00:00Z", "2026-12-02T02:00:00.0000000Z")]
    [InlineData("2026-12-02T02:00:00.5Z", "2026-12-02T02:00:00.5000000Z")]
    [InlineData("2026-12-02T04:00:00+02:00", "2026-12-02T02:00:00.0000000Z")]
    [InlineData("2026-12-01T21:00:00.25-05:00", "2026-12-02T02:00:00.2500000Z")]
    [InlineData("2026-12-02T02:00:00", "2026-12-02T02:00:00.0000000Z")]
    [InlineData("2026-12-02", "2026-12-02T00:00:00.0000000Z")]
    public void Parse_DateInIso8601_ReadsAsUtc(string text, string expectedUtc)
    {
        var dict = Assert.IsType<Dictionary<string, object?>>(PlistUtils.Parse(Plist($"<dict><key>d</key><date>{text}</date></dict>")));
        var date = Assert.IsType<DateTime>(dict["d"]);
        Assert.Equal(DateTimeKind.Utc, date.Kind);
        Assert.Equal(expectedUtc, date.ToString("o"));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("02/12/2026")]
    [InlineData("Dec 2 2026")]
    [InlineData("2 December 2026")]
    [InlineData("2026-13-02")]
    public void Parse_DateNotInIso8601_ThrowsNamingThePath(string text)
    {
        var ex = Assert.Throws<PlistFormatException>(() => PlistUtils.Parse(Plist($"<dict><key>d</key><date>{text}</date></dict>")));
        Assert.StartsWith("/d/: <date>", ex.Message);
    }

    [Fact]
    public void LooksLikePlist_PlistAndYaml_AreToldApart()
    {
        Assert.True(PlistUtils.LooksLikePlist(Plist("<dict/>")));
        Assert.True(PlistUtils.LooksLikePlist("\uFEFF  <plist version=\"1.0\"><dict/></plist>"));
        Assert.True(PlistUtils.LooksLikePlist(PlistUtils.AppleDoctype + "<plist><dict/></plist>"));
        Assert.False(PlistUtils.LooksLikePlist("name: x\nversion: '1'\n"));
        Assert.False(PlistUtils.LooksLikePlist("bplist00"));
        Assert.False(PlistUtils.LooksLikePlist(""));
        Assert.False(PlistUtils.LooksLikePlist(null));
        Assert.False(PlistUtils.LooksLikePlist("name: x\ninstallcheck_script: |\n  '<plist version=\"1.0\">' | Out-File x.plist\n"));
        Assert.True(PlistUtils.LooksLikePlist("<?xml version=\"1.0\"?>\n<plist version=\"1.0\"><dict/></plist>"));
        Assert.True(PlistUtils.LooksLikePlist("\r\n\t" + Plist("<dict/>")));
    }

    [Fact]
    public void Parse_DeepNesting_ThrowsPlistFormatException()
    {
        var text = Header + "<plist version=\"1.0\">" + string.Concat(Enumerable.Repeat("<array>", 100000)) + string.Concat(Enumerable.Repeat("</array>", 100000)) + "</plist>";
        var ex = Record.Exception(() => PlistUtils.Parse(text));
        Assert.IsType<PlistFormatException>(ex);
    }

    private static string NestedArrays(int depth)
        => Header + "<plist version=\"1.0\">" + string.Concat(Enumerable.Repeat("<array>", depth))
           + string.Concat(Enumerable.Repeat("</array>", depth)) + "</plist>";

    [Fact]
    public void Parse_SixtyFourNestedContainers_AreRead()
    {
        Assert.IsType<List<object?>>(PlistUtils.Parse(NestedArrays(64)));
    }

    [Fact]
    public void Parse_SixtyFiveNestedContainers_Throws()
    {
        Assert.Throws<PlistFormatException>(() => PlistUtils.Parse(NestedArrays(65)));
    }

    [Fact]
    public void Parse_PaddedValuesBomAndKeysDifferingInCase_AreRead()
    {
        var dict = Assert.IsType<Dictionary<string, object?>>(PlistUtils.Parse(Plist(
            "<dict><key>i</key><integer> 7 </integer><key>r</key><real> 1e3 </real><key>d</key><date> 2026-12-02T02:00:00Z </date>"
            + "<key>A</key><string>upper</string><key>a</key><string>lower</string><key>version</key><real> 1.10 </real></dict>")));
        Assert.Equal(7L, dict["i"]);
        Assert.Equal(1000.0, dict["r"]);
        Assert.Equal(new DateTime(2026, 12, 2, 2, 0, 0, DateTimeKind.Utc), dict["d"]);
        Assert.Equal("upper", dict["A"]);
        Assert.Equal("lower", dict["a"]);
        Assert.Equal("1.10", dict["version"]);
        Assert.IsType<Dictionary<string, object?>>(PlistUtils.Parse("\uFEFF" + Plist("<dict/>")));
    }

    [Fact]
    public void Parse_LeadingWhiteSpace_IsAccepted()
    {
        var text = "\n  " + Header + "<plist version=\"1.0\"><dict/></plist>\n";
        Assert.True(PlistUtils.LooksLikePlist(text));
        Assert.NotNull(PlistUtils.Parse(text));
    }

    // --- ToYaml: the typed models bind the converted text as they bind YAML -------------
    [Fact]
    public void ToYaml_PkgInfo_BindsIntoImportModelWithEveryTypeKept()
    {
        var yaml = PlistUtils.ToYaml(Plist(PkgInfoPlistBody), RepoFileKind.PkgInfo);
        var pkg = YamlUtils.DeserializePkgInfo<PkgsInfo>(yaml);

        Assert.NotNull(pkg);
        Assert.Equal("ExampleApp", pkg!.Name);
        Assert.Equal("Example App", pkg.DisplayName);
        Assert.Equal("1.10", pkg.Version);
        Assert.Equal(new List<string> { "Production" }, pkg.Catalogs);
        Assert.True(pkg.UnattendedInstall);
        Assert.False(pkg.UnattendedUninstall);
        Assert.Equal("util/ExampleApp-1.10.msi", pkg.Installer?.Location);
        Assert.Equal(1661239L, pkg.Installer?.Size);
        Assert.Equal("$x = 1\nif ($x -eq 1) { exit 0 }\nexit 1\n", pkg.InstallCheckScript);
        Assert.NotNull(pkg.Metadata);
        Assert.Equal("tester", pkg.Metadata!["created_by"]?.ToString());
    }

    [Fact]
    public void ToYaml_StringsThatLookLikeOtherTypes_StayStrings()
    {
        var body = """
            <dict>
                <key>name</key><string>Probe</string>
                <key>version</key><string>1.10</string>
                <key>display_name</key><string>null</string>
                <key>category</key><string>true</string>
                <key>developer</key><string>1e3</string>
                <key>description</key><string>~</string>
                <key>icon_name</key><string>007</string>
            </dict>
            """;
        var yaml = PlistUtils.ToYaml(Plist(body), RepoFileKind.PkgInfo);
        var item = YamlUtils.Deserializer.Deserialize<ClientModels.CatalogItem>(yaml);

        Assert.Contains("version: '1.10'\n", yaml);
        Assert.Equal("1.10", item.Version);
        Assert.Equal("null", item.DisplayName);
        Assert.Equal("true", item.Category);
        Assert.Equal("1e3", item.Developer);
        Assert.Equal("~", item.Description);
        Assert.Equal("007", item.IconName);
    }

    [Fact]
    public void ToYaml_Date_BindsAsTheMomentItNames()
    {
        var yaml = PlistUtils.ToYaml(Plist(PkgInfoPlistBody), RepoFileKind.PkgInfo);
        var item = YamlUtils.Deserializer.Deserialize<ClientModels.CatalogItem>(yaml);

        Assert.Contains("force_install_after_date: 2026-12-02T02:00:00Z\n", yaml);
        Assert.NotNull(item.ForceInstallAfterDate);
        Assert.Equal(new DateTime(2026, 12, 2, 2, 0, 0, DateTimeKind.Utc), item.ForceInstallAfterDate!.Value.ToUniversalTime());
    }

    [Fact]
    public void ToYaml_CatalogBareArray_BindsIntoClientCatalogModel()
    {
        var body = """
            <array>
                <dict><key>name</key><string>A</string><key>version</key><string>26.03</string></dict>
                <dict><key>name</key><string>B</string><key>version</key><string>2.0</string></dict>
            </array>
            """;
        var yaml = PlistUtils.ToYaml(Plist(body), RepoFileKind.Catalog);
        Assert.StartsWith("items:", yaml);

        var client = YamlUtils.DeserializeCatalog<ClientModels.CatalogWrapper>(yaml);
        Assert.Equal(new[] { "A", "B" }, client!.Items.Select(i => i.Name));
        Assert.Equal("26.03", client.Items[0].Version);
    }

    [Fact]
    public void ToYaml_CrlfInString_BecomesLf()
    {
        var text = Header + "<plist version=\"1.0\"><dict><key>name</key><string>X</string><key>postinstall_script</key><string>a\r\nb\r\n</string></dict></plist>";
        var yaml = PlistUtils.ToYaml(text, RepoFileKind.PkgInfo);
        var pkg = YamlUtils.DeserializePkgInfo<PkgsInfo>(yaml);
        Assert.Equal("a\nb\n", pkg!.PostinstallScript);
    }

    [Fact]
    public void ToYaml_Catalog_PutsNameFirst()
    {
        var plist = Header + "<plist version=\"1.0\"><array><dict><key>display_name</key><string>App One</string><key>name</key><string>A</string><key>version</key><string>1.0</string></dict></array></plist>\n";
        var yaml = PlistUtils.ToYaml(plist, RepoFileKind.Catalog);
        Assert.Contains("- name: A\n", yaml);
        Assert.True(yaml.IndexOf("name: A", StringComparison.Ordinal) < yaml.IndexOf("display_name", StringComparison.Ordinal));
    }

    [Fact]
    public void ToYaml_RepeatedKey_KeepsTheLastValue()
    {
        var plist = Plist("<dict><key>managed_installs</key><array><string>A</string></array><key>catalogs</key><array><string>Production</string></array><key>managed_installs</key><array><string>B</string></array></dict>");
        var manifest = YamlUtils.DeserializeManifest<ClientModels.ManifestFile>(PlistUtils.ToYaml(plist, RepoFileKind.Manifest));
        Assert.Equal(new List<string> { "B" }, manifest!.ManagedInstalls);
        Assert.Equal(new List<string> { "Production" }, manifest.Catalogs);
    }

    [Fact]
    public void ToYaml_RootOfTheWrongShape_Throws()
    {
        Assert.Throws<PlistFormatException>(() => PlistUtils.ToYaml(Plist("<array/>"), RepoFileKind.Manifest));
        Assert.Throws<PlistFormatException>(() => PlistUtils.ToYaml(Plist("<string>x</string>"), RepoFileKind.PkgInfo));
    }

    [Theory]
    [InlineData("version", "<real>1.10</real>", "1.10")]
    [InlineData("minimum_os_version", "<real>10.10</real>", "10.10")]
    [InlineData("maximum_os_version", "<real>11.0</real>", "11.0")]
    [InlineData("minimum_cimian_version", "<integer>007</integer>", "007")]
    public void ToYaml_TextKeyHeldAsNumber_IsWrittenAsText(string key, string element, string text)
    {
        var yaml = PlistUtils.ToYaml(Plist($"<dict><key>name</key><string>A</string><key>{key}</key>{element}</dict>"), RepoFileKind.PkgInfo);
        Assert.Contains($"{key}: '{text}'\n", yaml);
    }

    [Fact]
    public void ToYaml_RealOutsideTheTextKeys_StaysANumber()
    {
        var yaml = PlistUtils.ToYaml(Plist("<dict><key>name</key><string>A</string><key>ratio</key><real>1.5</real></dict>"), RepoFileKind.PkgInfo);
        Assert.Contains("ratio: 1.5\n", yaml);
    }

    [Theory]
    [InlineData("1.10")]
    [InlineData("0x10")]
    [InlineData(".inf")]
    [InlineData("1:30")]
    [InlineData("yes")]
    [InlineData("OFF")]
    [InlineData("TRUE")]
    [InlineData("Null")]
    [InlineData("~")]
    [InlineData("a\tb")]
    public void ToYaml_StringYamlWouldReadAsAnotherType_IsQuoted(string text)
    {
        var yaml = PlistUtils.ToYaml(Plist($"<dict><key>name</key><string>A</string><key>description</key><string>{text}</string></dict>"), RepoFileKind.PkgInfo);
        Assert.Contains($"description: '{text}'\n", yaml);
    }

    [Fact]
    public void ToYaml_PkgInfoWithSortedKeys_PutsCimiansKeysFirstAndMetadataLast()
    {
        // Keys sorted, as plistlib and plutil write them.
        var yaml = PlistUtils.ToYaml(Plist("<dict><key>_metadata</key><dict><key>created_by</key><string>tester</string></dict>"
            + "<key>catalogs</key><array><string>Production</string></array><key>display_name</key><string>Example App</string>"
            + "<key>name</key><string>ExampleApp</string><key>version</key><string>1.10</string></dict>"), RepoFileKind.PkgInfo);
        Assert.Equal("name: ExampleApp\ndisplay_name: Example App\nversion: '1.10'\ncatalogs:\n- Production\n_metadata:\n  created_by: tester\n", yaml);
    }

    [Theory]
    [InlineData("\n", "\n")]
    [InlineData("exit 0\n\n", "exit 0\n\n")]
    [InlineData("a\u0085b", "a\u0085b")]
    [InlineData("a\t\u2028\tb", "a\t\u2028\tb")]
    [InlineData("a\t\u2029\tb", "a\t\u2029\tb")]
    [InlineData("a&#13;b", "a\rb")]
    public void ToYaml_StringWithUnusualLineBreaks_KeepsItsText(string xmlText, string expected)
    {
        var plist = Plist($"<dict><key>name</key><string>A</string><key>postinstall_script</key><string>{xmlText}</string></dict>");
        var pkg = YamlUtils.DeserializePkgInfo<PkgsInfo>(PlistUtils.ToYaml(plist, RepoFileKind.PkgInfo));
        Assert.Equal(expected, pkg!.PostinstallScript);
    }

    [Theory]
    [InlineData("\tx", "\tx")]
    [InlineData("true", "true")]
    [InlineData("a *b", "a *b")]
    [InlineData("_ *3", "_ *3")]
    [InlineData("d!d *g+d", "d!d *g+d")]
    [InlineData("a&#x85;b", "a\u0085b")]
    public void ToYaml_KeyYamlWouldMisread_KeepsItsText(string xmlKey, string key)
    {
        // One key per document: a key YAML misreads can hide behind a neighbour that happens to parse.
        var plist = Plist($"<dict><key>name</key><string>A</string><key>_metadata</key><dict><key>{xmlKey}</key><string>v</string></dict></dict>");
        var meta = YamlUtils.ExtractMetadataBlock(PlistUtils.ToYaml(plist, RepoFileKind.PkgInfo));
        Assert.NotNull(meta);
        Assert.Equal("v", meta![key]);
    }
}
