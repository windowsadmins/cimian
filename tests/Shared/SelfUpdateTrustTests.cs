using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// The self-update flag names an installer that CimianWatcher runs as SYSTEM. Before the
/// ManagedInstalls lockdown any user could create that flag; it must not be honoured
/// unless only SYSTEM or an administrator could have written it and the installer.
/// </summary>
public class SelfUpdateTrustTests : IDisposable
{
    private readonly string _dir;

    public SelfUpdateTrustTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "SelfUpdateTrust", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RequestAnyUserCouldHaveWritten_IsRefused()
    {
        var flag = Path.Combine(_dir, ".cimian.selfupdate");
        var msi = Path.Combine(_dir, "evil.msi");
        File.WriteAllText(flag, $"Item: Cimian\nVersion: 9\nInstallerType: msi\nLocalFile: {msi}\n");
        File.WriteAllText(msi, "not really an msi");

        Assert.False(SelfUpdateService.IsTrustedRequest(flag, msi, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void MissingInstaller_IsRefused()
    {
        var flag = Path.Combine(_dir, ".cimian.selfupdate");
        File.WriteAllText(flag, "Item: Cimian\n");

        Assert.False(SelfUpdateService.IsTrustedRequest(flag, Path.Combine(_dir, "gone.msi"), out _));
    }
}
