using Xunit;
using Cimian.CLI.Cimiimport.Services;
using Cimian.Core.Msi;
using Cimian.Tests.Shared;

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
    private readonly TestMsiFactory _msis = new();

    public void Dispose() => _msis.Dispose();

    [Fact]
    public void NoFileTable_IsWrapper()
    {
        // The regression: SELECT from a nonexistent table throws, and the catch
        // failed soft to true, so the purest wrapper shape -- no File table at
        // all -- was reported as installing files.
        using var db = MsiDatabase.OpenReadOnly(_msis.Create());

        Assert.False(MsiBomReader.HasInstalledFiles(db));
    }

    [Fact]
    public void EmptyFileTable_IsWrapper()
    {
        using var db = MsiDatabase.OpenReadOnly(_msis.Create(
            "CREATE TABLE `File` (`File` CHAR(72) NOT NULL PRIMARY KEY `File`)"));

        Assert.False(MsiBomReader.HasInstalledFiles(db));
    }

    [Fact]
    public void PopulatedFileTable_IsNotWrapper()
    {
        using var db = MsiDatabase.OpenReadOnly(_msis.Create(
            "CREATE TABLE `File` (`File` CHAR(72) NOT NULL PRIMARY KEY `File`)",
            "INSERT INTO `File` (`File`) VALUES ('payload.exe')"));

        Assert.True(MsiBomReader.HasInstalledFiles(db));
    }
}

/// <summary>
/// EnumerateInstalledFiles against a real MSI whose File, Component and
/// Directory tables mirror what WiX and cimipkg write: short|long names, a
/// "." DefaultDir, a source:target DefaultDir, a well-known root, a null
/// Version and a file that is not its component's keypath.
/// </summary>
public class MsiBomReaderEnumerateTests : IDisposable
{
    private readonly TestMsiFactory _msis = new();

    public void Dispose() => _msis.Dispose();

    private string BuildBomMsi() => _msis.Create(
        "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)",
        "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)",
        "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)",
        "INSERT INTO `Directory` (`Directory`, `DefaultDir`) VALUES ('TARGETDIR', 'SourceDir')",
        "INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('ProgramFiles64Folder', 'TARGETDIR', '.')",
        "INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('INSTALLDIR', 'ProgramFiles64Folder', 'MYAPP~1|My App')",
        "INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('BINDIR', 'INSTALLDIR', 'src:bin')",
        "INSERT INTO `Component` (`Component`, `Directory_`, `Attributes`, `KeyPath`) VALUES ('Main', 'INSTALLDIR', 256, 'main.exe')",
        "INSERT INTO `Component` (`Component`, `Directory_`, `Attributes`, `KeyPath`) VALUES ('Tools', 'BINDIR', 256, 'readme.txt')",
        "INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Version`, `Sequence`) VALUES ('main.exe', 'Main', 'MYAPP~1.EXE|MyApp.exe', 5000, '1.2.3.4', 1)",
        "INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Sequence`) VALUES ('tool.exe', 'Tools', 'tool.exe', 9000, 2)",
        "INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Sequence`) VALUES ('readme.txt', 'Tools', 'readme.txt', 100, 3)");

    [Fact]
    public void ResolvesPathsSizesVersionsAndKeyPaths_LargestFirst()
    {
        using var db = MsiDatabase.OpenReadOnly(BuildBomMsi());
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var files = MsiBomReader.EnumerateInstalledFiles(db);

        Assert.Equal(2, files.Count);
        Assert.Equal(new MsiInstalledFile(Path.Combine(programFiles, "My App", "bin", "tool.exe"), 9000, null, false), files[0]);
        Assert.Equal(new MsiInstalledFile(Path.Combine(programFiles, "My App", "MyApp.exe"), 5000, "1.2.3.4", true), files[1]);
    }

    [Fact]
    public void ExtensionFilterIsApplied()
    {
        using var db = MsiDatabase.OpenReadOnly(BuildBomMsi());

        var files = MsiBomReader.EnumerateInstalledFiles(db, [".txt"]);

        Assert.Equal("readme.txt", Path.GetFileName(Assert.Single(files).AbsolutePath));
    }

    [Fact]
    public void MissingTables_ReturnsEmpty()
    {
        using var db = MsiDatabase.OpenReadOnly(_msis.Create());

        Assert.Empty(MsiBomReader.EnumerateInstalledFiles(db));
    }
}
