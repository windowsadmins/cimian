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
                Uninstaller = new Installer { Location = "missing/uninstaller.exe" }
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
