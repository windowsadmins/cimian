using System.Security.AccessControl;
using System.Security.Principal;
using Cimian.Core;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// The repair only takes access away from objects whose permissions it does not set
/// outright. These check the DACL arithmetic itself and run unelevated; the walk over a
/// real tree is in <see cref="RepairWalkerTests"/>.
/// </summary>
public class RepairTightenTests
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier NamedAdmin = new("S-1-5-21-1111111111-2222222222-3333333333-1001");
    private static readonly SecurityIdentifier NamedUser = new("S-1-5-21-1111111111-2222222222-3333333333-1002");

    private const int Ordinary = (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);

    private static bool Trusted(SecurityIdentifier sid) =>
        ManagedInstallsSecurity.IsTrustedPrincipal(sid) || sid == NamedAdmin;

    private static RawAcl Acl(params (SecurityIdentifier Sid, FileSystemRights Rights, AceFlags Flags, AceQualifier Qualifier)[] aces)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, aces.Length);
        foreach (var a in aces)
        {
            acl.InsertAce(acl.Count, new CommonAce(a.Flags, a.Qualifier, (int)a.Rights, a.Sid, false, null));
        }
        return acl;
    }

    private static (SecurityIdentifier, FileSystemRights, AceFlags, AceQualifier) Allow(SecurityIdentifier sid, FileSystemRights rights,
        AceFlags flags = AceFlags.None) => (sid, rights, flags, AceQualifier.AccessAllowed);

    private static List<(string Sid, int Mask)> Allowed(RawAcl acl) =>
        acl.OfType<CommonAce>().Where(a => a.AceQualifier == AceQualifier.AccessAllowed)
            .Select(a => (a.SecurityIdentifier.Value, a.AccessMask)).ToList();

    [Fact]
    public void AdminLockedBackupOfConfig_IsLeftAlone()
    {
        // Config.yaml.bak an administrator locked to SYSTEM and Administrators: nothing to take away.
        var dacl = Acl(Allow(System, FileSystemRights.FullControl), Allow(Admins, FileSystemRights.FullControl));

        Assert.Null(ManagedInstallsSecurity.TightenAcl(dacl, Ordinary, Trusted));
    }

    [Fact]
    public void EmptyExplicitDacl_IsLeftAlone()
    {
        Assert.Null(ManagedInstallsSecurity.TightenAcl(new RawAcl(GenericAcl.AclRevision, 0), Ordinary, Trusted));
    }

    [Fact]
    public void UsersReadOnAnOrdinaryObject_IsWithinWhatTheRootHandsDown()
    {
        var dacl = Acl(Allow(Admins, FileSystemRights.FullControl), Allow(Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));

        Assert.Null(ManagedInstallsSecurity.TightenAcl(dacl, Ordinary, Trusted));
    }

    [Fact]
    public void UsersModify_IsCutBackToRead()
    {
        var dacl = Acl(Allow(Admins, FileSystemRights.FullControl), Allow(Users, FileSystemRights.Modify | FileSystemRights.Synchronize));

        var tightened = ManagedInstallsSecurity.TightenAcl(dacl, Ordinary, Trusted);

        Assert.NotNull(tightened);
        Assert.Contains((Users.Value, Ordinary), Allowed(tightened!));
        Assert.Contains((Admins.Value, (int)FileSystemRights.FullControl), Allowed(tightened!));
    }

    [Fact]
    public void UsersRead_InsideAnAdminOnlyFolder_IsRemoved()
    {
        // A cached installer must not be readable by a standard user.
        var dacl = Acl(Allow(Admins, FileSystemRights.FullControl), Allow(Users, FileSystemRights.Read));

        var tightened = ManagedInstallsSecurity.TightenAcl(dacl, 0, Trusted);

        Assert.NotNull(tightened);
        Assert.DoesNotContain(Allowed(tightened!), a => a.Sid == Users.Value);
    }

    [Fact]
    public void OtherUntrustedPrincipals_AreRemoved()
    {
        var dacl = Acl(Allow(Admins, FileSystemRights.FullControl), Allow(Everyone, FileSystemRights.Read),
            Allow(NamedUser, FileSystemRights.FullControl));

        var tightened = ManagedInstallsSecurity.TightenAcl(dacl, Ordinary, Trusted);

        Assert.Equal([(Admins.Value, (int)FileSystemRights.FullControl)], Allowed(tightened!));
    }

    [Fact]
    public void AnAdministratorsMembersEntry_IsKept()
    {
        var dacl = Acl(Allow(Admins, FileSystemRights.FullControl), Allow(NamedAdmin, FileSystemRights.FullControl));

        Assert.Null(ManagedInstallsSecurity.TightenAcl(dacl, Ordinary, Trusted));
    }

    [Fact]
    public void AnAdministratorsMembersEntry_IsHandedToTheGroup_WhereTheRuntimeChecks()
    {
        var dacl = Acl(Allow(System, FileSystemRights.FullControl), Allow(NamedAdmin, FileSystemRights.Modify));

        var tightened = ManagedInstallsSecurity.TightenAcl(dacl, 0, Trusted, sid => sid == NamedAdmin);

        Assert.Equal([(System.Value, (int)FileSystemRights.FullControl), (Admins.Value, (int)FileSystemRights.Modify)], Allowed(tightened!));
    }

    [Fact]
    public void InheritOnlyEntryForUsers_IsCutBackToo()
    {
        // It grants nothing on the folder itself but Modify on everything created inside it.
        var dacl = Acl(Allow(Admins, FileSystemRights.FullControl),
            Allow(Users, FileSystemRights.Modify, AceFlags.ObjectInherit | AceFlags.ContainerInherit | AceFlags.InheritOnly));

        var tightened = ManagedInstallsSecurity.TightenAcl(dacl, Ordinary, Trusted);

        var users = Assert.Single(tightened!.OfType<CommonAce>(), a => a.SecurityIdentifier == Users);
        Assert.Equal(0, users.AccessMask & ~Ordinary);
        Assert.True((users.AceFlags & AceFlags.InheritOnly) != 0);
    }

    [Fact]
    public void DenyEntries_AreKept()
    {
        var dacl = Acl(
            (Users, FileSystemRights.Write, AceFlags.None, AceQualifier.AccessDenied),
            Allow(Admins, FileSystemRights.FullControl),
            Allow(Everyone, FileSystemRights.Read));

        var tightened = ManagedInstallsSecurity.TightenAcl(dacl, Ordinary, Trusted);

        Assert.Contains(tightened!.OfType<CommonAce>(), a => a.AceQualifier == AceQualifier.AccessDenied && a.SecurityIdentifier == Users);
        Assert.DoesNotContain(tightened!.OfType<CommonAce>(), a => a.SecurityIdentifier == Everyone);
    }

    [Fact]
    public void InheritedEntries_AreNotRewritten()
    {
        // Inherited entries come from the parent, which the repair handles in its own right.
        var dacl = Acl(Allow(Users, FileSystemRights.Modify, AceFlags.Inherited));

        Assert.Null(ManagedInstallsSecurity.TightenAcl(dacl, 0, Trusted));
    }

    [Fact]
    public void NullDacl_BecomesEmpty()
    {
        // A NULL DACL grants everyone everything.
        var tightened = ManagedInstallsSecurity.TightenAcl(null, Ordinary, Trusted);

        Assert.NotNull(tightened);
        Assert.Equal(0, tightened!.Count);
    }

    [Fact]
    public void Layout_PutsEveryLocationUnderItsRoot()
    {
        var layout = new ManagedInstallsSecurity.RepairLayout(@"C:\Temp\MI");

        Assert.Equal(@"C:\Temp\MI\Cache", layout.CacheDir);
        Assert.Equal(@"C:\Temp\MI\Config.yaml", layout.ConfigYaml);
        Assert.Contains(@"C:\Temp\MI\sbin", layout.ExecutedDirs);
        Assert.Contains(@"C:\Temp\MI\selfservice", layout.UserRequestFolders);
        Assert.Equal(CimianPaths.CacheDir, ManagedInstallsSecurity.RepairLayout.Default.CacheDir);
    }
}
