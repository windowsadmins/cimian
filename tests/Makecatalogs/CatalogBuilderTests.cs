using Xunit;
using Cimian.CLI.Makecatalogs.Models;
using Cimian.CLI.Makecatalogs.Services;

namespace Cimian.Tests.Makecatalogs;

/// <summary>
/// Tests for CatalogBuilder service
/// Migrated from Go: cmd/makecatalogs/main.go
/// </summary>
public class CatalogBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogBuilder _builder;
    private readonly List<string> _logs;
    private readonly List<string> _warnings;
    private readonly List<string> _successes;

    public CatalogBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"makecatalogs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "pkgsinfo"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "pkgs"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "catalogs"));

        _logs = new List<string>();
        _warnings = new List<string>();
        _successes = new List<string>();

        _builder = new CatalogBuilder(
            log: msg => _logs.Add(msg),
            warn: msg => _warnings.Add(msg),
            success: msg => _successes.Add(msg)
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private void CreatePkgInfo(string relativePath, string yaml)
    {
        var fullPath = Path.Combine(_tempDir, "pkgsinfo", relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(fullPath, yaml);
    }

    private void CreatePayload(string relativePath)
    {
        var fullPath = Path.Combine(_tempDir, "pkgs", relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(fullPath, "dummy payload");
    }

    [Fact]
    public void ScanRepo_FindsYamlFiles()
    {
        // Arrange
        CreatePkgInfo("app1.yaml", @"
name: App1
version: 1.0.0
catalogs:
  - production
");
        CreatePkgInfo("subfolder/app2.yaml", @"
name: App2
version: 2.0.0
catalogs:
  - testing
");

        // Act
        var items = _builder.ScanRepo(_tempDir);

        // Assert
        Assert.Equal(2, items.Count);
        Assert.Contains(items, p => p.Name == "App1");
        Assert.Contains(items, p => p.Name == "App2");
    }

    [Fact]
    public void ScanRepo_SetsFilePath()
    {
        CreatePkgInfo("myapp.yaml", @"
name: MyApp
version: 1.0.0
catalogs: []
");

        var items = _builder.ScanRepo(_tempDir);

        Assert.Single(items);
        Assert.Contains("myapp.yaml", items[0].FilePath);
    }

    [Fact]
    public void ScanRepo_ThrowsForMissingDirectory()
    {
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent");
        Assert.Throws<DirectoryNotFoundException>(() => _builder.ScanRepo(nonExistentPath));
    }

    [Fact]
    public void ScanRepo_WarnsOnInvalidYaml()
    {
        CreatePkgInfo("invalid.yaml", "{ this is not valid yaml: [");

        var items = _builder.ScanRepo(_tempDir);

        Assert.Empty(items);
        Assert.Single(_warnings);
    }

    [Fact]
    public void ScanRepo_RecordsParseErrors()
    {
        CreatePkgInfo("invalid.yaml", "{ this is not valid yaml: [");

        _builder.ScanRepo(_tempDir);

        Assert.Single(_builder.ParseErrors);
        Assert.Contains("invalid.yaml", _builder.ParseErrors[0]);
    }

    [Fact]
    public void ScanRepo_ClearsParseErrorsBetweenRuns()
    {
        CreatePkgInfo("invalid.yaml", "{ this is not valid yaml: [");
        _builder.ScanRepo(_tempDir);
        Assert.Single(_builder.ParseErrors);

        File.Delete(Path.Combine(_tempDir, "pkgsinfo", "invalid.yaml"));
        _builder.ScanRepo(_tempDir);

        Assert.Empty(_builder.ParseErrors);
    }

    [Fact]
    public void Run_FailsAndDoesNotReportSuccess_WhenPkgsinfoCannotBeParsed()
    {
        // The regression: a package that fails to parse is absent from the catalogs
        // that get written, but the run previously printed "completed successfully"
        // and returned 0, so a pipeline would publish an incomplete catalog with no
        // signal. Exit code and success message are both part of the contract here.
        CreatePkgInfo("good.yaml", @"name: GoodApp
version: 1.0
catalogs:
  - Testing
");
        CreatePkgInfo("broken.yaml", "{ this is not valid yaml: [");

        var exitCode = _builder.Run(_tempDir, skipPayloadCheck: true);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(_successes, m => m.Contains("completed successfully"));
        Assert.Contains(_warnings, m => m.Contains("1 pkgsinfo skipped"));
        Assert.Contains(_warnings, m => m.Contains("broken.yaml"));
    }

    [Fact]
    public void Run_TolerateParseErrors_SucceedsButStillReportsSkipped()
    {
        // The escape hatch keeps the old exit code for pipelines that need it, but
        // must never restate the unqualified "completed successfully" -- the whole
        // point is that the outcome stays visible.
        CreatePkgInfo("good.yaml", @"name: GoodApp
version: 1.0
catalogs:
  - Testing
");
        CreatePkgInfo("broken.yaml", "{ this is not valid yaml: [");

        var exitCode = _builder.Run(_tempDir, skipPayloadCheck: true, tolerateParseErrors: true);

        Assert.Equal(0, exitCode);
        Assert.Contains(_warnings, m => m.Contains("1 pkgsinfo skipped"));
        Assert.Contains(_successes, m => m.Contains("1 skipped pkgsinfo"));
        Assert.DoesNotContain(_successes, m => m.Contains("completed successfully"));
    }

    [Fact]
    public void Run_ReportsSuccess_WhenAllPkgsinfoParse()
    {
        CreatePkgInfo("good.yaml", @"name: GoodApp
version: 1.0
catalogs:
  - Testing
");

        var exitCode = _builder.Run(_tempDir, skipPayloadCheck: true);

        Assert.Equal(0, exitCode);
        Assert.Contains(_successes, m => m.Contains("completed successfully"));
    }

    [Fact]
    public void VerifyPayloads_ReturnsEmptyForExistingPayloads()
    {
        // Arrange
        CreatePayload("app1/installer.exe");
        var items = new List<PkgsInfo>
        {
            new PkgsInfo
            {
                Name = "App1",
                FilePath = "test.yaml",
                Installer = new Installer { Location = "app1/installer.exe" }
            }
        };

        // Act
        var warnings = _builder.VerifyPayloads(_tempDir, items);

        // Assert
        Assert.Empty(warnings);
    }

    [Fact]
    public void VerifyPayloads_WarnsForMissingInstaller()
    {
        var items = new List<PkgsInfo>
        {
            new PkgsInfo
            {
                Name = "App1",
                FilePath = "test.yaml",
                Installer = new Installer { Location = "missing/file.exe" }
            }
        };

        var warnings = _builder.VerifyPayloads(_tempDir, items);

        Assert.Single(warnings);
        Assert.Contains("missing installer", warnings[0]);
    }

    [Fact]
    public void VerifyPayloads_WarnsForMissingUninstaller()
    {
        var items = new List<PkgsInfo>
        {
            new PkgsInfo
            {
                Name = "App1",
                FilePath = "test.yaml",
                Uninstaller = [new Installer { Location = "missing/uninstaller.exe" }]
            }
        };

        var warnings = _builder.VerifyPayloads(_tempDir, items);

        Assert.Single(warnings);
        Assert.Contains("missing uninstaller", warnings[0]);
    }

    [Fact]
    public void BuildCatalogs_AlwaysIncludesAllCatalog()
    {
        var items = new List<PkgsInfo>
        {
            new PkgsInfo { Name = "App1", Catalogs = new List<string> { "production" } }
        };

        var catalogs = _builder.BuildCatalogs(items, silent: true);

        Assert.True(catalogs.ContainsKey("All"));
        Assert.Single(catalogs["All"]);
    }

    [Fact]
    public void BuildCatalogs_AddsToNamedCatalogs()
    {
        var items = new List<PkgsInfo>
        {
            new PkgsInfo { Name = "App1", Catalogs = new List<string> { "production", "testing" } },
            new PkgsInfo { Name = "App2", Catalogs = new List<string> { "production" } }
        };

        var catalogs = _builder.BuildCatalogs(items, silent: true);

        Assert.Equal(3, catalogs.Count); // All, production, testing
        Assert.Equal(2, catalogs["production"].Count);
        Assert.Single(catalogs["testing"]);
    }

    [Fact]
    public void BuildCatalogs_LogsWhenNotSilent()
    {
        var items = new List<PkgsInfo>
        {
            new PkgsInfo { Name = "App1", FilePath = "app1.yaml", Catalogs = new List<string> { "prod" } }
        };

        _builder.BuildCatalogs(items, silent: false);

        Assert.NotEmpty(_logs);
    }

    [Fact]
    public void BuildCatalogs_IsCaseInsensitive()
    {
        var items = new List<PkgsInfo>
        {
            new PkgsInfo { Name = "App1", Catalogs = new List<string> { "Production" } },
            new PkgsInfo { Name = "App2", Catalogs = new List<string> { "production" } }
        };

        var catalogs = _builder.BuildCatalogs(items, silent: true);

        // Should merge into single catalog (case-insensitive dictionary)
        Assert.Equal(2, catalogs.Count); // All + production (merged)
    }

    [Fact]
    public void WriteCatalogs_CreatesCatalogFiles()
    {
        var catalogs = new Dictionary<string, List<PkgsInfo>>(StringComparer.OrdinalIgnoreCase)
        {
            ["production"] = new List<PkgsInfo>
            {
                new PkgsInfo { Name = "App1", Version = "1.0.0" }
            }
        };

        _builder.WriteCatalogs(_tempDir, catalogs, silent: true);

        var catalogPath = Path.Combine(_tempDir, "catalogs", "production.yaml");
        Assert.True(File.Exists(catalogPath));

        var content = File.ReadAllText(catalogPath);
        Assert.Contains("App1", content);
    }

    [Fact]
    public void WriteCatalogs_RemovesStaleCatalogs()
    {
        // Create a stale catalog
        var stalePath = Path.Combine(_tempDir, "catalogs", "stale.yaml");
        File.WriteAllText(stalePath, "items: []");

        // Write new catalogs without "stale"
        var catalogs = new Dictionary<string, List<PkgsInfo>>(StringComparer.OrdinalIgnoreCase)
        {
            ["current"] = new List<PkgsInfo>()
        };

        _builder.WriteCatalogs(_tempDir, catalogs, silent: false);

        Assert.False(File.Exists(stalePath));
        Assert.Single(_warnings); // Should warn about removal
    }

    [Fact]
    public void WriteCatalogs_LogsSuccessWhenNotSilent()
    {
        var catalogs = new Dictionary<string, List<PkgsInfo>>(StringComparer.OrdinalIgnoreCase)
        {
            ["test"] = new List<PkgsInfo>()
        };

        _builder.WriteCatalogs(_tempDir, catalogs, silent: false);

        Assert.Contains(_successes, s => s.Contains("test") && s.Contains("0 items"));
    }

    [Fact]
    public void Run_CompletesSuccessfully()
    {
        CreatePkgInfo("app.yaml", @"
name: TestApp
version: 1.0.0
catalogs:
  - production
");

        var result = _builder.Run(_tempDir, skipPayloadCheck: true, silent: true);

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(_tempDir, "catalogs", "All.yaml")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "catalogs", "production.yaml")));
    }

    [Fact]
    public void Run_ReportsPayloadWarnings()
    {
        CreatePkgInfo("app.yaml", @"
name: TestApp
version: 1.0.0
catalogs:
  - production
installer:
  location: missing/file.exe
");

        _builder.Run(_tempDir, skipPayloadCheck: false, silent: true);

        Assert.Contains(_warnings, w => w.Contains("missing installer"));
    }

    [Fact]
    public void Run_SkipsPayloadCheckWhenRequested()
    {
        CreatePkgInfo("app.yaml", @"
name: TestApp
version: 1.0.0
catalogs:
  - production
installer:
  location: missing/file.exe
");

        _builder.Run(_tempDir, skipPayloadCheck: true, silent: true);

        Assert.DoesNotContain(_warnings, w => w.Contains("missing installer"));
    }

    #region Loop Fingerprint

    private static PkgsInfo SamplePkg() => new()
    {
        Name = "LoopPkg",
        Version = "1.0.0",
        Catalogs = new List<string> { "Production" },
        Installer = new Installer
        {
            Location = "apps/LoopPkg-1.0.0.msi",
            Hash = "abc123",
            Type = "msi",
            ProductCode = "{11111111-1111-1111-1111-111111111111}",
            Switches = new List<string> { "/qn" }
        }
    };

    [Fact]
    public void StampLoopFingerprints_IsDeterministic()
    {
        var a = SamplePkg();
        var b = SamplePkg();

        _builder.StampLoopFingerprints(new List<PkgsInfo> { a });
        _builder.StampLoopFingerprints(new List<PkgsInfo> { b });

        Assert.False(string.IsNullOrEmpty(a.LoopFingerprint));
        Assert.Equal(a.LoopFingerprint, b.LoopFingerprint);
    }

    [Fact]
    public void StampLoopFingerprints_IsStableAcrossRestamping()
    {
        // The field is part of the serialized item, so it has to be excluded from its own
        // hash — otherwise every makecatalogs run produced a different value and cleared
        // loop suppression fleet-wide for every package, every time.
        var pkg = SamplePkg();

        _builder.StampLoopFingerprints(new List<PkgsInfo> { pkg });
        var first = pkg.LoopFingerprint;
        _builder.StampLoopFingerprints(new List<PkgsInfo> { pkg });

        Assert.Equal(first, pkg.LoopFingerprint);
    }

    [Theory]
    // The fields our real loop fixes touch. A hand-picked field list missed every one of
    // these; hashing the whole item is what makes "publish the fix" the central clear.
    [InlineData("product_code")]
    [InlineData("switches")]
    [InlineData("blocking_applications")]
    [InlineData("installer_timeout")]
    [InlineData("requires")]
    [InlineData("version")]
    [InlineData("postinstall_script")]
    [InlineData("installs")]
    public void StampLoopFingerprints_ChangesWhenAnyInstallBehaviorFieldChanges(string field)
    {
        var baseline = SamplePkg();
        _builder.StampLoopFingerprints(new List<PkgsInfo> { baseline });

        var edited = SamplePkg();
        switch (field)
        {
            case "product_code": edited.Installer!.ProductCode = "{22222222-2222-2222-2222-222222222222}"; break;
            case "switches": edited.Installer!.Switches = new List<string> { "/qn", "/norestart" }; break;
            case "blocking_applications": edited.BlockingApplications = new List<string> { "loop.exe" }; break;
            case "installer_timeout": edited.InstallerTimeout = 3600; break;
            case "requires": edited.Requires = new List<string> { "OtherPkg" }; break;
            case "version": edited.Version = "1.0.1"; break;
            case "postinstall_script": edited.PostinstallScript = "Write-Host fixed"; break;
            case "installs": edited.Installs = new List<InstallItem> { new() { Type = "file", Path = "C:/app.exe" } }; break;
        }

        _builder.StampLoopFingerprints(new List<PkgsInfo> { edited });

        Assert.NotEqual(baseline.LoopFingerprint, edited.LoopFingerprint);
    }

    [Fact]
    public void StampLoopFingerprints_IgnoresLineEndingStyle()
    {
        var lf = SamplePkg();
        lf.PostinstallScript = "line one\nline two\n";
        var crlf = SamplePkg();
        crlf.PostinstallScript = "line one\r\nline two\r\n";

        _builder.StampLoopFingerprints(new List<PkgsInfo> { lf });
        _builder.StampLoopFingerprints(new List<PkgsInfo> { crlf });

        Assert.Equal(lf.LoopFingerprint, crlf.LoopFingerprint);
    }

    [Fact]
    public void Run_WritesLoopFingerprintIntoCatalogs()
    {
        CreatePkgInfo("LoopPkg.yaml",
            "name: LoopPkg\n" +
            "version: 1.0.0\n" +
            "catalogs:\n" +
            "  - Production\n");

        var exitCode = _builder.Run(_tempDir, skipPayloadCheck: true, silent: true);

        Assert.Equal(0, exitCode);
        var catalog = File.ReadAllText(Path.Combine(_tempDir, "catalogs", "Production.yaml"));
        Assert.Contains("loop_fingerprint:", catalog);
    }

    #endregion

    [Fact]
    public void StampLoopFingerprints_KeepsScriptBlankLines_CollapsesDescription()
    {
        // A script can embed content verified byte-for-byte (a here-string checked
        // against a SHA-256), so its blank lines must reach the catalog unchanged.
        var pkg = new PkgsInfo
        {
            Name = "App1",
            Description = "one\n\n\n\ntwo",
            PreinstallScript = "$content = @'\r\nfirst\r\n\r\n\r\nsecond\r\n'@",
        };

        _builder.StampLoopFingerprints(new List<PkgsInfo> { pkg });

        Assert.Equal("one\n\ntwo", pkg.Description);
        Assert.Equal("$content = @'\nfirst\n\n\nsecond\n'@", pkg.PreinstallScript);
    }

    private Dictionary<object, object> ReadCatalogItem(string catalog, string name)
    {
        var yaml = File.ReadAllText(Path.Combine(_tempDir, "catalogs", catalog + ".yaml"));
        var root = Cimian.Core.Services.YamlUtils.Deserializer.Deserialize<Dictionary<object, object>>(yaml);
        return ((List<object>)root["items"])
            .Cast<Dictionary<object, object>>()
            .Single(i => (string)i["name"] == name);
    }

    [Fact]
    public void Run_CarriesKeysTheModelDoesNotDeclareIntoCatalogs()
    {
        CreatePkgInfo("Future-1.0.yaml",
            "name: Future\n" +
            "version: 1.0\n" +
            "catalogs:\n" +
            "  - Testing\n" +
            "future_flag: true\n" +
            "vendor_data:\n" +
            "  channel: beta\n" +
            "  rings:\n" +
            "    - pilot\n" +
            "    - broad\n");

        var exitCode = _builder.Run(_tempDir, skipPayloadCheck: true, silent: true);

        Assert.Equal(0, exitCode);
        var item = ReadCatalogItem("Testing", "Future");
        Assert.Equal("true", item["future_flag"]);
        var vendor = (Dictionary<object, object>)item["vendor_data"];
        Assert.Equal("beta", vendor["channel"]);
        Assert.Equal(new object[] { "pilot", "broad" }, (List<object>)vendor["rings"]);
    }

    [Fact]
    public void Run_CarriesKeysTheClientReadsButTheModelDoesNotDeclare()
    {
        // Keys managedsoftwareupdate reads that makecatalogs' model is missing: a
        // command uninstaller, a registry installs check, the installer_type alias and
        // the Munki-style installer_item_* keys the client falls back to.
        CreatePkgInfo("Legacy-1.0.yaml",
            "name: Legacy\n" +
            "version: 1.0\n" +
            "catalogs:\n" +
            "  - Testing\n" +
            "installer_type: exe\n" +
            "installer_item_location: apps/Legacy-1.0.exe\n" +
            "installer_item_hash: 0123abcd\n" +
            "installer_item_size: 2048\n" +
            "uninstaller:\n" +
            "  - type: ps1\n" +
            "    command: Remove-Item 'C:\\Program Files\\Legacy' -Recurse\n" +
            "installs:\n" +
            "  - type: registry\n" +
            "    key_path: HKLM\\SOFTWARE\\Legacy\n" +
            "    version: 1.0\n");

        _builder.Run(_tempDir, skipPayloadCheck: true, silent: true);

        var item = ReadCatalogItem("Testing", "Legacy");
        Assert.Equal("exe", item["installer_type"]);
        Assert.Equal("apps/Legacy-1.0.exe", item["installer_item_location"]);
        Assert.Equal("0123abcd", item["installer_item_hash"]);
        Assert.Equal("2048", item["installer_item_size"]);
        var uninstaller = (Dictionary<object, object>)((List<object>)item["uninstaller"]).Single();
        Assert.Equal("ps1", uninstaller["type"]);
        Assert.Equal("Remove-Item 'C:\\Program Files\\Legacy' -Recurse", uninstaller["command"]);
        var installs = (Dictionary<object, object>)((List<object>)item["installs"]).Single();
        Assert.Equal("registry", installs["type"]);
        Assert.Equal("HKLM\\SOFTWARE\\Legacy", installs["key_path"]);
    }

    [Fact]
    public void Run_LeavesNotesAndUnderscoreKeysOutOfCatalogs()
    {
        // Munki's makecatalogs drops admin notes and any key starting with "_"
        // (such as _metadata); they are for whoever edits the pkgsinfo, not for clients.
        CreatePkgInfo("Quiet-1.0.yaml",
            "name: Quiet\n" +
            "version: 1.0\n" +
            "catalogs:\n" +
            "  - Testing\n" +
            "notes: ask before upgrading\n" +
            "_metadata:\n" +
            "  created_by: someone\n");

        _builder.Run(_tempDir, skipPayloadCheck: true, silent: true);

        var item = ReadCatalogItem("Testing", "Quiet");
        Assert.False(item.ContainsKey("notes"));
        Assert.False(item.ContainsKey("_metadata"));
    }

    [Fact]
    public void Run_CarriesUnknownKeysBesideAMultilineScript()
    {
        CreatePkgInfo("Scripted-1.0.yaml",
            "name: Scripted\n" +
            "version: 1.0\n" +
            "catalogs:\n" +
            "  - Testing\n" +
            "postinstall_script: |\n" +
            "  Write-Host 'one'\n" +
            "\n" +
            "  Write-Host 'two'\n" +
            "future_script: |\n" +
            "  $x = 1\n" +
            "  $y = 2\n");

        _builder.Run(_tempDir, skipPayloadCheck: true, silent: true);

        var item = ReadCatalogItem("Testing", "Scripted");
        Assert.Equal("Write-Host 'one'\n\nWrite-Host 'two'\n", item["postinstall_script"]);
        Assert.Equal("$x = 1\n$y = 2\n", item["future_script"]);
    }

    [Fact]
    public void Run_CatalogIsUnchanged_WhenNoPkgsinfoHasUnknownKeys()
    {
        CreatePkgInfo("Plain-1.0.yaml",
            "name: Plain\n" +
            "version: 1.0\n" +
            "catalogs:\n" +
            "  - Testing\n" +
            "postinstall_script: |\n" +
            "  Write-Host 'one'\n" +
            "\n" +
            "  Write-Host 'two'\n" +
            "installs:\n" +
            "  - type: file\n" +
            "    path: C:\\Program Files\\Plain\\plain.exe\n");

        _builder.Run(_tempDir, skipPayloadCheck: true, silent: true);

        var items = _builder.ScanRepo(_tempDir);
        _builder.StampLoopFingerprints(items);
        var expected = Cimian.Core.Services.YamlUtils.SerializeCatalog(new CatalogFile { Items = items });
        Assert.Equal(expected, File.ReadAllText(Path.Combine(_tempDir, "catalogs", "Testing.yaml")));
    }

    [Fact]
    public void StampLoopFingerprints_ChangesWhenAnUnknownKeyChanges()
    {
        CreatePkgInfo("a/Future-1.0.yaml", "name: Future\nversion: 1.0\nfuture_flag: one\n");
        CreatePkgInfo("b/Future-1.0.yaml", "name: Future\nversion: 1.0\nfuture_flag: two\n");
        CreatePkgInfo("c/Future-1.0.yaml", "name: Future\nversion: 1.0\n");

        var items = _builder.ScanRepo(_tempDir).OrderBy(i => i.FilePath).ToList();
        _builder.StampLoopFingerprints(items);

        Assert.NotEqual(items[0].LoopFingerprint, items[1].LoopFingerprint);
        Assert.NotEqual(items[0].LoopFingerprint, items[2].LoopFingerprint);
    }
}

/// <summary>
/// Tests for PkgsInfo model
/// </summary>
public class PkgsInfoTests
{
    [Fact]
    public void PkgsInfo_DefaultsToEmptyLists()
    {
        var pkg = new PkgsInfo();

        Assert.NotNull(pkg.Catalogs);
        Assert.Empty(pkg.Catalogs);
    }

    [Fact]
    public void Installer_AllPropertiesNullable()
    {
        var installer = new Installer();

        Assert.Null(installer.Location);
        Assert.Null(installer.Hash);
        Assert.Null(installer.Type);
        Assert.Null(installer.Size);
    }
}
