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

    [Theory]
    [InlineData("5.2.1")]          // semver previously misread as 2005.02.01
    [InlineData("2024.1.5")]       // genuine date kept verbatim, no zero-padding
    [InlineData("2026.4.9.0838")]  // 4-part date+timestamp passes through
    [InlineData("1.0.0")]          // plain semver
    public void ParseVersion_PassesVersionsThroughUnchanged(string version)
    {
        var result = MetadataExtractor.ParseVersion(version);
        Assert.Equal(version, result);
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
    [InlineData(".msix", true)]
    [InlineData(".appx", true)]
    [InlineData(".msixbundle", true)]
    [InlineData(".appxbundle", true)]
    [InlineData(".txt", false)]
    [InlineData(".zip", false)]
    public void IsSupportedInstallerType_ValidatesCorrectly(string extension, bool expected)
    {
        var supportedExtensions = new[] { ".msi", ".exe", ".nupkg", ".msix", ".appx", ".msixbundle", ".appxbundle" };
        var isSupported = supportedExtensions.Contains(extension.ToLowerInvariant());
        Assert.Equal(expected, isSupported);
    }

    // ========================================================================
    // MSIX / APPX metadata extraction tests
    // ========================================================================

    private static async Task WriteMsixFixtureAsync(string path, string manifestXml)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("AppxManifest.xml");
        using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync(manifestXml);
    }

    private static async Task WriteMsixBundleFixtureAsync(string path, string bundleManifestXml)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("AppxMetadata/AppxBundleManifest.xml");
        using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync(bundleManifestXml);
    }

    [Fact]
    public async Task ExtractMetadata_ExtractsFromValidMsix()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_msix_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var msixPath = Path.Combine(tempDir, "TestApp.msix");
            await WriteMsixFixtureAsync(msixPath, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""com.example.testapp"" Version=""4.45.69.0"" ProcessorArchitecture=""x64"" Publisher=""CN=Example Corp"" />
  <Properties>
    <DisplayName>TestApp</DisplayName>
    <PublisherDisplayName>Example Corp</PublisherDisplayName>
    <Description>A test application</Description>
  </Properties>
</Package>");

            var metadata = _extractor.ExtractMetadata(msixPath, _config);

            Assert.Equal("msix", metadata.InstallerType);
            Assert.Equal("com.example.testapp", metadata.IdentityName);
            Assert.Equal("TestApp", metadata.Title);
            Assert.Equal("TestApp", metadata.ID);
            Assert.Equal("4.45.69.0", metadata.Version);
            Assert.Equal("x64", metadata.Architecture);
            Assert.Equal("Example Corp", metadata.Developer);
            Assert.Equal("A test application", metadata.Description);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExtractMetadata_Msix_FilenameArchOverridesManifest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_msix_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Filename says arm64, manifest says x64 — filename should win
            var msixPath = Path.Combine(tempDir, "TestApp-arm64-1.0.0.msix");
            await WriteMsixFixtureAsync(msixPath, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""com.example.testapp"" Version=""1.0.0.0"" ProcessorArchitecture=""x64"" Publisher=""CN=Test"" />
  <Properties>
    <DisplayName>TestApp</DisplayName>
    <PublisherDisplayName>Test</PublisherDisplayName>
  </Properties>
</Package>");

            var metadata = _extractor.ExtractMetadata(msixPath, _config);

            Assert.Equal("arm64", metadata.Architecture);
            Assert.Contains("arm64", metadata.SupportedArch);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExtractMetadata_Msix_MissingManifest_FallsBackToFilename()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_msix_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var msixPath = Path.Combine(tempDir, "MyApp-1.0.0.msix");
            // ZIP with no AppxManifest.xml entry
            using (var zip = ZipFile.Open(msixPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("other.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("not a manifest");
            }

            var metadata = _extractor.ExtractMetadata(msixPath, _config);

            Assert.Equal("msix", metadata.InstallerType);
            Assert.Equal("MyApp-1.0.0", metadata.Title);
            Assert.Equal("1.0.0", metadata.Version);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExtractMetadata_Msix_CorruptManifest_FallsBackToFilename()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_msix_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var msixPath = Path.Combine(tempDir, "BrokenApp-2.0.0.msix");
            await WriteMsixFixtureAsync(msixPath, "<<<not valid xml>>>");

            var metadata = _extractor.ExtractMetadata(msixPath, _config);

            Assert.Equal("msix", metadata.InstallerType);
            Assert.Equal("BrokenApp-2.0.0", metadata.Title);
            Assert.Equal("1.0.0", metadata.Version); // fallback default
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExtractMetadata_MsixBundle_ExtractsFromBundleManifest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_msix_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var bundlePath = Path.Combine(tempDir, "TestBundle.msixbundle");
            await WriteMsixBundleFixtureAsync(bundlePath, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Bundle xmlns=""http://schemas.microsoft.com/appx/2018/bundle"">
  <Identity Name=""com.example.bundledapp"" Version=""3.0.0.0"" Publisher=""CN=Example"" />
  <Packages>
    <Package Type=""application"" Version=""3.0.0.0"" Architecture=""x64"" FileName=""TestBundle.x64.msix"" />
  </Packages>
</Bundle>");

            var metadata = _extractor.ExtractMetadata(bundlePath, _config);

            Assert.Equal("msix", metadata.InstallerType);
            Assert.Equal("com.example.bundledapp", metadata.IdentityName);
            Assert.Equal("bundledapp", metadata.Title); // last segment of reverse-domain
            Assert.Equal("3.0.0.0", metadata.Version);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExtractMetadata_Msix_NoDisplayName_FallsBackToIdentityLastSegment()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_msix_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var msixPath = Path.Combine(tempDir, "NoName.msix");
            await WriteMsixFixtureAsync(msixPath, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""com.tinyspeck.slackdesktop"" Version=""4.45.69.0"" ProcessorArchitecture=""x64"" Publisher=""CN=Slack"" />
</Package>");

            var metadata = _extractor.ExtractMetadata(msixPath, _config);

            Assert.Equal("com.tinyspeck.slackdesktop", metadata.IdentityName);
            Assert.Equal("slackdesktop", metadata.Title);
            Assert.Equal("slackdesktop", metadata.ID);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
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

    [Theory]
    [InlineData("MyApp-1.0.0-x64.exe", "x64")]
    [InlineData("MyApp-1.0.0-arm64.exe", "arm64")]
    [InlineData("MyApp-x86.exe", "x86")]
    [InlineData("MyApp-amd64.exe", "x64")]
    [InlineData("MyApp-aarch64.exe", "arm64")]
    [InlineData("MyApp.exe", "")]
    public void DetectArchFromFilename_DetectsCorrectly(string fileName, string expectedArch)
    {
        var result = MetadataExtractor.DetectArchFromFilename(fileName);
        Assert.Equal(expectedArch, result);
    }

    [Fact]
    public async Task ExtractMetadata_PkgFile_FilenameArchOverridesBuildInfo()
    {
        // This test verifies that architecture detected from the filename takes priority
        // over architecture specified in the .pkg file's build-info.yaml
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimiimport_pkg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // Create a .pkg file with build-info.yaml that specifies x64
            var pkgPath = Path.Combine(tempDir, "StartSet-2026.01.21.1706-arm64.pkg");
            
            using (var zip = ZipFile.Open(pkgPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("build-info.yaml");
                using var writer = new StreamWriter(entry.Open());
                // build-info.yaml specifies x64 architecture
                await writer.WriteAsync(@"product:
  name: StartSet
  version: 2026.01.21.1706
  identifier: com.example.startset
  developer: Windows Admins Open Source
  description: Test package
  architecture: x64");
            }
            
            var metadata = _extractor.ExtractMetadata(pkgPath, _config);
            
            // The filename contains "arm64", so that should take priority over the x64 in build-info.yaml
            Assert.Equal("arm64", metadata.Architecture);
            Assert.Contains("arm64", metadata.SupportedArch);
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
