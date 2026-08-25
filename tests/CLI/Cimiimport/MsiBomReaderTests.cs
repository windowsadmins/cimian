using Xunit;
using Cimian.CLI.Cimiimport.Services;
using WixToolset.Dtf.WindowsInstaller;

namespace Cimian.Tests.CLI.Cimiimport;

/// <summary>
/// Unit tests for the heuristic that picks a single primary binary out of the
/// MSI BOM enumeration. The Database-backed enumeration itself is exercised
/// end-to-end via integration with real MSIs; here we lock in the pick logic.
/// </summary>
public class MsiBomReaderTests
{
    [Fact]
    public void PickPrimaryBinary_EmptyList_ReturnsNull()
    {
        var result = MsiBomReader.PickPrimaryBinary(new List<MsiInstalledFile>(), "AnyProduct");
        Assert.Null(result);
    }

    [Fact]
    public void PickPrimaryBinary_SingleExe_ReturnsThatExe()
    {
        var files = new List<MsiInstalledFile>
        {
            new(@"C:\Program Files\AnyProduct\only.exe", 100, "1.0.0.0", IsKeyPath: true),
        };

        var result = MsiBomReader.PickPrimaryBinary(files, "AnyProduct");

        Assert.Equal(@"C:\Program Files\AnyProduct\only.exe", result);
    }

    [Fact]
    public void PickPrimaryBinary_NameMatchWinsOverLargest()
    {
        // The list is ordered largest-first, so "huge.exe" would be the
        // fallback. "MyProduct" matches MyProduct.exe — that should win even
        // though it isn't the biggest.
        var files = new List<MsiInstalledFile>
        {
            new(@"C:\Program Files\MyProduct\huge.exe",      100_000_000, "1.0", IsKeyPath: true),
            new(@"C:\Program Files\MyProduct\MyProduct.exe",  10_000_000, "1.0", IsKeyPath: true),
            new(@"C:\Program Files\MyProduct\helper.exe",      1_000_000, "1.0", IsKeyPath: true),
        };

        var result = MsiBomReader.PickPrimaryBinary(files, "MyProduct");

        Assert.Equal(@"C:\Program Files\MyProduct\MyProduct.exe", result);
    }

    [Fact]
    public void PickPrimaryBinary_NameMatchIsCaseInsensitive()
    {
        var files = new List<MsiInstalledFile>
        {
            new(@"C:\Program Files\ReportMate\manageDREPORTSrunner.exe", 25_000_000, "1.0", IsKeyPath: true),
            new(@"C:\Program Files\ReportMate\speedtest.exe",             2_000_000, "1.0", IsKeyPath: true),
        };

        // Note: product name "ManagedReportsRunner" matches the .exe stem
        // case-insensitively even though casing differs in both.
        var result = MsiBomReader.PickPrimaryBinary(files, "managedreportsrunner");

        Assert.Equal(@"C:\Program Files\ReportMate\manageDREPORTSrunner.exe", result);
    }

    [Fact]
    public void PickPrimaryBinary_NoNameMatch_ReturnsLargest()
    {
        // ReportMate's real scenario: product name "ReportMate" doesn't match
        // any .exe filename, but managedreportsrunner.exe is the largest. The
        // largest-wins fallback gives the correct keypath.
        var files = new List<MsiInstalledFile>
        {
            new(@"C:\Program Files\ReportMate\managedreportsrunner.exe", 25_000_000, "2026.05.14.1242", IsKeyPath: true),
            new(@"C:\Program Files\ReportMate\speedtest.exe",             2_000_000, "3.8.0",           IsKeyPath: true),
        };

        var result = MsiBomReader.PickPrimaryBinary(files, "ReportMate");

        Assert.Equal(@"C:\Program Files\ReportMate\managedreportsrunner.exe", result);
    }

    [Fact]
    public void PickPrimaryBinary_EmptyProductName_FallsThroughToLargest()
    {
        var files = new List<MsiInstalledFile>
        {
            new(@"C:\Program Files\Vendor\big.exe",   100, "1.0", IsKeyPath: true),
            new(@"C:\Program Files\Vendor\small.exe",  10, "1.0", IsKeyPath: true),
        };

        var result = MsiBomReader.PickPrimaryBinary(files, "");

        Assert.Equal(@"C:\Program Files\Vendor\big.exe", result);
    }
}

/// <summary>
/// HasInstalledFiles decides whether an MSI is a "wrapper" (no payload of its
/// own). That single boolean gates the ArpDisplayName hint, which gates the
/// runtime's ARP DisplayName fallback -- so getting it wrong surfaces as
/// "MSI not registered in Windows Installer" for a product that installed fine.
/// These build real MSI databases because the distinction under test is
/// table-missing vs table-empty, which cannot be expressed without one.
/// </summary>
public class MsiBomReaderHasInstalledFilesTests : IDisposable
{
    private readonly List<string> _temp = new();

    private Database NewMsi()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bomtest_{Guid.NewGuid():N}.msi");
        _temp.Add(path);
        return new Database(path, DatabaseOpenMode.CreateDirect);
    }

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    [Fact]
    public void NoFileTable_IsWrapper()
    {
        // The regression: SELECT from a nonexistent table throws, and the catch
        // failed soft to true, so the purest wrapper shape -- no File table at
        // all -- was reported as installing files.
        using var db = NewMsi();

        Assert.False(MsiBomReader.HasInstalledFiles(db));
    }

    [Fact]
    public void EmptyFileTable_IsWrapper()
    {
        using var db = NewMsi();
        db.Execute("CREATE TABLE `File` (`File` CHAR(72) NOT NULL PRIMARY KEY `File`)");

        Assert.False(MsiBomReader.HasInstalledFiles(db));
    }

    [Fact]
    public void PopulatedFileTable_IsNotWrapper()
    {
        using var db = NewMsi();
        db.Execute("CREATE TABLE `File` (`File` CHAR(72) NOT NULL PRIMARY KEY `File`)");
        db.Execute("INSERT INTO `File` (`File`) VALUES ('payload.exe')");

        Assert.True(MsiBomReader.HasInstalledFiles(db));
    }
}
