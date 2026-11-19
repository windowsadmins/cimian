using Cimian.CLI.managedsoftwareupdate.Services;
using WixToolset.Dtf.WindowsInstaller;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

public class MsiSourceStagingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"msi-source-tests-{Guid.NewGuid():N}");

    public MsiSourceStagingTests() => Directory.CreateDirectory(root);

    [Fact]
    public void RenamedUpdate_RetainsExactNewBytesUnderRegisteredName()
    {
        var incoming = CreatePackage("Example-2.5.1.200.msi");
        var original = File.ReadAllBytes(incoming);
        var staged = MsiSourceStaging.Prepare(incoming, "Example-2.5.1.100.msi");

        Assert.Equal("Example-2.5.1.100.msi", Path.GetFileName(staged));
        Assert.NotEqual(Path.GetDirectoryName(incoming), Path.GetDirectoryName(staged));
        Assert.Equal(original, File.ReadAllBytes(staged));
        Assert.Equal(original, File.ReadAllBytes(incoming));
        Assert.Equal(staged, MsiSourceStaging.Prepare(incoming, "Example-2.5.1.100.msi"));
        // The retained copy is usable after normal download-cache removal.
        File.Delete(incoming);
        using var database = new Database(staged, DatabaseOpenMode.ReadOnly);
        Assert.Equal("2.5.1.200", database.ExecuteScalar("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'"));
    }

    [Fact]
    public void SameRegisteredNameOrMissingName_DoesNotRelocate()
    {
        var incoming = Path.Combine(root, "Example.msi");
        Assert.Equal(incoming, MsiSourceStaging.Prepare(incoming, "EXAMPLE.MSI"));
        Assert.Equal(incoming, MsiSourceStaging.Prepare(incoming, null));
    }

    [Fact]
    public void DifferentContents_UseSeparateSourcesWithoutOverwritingOldVersion()
    {
        var incoming = CreatePackage("new.msi");
        var first = MsiSourceStaging.Prepare(incoming, "original.msi");
        var firstBytes = File.ReadAllBytes(first);
        using (var database = new Database(incoming, DatabaseOpenMode.Direct))
        {
            database.Execute("UPDATE `Property` SET `Value` = '2.5.1.300' WHERE `Property` = 'ProductVersion'");
            database.Commit();
        }
        var second = MsiSourceStaging.Prepare(incoming, "original.msi");
        Assert.NotEqual(first, second);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));
        Assert.Equal(File.ReadAllBytes(incoming), File.ReadAllBytes(second));
    }

    [Fact]
    public void CorruptExistingSource_IsRejectedWithoutOverwritingEitherFile()
    {
        var incoming = CreatePackage("new.msi");
        var staged = MsiSourceStaging.Prepare(incoming, "original.msi");
        var incomingBytes = File.ReadAllBytes(incoming);
        File.WriteAllText(staged, "unexpected contents");
        Assert.Throws<IOException>(() => MsiSourceStaging.Prepare(incoming, "original.msi"));
        Assert.Equal("unexpected contents", File.ReadAllText(staged));
        Assert.Equal(incomingBytes, File.ReadAllBytes(incoming));
    }

    [Theory]
    [InlineData("../escape.msi")]
    [InlineData("..\\escape.msi")]
    [InlineData("C:\\escape.msi")]
    [InlineData("\\\\server\\share\\escape.msi")]
    [InlineData("original.msi:stream.msi")]
    [InlineData("original.msi\" REBOOT=Force")]
    [InlineData("original.exe")]
    [InlineData("original.msi ")]
    [InlineData("NUL.msi")]
    [InlineData("COM1.msi")]
    [InlineData("LPT¹.msi")]
    [InlineData("")]
    public void UnsafeNames_AreRejected(string name)
    {
        Assert.Throws<InvalidOperationException>(() => MsiSourceStaging.ValidatePackageName(name));
        Assert.False(Directory.Exists(Path.Combine(root, ".msi-sources")));
    }

    [Theory]
    [InlineData("files.cab")]
    [InlineData("../files.cab")]
    public void ExternalCabinet_IsRejectedBeforeRelocation(string cabinet)
    {
        var incoming = CreatePackage("new.msi", cabinet);
        Assert.Throws<InvalidOperationException>(() => MsiSourceStaging.Prepare(incoming, "original.msi"));
        Assert.False(Directory.Exists(Path.Combine(root, ".msi-sources")));
    }

    [Fact]
    public void EmbeddedCabinet_AllowsRelocation()
    {
        var incoming = CreatePackage("new.msi", "#embedded.cab");
        Assert.NotEqual(incoming, MsiSourceStaging.Prepare(incoming, "original.msi"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0x2000, true)]
    public void LooseFiles_AreRejectedBeforeRelocation(int attributes, bool compressedDefault)
    {
        var incoming = CreatePackage("new.msi", attributes: attributes, compressedDefault: compressedDefault);
        Assert.Throws<InvalidOperationException>(() => MsiSourceStaging.Prepare(incoming, "original.msi"));
        Assert.False(Directory.Exists(Path.Combine(root, ".msi-sources")));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0x4000, false)]
    public void CompressedFiles_AllowRelocation(int attributes, bool compressedDefault)
    {
        var incoming = CreatePackage("new.msi", "#embedded.cab", attributes, compressedDefault);
        Assert.NotEqual(incoming, MsiSourceStaging.Prepare(incoming, "original.msi"));
    }

    private string CreatePackage(string name, string? cabinet = null, int? attributes = null, bool compressedDefault = false)
    {
        var path = Path.Combine(root, name);
        using var database = new Database(path, DatabaseOpenMode.CreateDirect);
        database.Execute("CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)");
        database.Execute("INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductVersion', '2.5.1.200')");
        if (cabinet != null)
        {
            database.Execute("CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `Cabinet` CHAR(255) PRIMARY KEY `DiskId`)");
            database.Execute($"INSERT INTO `Media` (`DiskId`, `Cabinet`) VALUES (1, '{cabinet}')");
        }
        if (attributes != null)
        {
            database.Execute("CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Attributes` SHORT PRIMARY KEY `File`)");
            database.Execute($"INSERT INTO `File` (`File`, `Attributes`) VALUES ('Example', {attributes})");
            database.SummaryInfo.WordCount = compressedDefault ? 2 : 0;
            database.SummaryInfo.Template = "x64;1033";
            database.SummaryInfo.RevisionNumber = $"{{{Guid.NewGuid()}}}";
            database.SummaryInfo.PageCount = 200;
        }
        database.Commit();
        return path;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
