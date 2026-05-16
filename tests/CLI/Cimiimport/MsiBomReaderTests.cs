using Xunit;
using Cimian.CLI.Cimiimport.Services;

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
