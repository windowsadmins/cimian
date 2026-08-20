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

    #region Cached-file verification memo

    // Re-hashing a multi-GB package on every hourly run is what starved the
    // scheduled task of its 30-minute budget. Once a file has been verified,
    // an unchanged size and mtime stand in for the hash.

    private static string MarkerFor(string path) => path + ".verified";

    private string WriteCachedFile(string name, string content)
    {
        var path = Path.Combine(_testCacheDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task DownloadFileAsync_CacheHit_RecordsVerificationMarker()
    {
        var path = WriteCachedFile("cached.msi", "Hello, World!");
        var hash = DownloadService.CalculateSHA256(path);

        var result = await _service.DownloadFileAsync("https://test.example.com/unused", path, hash);

        Assert.True(result);
        Assert.True(File.Exists(MarkerFor(path)));

        var parts = File.ReadAllText(MarkerFor(path)).Split('|');
        var info = new FileInfo(path);
        Assert.Equal(hash, parts[0]);
        Assert.Equal(info.Length.ToString(), parts[1]);
        Assert.Equal(info.LastWriteTimeUtc.Ticks.ToString(), parts[2]);
    }

    [Fact]
    public async Task DownloadFileAsync_MarkerForDifferentHash_IsIgnoredAndRewritten()
    {
        // A new version in the catalog means a new expected hash; the old marker
        // must not be allowed to vouch for the file.
        var path = WriteCachedFile("stale-marker.msi", "Hello, World!");
        var hash = DownloadService.CalculateSHA256(path);
        var info = new FileInfo(path);
        File.WriteAllText(MarkerFor(path), $"{new string('a', 64)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");

        var result = await _service.DownloadFileAsync("https://test.example.com/unused", path, hash);

        Assert.True(result);
        Assert.StartsWith(hash, File.ReadAllText(MarkerFor(path)));
    }

    [Fact]
    public async Task DownloadFileAsync_MarkerWithStaleMtime_IsIgnoredAndRewritten()
    {
        var path = WriteCachedFile("touched.msi", "Hello, World!");
        var hash = DownloadService.CalculateSHA256(path);
        var info = new FileInfo(path);
        var staleTicks = info.LastWriteTimeUtc.AddDays(-1).Ticks;
        File.WriteAllText(MarkerFor(path), $"{hash}|{info.Length}|{staleTicks}");

        var result = await _service.DownloadFileAsync("https://test.example.com/unused", path, hash);

        Assert.True(result);
        Assert.EndsWith(new FileInfo(path).LastWriteTimeUtc.Ticks.ToString(), File.ReadAllText(MarkerFor(path)));
    }

    [Fact]
    public async Task DownloadFileAsync_MalformedMarker_FallsBackToHashing()
    {
        var path = WriteCachedFile("garbage-marker.msi", "Hello, World!");
        var hash = DownloadService.CalculateSHA256(path);
        File.WriteAllText(MarkerFor(path), "not a marker");

        var result = await _service.DownloadFileAsync("https://test.example.com/unused", path, hash);

        Assert.True(result);
        Assert.StartsWith(hash, File.ReadAllText(MarkerFor(path)));
    }

    [Fact]
    public void ValidateAndCleanCache_RemovesOrphanedVerificationMarkers()
    {
        var kept = WriteCachedFile("present.msi", "content");
        File.WriteAllText(MarkerFor(kept), "hash|7|123");
        var orphanMarker = MarkerFor(Path.Combine(_testCacheDir, "deleted.msi"));
        File.WriteAllText(orphanMarker, "hash|7|123");

        _service.ValidateAndCleanCache();

        Assert.True(File.Exists(MarkerFor(kept)));
        Assert.False(File.Exists(orphanMarker));
    }

    #endregion

    #region Hashing

    [Fact]
    public void CalculateSHA256_FileLargerThanBuffer_MatchesSingleShotHash()
    {
        // Hashing now streams through a 1MB buffer in TransformBlock chunks
        // rather than one ComputeHash(stream) call — guard the equivalence.
        var path = Path.Combine(_testCacheDir, "large.bin");
        var data = new byte[(3 * 1024 * 1024) + 7];
        new Random(1234).NextBytes(data);
        File.WriteAllBytes(path, data);

        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

        Assert.Equal(expected, DownloadService.CalculateSHA256(path));
    }

    #endregion

    #region Cache retention

    // CacheRetentionDays was a configuration field that nothing acted on: no code path
    // ever deleted a cached payload, so every installer a machine had downloaded stayed
    // on disk forever. These pin the behaviour the setting now has.

    private string WriteCacheFile(string relativePath, DateTime lastWrite, string content = "payload")
    {
        var full = Path.Combine(_testCacheDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTime(full, lastWrite);
        return full;
    }

    [Fact]
    public void ValidateAndCleanCache_RemovesPayloadsPastRetention()
    {
        var stale = WriteCacheFile(Path.Combine("design", "Suite-2025.1.pkg"), DateTime.Now.AddDays(-60));
        var current = WriteCacheFile(Path.Combine("design", "Suite-2026.8.pkg"), DateTime.Now.AddDays(-3));

        _service.ValidateAndCleanCache();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(current));
    }

    [Fact]
    public void ValidateAndCleanCache_RemovesTheVerificationMarkerWithItsPayload()
    {
        var stale = WriteCacheFile(Path.Combine("apps", "Tool-1.0.msi"), DateTime.Now.AddDays(-60));
        var marker = WriteCacheFile(Path.Combine("apps", "Tool-1.0.msi.verified"), DateTime.Now.AddDays(-60), "ok");

        _service.ValidateAndCleanCache();

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void ValidateAndCleanCache_KeepsAMarkerWhosePayloadIsStillCurrent()
    {
        // The marker is older than the window but the payload it vouches for is not.
        // Dropping it would force a pointless re-verification of a good download.
        var payload = WriteCacheFile(Path.Combine("apps", "Tool-2.0.msi"), DateTime.Now.AddDays(-3));
        var marker = WriteCacheFile(Path.Combine("apps", "Tool-2.0.msi.verified"), DateTime.Now.AddDays(-60), "ok");

        _service.ValidateAndCleanCache();

        Assert.True(File.Exists(payload));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void ValidateAndCleanCache_LeavesPartialDownloadsToTheirOwnRule()
    {
        // .downloading files are resumable and already have a 24-hour rule; retention
        // must not race it.
        var partial = WriteCacheFile("Big-1.0.pkg.downloading", DateTime.Now.AddHours(-1));

        _service.ValidateAndCleanCache();

        Assert.True(File.Exists(partial));
    }

    [Fact]
    public void ValidateAndCleanCache_RemovesCategoryDirectoriesLeftEmpty()
    {
        WriteCacheFile(Path.Combine("animation", "Renderer-1.0.exe"), DateTime.Now.AddDays(-60));

        _service.ValidateAndCleanCache();

        Assert.False(Directory.Exists(Path.Combine(_testCacheDir, "animation")));
        // The cache root is a known path other code expects; it stays.
        Assert.True(Directory.Exists(_testCacheDir));
    }

    [Fact]
    public void ValidateAndCleanCache_RetentionIsDisabledByANonPositiveSetting()
    {
        _testConfig.CacheRetentionDays = 0;
        var ancient = WriteCacheFile(Path.Combine("apps", "Tool-0.1.msi"), DateTime.Now.AddDays(-400));

        _service.ValidateAndCleanCache();

        Assert.True(File.Exists(ancient));
    }

    #endregion
}
