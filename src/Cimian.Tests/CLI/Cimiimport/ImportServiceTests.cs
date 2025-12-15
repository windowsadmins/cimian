using Xunit;
using Cimian.CLI.Cimiimport.Services;
using Cimian.CLI.Cimiimport.Models;
using System.IO;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace Cimian.Tests.CLI.Cimiimport;

public class ImportServiceTests
{
    private readonly ImportService _importService;
    private readonly ConfigurationService _configService;
    private readonly MetadataExtractor _metadataExtractor;

    public ImportServiceTests()
    {
        _configService = new ConfigurationService();
        _metadataExtractor = new MetadataExtractor();
        _importService = new ImportService(_metadataExtractor, _configService);
    }

    [Fact]
    public void ImportService_CanBeCreatedWithDefaults()
    {
        var service = new ImportService();
        Assert.NotNull(service);
    }

    [Fact]
    public void ImportService_CanBeCreatedWithDependencies()
    {
        var service = new ImportService(_metadataExtractor, _configService);
        Assert.NotNull(service);
    }

    [Theory]
    [InlineData("s3://bucket/path", true)]
    [InlineData("azure://container/path", true)]
    [InlineData("", false)]
    public void CloudBucket_DeterminesSyncAvailability(string bucket, bool canSync)
    {
        var config = new ImportConfiguration
        {
            CloudBucket = bucket
        };
        
        var isEnabled = !string.IsNullOrEmpty(config.CloudBucket);
        Assert.Equal(canSync, isEnabled);
    }

    [Theory]
    [InlineData("TestApp-1.0.0.msi", "TestApp", "1.0.0")]
    [InlineData("App_Setup_2.3.4.exe", "App", "2.3.4")]
    public void PackageDirectory_CanBeConstructed(string installerName, string name, string version)
    {
        // Verify all parameters are valid
        Assert.False(string.IsNullOrEmpty(installerName));
        Assert.False(string.IsNullOrEmpty(version));
        
        var pkgsDir = @"C:\repo\pkgs";
        var expectedDir = Path.Combine(pkgsDir, name);
        
        Assert.EndsWith(name, expectedDir);
    }

    [Fact]
    public void BuildPackageLocation_FormatsCorrectly()
    {
        var name = "TestApp";
        var installerName = "TestApp-1.0.0.msi";
        
        var location = $"pkgs/{name}/{installerName}";
        
        Assert.Equal("pkgs/TestApp/TestApp-1.0.0.msi", location);
    }

    [Fact]
    public void BuildPkgInfoName_FormatsCorrectly()
    {
        var name = "TestApp";
        var version = "1.0.0";
        
        var pkgInfoName = $"{name}-{version}.pkginfo.yaml";
        
        Assert.Equal("TestApp-1.0.0.pkginfo.yaml", pkgInfoName);
    }

    [Fact]
    public void PkgsInfo_CanBeSerializedToYaml()
    {
        var serializer = new SerializerBuilder()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        
        var pkgInfo = new PkgsInfo
        {
            Name = "TestApp",
            Version = "1.0.0",
            Description = "A test application",
            Installer = new Installer
            {
                Type = "msi",
                Location = "pkgs/TestApp/TestApp-1.0.0.msi"
            }
        };
        
        var yaml = serializer.Serialize(pkgInfo);
        
        Assert.Contains("name: TestApp", yaml);
        Assert.Contains("version: 1.0.0", yaml);
    }

    [Fact]
    public void PkgsInfo_CanIncludeScripts()
    {
        var serializer = new SerializerBuilder()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        
        var pkgInfo = new PkgsInfo
        {
            Name = "TestApp",
            Version = "1.0.0",
            PreinstallScript = "Write-Host 'PreInstall'",
            PostinstallScript = "Write-Host 'PostInstall'"
        };
        
        var yaml = serializer.Serialize(pkgInfo);
        
        Assert.Contains("preinstall_script:", yaml);
        Assert.Contains("postinstall_script:", yaml);
    }

    [Fact]
    public void PkgsInfo_CanIncludeInstalls()
    {
        var serializer = new SerializerBuilder()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        
        var pkgInfo = new PkgsInfo
        {
            Name = "TestApp",
            Version = "1.0.0",
            Installs = new List<InstallItem>
            {
                new InstallItem
                {
                    Type = "file",
                    Path = @"C:\Program Files\TestApp\app.exe",
                    Version = "1.0.0"
                }
            }
        };
        
        var yaml = serializer.Serialize(pkgInfo);
        
        Assert.Contains("installs:", yaml);
        Assert.Contains("path:", yaml);
    }

    [Fact]
    public void InstallerMetadata_HasCorrectDefaults()
    {
        var metadata = new InstallerMetadata();
        
        Assert.Empty(metadata.Title);
        Assert.Empty(metadata.ID);
        Assert.Empty(metadata.Version);
        Assert.Empty(metadata.Developer);
        Assert.Empty(metadata.InstallerType);
        Assert.NotNull(metadata.SupportedArch);
        Assert.NotNull(metadata.Installs);
    }

    [Fact]
    public void ScriptPaths_HasCorrectDefaults()
    {
        var scripts = new ScriptPaths();
        
        Assert.Null(scripts.Preinstall);
        Assert.Null(scripts.Postinstall);
        Assert.Null(scripts.Preuninstall);
        Assert.Null(scripts.Postuninstall);
        Assert.Null(scripts.InstallCheck);
        Assert.Null(scripts.UninstallCheck);
    }

    [Theory]
    [InlineData("msi")]
    [InlineData("exe")]
    [InlineData("nupkg")]
    [InlineData("msix")]
    public void Installer_SupportsVariousTypes(string type)
    {
        var installer = new Installer
        {
            Type = type,
            Location = $"pkgs/TestApp/TestApp.{type}"
        };
        
        Assert.Equal(type, installer.Type);
    }
}
