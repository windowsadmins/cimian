using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests that the runtime catalog model deserializes the way CatalogService parses
/// downloaded catalogs (via the shared YamlUtils.Deserializer, no naming convention).
/// </summary>
public class CatalogDeserializationTests
{
    [Fact]
    public void CatalogItem_BindsOnDemand_FromPascalCaseAlias()
    {
        // Regression: the old CatalogService deserializer applied
        // UnderscoredNamingConvention, which rewrote the `OnDemand` alias to
        // `on_demand` on read and silently dropped `OnDemand: true` from catalogs.
        // The provisioning/enrollment nopkg items depend on this field binding.
        const string yaml = """
            name: Ω ProvisioningManifestEnrollment
            version: 2025.12.10
            OnDemand: true
            installer:
              type: nopkg
            """;

        var item = YamlUtils.Deserializer.Deserialize<CatalogItem>(yaml);

        Assert.NotNull(item);
        Assert.Equal("Ω ProvisioningManifestEnrollment", item!.Name);
        Assert.True(item.OnDemand, $"OnDemand was {item.OnDemand} — catalog field was dropped on parse");
    }

    [Fact]
    public void CatalogItem_OnDemandDefaultsFalse_WhenAbsent()
    {
        const string yaml = """
            name: RegularPackage
            version: 1.0.0
            installer:
              type: msi
            """;

        var item = YamlUtils.Deserializer.Deserialize<CatalogItem>(yaml);

        Assert.NotNull(item);
        Assert.False(item!.OnDemand);
    }

    private static CatalogItem LoadSingleItem(string yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CimianTests", "Catalogs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "Testing.yaml");
            File.WriteAllText(path, yaml);
            var service = new CatalogService(new CimianConfig(), new HttpClient());
            return Assert.Single(service.LoadLocalCatalog(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadCatalog_MunkiInstallerItemKeysOnly_FillsTheInstallerBlock()
    {
        var item = LoadSingleItem("""
            items:
              - name: Firefox
                version: 130.0
                installer_item_location: apps/firefox/Firefox-130.0.exe
                installer_item_hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                installer_item_size: 2048
            """);

        Assert.Equal("apps/firefox/Firefox-130.0.exe", item.Installer.Location);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", item.Installer.Hash);
        Assert.Equal(2048L * 1024, item.Installer.Size);
    }

    [Fact]
    public void LoadCatalog_BothForms_KeepsTheInstallerBlock()
    {
        var item = LoadSingleItem("""
            items:
              - name: Firefox
                version: 130.0
                installer:
                  location: apps/firefox/Firefox-130.0.exe
                  hash: aaaa
                  size: 4096
                  type: exe
                installer_item_location: installer-item.7.Firefox-130.0.exe
                installer_item_hash: bbbb
                installer_item_size: 2
            """);

        Assert.Equal("apps/firefox/Firefox-130.0.exe", item.Installer.Location);
        Assert.Equal("aaaa", item.Installer.Hash);
        Assert.Equal(4096L, item.Installer.Size);
    }

    [Fact]
    public void LoadCatalog_InstallerBlockOnly_IsUnchanged()
    {
        var item = LoadSingleItem("""
            items:
              - name: Firefox
                version: 130.0
                installer:
                  location: apps/firefox/Firefox-130.0.exe
                  hash: aaaa
                  size: 4096
                  type: exe
            """);

        Assert.Equal("apps/firefox/Firefox-130.0.exe", item.Installer.Location);
        Assert.Equal("aaaa", item.Installer.Hash);
        Assert.Equal(4096L, item.Installer.Size);
        Assert.Equal("exe", item.Installer.Type);
    }
}
