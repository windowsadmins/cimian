using System.IO.Compression;
using Cimian.CLI.Cimipkg.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cimian.Tests.Cimipkg;

/// <summary>
/// Tests for the ZipArchiveHelper service.
/// </summary>
public class ZipArchiveHelperTests
{
    private readonly ZipArchiveHelper _helper;

    public ZipArchiveHelperTests()
    {
        var logger = new Mock<ILogger<ZipArchiveHelper>>();
        _helper = new ZipArchiveHelper(logger.Object);
    }

    #region CreateArchive Tests

    [Fact]
    public void CreateArchive_SingleFile_CreatesValidZip()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "test.txt"), "Hello World");

            _helper.CreateArchive(sourceDir, zipPath);

            Assert.True(File.Exists(zipPath));
            
            using var archive = ZipFile.OpenRead(zipPath);
            Assert.Single(archive.Entries);
            Assert.Equal("test.txt", archive.Entries[0].FullName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreateArchive_NestedDirectories_PreservesStructure()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(Path.Combine(sourceDir, "subdir"));

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "root.txt"), "root");
            File.WriteAllText(Path.Combine(sourceDir, "subdir", "nested.txt"), "nested");

            _helper.CreateArchive(sourceDir, zipPath);

            using var archive = ZipFile.OpenRead(zipPath);
            var fileEntries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            Assert.Equal(2, fileEntries.Count);
            Assert.Contains(fileEntries, e => e.FullName == "root.txt");
            Assert.Contains(fileEntries, e => e.FullName == "subdir/nested.txt");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreateArchive_EmptyDirectory_CreatesEmptyZip()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        try
        {
            _helper.CreateArchive(sourceDir, zipPath);

            Assert.True(File.Exists(zipPath));
            using var archive = ZipFile.OpenRead(zipPath);
            Assert.Empty(archive.Entries);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreateArchive_NonexistentSource_ThrowsException()
    {
        var tempDir = CreateTempDirectory();

        try
        {
            Assert.Throws<DirectoryNotFoundException>(() =>
                _helper.CreateArchive("/nonexistent/path", Path.Combine(tempDir, "test.zip")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreateArchive_OverwritesExistingFile()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "file1.txt"), "first");
            _helper.CreateArchive(sourceDir, zipPath);

            File.WriteAllText(Path.Combine(sourceDir, "file2.txt"), "second");
            _helper.CreateArchive(sourceDir, zipPath);

            using var archive = ZipFile.OpenRead(zipPath);
            Assert.Equal(2, archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Count());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region ExtractArchive Tests

    [Fact]
    public void ExtractArchive_ValidZip_ExtractsAllFiles()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        var destDir = Path.Combine(tempDir, "dest");
        Directory.CreateDirectory(sourceDir);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "test.txt"), "Hello");
            _helper.CreateArchive(sourceDir, zipPath);

            _helper.ExtractArchive(zipPath, destDir);

            Assert.True(File.Exists(Path.Combine(destDir, "test.txt")));
            Assert.Equal("Hello", File.ReadAllText(Path.Combine(destDir, "test.txt")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractArchive_NonexistentZip_ThrowsException()
    {
        var tempDir = CreateTempDirectory();

        try
        {
            Assert.Throws<FileNotFoundException>(() =>
                _helper.ExtractArchive("/nonexistent.zip", tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region ExtractFile Tests

    [Fact]
    public void ExtractFile_ExistingEntry_ExtractsSuccessfully()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "target.txt"), "Target Content");
            File.WriteAllText(Path.Combine(sourceDir, "other.txt"), "Other Content");
            _helper.CreateArchive(sourceDir, zipPath);

            var destPath = Path.Combine(tempDir, "extracted.txt");
            var result = _helper.ExtractFile(zipPath, "target.txt", destPath);

            Assert.True(result);
            Assert.True(File.Exists(destPath));
            Assert.Equal("Target Content", File.ReadAllText(destPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractFile_NonexistentEntry_ReturnsFalse()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "existing.txt"), "Content");
            _helper.CreateArchive(sourceDir, zipPath);

            var result = _helper.ExtractFile(zipPath, "nonexistent.txt", Path.Combine(tempDir, "out.txt"));

            Assert.False(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region UpdateFile Tests

    [Fact]
    public void UpdateFile_ExistingEntry_UpdatesContent()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "target.txt"), "Original");
            _helper.CreateArchive(sourceDir, zipPath);

            var newContentPath = Path.Combine(tempDir, "newcontent.txt");
            File.WriteAllText(newContentPath, "Updated");

            _helper.UpdateFile(zipPath, "target.txt", newContentPath);

            // Verify update
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("target.txt");
            Assert.NotNull(entry);

            using var reader = new StreamReader(entry!.Open());
            Assert.Equal("Updated", reader.ReadToEnd());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void UpdateFile_NewEntry_AddsToArchive()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "existing.txt"), "Existing");
            _helper.CreateArchive(sourceDir, zipPath);

            var newFilePath = Path.Combine(tempDir, "new.txt");
            File.WriteAllText(newFilePath, "New Content");

            _helper.UpdateFile(zipPath, "new.txt", newFilePath);

            using var archive = ZipFile.OpenRead(zipPath);
            Assert.Equal(2, archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Count());
            Assert.NotNull(archive.GetEntry("new.txt"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region CalculateContentHash Tests

    [Fact]
    public void CalculateContentHash_SameContent_SameHash()
    {
        var tempDir = CreateTempDirectory();
        var zipPath1 = Path.Combine(tempDir, "test1.zip");
        var zipPath2 = Path.Combine(tempDir, "test2.zip");
        var sourceDir1 = Path.Combine(tempDir, "source1");
        var sourceDir2 = Path.Combine(tempDir, "source2");
        Directory.CreateDirectory(sourceDir1);
        Directory.CreateDirectory(sourceDir2);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir1, "file.txt"), "Same Content");
            File.WriteAllText(Path.Combine(sourceDir2, "file.txt"), "Same Content");

            _helper.CreateArchive(sourceDir1, zipPath1);
            _helper.CreateArchive(sourceDir2, zipPath2);

            var hash1 = _helper.CalculateContentHash(zipPath1);
            var hash2 = _helper.CalculateContentHash(zipPath2);

            Assert.Equal(hash1, hash2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CalculateContentHash_DifferentContent_DifferentHash()
    {
        var tempDir = CreateTempDirectory();
        var zipPath1 = Path.Combine(tempDir, "test1.zip");
        var zipPath2 = Path.Combine(tempDir, "test2.zip");
        var sourceDir1 = Path.Combine(tempDir, "source1");
        var sourceDir2 = Path.Combine(tempDir, "source2");
        Directory.CreateDirectory(sourceDir1);
        Directory.CreateDirectory(sourceDir2);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir1, "file.txt"), "Content One");
            File.WriteAllText(Path.Combine(sourceDir2, "file.txt"), "Content Two");

            _helper.CreateArchive(sourceDir1, zipPath1);
            _helper.CreateArchive(sourceDir2, zipPath2);

            var hash1 = _helper.CalculateContentHash(zipPath1);
            var hash2 = _helper.CalculateContentHash(zipPath2);

            Assert.NotEqual(hash1, hash2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CalculateContentHash_ExcludesBuildInfoYaml()
    {
        var tempDir = CreateTempDirectory();
        var zipPath1 = Path.Combine(tempDir, "test1.zip");
        var zipPath2 = Path.Combine(tempDir, "test2.zip");
        var sourceDir1 = Path.Combine(tempDir, "source1");
        var sourceDir2 = Path.Combine(tempDir, "source2");
        Directory.CreateDirectory(sourceDir1);
        Directory.CreateDirectory(sourceDir2);

        try
        {
            // Same content file, different build-info.yaml
            File.WriteAllText(Path.Combine(sourceDir1, "payload.txt"), "Same");
            File.WriteAllText(Path.Combine(sourceDir1, "build-info.yaml"), "version: 1.0.0");

            File.WriteAllText(Path.Combine(sourceDir2, "payload.txt"), "Same");
            File.WriteAllText(Path.Combine(sourceDir2, "build-info.yaml"), "version: 2.0.0");

            _helper.CreateArchive(sourceDir1, zipPath1);
            _helper.CreateArchive(sourceDir2, zipPath2);

            var hash1 = _helper.CalculateContentHash(zipPath1);
            var hash2 = _helper.CalculateContentHash(zipPath2);

            // Hashes should be equal because build-info.yaml is excluded
            Assert.Equal(hash1, hash2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region ListEntries Tests

    [Fact]
    public void ListEntries_ReturnsAllEntries()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(Path.Combine(sourceDir, "subdir"));

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "root.txt"), "root content");
            File.WriteAllText(Path.Combine(sourceDir, "subdir", "nested.txt"), "nested content");

            _helper.CreateArchive(sourceDir, zipPath);

            var entries = _helper.ListEntries(zipPath);

            Assert.True(entries.Count >= 2);
            Assert.Contains(entries, e => e.FullName == "root.txt");
            Assert.Contains(entries, e => e.FullName == "subdir/nested.txt");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ListEntries_IncludesFileInfo()
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "test.zip");
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        try
        {
            var content = "Test content for size check";
            File.WriteAllText(Path.Combine(sourceDir, "sized.txt"), content);

            _helper.CreateArchive(sourceDir, zipPath);

            var entries = _helper.ListEntries(zipPath);
            var entry = entries.First(e => e.FullName == "sized.txt");

            Assert.Equal("sized.txt", entry.Name);
            Assert.Equal(content.Length, entry.Length);
            Assert.False(entry.IsDirectory);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cimipkg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
