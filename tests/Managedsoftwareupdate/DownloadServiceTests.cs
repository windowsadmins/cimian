using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for DownloadService - package download with hash validation.
/// </summary>
public class DownloadServiceTests : IDisposable
{
    private readonly CimianConfig _testConfig;
    private readonly string _testCacheDir;
    private readonly DownloadService _service;

    public DownloadServiceTests()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), "CimianTests", "Cache", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testCacheDir);

        _testConfig = new CimianConfig
        {
            CachePath = _testCacheDir,
            SoftwareRepoURL = "https://test.example.com/repo"
        };

        _service = new DownloadService(_testConfig);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testCacheDir))
            {
                Directory.Delete(_testCacheDir, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    #region SHA256 Hash Calculation Tests

    [Fact]
    public void CalculateSHA256_EmptyFile_ReturnsKnownHash()
    {
        var emptyFile = Path.Combine(_testCacheDir, "empty.txt");
        File.WriteAllText(emptyFile, "");

        var hash = DownloadService.CalculateSHA256(emptyFile);

        // SHA256 of empty string is well-known
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    [Fact]
    public void CalculateSHA256_KnownContent_ReturnsExpectedHash()
    {
        var testFile = Path.Combine(_testCacheDir, "test.txt");
        File.WriteAllText(testFile, "Hello, World!");

        var hash = DownloadService.CalculateSHA256(testFile);

        // SHA256 of "Hello, World!" (without newline)
        Assert.Equal("dffd6021bb2bd5b0af676290809ec3a53191dd81c7f70a4b28688a362182986f", hash);
    }

    [Fact]
    public void CalculateSHA256_NonExistentFile_ThrowsException()
    {
        var nonExistent = Path.Combine(_testCacheDir, "nonexistent.txt");

        Assert.Throws<FileNotFoundException>(() => DownloadService.CalculateSHA256(nonExistent));
    }

    [Fact]
    public void CalculateSHA256_BinaryContent_Works()
    {
        var binaryFile = Path.Combine(_testCacheDir, "binary.bin");
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD };
        File.WriteAllBytes(binaryFile, binaryData);

        var hash = DownloadService.CalculateSHA256(binaryFile);

        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length); // SHA256 produces 64 hex chars
        Assert.Matches("^[a-f0-9]+$", hash);
    }

    [Fact]
    public void CalculateSHA256_ReturnsLowercaseHex()
    {
        var testFile = Path.Combine(_testCacheDir, "lowercase.txt");
        File.WriteAllText(testFile, "test");

        var hash = DownloadService.CalculateSHA256(testFile);

        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    #endregion

    #region BuildFullUrl Tests

    [Fact]
    public void BuildFullUrl_RelativePath_PrefixesWithRepoUrl()
    {
        var url = _service.BuildFullUrl("apps/myapp/installer.msi");

        Assert.StartsWith("https://test.example.com/repo/pkgs/", url);
        Assert.Contains("apps/myapp/installer.msi", url);
    }

    [Fact]
    public void BuildFullUrl_AbsoluteHttpUrl_ReturnsAsIs()
    {
        var absoluteUrl = "https://other.example.com/file.msi";

        var url = _service.BuildFullUrl(absoluteUrl);

        Assert.Equal(absoluteUrl, url);
    }

    [Fact]
    public void BuildFullUrl_AbsoluteHttpsUrl_ReturnsAsIs()
    {
        var absoluteUrl = "http://insecure.example.com/file.msi";

        var url = _service.BuildFullUrl(absoluteUrl);

        Assert.Equal(absoluteUrl, url);
    }

    [Fact]
    public void BuildFullUrl_LeadingSlash_HandlesCorrectly()
    {
        var url = _service.BuildFullUrl("/apps/myapp/installer.msi");

        Assert.Contains("/pkgs/apps/myapp/installer.msi", url);
        Assert.DoesNotContain("//apps", url);
    }

    [Fact]
    public void BuildFullUrl_BackslashPath_ConvertedToForwardSlash()
    {
        var url = _service.BuildFullUrl(@"apps\myapp\installer.msi");

        Assert.Contains("/apps/myapp/installer.msi", url);
        Assert.DoesNotContain(@"\", url);
    }

    #endregion

    #region GetCachePath Tests

    [Fact]
    public void GetCachePath_WithCategory_IncludesCategory()
    {
        var item = new CatalogItem
        {
            Name = "TestApp",
            Version = "1.0.0",
            Category = "Utilities",
            Installer = new InstallerInfo { Location = "apps/testapp/setup.msi" }
        };

        var cachePath = _service.GetCachePath(item);

        Assert.Contains("utilities", cachePath.ToLowerInvariant());
        Assert.EndsWith("setup.msi", cachePath);
    }

    [Fact]
    public void GetCachePath_WithoutCategory_JustFilename()
    {
        var item = new CatalogItem
        {
            Name = "TestApp",
            Version = "1.0.0",
            Installer = new InstallerInfo { Location = "apps/testapp/setup.msi" }
        };

        var cachePath = _service.GetCachePath(item);

        Assert.StartsWith(_testCacheDir, cachePath);
        Assert.EndsWith("setup.msi", cachePath);
    }

    [Fact]
    public void GetCachePath_CategoryWithSpaces_ReplacedWithUnderscores()
    {
        var item = new CatalogItem
        {
            Name = "TestApp",
            Category = "My Cool Category",
            Installer = new InstallerInfo { Location = "setup.msi" }
        };

        var cachePath = _service.GetCachePath(item);

        Assert.Contains("my_cool_category", cachePath.ToLowerInvariant());
        Assert.DoesNotContain(" ", cachePath);
    }

    #endregion

    #region GetCacheStatus Tests

    [Fact]
    public void GetCacheStatus_EmptyCache_ReturnsZeros()
    {
        var emptyDir = Path.Combine(_testCacheDir, "empty-cache");
        Directory.CreateDirectory(emptyDir);
        var config = new CimianConfig { CachePath = emptyDir };
        var service = new DownloadService(config);

        var (fileCount, totalSize, corruptCount) = service.GetCacheStatus();

        Assert.Equal(0, fileCount);
        Assert.Equal(0, totalSize);
        Assert.Equal(0, corruptCount);
    }

    [Fact]
    public void GetCacheStatus_NonExistentCache_ReturnsZeros()
    {
        var config = new CimianConfig { CachePath = @"C:\NonExistent\Path\12345" };
        var service = new DownloadService(config);

        var (fileCount, totalSize, corruptCount) = service.GetCacheStatus();

        Assert.Equal(0, fileCount);
        Assert.Equal(0, totalSize);
        Assert.Equal(0, corruptCount);
    }

    [Fact]
    public void GetCacheStatus_WithFiles_ReturnsCounts()
    {
        File.WriteAllText(Path.Combine(_testCacheDir, "file1.msi"), "content1");
        File.WriteAllText(Path.Combine(_testCacheDir, "file2.exe"), "content22");

        var (fileCount, totalSize, corruptCount) = _service.GetCacheStatus();

        Assert.Equal(2, fileCount);
        Assert.True(totalSize > 0);
        Assert.Equal(0, corruptCount);
    }

    [Fact]
    public void GetCacheStatus_WithCorruptFiles_CountsCorrectly()
    {
        File.WriteAllText(Path.Combine(_testCacheDir, "good.msi"), "content");
        File.WriteAllText(Path.Combine(_testCacheDir, "corrupt.exe"), ""); // 0 bytes = corrupt

        var (fileCount, totalSize, corruptCount) = _service.GetCacheStatus();

        Assert.Equal(2, fileCount);
        Assert.Equal(1, corruptCount);
    }

    #endregion

    #region ValidateAndCleanCache Tests

    [Fact]
    public void ValidateAndCleanCache_RemovesZeroByteFiles()
    {
        var goodFile = Path.Combine(_testCacheDir, "good.msi");
        var corruptFile = Path.Combine(_testCacheDir, "corrupt.msi");
        
        File.WriteAllText(goodFile, "valid content");
        File.WriteAllText(corruptFile, ""); // 0 bytes

        _service.ValidateAndCleanCache();

        Assert.True(File.Exists(goodFile));
        Assert.False(File.Exists(corruptFile));
    }

    [Fact]
    public void ValidateAndCleanCache_NonExistentCache_NoException()
    {
        var config = new CimianConfig { CachePath = @"C:\NonExistent\Path\12345" };
        var service = new DownloadService(config);

        // Should not throw
        service.ValidateAndCleanCache();
    }

    #endregion

    #region ClearCacheSelective Tests

    [Fact]
    public void ClearCacheSelective_RemovesMatchingFiles()
    {
        File.WriteAllText(Path.Combine(_testCacheDir, "app1-1.0.0.msi"), "content");
        File.WriteAllText(Path.Combine(_testCacheDir, "app2-1.0.0.msi"), "content");
        File.WriteAllText(Path.Combine(_testCacheDir, "other-1.0.0.msi"), "content");

        _service.ClearCacheSelective(new HashSet<string> { "app1", "app2" });

        Assert.False(File.Exists(Path.Combine(_testCacheDir, "app1-1.0.0.msi")));
        Assert.False(File.Exists(Path.Combine(_testCacheDir, "app2-1.0.0.msi")));
        Assert.True(File.Exists(Path.Combine(_testCacheDir, "other-1.0.0.msi")));
    }

    [Fact]
    public void ClearCacheSelective_EmptySet_KeepsAllFiles()
    {
        File.WriteAllText(Path.Combine(_testCacheDir, "app1.msi"), "content");
        File.WriteAllText(Path.Combine(_testCacheDir, "app2.msi"), "content");

        _service.ClearCacheSelective(new HashSet<string>());

        Assert.True(File.Exists(Path.Combine(_testCacheDir, "app1.msi")));
        Assert.True(File.Exists(Path.Combine(_testCacheDir, "app2.msi")));
    }

    #endregion
}
