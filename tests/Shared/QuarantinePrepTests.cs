using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Preparing quarantine\, real hard links, and what a refusal tells an administrator.
/// These run unelevated; the full repair walk sets owners and needs SYSTEM or an
/// elevated administrator, so it is exercised on a machine, not here.
/// </summary>
public class QuarantinePrepTests : IDisposable
{
    private readonly string _dir;

    public QuarantinePrepTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "QuarantinePrep", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr security);

    private static void MakeJunction(string link, string target)
    {
        // Any user can make a junction in a folder they can write; no privilege needed.
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "junction was not created");
    }

    [Fact]
    public void QuarantineThatIsAJunction_IsRemovedWithoutTouchingItsTarget()
    {
        var target = Path.Combine(_dir, "where-the-user-wants-files");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "untouched");
        var quarantine = Path.Combine(_dir, "quarantine");
        MakeJunction(quarantine, target);

        var secured = new List<string>();
        var usable = ManagedInstallsSecurity.PrepareQuarantineDir(quarantine, _dir,
            d => { secured.Add(d); return true; }, _ => { }, out _);

        Assert.True(Directory.Exists(quarantine));
        Assert.True((File.GetAttributes(quarantine) & FileAttributes.ReparsePoint) == 0, "quarantine is still a link");
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Equal(new[] { quarantine }, secured);
        // The test folder sits in a user profile, so it can never pass as admin-only.
        Assert.False(usable);
    }

    [Fact]
    public void QuarantineAUserMade_IsMovedAsideAndReplaced()
    {
        var quarantine = Path.Combine(_dir, "quarantine");
        Directory.CreateDirectory(quarantine);
        File.WriteAllText(Path.Combine(quarantine, "planted.txt"), "x");
        var owner = (SecurityIdentifier)new DirectoryInfo(quarantine).GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier))!;

        ManagedInstallsSecurity.PrepareQuarantineDir(quarantine, _dir, _ => true, _ => { }, out _);

        var aside = Directory.GetDirectories(_dir, "quarantine.untrusted-*");
        if (ManagedInstallsSecurity.IsTrustedPrincipal(owner))
        {
            // Elevated runs create folders owned by Administrators: nothing to move aside.
            Assert.Empty(aside);
        }
        else
        {
            var moved = Assert.Single(aside);
            Assert.True(File.Exists(Path.Combine(moved, "planted.txt")));
            Assert.False(File.Exists(Path.Combine(quarantine, "planted.txt")));
        }
        Assert.True(Directory.Exists(quarantine));
    }

    [Fact]
    public void IsAdminOnly_RealHardLinkOnTheSameVolume_IsRefusedAsAHardLink()
    {
        var target = Path.Combine(_dir, "original.ps1");
        File.WriteAllText(target, "exit 0");
        var link = Path.Combine(_dir, "preflight.ps1");
        Assert.True(CreateHardLink(link, target, IntPtr.Zero));

        Assert.False(ManagedInstallsSecurity.IsAdminOnly(link, out var reason));
        Assert.Contains("hard link", reason);

        Assert.False(ManagedInstallsSecurity.TryReadRequest(link, requireTrustedOwner: false, out _, out var readReason));
        Assert.Contains("hard link", readReason);
    }

    [Fact]
    public void Refusal_NamesThePrincipalAndTheRights()
    {
        var s = new FileSecurity();
        s.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        s.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Write, AccessControlType.Allow));

        var reason = ManagedInstallsSecurity.FindUntrustedWriter(s);

        Assert.Contains("Users", reason);
        Assert.Contains("Write", reason);
    }
}
