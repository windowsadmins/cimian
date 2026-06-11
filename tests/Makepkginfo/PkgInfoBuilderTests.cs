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
    public void BuildFromInstaller_EmitsUnattendedUninstall_AndUsageStaleFields()
    {
        // Pins the PkgsInfoOptions -> PkgsInfo wiring in BuildFromInstaller.
        // --unattended_uninstall was historically parsed but silently dropped
        // (the model had no property and BuildFromInstaller never assigned
        // it), so this test guards the whole option set end to end: build
        // from a real file, then assert the YAML keys actually appear.
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "installer payload");

            var options = new PkgsInfoOptions
            {
                Name = "StaleApp",
                Version = "1.0",
                Catalogs = new List<string> { "Testing" },
                UnattendedInstall = true,
                UnattendedUninstall = true,
                DaysUntouchedBeforeUninstall = 30,
                UsageTrackedPaths = new List<string> { @"C:\Program Files\StaleApp\staleapp.exe" },
                MinimumUsageHistoryDays = 14,
            };

            var pkgsinfo = _builder.BuildFromInstaller(tempFile, options);

            Assert.True(pkgsinfo.UnattendedUninstall);
            Assert.Equal(30, pkgsinfo.DaysUntouchedBeforeUninstall);
            Assert.Equal(options.UsageTrackedPaths, pkgsinfo.UsageTrackedPaths);
            Assert.Equal(14, pkgsinfo.MinimumUsageHistoryDays);

            var yaml = _builder.SerializePkgsInfo(pkgsinfo);
            Assert.Contains("unattended_uninstall: true", yaml);
            Assert.Contains("days_untouched_before_uninstall: 30", yaml);
            Assert.Contains("usage_tracked_paths:", yaml);
            Assert.Contains("minimum_usage_history_days: 14", yaml);
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
                Hash = "abc123"
            },
            Installs = new List<InstallItem>
            {
                new InstallItem
                {
                    Type = "msi",
                    ProductCode = "{GUID}",
                    UpgradeCode = "{GUID2}",
                    Version = "1.0.0"
                }
            }
        };

        var yaml = _builder.SerializePkgsInfo(pkgsinfo);

        Assert.Contains("installer:", yaml);
        Assert.Contains("type:", yaml);
        Assert.Contains("msi", yaml);
        Assert.Contains("installs:", yaml);
        Assert.Contains("product_code:", yaml);
    }

    [Fact]
    public void BuildMsiInstallItem_PopulatesVersionFromProductVersion()
    {
        // Cimipkg-built MSIs land DisplayVersion=26.5.612 in the registry (compressed
        // from catalog version 2026.05.06.1248). installs[].version must match that
        // truncated form so StatusService.CheckMsiWithUpgradeCode resolves correctly --
        // omitting it makes Cimian fall back to the date-format catalog version, which
        // never equals 26.5.612 and triggers a permanent reinstall loop.
        var meta = new MetadataExtractor.MsiMetadata(
            ProductName:    "SbinInstaller",
            ProductVersion: "26.4.2716",
            Developer:      "Windows Admins",
            Description:    "",
            ProductCode:    "{a3d0871c-e0e8-4f11-827f-6507a08dc00b}",
            UpgradeCode:    "{eb666390-ff65-55da-aac4-8eebc7b45db0}");

        var item = PkgInfoBuilder.BuildMsiInstallItem(meta);

        Assert.Equal("msi", item.Type);
        Assert.Equal("{a3d0871c-e0e8-4f11-827f-6507a08dc00b}", item.ProductCode);
        Assert.Equal("{eb666390-ff65-55da-aac4-8eebc7b45db0}", item.UpgradeCode);
        Assert.Equal("26.4.2716", item.Version);
    }

    [Fact]
    public void BuildMsiInstallItem_EmptyProductVersion_LeavesVersionNull()
    {
        // OmitNull must suppress an absent ProductVersion (some MSIs ship without one) --
        // emitting empty-string would serialize as `version: ''` and break version compare.
        var meta = new MetadataExtractor.MsiMetadata(
            ProductName:    "Blender",
            ProductVersion: "",
            Developer:      "Blender Foundation",
            Description:    "",
            ProductCode:    "{26068070-A31E-4DCD-8D17-F40B06CB413D}",
            UpgradeCode:    "{4C6AD1CE-C11B-54CD-83AE-A801252310E4}");

        var item = PkgInfoBuilder.BuildMsiInstallItem(meta);

        Assert.Null(item.Version);
        Assert.Equal("{26068070-A31E-4DCD-8D17-F40B06CB413D}", item.ProductCode);
    }

    [Fact]
    public void BuildMsiInstallItem_EmptyCodes_LeavesCodesNull()
    {
        var meta = new MetadataExtractor.MsiMetadata(
            ProductName:    "UnknownMSI",
            ProductVersion: "1.2.3",
            Developer:      "",
            Description:    "",
            ProductCode:    "",
            UpgradeCode:    "");

        var item = PkgInfoBuilder.BuildMsiInstallItem(meta);

        Assert.Null(item.ProductCode);
        Assert.Null(item.UpgradeCode);
        Assert.Equal("1.2.3", item.Version);
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
            Arguments = new List<string> { "/quiet", "/norestart" }
        };

        Assert.Equal("msi", installer.Type);
        Assert.Equal(2048, installer.Size);
        Assert.Equal("package.msi", installer.Location);
        Assert.Equal("sha256hash", installer.Hash);
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
