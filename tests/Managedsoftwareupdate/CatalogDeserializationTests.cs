using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
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

    [Fact]
    public void CatalogItem_BindsMunkiInstallerItemKeys()
    {
        const string yaml = """
            name: Firefox
            version: 130.0
            installer_item_location: installer-item.7.Firefox-130.0.exe
            installer_item_hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
            installer_item_size: 2048
            """;

        var item = YamlUtils.Deserializer.Deserialize<CatalogItem>(yaml);

        Assert.Equal("installer-item.7.Firefox-130.0.exe", item.EffectiveInstallerLocation());
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", item.EffectiveInstallerHash());
        Assert.Equal(2048L * 1024, item.EffectiveInstallerSize());
    }
}
