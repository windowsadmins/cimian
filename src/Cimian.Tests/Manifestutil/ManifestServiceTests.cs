using Xunit;
using Cimian.CLI.Manifestutil.Services;
using Cimian.CLI.Manifestutil.Models;

namespace Cimian.Tests.Manifestutil;

/// <summary>
/// Tests for ManifestService
/// Migrated from Go: cmd/manifestutil/main.go
/// </summary>
public class ManifestServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ManifestService _service;

    public ManifestServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"manifestutil_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _service = new ManifestService();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void ListManifests_ReturnsYamlFiles()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "production.yaml"), "name: production");
        File.WriteAllText(Path.Combine(_tempDir, "testing.yaml"), "name: testing");
        File.WriteAllText(Path.Combine(_tempDir, "readme.txt"), "not a manifest");

        // Act
        var manifests = _service.ListManifests(_tempDir).ToList();

        // Assert
        Assert.Equal(2, manifests.Count);
        Assert.Contains("production.yaml", manifests);
        Assert.Contains("testing.yaml", manifests);
        Assert.DoesNotContain("readme.txt", manifests);
    }

    [Fact]
    public void ListManifests_ReturnsEmptyForEmptyDirectory()
    {
        var manifests = _service.ListManifests(_tempDir).ToList();
        Assert.Empty(manifests);
    }

    [Fact]
    public void ListManifests_ThrowsForNonExistentDirectory()
    {
        var nonExistentDir = Path.Combine(_tempDir, "nonexistent");
        Assert.Throws<DirectoryNotFoundException>(() => _service.ListManifests(nonExistentDir).ToList());
    }

    [Fact]
    public void CreateNewManifest_CreatesEmptyManifest()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDir, "new_manifest.yaml");

        // Act
        _service.CreateNewManifest(manifestPath, "new_manifest");

        // Assert
        Assert.True(File.Exists(manifestPath));
        var manifest = _service.GetManifest(manifestPath);
        Assert.Equal("new_manifest", manifest.Name);
        Assert.Null(manifest.ManagedInstalls);
        Assert.Null(manifest.ManagedUninstalls);
    }

    [Fact]
    public void GetManifest_LoadsManifestCorrectly()
    {
        // Arrange
        var yaml = @"
name: test_manifest
managed_installs:
  - package1
  - package2
managed_uninstalls:
  - old_package
catalogs:
  - production
";
        var manifestPath = Path.Combine(_tempDir, "test.yaml");
        File.WriteAllText(manifestPath, yaml);

        // Act
        var manifest = _service.GetManifest(manifestPath);

        // Assert
        Assert.Equal("test_manifest", manifest.Name);
        Assert.NotNull(manifest.ManagedInstalls);
        Assert.Equal(2, manifest.ManagedInstalls.Count);
        Assert.Contains("package1", manifest.ManagedInstalls);
        Assert.Contains("package2", manifest.ManagedInstalls);
        Assert.NotNull(manifest.ManagedUninstalls);
        Assert.Single(manifest.ManagedUninstalls);
        Assert.Contains("old_package", manifest.ManagedUninstalls);
        Assert.NotNull(manifest.Catalogs);
        Assert.Single(manifest.Catalogs);
    }

    [Fact]
    public void GetManifest_ThrowsForNonExistentFile()
    {
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent.yaml");
        Assert.Throws<FileNotFoundException>(() => _service.GetManifest(nonExistentPath));
    }

    [Fact]
    public void GetManifest_NormalizesIncludedManifestPaths()
    {
        // Arrange
        var yaml = @"
name: test
included_manifests:
  - subdir\nested.yaml
  - other\\path.yaml
";
        var manifestPath = Path.Combine(_tempDir, "test.yaml");
        File.WriteAllText(manifestPath, yaml);

        // Act
        var manifest = _service.GetManifest(manifestPath);

        // Assert
        Assert.NotNull(manifest.IncludedManifests);
        Assert.All(manifest.IncludedManifests, path => Assert.DoesNotContain("\\", path));
        Assert.Contains("subdir/nested.yaml", manifest.IncludedManifests);
    }

    [Fact]
    public void SaveManifest_PreservesData()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "save_test",
            ManagedInstalls = new List<string> { "app1", "app2" },
            Catalogs = new List<string> { "production" }
        };
        var manifestPath = Path.Combine(_tempDir, "save_test.yaml");

        // Act
        _service.SaveManifest(manifestPath, manifest);
        var loaded = _service.GetManifest(manifestPath);

        // Assert
        Assert.Equal("save_test", loaded.Name);
        Assert.NotNull(loaded.ManagedInstalls);
        Assert.Equal(2, loaded.ManagedInstalls.Count);
        Assert.Contains("app1", loaded.ManagedInstalls);
        Assert.NotNull(loaded.Catalogs);
        Assert.Single(loaded.Catalogs);
    }

    [Fact]
    public void SaveManifest_OmitsNullAndEmptyCollections()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "minimal",
            ManagedInstalls = new List<string> { "app1" }
        };
        var manifestPath = Path.Combine(_tempDir, "minimal.yaml");

        // Act
        _service.SaveManifest(manifestPath, manifest);
        var content = File.ReadAllText(manifestPath);

        // Assert
        Assert.Contains("name:", content);
        Assert.Contains("managed_installs:", content);
        Assert.DoesNotContain("managed_uninstalls:", content);
        Assert.DoesNotContain("optional_installs:", content);
    }

    [Fact]
    public void AddPackageToManifest_AddsToManagedInstalls()
    {
        // Arrange
        var manifest = new PackageManifest { Name = "test" };

        // Act
        _service.AddPackageToManifest(manifest, "new_package", ManifestSection.ManagedInstalls);

        // Assert
        Assert.NotNull(manifest.ManagedInstalls);
        Assert.Single(manifest.ManagedInstalls);
        Assert.Contains("new_package", manifest.ManagedInstalls);
    }

    [Fact]
    public void AddPackageToManifest_AddsToManagedUninstalls()
    {
        var manifest = new PackageManifest { Name = "test" };

        _service.AddPackageToManifest(manifest, "package", ManifestSection.ManagedUninstalls);

        Assert.NotNull(manifest.ManagedUninstalls);
        Assert.Contains("package", manifest.ManagedUninstalls);
    }

    [Fact]
    public void AddPackageToManifest_AddsToManagedUpdates()
    {
        var manifest = new PackageManifest { Name = "test" };

        _service.AddPackageToManifest(manifest, "package", ManifestSection.ManagedUpdates);

        Assert.NotNull(manifest.ManagedUpdates);
        Assert.Contains("package", manifest.ManagedUpdates);
    }

    [Fact]
    public void AddPackageToManifest_AddsToOptionalInstalls()
    {
        var manifest = new PackageManifest { Name = "test" };

        _service.AddPackageToManifest(manifest, "package", ManifestSection.OptionalInstalls);

        Assert.NotNull(manifest.OptionalInstalls);
        Assert.Contains("package", manifest.OptionalInstalls);
    }

    [Fact]
    public void AddPackageToManifest_DoesNotAddDuplicate()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test",
            ManagedInstalls = new List<string> { "existing" }
        };

        // Act
        _service.AddPackageToManifest(manifest, "existing", ManifestSection.ManagedInstalls);
        _service.AddPackageToManifest(manifest, "EXISTING", ManifestSection.ManagedInstalls); // Case insensitive

        // Assert
        Assert.Single(manifest.ManagedInstalls);
    }

    [Fact]
    public void RemovePackageFromManifest_RemovesPackage()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test",
            ManagedInstalls = new List<string> { "package1", "package2", "package3" }
        };

        // Act
        var removed = _service.RemovePackageFromManifest(manifest, "package2", ManifestSection.ManagedInstalls);

        // Assert
        Assert.True(removed);
        Assert.Equal(2, manifest.ManagedInstalls.Count);
        Assert.DoesNotContain("package2", manifest.ManagedInstalls);
    }

    [Fact]
    public void RemovePackageFromManifest_ReturnsFalseForNonExistent()
    {
        var manifest = new PackageManifest
        {
            Name = "test",
            ManagedInstalls = new List<string> { "package1" }
        };

        var removed = _service.RemovePackageFromManifest(manifest, "nonexistent", ManifestSection.ManagedInstalls);

        Assert.False(removed);
        Assert.Single(manifest.ManagedInstalls);
    }

    [Fact]
    public void RemovePackageFromManifest_IsCaseInsensitive()
    {
        var manifest = new PackageManifest
        {
            Name = "test",
            ManagedInstalls = new List<string> { "PackageName" }
        };

        var removed = _service.RemovePackageFromManifest(manifest, "packagename", ManifestSection.ManagedInstalls);

        Assert.True(removed);
        Assert.Empty(manifest.ManagedInstalls);
    }

    [Fact]
    public void RemovePackageFromManifest_ReturnsFalseForNullSection()
    {
        var manifest = new PackageManifest { Name = "test" };

        var removed = _service.RemovePackageFromManifest(manifest, "package", ManifestSection.ManagedInstalls);

        Assert.False(removed);
    }

    [Fact]
    public void LoadConfig_LoadsConfigCorrectly()
    {
        // Arrange
        var yaml = @"
repo_path: C:\Cimian\Repo
managed_installs_dir: C:\ProgramData\ManagedInstalls
";
        var configPath = Path.Combine(_tempDir, "config.yaml");
        File.WriteAllText(configPath, yaml);

        // Act
        var config = _service.LoadConfig(configPath);

        // Assert
        Assert.Equal(@"C:\Cimian\Repo", config.RepoPath);
        Assert.Equal(@"C:\ProgramData\ManagedInstalls", config.ManagedInstallsDir);
    }

    [Fact]
    public void LoadConfig_ThrowsForNonExistentFile()
    {
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent.yaml");
        Assert.Throws<FileNotFoundException>(() => _service.LoadConfig(nonExistentPath));
    }
}

/// <summary>
/// Tests for ManifestSection parsing
/// </summary>
public class ManifestSectionExtensionsTests
{
    [Theory]
    [InlineData("managed_installs", ManifestSection.ManagedInstalls)]
    [InlineData("managed_uninstalls", ManifestSection.ManagedUninstalls)]
    [InlineData("managed_updates", ManifestSection.ManagedUpdates)]
    [InlineData("optional_installs", ManifestSection.OptionalInstalls)]
    [InlineData("MANAGED_INSTALLS", ManifestSection.ManagedInstalls)]
    public void Parse_ParsesSectionCorrectly(string input, ManifestSection expected)
    {
        var result = ManifestSectionExtensions.Parse(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("managed")]
    public void Parse_ThrowsForInvalidSection(string input)
    {
        Assert.Throws<ArgumentException>(() => ManifestSectionExtensions.Parse(input));
    }

    [Theory]
    [InlineData(ManifestSection.ManagedInstalls, "managed_installs")]
    [InlineData(ManifestSection.ManagedUninstalls, "managed_uninstalls")]
    [InlineData(ManifestSection.ManagedUpdates, "managed_updates")]
    [InlineData(ManifestSection.OptionalInstalls, "optional_installs")]
    public void ToYamlName_ReturnsCorrectName(ManifestSection section, string expected)
    {
        var result = section.ToYamlName();
        Assert.Equal(expected, result);
    }
}
