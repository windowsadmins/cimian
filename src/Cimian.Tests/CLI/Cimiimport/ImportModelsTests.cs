using Xunit;
using Cimian.CLI.Cimiimport.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.Tests.CLI.Cimiimport;

public class ImportModelsTests
{
    [Fact]
    public void PkgsInfo_DefaultValues_AreCorrect()
    {
        var pkgInfo = new PkgsInfo();
        
        Assert.Empty(pkgInfo.Name);
        Assert.Empty(pkgInfo.Version);
        Assert.Empty(pkgInfo.Description);
        Assert.Null(pkgInfo.Installer);
        Assert.Null(pkgInfo.Installs);
        Assert.NotNull(pkgInfo.Catalogs);
    }

    [Fact]
    public void Installer_DefaultValues_AreCorrect()
    {
        var installer = new Installer();
        
        Assert.Empty(installer.Location);
        Assert.Empty(installer.Hash);
        Assert.Empty(installer.Type);
        Assert.Null(installer.Arguments);
    }

    [Fact]
    public void InstallItem_DefaultValues_AreCorrect()
    {
        var item = new InstallItem();
        
        Assert.Equal("file", item.Type);
        Assert.Empty(item.Path);
        Assert.Null(item.Version);
        Assert.Null(item.MD5Checksum);
    }

    [Fact]
    public void ScriptPaths_DefaultValues_AreCorrect()
    {
        var scripts = new ScriptPaths();
        
        Assert.Null(scripts.Preinstall);
        Assert.Null(scripts.Postinstall);
        Assert.Null(scripts.Preuninstall);
        Assert.Null(scripts.Postuninstall);
        Assert.Null(scripts.InstallCheck);
        Assert.Null(scripts.UninstallCheck);
    }

    [Fact]
    public void InstallerMetadata_DefaultValues_AreCorrect()
    {
        var metadata = new InstallerMetadata();
        
        Assert.Empty(metadata.Title);
        Assert.Empty(metadata.ID);
        Assert.Empty(metadata.Version);
        Assert.Empty(metadata.Developer);
        Assert.Empty(metadata.ProductCode);
        Assert.Empty(metadata.UpgradeCode);
    }

    [Fact]
    public void ImportConfiguration_DefaultValues_AreCorrect()
    {
        var config = new ImportConfiguration();
        
        Assert.Empty(config.RepoPath);
        Assert.Empty(config.CloudBucket);
        Assert.Equal("none", config.CloudProvider);
        Assert.Equal("Development", config.DefaultCatalog);
        Assert.Equal("x64,arm64", config.DefaultArch);
        Assert.True(config.OpenImportedYaml);
    }

    [Fact]
    public void PkgsInfo_Serializes_WithUnderscoredNaming()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        
        var pkgInfo = new PkgsInfo
        {
            Name = "TestApp",
            Version = "1.0.0",
            DisplayName = "Test Application"
        };
        
        var yaml = serializer.Serialize(pkgInfo);
        
        Assert.Contains("name: TestApp", yaml);
        Assert.Contains("display_name: Test Application", yaml);
    }

    [Fact]
    public void PkgsInfo_Deserializes_FromUnderscoredNaming()
    {
        var yaml = @"name: TestApp
version: '1.0.0'
description: A test app
display_name: Test Application";
        
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        
        var pkgInfo = deserializer.Deserialize<PkgsInfo>(yaml);
        
        Assert.Equal("TestApp", pkgInfo.Name);
        Assert.Equal("1.0.0", pkgInfo.Version);
        Assert.Equal("A test app", pkgInfo.Description);
        Assert.Equal("Test Application", pkgInfo.DisplayName);
    }

    [Fact]
    public void Installer_Serializes_WithLocation()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        
        var installer = new Installer
        {
            Location = "pkgs/TestApp/TestApp-1.0.0.msi",
            Hash = "abc123",
            Type = "msi"
        };
        
        var yaml = serializer.Serialize(installer);
        
        Assert.Contains("location:", yaml);
        Assert.Contains("hash: abc123", yaml);
        Assert.Contains("type: msi", yaml);
    }

    [Fact]
    public void InstallItem_Serializes_WithPath()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        
        var item = new InstallItem
        {
            Type = "file",
            Path = @"C:\Program Files\Test\app.exe",
            Version = "1.0.0"
        };
        
        var yaml = serializer.Serialize(item);
        
        Assert.Contains("type: file", yaml);
        Assert.Contains("path:", yaml);
        Assert.Contains("version:", yaml);
    }

    [Fact]
    public void PkgsInfo_WithInstaller_SerializesNested()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        
        var pkgInfo = new PkgsInfo
        {
            Name = "TestApp",
            Version = "1.0.0",
            Installer = new Installer
            {
                Type = "msi",
                Location = "pkgs/TestApp/TestApp.msi"
            }
        };
        
        var yaml = serializer.Serialize(pkgInfo);
        
        Assert.Contains("installer:", yaml);
        Assert.Contains("location:", yaml);
    }

    [Fact]
    public void PkgsInfo_WithCatalogs_SerializesList()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        
        var pkgInfo = new PkgsInfo
        {
            Name = "TestApp",
            Version = "1.0.0",
            Catalogs = new List<string> { "production", "testing" }
        };
        
        var yaml = serializer.Serialize(pkgInfo);
        
        Assert.Contains("catalogs:", yaml);
        Assert.Contains("production", yaml);
        Assert.Contains("testing", yaml);
    }

    [Fact]
    public void PkgsInfo_WithScripts_SerializesInline()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
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
    public void ImportConfiguration_Serializes_AllProperties()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        
        var config = new ImportConfiguration
        {
            RepoPath = @"C:\repo",
            CloudProvider = "aws",
            CloudBucket = "s3://bucket",
            DefaultCatalog = "Production",
            DefaultArch = "x64"
        };
        
        var yaml = serializer.Serialize(config);
        
        Assert.Contains("repo_path:", yaml);
        Assert.Contains("cloud_provider:", yaml);
        Assert.Contains("cloud_bucket:", yaml);
        Assert.Contains("default_catalog:", yaml);
        Assert.Contains("default_arch:", yaml);
    }

    [Fact]
    public void ImportConfiguration_Deserializes_FromYaml()
    {
        var yaml = @"repo_path: C:\test\repo
cloud_provider: azure
cloud_bucket: azure://container
default_catalog: Testing
default_arch: arm64";
        
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        
        var config = deserializer.Deserialize<ImportConfiguration>(yaml);
        
        Assert.Equal(@"C:\test\repo", config.RepoPath);
        Assert.Equal("azure", config.CloudProvider);
        Assert.Equal("azure://container", config.CloudBucket);
        Assert.Equal("Testing", config.DefaultCatalog);
        Assert.Equal("arm64", config.DefaultArch);
    }

    [Fact]
    public void AllCatalog_DefaultValues_AreCorrect()
    {
        var catalog = new AllCatalog();
        
        Assert.NotNull(catalog.Items);
        Assert.Empty(catalog.Items);
    }

    [Fact]
    public void AllCatalog_CanContainMultipleItems()
    {
        var catalog = new AllCatalog
        {
            Items = new List<PkgsInfo>
            {
                new PkgsInfo { Name = "App1", Version = "1.0.0" },
                new PkgsInfo { Name = "App2", Version = "2.0.0" }
            }
        };
        
        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal("App1", catalog.Items[0].Name);
        Assert.Equal("App2", catalog.Items[1].Name);
    }
}
