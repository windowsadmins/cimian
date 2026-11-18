using Cimian.CLI.managedsoftwareupdate.Services;
using WixToolset.Dtf.WindowsInstaller;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

public class MsiUpgradePolicyTests
{
    private const string Product = "{11111111-2222-3333-4444-55555555ABCD}";
    private const string OtherProduct = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";

    [Fact]
    public void UnregisteredPackage_ReadsNativeMsiMetadataWithoutRequestingAReinstall()
    {
        var path = Path.Combine(Path.GetTempPath(), $"msi-upgrade-policy-{Guid.NewGuid():N}.msi");
        try
        {
            using (var database = new Database(path, DatabaseOpenMode.CreateDirect))
            {
                database.Execute("CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)");
                database.Execute($"INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductCode', '{{{Guid.NewGuid()}}}')");
                database.Execute("INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductVersion', '2.5.1.200')");
                database.Commit();
            }

            Assert.Empty(MsiUpgradePolicy.GetAdditionalArgumentsForPackage(path, Array.Empty<string>()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("2.5.1.100", "2.5.1.200")]
    [InlineData("2.5.1", "2.5.1.200")]
    [InlineData("2.5.1.100", "2.6.0")]
    public void OlderSameProduct_UsesReinstallAndRecachesNewPackage(string installed, string incoming)
    {
        var arguments = MsiUpgradePolicy.GetAdditionalArguments(
            Product, incoming, Product, installed, Array.Empty<string>());

        Assert.Equal(new[] { "REINSTALL=ALL", "REINSTALLMODE=vomus" }, arguments);
    }

    [Theory]
    [InlineData(null, null)] // Fresh install.
    [InlineData(OtherProduct, "2.4.0")] // Major upgrade with a different ProductCode.
    [InlineData(Product, "2.5.1.200")] // Already current.
    [InlineData(Product, "2.5.1.300")] // Newer build: do not force a downgrade.
    [InlineData(Product, "unreadable")]
    public void NotAnOlderSameProduct_DoesNotAddMaintenanceArguments(string? installedCode, string? installedVersion)
    {
        var arguments = MsiUpgradePolicy.GetAdditionalArguments(
            Product, "2.5.1.200", installedCode, installedVersion, Array.Empty<string>());

        Assert.Empty(arguments);
    }

    [Theory]
    [InlineData("2.5", "2.5.0.0")]
    [InlineData("2.5.1", "2.5.1.0")]
    public void OmittedVersionFields_AreEquivalentToZero(string installed, string incoming)
    {
        Assert.Empty(MsiUpgradePolicy.GetAdditionalArguments(
            Product, incoming, Product, installed, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("REINSTALL=FeatureOne")]
    [InlineData("REINSTALLMODE=amus")]
    [InlineData("reinstall=ALL")]
    [InlineData("\"REINSTALL=ALL\"")]
    [InlineData("VENDOR=1 REINSTALLMODE=amus")]
    [InlineData("VENDOR=\"two words\" REINSTALL=FeatureOne")]
    [InlineData("VENDOR=1\tREINSTALLMODE=\"amus\"")]
    public void ConfiguredMaintenanceProperty_IsNotOverridden(string configuredArgument)
    {
        Assert.Empty(MsiUpgradePolicy.GetAdditionalArguments(
            Product, "2.5.1.200", Product, "2.5.1.100", new[] { configuredArgument }));
    }

    [Theory]
    [InlineData("TRANSFORMS=instance.mst")]
    [InlineData("VENDOR=1 TRANSFORMS=\"C:\\Msi transforms\\instance.mst\"")]
    [InlineData("MSINEWINSTANCE=1")]
    [InlineData("/n {AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}")]
    public void TransformedOrSelectedInstance_DoesNotUseRawPackageIdentity(string configuredArgument)
    {
        Assert.Empty(MsiUpgradePolicy.GetAdditionalArguments(
            Product, "2.5.1.200", Product, "2.5.1.100", new[] { configuredArgument }));
        // The guard must run before even opening the untransformed MSI database.
        Assert.Empty(MsiUpgradePolicy.GetAdditionalArgumentsForPackage(
            "missing-package.msi", new[] { configuredArgument }));
    }

    [Theory]
    [InlineData("VENDOR=\"text REINSTALL=ALL TRANSFORMS=instance.mst\"")]
    [InlineData("VENDOR=\"text \"\"quoted\"\" REINSTALLMODE=amus\"")]
    [InlineData("INSTALLDIR=\"C:\\Program Files\\Example\" VENDOR=1")]
    public void PropertyNamesInsideQuotedVendorValues_DoNotDisableUpgrade(string configuredArgument)
    {
        Assert.Equal(new[] { "REINSTALL=ALL", "REINSTALLMODE=vomus" },
            MsiUpgradePolicy.GetAdditionalArguments(
                Product, "2.5.1.200", Product, "2.5.1.100", new[] { configuredArgument }));
    }

    [Theory]
    [InlineData("invalid", "2.5.1.200")]
    [InlineData(Product, "unreadable")]
    public void InvalidPackageIdentity_DoesNotGuessMaintenanceMode(string productCode, string version)
    {
        Assert.Empty(MsiUpgradePolicy.GetAdditionalArguments(
            productCode, version, Product, "2.5.1.100", Array.Empty<string>()));
    }

    [Fact]
    public void VendorProperties_DoNotDisableAutomaticMaintenanceSelection()
    {
        var vendorArguments = new[] { "DONT_PROMPT_REBOOT=1", "DONT_START_FCT=1" };
        var arguments = MsiUpgradePolicy.GetAdditionalArguments(
            Product, "2.5.1.200", Product.ToLowerInvariant(), "2.5.1.100", vendorArguments);

        Assert.Equal(new[] { "REINSTALL=ALL", "REINSTALLMODE=vomus" }, arguments);
        Assert.Equal(new[] { "DONT_PROMPT_REBOOT=1", "DONT_START_FCT=1" }, vendorArguments);
    }
}
