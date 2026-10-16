using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core;
using Cimian.Core.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// SYSTEM downloads into CachePath and runs what it finds there. Inside ManagedInstalls only
/// Cache\ is a cache; outside it, system and shared folders are never one. Anything refused
/// falls back to Cache\. Nothing here changes a folder; CachePath is only checked at load.
/// </summary>
public class CachePathLocationTests
{
    private static string Constrained(string cachePath) =>
        ConfigurationService.ConstrainCachePath(new CimianConfig { CachePath = cachePath }).CachePath;

    [Fact]
    public void DefaultCache_IsKept() => Assert.Equal(CimianPaths.CacheDir, Constrained(CimianPaths.CacheDir));

    [Fact]
    public void SubfolderOfCache_IsKept()
    {
        var sub = Path.Combine(CimianPaths.CacheDir, "large");
        Assert.Equal(sub, Constrained(sub));
    }

    [Theory]
    [InlineData("")]
    [InlineData("triggers")]
    [InlineData("selfservice")]
    [InlineData("sbin")]
    [InlineData("conditions")]
    [InlineData("catalogs")]
    [InlineData("quarantine")]
    [InlineData("logs")]
    public void OtherFoldersInManagedInstalls_FallBackToCache(string relative)
    {
        var path = relative.Length == 0 ? CimianPaths.ManagedInstallsRoot : Path.Combine(CimianPaths.ManagedInstallsRoot, relative);
        Assert.Equal(CimianPaths.CacheDir, Constrained(path));
    }

    /// <summary>Each refused folder with the end of the reason it must be refused for.</summary>
    public static TheoryData<string, string> SystemFolders()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        var users = Path.Combine(systemDrive, "Users");
        var data = new TheoryData<string, string>
        {
            // ManagedInstalls lives in %ProgramData%, so that rule refuses it first.
            { programData, "contains ManagedInstalls" },
            { windows, $"is in {windows}" },
            { Path.Combine(windows, "Temp"), $"is in {windows}" },
            { programFiles, $"is in {programFiles}" },
            { Path.Combine(programFiles, "SomeApp", "cache"), $"is in {programFiles}" },
            { users, $"is in {users}" },
            { Path.Combine(users, "Public", "cache"), $"is in {users}" },
            { systemDrive, "is a drive root" },
            { @"D:\", "is a drive root" },
            { @"\\server\share\cache", "is a network path" },
        };
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(x86) && x86 != programFiles) data.Add(x86, $"is in {x86}");
        return data;
    }

    [Theory]
    [MemberData(nameof(SystemFolders))]
    public void SystemAndSharedFolders_AreRefusedForTheirRule_AndFallBackToCache(string path, string reasonEnding)
    {
        Assert.True(ManagedInstallsSecurity.IsForbiddenCacheLocation(path, out var reason));
        Assert.Equal($"{path} {reasonEnding}", reason);
        Assert.Equal(CimianPaths.CacheDir, Constrained(path));
    }

    [Fact]
    public void ExternalFolderThatDoesNotExist_IsNotCreated_AndFallsBack()
    {
        var path = Path.Combine(Path.GetTempPath(), "CimianTests", "NoCache", Guid.NewGuid().ToString("N"));

        Assert.Equal(CimianPaths.CacheDir, Constrained(path));
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void ExternalFolderUsersCanChange_FallsBack()
    {
        var path = Path.Combine(Path.GetTempPath(), "CimianTests", "UserCache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Assert.Equal(CimianPaths.CacheDir, Constrained(path));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
