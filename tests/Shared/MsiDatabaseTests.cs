using System.ComponentModel;
using Cimian.Core.Msi;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// The msi.dll reader that replaced WiX DTF. These pin the behaviour callers
/// relied on from DTF: a missing property reads as null, a null string field
/// as "", a null integer field as 0, and a missing table is reported rather
/// than thrown.
/// </summary>
public class MsiDatabaseTests : IDisposable
{
    private readonly TestMsiFactory _msis = new();

    public void Dispose() => _msis.Dispose();

    private string BuildPropertyMsi() => _msis.Create(
        "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)",
        "INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductName', 'Contoso Widget')",
        "INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductCode', '{11111111-2222-3333-4444-555555555555}')",
        new TestMsiSql("INSERT INTO `Property` (`Property`, `Value`) VALUES (?, ?)", "Owner's", "quoted"),
        new TestMsiSql("INSERT INTO `Property` (`Property`, `Value`) VALUES (?, ?)", "Long", new string('x', 1000)));

    [Fact]
    public void GetProperty_ReturnsValue()
    {
        using var db = MsiDatabase.OpenReadOnly(BuildPropertyMsi());

        Assert.Equal("Contoso Widget", db.GetProperty("ProductName"));
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", db.GetProperty("ProductCode"));
    }

    [Fact]
    public void GetProperty_Missing_ReturnsNull()
    {
        using var db = MsiDatabase.OpenReadOnly(BuildPropertyMsi());

        Assert.Null(db.GetProperty("UpgradeCode"));
    }

    [Fact]
    public void GetProperty_NameIsBoundAsParameter_NotSplicedIntoSql()
    {
        using var db = MsiDatabase.OpenReadOnly(BuildPropertyMsi());

        Assert.Equal("quoted", db.GetProperty("Owner's"));
        Assert.Null(db.GetProperty("x' OR `Property` <> '"));
    }

    [Fact]
    public void GetString_GrowsBufferForLongValues()
    {
        using var db = MsiDatabase.OpenReadOnly(BuildPropertyMsi());

        Assert.Equal(new string('x', 1000), db.GetProperty("Long"));
    }

    [Fact]
    public void NullFields_ReadAsEmptyStringAndZero()
    {
        var path = _msis.Create(
            "CREATE TABLE `T` (`K` CHAR(10) NOT NULL, `S` CHAR(10), `N` LONG PRIMARY KEY `K`)",
            "INSERT INTO `T` (`K`) VALUES ('a')");
        using var db = MsiDatabase.OpenReadOnly(path);
        using var view = db.OpenView("SELECT `S`, `N` FROM `T`");
        using var record = view.Fetch();

        Assert.NotNull(record);
        Assert.Equal("", record!.GetString(1));
        Assert.Equal(0, record.GetInteger(2));
        Assert.Null(view.Fetch());
    }

    [Fact]
    public void TableExists_ReportsPresentAndMissingTables()
    {
        using var db = MsiDatabase.OpenReadOnly(BuildPropertyMsi());

        Assert.True(db.TableExists("Property"));
        Assert.False(db.TableExists("File"));
    }

    [Fact]
    public void QueryOnMissingTable_Throws()
    {
        using var db = MsiDatabase.OpenReadOnly(BuildPropertyMsi());

        Assert.ThrowsAny<Win32Exception>(() => db.OpenView("SELECT `File` FROM `File`"));
    }

    [Fact]
    public void OpenReadOnly_NotAnMsi_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notmsi_{Guid.NewGuid():N}.msi");
        File.WriteAllText(path, "not an msi");
        try
        {
            Assert.ThrowsAny<Win32Exception>(() => MsiDatabase.OpenReadOnly(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenReadOnly_DoesNotModifyTheFile()
    {
        var path = BuildPropertyMsi();
        var before = File.ReadAllBytes(path);

        using (var db = MsiDatabase.OpenReadOnly(path))
        {
            db.GetProperty("ProductName");
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }
}

/// <summary>
/// ProductVersion normalisation, ported from DTF's ProductInstallation so the
/// agent reports the same installed version for an UpgradeCode match.
/// </summary>
public class MsiProductsTests
{
    [Theory]
    [InlineData("25.01.00.0", "25.1.0.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("7", "7.0")]
    [InlineData("1.2.3.4.5", "1.2.3.4")]
    [InlineData("1.2.3-beta", "1.2.3")]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void ParseVersion_MatchesDtf(string? raw, string? expected)
    {
        Assert.Equal(expected, MsiProducts.ParseVersion(raw)?.ToString());
    }

    [Fact]
    public void ParseVersion_TrailingDot_ThrowsLikeDtf()
    {
        // DTF only swallowed ArgumentException; the agent's per-product catch
        // relies on this surfacing rather than reading as "no version".
        Assert.Throws<FormatException>(() => MsiProducts.ParseVersion("1."));
    }

    [Fact]
    public void GetRelatedProducts_UnknownUpgradeCode_IsEmpty()
    {
        Assert.Empty(MsiProducts.GetRelatedProducts($"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}"));
    }
}
