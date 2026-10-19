using System.Runtime.InteropServices;
using Cimian.Core;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// How CimianWatcher (SYSTEM) reads run requests that the signed-in user drops in
/// ManagedInstalls\triggers\.
/// </summary>
public class TriggerRequestsTests : IDisposable
{
    private readonly string _dir;

    public TriggerRequestsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "TriggerRequests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr security);

    [Fact]
    public void NewRequestPath_IsUniquePerRequestAndInTheTriggersFolder()
    {
        // One fixed name let one user block another's request by holding the file open or
        // re-creating it. Each request gets its own file.
        var a = TriggerRequests.NewRequestPath(gui: true);
        var b = TriggerRequests.NewRequestPath(gui: true);

        Assert.NotEqual(a, b);
        Assert.Equal(CimianPaths.TriggersDir, Path.GetDirectoryName(a));
        Assert.True(TriggerRequests.IsRequestFile(a));
        Assert.True(TriggerRequests.IsGuiRequest(a));
        Assert.False(TriggerRequests.IsGuiRequest(TriggerRequests.NewRequestPath(gui: false)));
    }

    [Theory]
    [InlineData(".cimian.bootstrap", true)]
    [InlineData(".cimian.headless", true)]
    [InlineData(".cimian.bootstrap.5f0c", true)]
    [InlineData("SelfServeManifest.yaml", false)]
    [InlineData(".cimian.test", false)]
    public void IsRequestFile_RecognisesOnlyRequestNames(string name, bool expected)
    {
        Assert.Equal(expected, TriggerRequests.IsRequestFile(Path.Combine(_dir, name)));
    }

    [Fact]
    public void TryRead_SmallPlainFile_ReturnsItsContent()
    {
        var path = Path.Combine(_dir, ".cimian.bootstrap.1");
        File.WriteAllText(path, "Args: --checkonly\n");

        Assert.True(ManagedInstallsSecurity.TryReadRequest(path, requireTrustedOwner: false, out var content, out var reason), reason);
        Assert.Equal("Args: --checkonly\n", content);
    }

    [Fact]
    public void TryRead_OversizedFile_IsRefused()
    {
        var path = Path.Combine(_dir, ".cimian.bootstrap.big");
        File.WriteAllText(path, new string('x', TriggerRequests.MaxRequestBytes + 1));

        Assert.False(ManagedInstallsSecurity.TryReadRequest(path, requireTrustedOwner: false, out var content, out _));
        Assert.Null(content);
    }

    [Fact]
    public void TryRead_HardLink_IsRefused()
    {
        // A request that is a second name for some other file would have SYSTEM read that file.
        var target = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(target, "Args: --auto");
        var link = Path.Combine(_dir, ".cimian.bootstrap.link");
        Assert.True(CreateHardLink(link, target, IntPtr.Zero));

        Assert.False(ManagedInstallsSecurity.TryReadRequest(link, requireTrustedOwner: false, out _, out var reason));
        Assert.Contains("link", reason);
    }

    [Fact]
    public void TryRead_RootFlagNotOwnedByAnAdministrator_IsRefused()
    {
        var path = Path.Combine(_dir, ".cimian.bootstrap");
        File.WriteAllText(path, "Args: --auto");
        TestFileOwner.SetToCurrentUser(path);

        Assert.False(ManagedInstallsSecurity.TryReadRequest(path, requireTrustedOwner: true, out _, out _));
    }

    [Fact]
    public void Parse_NoArgsLine_UsesDefaults()
    {
        Assert.Equal(TriggerRequestKind.Defaults,
            TriggerRequests.Parse("Bootstrap triggered at: now\nMode: GUI\n", out var args, out _));
        Assert.Null(args);
    }

    [Fact]
    public void Parse_AllowedArgs_AreAccepted()
    {
        Assert.Equal(TriggerRequestKind.Custom,
            TriggerRequests.Parse("Source: ManagedSoftwareCenter\nArgs: --checkonly --show-status -vv\n", out var args, out _));
        Assert.Equal("--checkonly --show-status -vv", args);
    }

    [Fact]
    public void Parse_AnyRefusedArgsLine_RefusesTheWholeRequest_WithoutEchoingIt()
    {
        var kind = TriggerRequests.Parse(
            "Args: --checkonly\nArgs: --config C:\\Users\\Public\\SECRET-TOKEN.yaml\n", out var args, out var reason);

        Assert.Equal(TriggerRequestKind.Refused, kind);
        Assert.Null(args);
        // logs\ is readable by every user, so a refusal never repeats what the file said.
        Assert.DoesNotContain("SECRET-TOKEN", reason);
        Assert.DoesNotContain("--config", reason);
    }
}
