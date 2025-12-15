using Xunit;
using Cimian.CLI.Manifestutil.Services;

namespace Cimian.Tests.Manifestutil;

/// <summary>
/// Tests for SelfServiceManifestService
/// Migrated from Go: pkg/selfservice/selfservice.go
/// </summary>
public class SelfServiceManifestServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _manifestPath;
    private readonly SelfServiceManifestService _service;

    public SelfServiceManifestServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"selfservice_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _manifestPath = Path.Combine(_tempDir, "SelfServeManifest.yaml");
        _service = new SelfServiceManifestService(_manifestPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void Load_ReturnsEmptyManifestWhenFileDoesNotExist()
    {
        var manifest = _service.Load();

        Assert.Equal("SelfServeManifest", manifest.Name);
        Assert.NotNull(manifest.ManagedInstalls);
        Assert.Empty(manifest.ManagedInstalls);
        Assert.NotNull(manifest.ManagedUninstalls);
        Assert.Empty(manifest.ManagedUninstalls);
        Assert.NotNull(manifest.OptionalInstalls);
        Assert.Empty(manifest.OptionalInstalls);
    }

    [Fact]
    public void Load_LoadsExistingManifest()
    {
        // Arrange
        var yaml = @"
name: SelfServeManifest
managed_installs:
  - package1
  - package2
optional_installs:
  - optional1
";
        File.WriteAllText(_manifestPath, yaml);

        // Act
        var manifest = _service.Load();

        // Assert
        Assert.Equal("SelfServeManifest", manifest.Name);
        Assert.Equal(2, manifest.ManagedInstalls.Count);
        Assert.Contains("package1", manifest.ManagedInstalls);
        Assert.Contains("package2", manifest.ManagedInstalls);
        Assert.Single(manifest.OptionalInstalls);
    }

    [Fact]
    public void Load_SetsDefaultNameIfEmpty()
    {
        // Arrange
        var yaml = @"
managed_installs:
  - package1
";
        File.WriteAllText(_manifestPath, yaml);

        // Act
        var manifest = _service.Load();

        // Assert
        Assert.Equal("SelfServeManifest", manifest.Name);
    }

    [Fact]
    public void Save_CreatesDirectoryAndFile()
    {
        // Arrange
        var nestedPath = Path.Combine(_tempDir, "nested", "dir", "manifest.yaml");
        var service = new SelfServiceManifestService(nestedPath);
        var manifest = service.Load();
        manifest.ManagedInstalls.Add("test_package");

        // Act
        service.Save(manifest);

        // Assert
        Assert.True(File.Exists(nestedPath));
        var content = File.ReadAllText(nestedPath);
        Assert.Contains("test_package", content);
    }

    [Fact]
    public void AddToInstalls_AddsNewPackage()
    {
        // Act
        var added = _service.AddToInstalls("new_package");

        // Assert
        Assert.True(added);
        var manifest = _service.Load();
        Assert.Contains("new_package", manifest.ManagedInstalls);
    }

    [Fact]
    public void AddToInstalls_ReturnsFalseForDuplicate()
    {
        // Arrange
        _service.AddToInstalls("package1");

        // Act
        var addedAgain = _service.AddToInstalls("package1");

        // Assert
        Assert.False(addedAgain);
        var manifest = _service.Load();
        Assert.Single(manifest.ManagedInstalls);
    }

    [Fact]
    public void AddToInstalls_IsCaseInsensitive()
    {
        // Arrange
        _service.AddToInstalls("PackageName");

        // Act
        var addedAgain = _service.AddToInstalls("packagename");

        // Assert
        Assert.False(addedAgain);
    }

    [Fact]
    public void RemoveFromInstalls_RemovesExistingPackage()
    {
        // Arrange
        _service.AddToInstalls("package_to_remove");

        // Act
        var removed = _service.RemoveFromInstalls("package_to_remove");

        // Assert
        Assert.True(removed);
        var manifest = _service.Load();
        Assert.Empty(manifest.ManagedInstalls);
    }

    [Fact]
    public void RemoveFromInstalls_ReturnsFalseForNonExistent()
    {
        var removed = _service.RemoveFromInstalls("nonexistent");
        Assert.False(removed);
    }

    [Fact]
    public void RemoveFromInstalls_IsCaseInsensitive()
    {
        // Arrange
        _service.AddToInstalls("PackageName");

        // Act
        var removed = _service.RemoveFromInstalls("packagename");

        // Assert
        Assert.True(removed);
        var manifest = _service.Load();
        Assert.Empty(manifest.ManagedInstalls);
    }

    [Fact]
    public void RemoveFromInstalls_RemovesFromOptionalInstallsToo()
    {
        // Arrange
        var yaml = @"
name: SelfServeManifest
optional_installs:
  - optional_package
";
        File.WriteAllText(_manifestPath, yaml);

        // Act
        var removed = _service.RemoveFromInstalls("optional_package");

        // Assert
        Assert.True(removed);
        var manifest = _service.Load();
        Assert.Empty(manifest.OptionalInstalls);
    }

    [Fact]
    public void MultipleOperations_MaintainState()
    {
        // Add several packages
        _service.AddToInstalls("app1");
        _service.AddToInstalls("app2");
        _service.AddToInstalls("app3");

        // Remove one
        _service.RemoveFromInstalls("app2");

        // Verify final state
        var manifest = _service.Load();
        Assert.Equal(2, manifest.ManagedInstalls.Count);
        Assert.Contains("app1", manifest.ManagedInstalls);
        Assert.Contains("app3", manifest.ManagedInstalls);
        Assert.DoesNotContain("app2", manifest.ManagedInstalls);
    }
}
