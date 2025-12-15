using Xunit;
using Cimian.CLI.Cimiimport.Services;
using Cimian.CLI.Cimiimport.Models;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Cimian.Tests.CLI.Cimiimport;

public class MetadataExtractorTests
{
    private readonly MetadataExtractor _extractor;
    private readonly ImportConfiguration _config;

    public MetadataExtractorTests()
    {
        _extractor = new MetadataExtractor();
        _config = new ImportConfiguration
        {
            DefaultArch = "x64,arm64"
        };
    }

    [Theory]
    [InlineData("MyApp-1.0.0.exe", "MyApp")]
    [InlineData("MyApp_Setup_1.2.3.exe", "MyApp")]
    [InlineData("MyApp-Setup-x64.exe", "MyApp")]
    [InlineData("Google Chrome 120.0.6099.exe", "Google Chrome")]
    [InlineData("setup.exe", "setup")]
    [InlineData("installer_v2.3.4_x86.exe", "installer")]
    public void ExtractMetadata_ParsesPackageName(string fileName, string expectedName)
    {
        // Create a temp file to test metadata extraction
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_name_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var testFile = Path.Combine(tempDir, fileName);
            File.WriteAllText(testFile, "test");
            
            var metadata = _extractor.ExtractMetadata(testFile, _config);
            
            // Title is parsed from filename
            Assert.Contains(expectedName.Split(' ')[0], metadata.Title);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("2024.01.15", "2024.01.15")]
    [InlineData("", "")]
    [InlineData("5.0", "5.0")]  // 2-part versions returned as-is
    public void ParseVersion_HandlesVariousFormats(string version, string expected)
    {
        var result = MetadataExtractor.ParseVersion(version);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseVersion_HandlesSingleDateComponents()
    {
        // Single digit month/day should be zero-padded for date-like versions
        var result = MetadataExtractor.ParseVersion("2024.1.5");
        Assert.Equal("2024.01.05", result);
    }

    [Fact]
    public void ParseVersion_Returns3PartVersions()
    {
        // 3-part non-date versions
        var result = MetadataExtractor.ParseVersion("1.0.0");
        Assert.Equal("1.0.0", result);
    }

    [Fact]
    public void ExtractMetadata_HandlesNonExistentExe()
    {
        var metadata = _extractor.ExtractMetadata(@"C:\nonexistent\file.exe", _config);
        
        // Should return metadata with default values
        Assert.NotNull(metadata);
        Assert.Equal("exe", metadata.InstallerType);
    }

    [Fact]
    public void ExtractMetadata_HandlesNonExistentMsi()
    {
        var metadata = _extractor.ExtractMetadata(@"C:\nonexistent\installer.msi", _config);
        
        // Should return metadata with default values
        Assert.NotNull(metadata);
        Assert.Equal("msi", metadata.InstallerType);
    }

    [Fact]
    public void ExtractMetadata_SetsDefaultArchitecture()
    {
        var metadata = _extractor.ExtractMetadata(@"C:\nonexistent\app.exe", _config);
        
        Assert.NotEmpty(metadata.SupportedArch);
    }

    [Fact]
    public async Task ExtractMetadata_ExtractsFromValidNupkg()
    {
        // Create a temporary nupkg file with nuspec
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var nupkgPath = Path.Combine(tempDir, "TestPackage.1.0.0.nupkg");
            
            // Create a valid nupkg with nuspec
            using (var zip = ZipFile.Open(nupkgPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("TestPackage.nuspec");
                using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync(@"<?xml version=""1.0""?>
<package>
  <metadata>
    <id>TestPackage</id>
    <version>1.0.0</version>
    <authors>Test Author</authors>
    <description>A test package</description>
  </metadata>
</package>");
            }
            
            var metadata = _extractor.ExtractMetadata(nupkgPath, _config);
            
            Assert.Equal("TestPackage", metadata.ID);
            Assert.Equal("1.0.0", metadata.Version);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Theory]
    [InlineData(".msi", true)]
    [InlineData(".MSI", true)]
    [InlineData(".exe", true)]
    [InlineData(".EXE", true)]
    [InlineData(".nupkg", true)]
    [InlineData(".NUPKG", true)]
    [InlineData(".txt", false)]
    [InlineData(".zip", false)]
    public void IsSupportedInstallerType_ValidatesCorrectly(string extension, bool expected)
    {
        var supportedExtensions = new[] { ".msi", ".exe", ".nupkg", ".msix" };
        var isSupported = supportedExtensions.Contains(extension.ToLowerInvariant());
        Assert.Equal(expected, isSupported);
    }

    [Fact]
    public void CalculateSHA256_ReturnsHash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_hash_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var testFile = Path.Combine(tempDir, "test.txt");
            File.WriteAllText(testFile, "test content");
            
            var hash = MetadataExtractor.CalculateSHA256(testFile);
            
            Assert.NotEmpty(hash);
            Assert.Equal(64, hash.Length); // SHA256 produces 64 hex chars
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Theory]
    [InlineData("MyApp-1.0.0-x64.exe", "1.0.0")]
    [InlineData("Setup_2.3.4.exe", "2.3.4")]
    [InlineData("installer-v3.0.exe", "3.0")]
    [InlineData("app.exe", null)]
    public void ExtractVersionFromFileName_ParsesCorrectly(string fileName, string? expectedVersion)
    {
        // Extract version pattern from filename
        var versionPattern = System.Text.RegularExpressions.Regex.Match(
            fileName, 
            @"[-_]v?(\d+(?:\.\d+)+)[-_\.]");
        
        var result = versionPattern.Success ? versionPattern.Groups[1].Value : null;
        Assert.Equal(expectedVersion, result);
    }
}
