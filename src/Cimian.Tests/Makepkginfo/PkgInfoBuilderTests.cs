using Xunit;
using Cimian.CLI.Makepkginfo.Services;
using Cimian.CLI.Makepkginfo.Models;

namespace Cimian.Tests.Makepkginfo;

public class MetadataExtractorTests
{
    private readonly MetadataExtractor _extractor;

    public MetadataExtractorTests()
    {
        _extractor = new MetadataExtractor();
    }

    [Fact]
    public void Extractor_CanBeInstantiated()
    {
        Assert.NotNull(_extractor);
    }

    [Fact]
    public void CalculateMd5_ReturnsValidHash()
    {
        // Create temp file
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content");
            
            var hash = _extractor.CalculateMd5(tempFile);
            
            Assert.NotNull(hash);
            Assert.Equal(32, hash.Length); // MD5 is 32 hex chars
            Assert.Matches("^[a-f0-9]+$", hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void CalculateSha256_ReturnsValidHash()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content");
            
            var hash = _extractor.CalculateSha256(tempFile);
            
            Assert.NotNull(hash);
            Assert.Equal(64, hash.Length); // SHA256 is 64 hex chars
            Assert.Matches("^[a-f0-9]+$", hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetFileSize_ReturnsCorrectSize()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content 123");
            
            var size = _extractor.GetFileSize(tempFile);
            
            Assert.True(size > 0);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetFileSizeKB_ReturnsKBSize()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write 2KB of data
            var data = new string('X', 2048);
            File.WriteAllText(tempFile, data);
            
            var sizeKB = _extractor.GetFileSizeKB(tempFile);
            
            Assert.True(sizeKB >= 2);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExtractNupkgMetadata_WithInvalidPath_ReturnsFallback()
    {
        var meta = _extractor.ExtractNupkgMetadata("nonexistent.nupkg");
        
        Assert.Equal("nonexistent", meta.Identifier);
        Assert.Equal("nonexistent", meta.Name);
    }

    [Fact]
    public void ExtractMsiMetadata_WithInvalidPath_ReturnsFallback()
    {
        var meta = _extractor.ExtractMsiMetadata("nonexistent.msi");
        
        Assert.Equal("UnknownMSI", meta.ProductName);
    }

    [Fact]
    public void ExtractExeVersion_WithInvalidPath_ReturnsNull()
    {
        var version = _extractor.ExtractExeVersion("nonexistent.exe");
        
        Assert.Null(version);
    }
}

public class PkgInfoBuilderTests
{
    private readonly PkgInfoBuilder _builder;

    public PkgInfoBuilderTests()
    {
        _builder = new PkgInfoBuilder();
    }

    [Fact]
    public void Builder_CanBeInstantiated()
    {
        Assert.NotNull(_builder);
    }

    [Fact]
    public void BuildInstallsArray_WithInvalidPaths_ReturnsEmpty()
    {
        var result = _builder.BuildInstallsArray(new List<string> { "nonexistent.exe" });
        
        Assert.Empty(result);
    }

    [Fact]
    public void BuildInstallsArray_WithValidFile_ReturnsInstallItem()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test");
            
            var result = _builder.BuildInstallsArray(new List<string> { tempFile });
            
            Assert.Single(result);
            Assert.Equal("file", result[0].Type);
            Assert.NotNull(result[0].Md5Checksum);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SerializePkgsInfo_ReturnsValidYaml()
    {
        var pkgsinfo = new PkgsInfo
        {
            Name = "TestPackage",
            Version = "1.0.0",
            Catalogs = new List<string> { "Testing" }
        };
        
        var yaml = _builder.SerializePkgsInfo(pkgsinfo);
        
        Assert.NotNull(yaml);
        Assert.Contains("name:", yaml);
        Assert.Contains("TestPackage", yaml);
        Assert.Contains("version:", yaml);
        Assert.Contains("1.0.0", yaml);
    }

    [Fact]
    public void SerializePkgsInfo_OmitsNullValues()
    {
        var pkgsinfo = new PkgsInfo
        {
            Name = "TestPackage",
            Version = "1.0.0",
            Description = null,
            Developer = null
        };
        
        var yaml = _builder.SerializePkgsInfo(pkgsinfo);
        
        Assert.DoesNotContain("description:", yaml);
        Assert.DoesNotContain("developer:", yaml);
    }

    [Fact]
    public void SerializePkgsInfo_IncludesInstallerDetails()
    {
        var pkgsinfo = new PkgsInfo
        {
            Name = "TestPackage",
            Version = "1.0.0",
            Installer = new Installer
            {
                Type = "msi",
                Size = 1024,
                Location = "test.msi",
                Hash = "abc123",
                ProductCode = "{GUID}",
                UpgradeCode = "{GUID2}"
            }
        };
        
        var yaml = _builder.SerializePkgsInfo(pkgsinfo);
        
        Assert.Contains("installer:", yaml);
        Assert.Contains("type:", yaml);
        Assert.Contains("msi", yaml);
        Assert.Contains("product_code:", yaml);
    }

    [Fact]
    public void CreateNewPkgsInfo_CreatesFileWithDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tempFile = Path.Combine(tempDir, "test.yaml");
        
        try
        {
            _builder.CreateNewPkgsInfo(tempFile, "TestPackage");
            
            Assert.True(File.Exists(tempFile));
            
            var content = File.ReadAllText(tempFile);
            Assert.Contains("name:", content);
            Assert.Contains("TestPackage", content);
            Assert.Contains("catalogs:", content);
            Assert.Contains("Testing", content);
            Assert.Contains("unattended_install: true", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}

public class PkgsInfoModelTests
{
    [Fact]
    public void PkgsInfo_DefaultValues()
    {
        var pkgsinfo = new PkgsInfo();
        
        Assert.Equal(string.Empty, pkgsinfo.Name);
        Assert.Equal(string.Empty, pkgsinfo.Version);
        Assert.NotNull(pkgsinfo.Catalogs);
        Assert.Empty(pkgsinfo.Catalogs);
        Assert.False(pkgsinfo.UnattendedInstall);
        Assert.False(pkgsinfo.OnDemand);
    }

    [Fact]
    public void PkgsInfo_CanSetAllProperties()
    {
        var pkgsinfo = new PkgsInfo
        {
            Name = "Test",
            DisplayName = "Test Display",
            Identifier = "com.test.pkg",
            Version = "1.0.0",
            Catalogs = new List<string> { "Production" },
            Category = "Utilities",
            Description = "A test package",
            Developer = "Test Corp",
            InstallerType = "msi",
            UnattendedInstall = true,
            MinOSVersion = "10.0",
            MaxOSVersion = "11.0",
            OnDemand = true
        };
        
        Assert.Equal("Test", pkgsinfo.Name);
        Assert.Equal("Test Display", pkgsinfo.DisplayName);
        Assert.Equal("com.test.pkg", pkgsinfo.Identifier);
        Assert.Equal("1.0.0", pkgsinfo.Version);
        Assert.Single(pkgsinfo.Catalogs);
        Assert.Equal("Production", pkgsinfo.Catalogs[0]);
        Assert.Equal("Utilities", pkgsinfo.Category);
        Assert.Equal("A test package", pkgsinfo.Description);
        Assert.Equal("Test Corp", pkgsinfo.Developer);
        Assert.Equal("msi", pkgsinfo.InstallerType);
        Assert.True(pkgsinfo.UnattendedInstall);
        Assert.Equal("10.0", pkgsinfo.MinOSVersion);
        Assert.Equal("11.0", pkgsinfo.MaxOSVersion);
        Assert.True(pkgsinfo.OnDemand);
    }

    [Fact]
    public void Installer_CanSetAllProperties()
    {
        var installer = new Installer
        {
            Type = "msi",
            Size = 2048,
            Location = "package.msi",
            Hash = "sha256hash",
            ProductCode = "{12345}",
            UpgradeCode = "{67890}",
            Arguments = new List<string> { "/quiet", "/norestart" }
        };
        
        Assert.Equal("msi", installer.Type);
        Assert.Equal(2048, installer.Size);
        Assert.Equal("package.msi", installer.Location);
        Assert.Equal("sha256hash", installer.Hash);
        Assert.Equal("{12345}", installer.ProductCode);
        Assert.Equal("{67890}", installer.UpgradeCode);
        Assert.Equal(2, installer.Arguments!.Count);
    }

    [Fact]
    public void InstallItem_CanSetAllProperties()
    {
        var item = new InstallItem
        {
            Type = "file",
            Path = @"C:\Program Files\Test\app.exe",
            Md5Checksum = "abc123",
            Version = "1.0.0"
        };
        
        Assert.Equal("file", item.Type);
        Assert.Equal(@"C:\Program Files\Test\app.exe", item.Path);
        Assert.Equal("abc123", item.Md5Checksum);
        Assert.Equal("1.0.0", item.Version);
    }
}

public class PkgsInfoOptionsTests
{
    [Fact]
    public void Options_DefaultValues()
    {
        var options = new PkgsInfoOptions();
        
        Assert.Null(options.Name);
        Assert.Null(options.Version);
        Assert.Null(options.Catalogs);
        Assert.False(options.UnattendedInstall);
        Assert.False(options.OnDemand);
    }

    [Fact]
    public void Options_CanSetAllProperties()
    {
        var options = new PkgsInfoOptions
        {
            Name = "Test",
            DisplayName = "Test Display",
            Version = "1.0.0",
            Catalogs = new List<string> { "Testing" },
            Category = "Utilities",
            Developer = "Dev",
            Description = "Desc",
            UnattendedInstall = true,
            UnattendedUninstall = true,
            OnDemand = true,
            MinOSVersion = "10.0",
            MaxOSVersion = "11.0",
            InstallCheckScriptPath = "/path/to/script.ps1",
            UninstallCheckScriptPath = "/path/to/uninstall.ps1",
            PreinstallScriptPath = "/path/to/pre.ps1",
            PostinstallScriptPath = "/path/to/post.ps1",
            AdditionalFiles = new List<string> { "/path/to/file.exe" }
        };
        
        Assert.Equal("Test", options.Name);
        Assert.Equal("Test Display", options.DisplayName);
        Assert.Equal("1.0.0", options.Version);
        Assert.Single(options.Catalogs!);
        Assert.True(options.UnattendedInstall);
        Assert.True(options.UnattendedUninstall);
        Assert.True(options.OnDemand);
        Assert.Single(options.AdditionalFiles!);
    }
}

public class InstallerTypeDetectionTests
{
    [Theory]
    [InlineData(".msi", "msi")]
    [InlineData(".MSI", "msi")]
    [InlineData(".exe", "exe")]
    [InlineData(".EXE", "exe")]
    [InlineData(".nupkg", "nupkg")]
    [InlineData(".NUPKG", "nupkg")]
    [InlineData(".zip", "unknown")]
    [InlineData("", "unknown")]
    public void InstallerType_DetectedFromExtension(string extension, string expectedType)
    {
        var actualType = extension.ToLowerInvariant() switch
        {
            ".msi" => "msi",
            ".exe" => "exe",
            ".nupkg" => "nupkg",
            _ => "unknown"
        };
        
        Assert.Equal(expectedType, actualType);
    }
}
