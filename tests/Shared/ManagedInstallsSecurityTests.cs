using System.Security.AccessControl;
using System.Security.Principal;
using Cimian.Core;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// The trust rules behind every "may SYSTEM run this?" decision, and the ACLs the
/// installer writes on ManagedInstalls.
/// </summary>
public class ManagedInstallsSecurityTests : IDisposable
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    private readonly string _dir;

    public ManagedInstallsSecurityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "MISecurity", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static FileSecurity AdminOnlyFile()
    {
        var s = new FileSecurity();
        s.SetOwner(Admins);
        s.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return s;
    }

    [Fact]
    public void FindUntrustedWriter_AdminOwnedAndUsersReadOnly_IsTrusted()
    {
        Assert.Null(ManagedInstallsSecurity.FindUntrustedWriter(AdminOnlyFile()));
    }

    [Fact]
    public void FindUntrustedWriter_TrustedInstallerOwner_IsTrusted()
    {
        var s = AdminOnlyFile();
        s.SetOwner(new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"));
        Assert.Null(ManagedInstallsSecurity.FindUntrustedWriter(s));
    }

    [Fact]
    public void FindUntrustedWriter_UserOwner_IsUntrusted()
    {
        var s = AdminOnlyFile();
        s.SetOwner(Users);
        Assert.Contains("owned by", ManagedInstallsSecurity.FindUntrustedWriter(s));
    }

    [Theory]
    [InlineData(FileSystemRights.Write)]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.FullControl)]
    public void FindUntrustedWriter_AnyWriteRightForUsers_IsUntrusted(FileSystemRights rights)
    {
        var s = AdminOnlyFile();
        s.AddAccessRule(new FileSystemAccessRule(Users, rights, AccessControlType.Allow));
        Assert.Contains("writable by", ManagedInstallsSecurity.FindUntrustedWriter(s));
    }

    [Fact]
    public void FindUntrustedWriter_EveryoneOrAuthenticatedUsersWrite_IsUntrusted()
    {
        foreach (var sid in new[] { Everyone, AuthenticatedUsers })
        {
            var s = AdminOnlyFile();
            s.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Write, AccessControlType.Allow));
            Assert.NotNull(ManagedInstallsSecurity.FindUntrustedWriter(s));
        }
    }

    [Fact]
    public void FindUntrustedWriter_InheritOnlyEntry_DoesNotApplyToTheFolderItself()
    {
        var s = new DirectorySecurity();
        s.SetOwner(Admins);
        s.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(CreatorOwner, FileSystemRights.Modify,
            InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        Assert.Null(ManagedInstallsSecurity.FindUntrustedWriter(s));
    }

    [Fact]
    public void FindUntrustedWriter_ProgramDataDefault_IsUntrusted()
    {
        // %ProgramData% grants Users "create files / create folders" on the folder itself,
        // which is exactly what let a user add sbin\ and conditions\.
        var s = new DirectorySecurity();
        s.SetOwner(System);
        s.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        Assert.Contains("writable by", ManagedInstallsSecurity.FindUntrustedWriter(s));
    }

    [Fact]
    public void IsAdminOnly_FileCreatedByTheTestUser_IsRefused()
    {
        // Test runs are not SYSTEM, so a file they create is owned by an ordinary account:
        // exactly the shape of a script a standard user planted.
        var script = Path.Combine(_dir, "preflight.ps1");
        File.WriteAllText(script, "exit 0");

        Assert.False(ManagedInstallsSecurity.IsAdminOnly(script, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void IsAdminOnly_HardLinkedFile_IsRefused()
    {
        // notepad.exe is admin-only but has a second name under WinSxS. A file with more
        // than one name can be changed through the other one, so it is never trusted.
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(notepad));

        Assert.False(ManagedInstallsSecurity.IsAdminOnly(notepad, out var reason));
        Assert.Contains("link", reason);
    }

    [Fact]
    public void IsAdminOnly_SystemFolder_IsTrustedAllTheWayToTheVolumeRoot()
    {
        // The volume root lets users add folders, which is harmless for a path that already
        // exists: nothing above an existing folder can swap it without delete or ACL rights.
        Assert.True(ManagedInstallsSecurity.IsAdminOnly(Environment.SystemDirectory, out var reason), reason);
    }

    [Fact]
    public void CreateQuarantineSession_RefusesAFolderAnyUserCanWrite()
    {
        // A quarantine folder a user made, or links inside it, would let SYSTEM move
        // files wherever the user chose. A session is only used inside an admin-only chain.
        Assert.Null(ManagedInstallsSecurity.CreateQuarantineSession(_dir, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryCreateDirectoryExclusive_FailsWhenTheNameAlreadyExists()
    {
        var existing = Path.Combine(_dir, "taken");
        Directory.CreateDirectory(existing);

        Assert.False(ManagedInstallsSecurity.TryCreateDirectoryExclusive(existing));
        Assert.True(ManagedInstallsSecurity.TryCreateDirectoryExclusive(Path.Combine(_dir, "fresh")));
    }

    [Fact]
    public void IsAdminOnly_MissingFile_IsRefused()
    {
        Assert.False(ManagedInstallsSecurity.IsAdminOnly(Path.Combine(_dir, "nope.ps1"), out _));
    }

    [Fact]
    public void RootAcl_GivesUsersNoWriteAnywhere()
    {
        var s = new DirectorySecurity();
        s.SetOwner(Admins);
        s.SetSecurityDescriptorBinaryForm(ToBinary(ManagedInstallsSecurity.RootAcl()), AccessControlSections.Access);
        Assert.Null(ManagedInstallsSecurity.FindUntrustedWriter(s));
    }

    [Fact]
    public void AdminOnlyAcl_HasNoEntryForUsers()
    {
        var aces = ManagedInstallsSecurity.AdminOnlyAcl().Cast<CommonAce>().ToList();
        Assert.All(aces, a => Assert.True(a.SecurityIdentifier == System || a.SecurityIdentifier == Admins));
    }

    [Theory]
    [MemberData(nameof(AdminOnlyLocations))]
    public void AdminOnlyLocations_AreClosedToUsers(string dir)
    {
        // Cache holds downloaded installers, some of them scripts carrying secrets, and
        // SYSTEM runs them; sbin and conditions hold scripts SYSTEM runs.
        Assert.True(ManagedInstallsSecurity.IsAdminOnlyLocation(dir));
    }

    public static TheoryData<string> AdminOnlyLocations() => new()
    {
        CimianPaths.CacheDir, CimianPaths.SbinDir, CimianPaths.ConditionsDir, CimianPaths.SelfUpdateBackupDir,
    };

    [Fact]
    public void LogsAndReportsStayReadableForTheGui()
    {
        Assert.False(ManagedInstallsSecurity.IsAdminOnlyLocation(CimianPaths.LogsDir));
        Assert.False(ManagedInstallsSecurity.IsAdminOnlyLocation(CimianPaths.ReportsDir));
        Assert.False(ManagedInstallsSecurity.IsAdminOnlyLocation(CimianPaths.IconsDir));
    }

    [Fact]
    public void TriggersAcl_UsersCanAddFilesButNothingInheritsUsersWrite()
    {
        var aces = ManagedInstallsSecurity.TriggersAcl().Cast<CommonAce>().ToList();
        var users = Assert.Single(aces, a => a.SecurityIdentifier == Users);
        Assert.Equal(AceFlags.None, users.AceFlags & (AceFlags.ObjectInherit | AceFlags.ContainerInherit));
        Assert.True((users.AccessMask & (int)FileSystemRights.CreateFiles) != 0);
        Assert.True((users.AccessMask & (int)FileSystemRights.CreateDirectories) == 0);
        Assert.True((users.AccessMask & (int)FileSystemRights.DeleteSubdirectoriesAndFiles) == 0);

        // Creators manage their own requests only; the entry never lands on the folder.
        var creator = Assert.Single(aces, a => a.SecurityIdentifier == CreatorOwner);
        Assert.True((creator.AceFlags & AceFlags.InheritOnly) != 0);
        Assert.True((creator.AceFlags & AceFlags.ContainerInherit) == 0);
    }

    [Fact]
    public void UserReadableAcls_AllowSynchronousWindowsIo()
    {
        var root = Assert.Single(ManagedInstallsSecurity.RootAcl().Cast<CommonAce>(), a => a.SecurityIdentifier == Users);
        var requests = ManagedInstallsSecurity.TriggersAcl().Cast<CommonAce>().ToList();
        var requestFolder = Assert.Single(requests, a => a.SecurityIdentifier == Users);
        var ownRequest = Assert.Single(requests, a => a.SecurityIdentifier == CreatorOwner);

        // Directory enumeration and ordinary FileStream opens request SYNCHRONIZE.
        // Hand-built ACEs must include it just as FileSystemAccessRule allow entries do.
        foreach (var ace in new[] { root, requestFolder, ownRequest })
        {
            Assert.True((ace.AccessMask & (int)FileSystemRights.Synchronize) != 0);
        }
    }

    [Fact]
    public void UserWritableFileAcl_CannotDeleteOrChangePermissions()
    {
        var users = Assert.Single(ManagedInstallsSecurity.UserWritableFileAcl().Cast<CommonAce>());
        Assert.True((users.AccessMask & (int)FileSystemRights.WriteData) != 0);
        Assert.True((users.AccessMask & (int)(FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0);
        Assert.Equal(AceFlags.None, users.AceFlags);
    }

    [Fact]
    public void Matches_ComparesOwnerTrustProtectionAndExplicitEntriesOnly()
    {
        var acl = ManagedInstallsSecurity.AdminOnlyAcl();
        var sd = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            Admins, null, null, acl);
        Assert.True(ManagedInstallsSecurity.Matches(sd, acl, isProtected: true));
        Assert.False(ManagedInstallsSecurity.Matches(sd, acl, isProtected: false));
        Assert.False(ManagedInstallsSecurity.Matches(sd, ManagedInstallsSecurity.RootAcl(), isProtected: true));

        var userOwned = new RawSecurityDescriptor(sd.ControlFlags, Users, null, null, acl);
        Assert.False(ManagedInstallsSecurity.Matches(userOwned, acl, isProtected: true));

        // SYSTEM owns what it creates; that is as good as Administrators.
        var systemOwned = new RawSecurityDescriptor(sd.ControlFlags, System, null, null, acl);
        Assert.True(ManagedInstallsSecurity.Matches(systemOwned, acl, isProtected: true));
    }

    [Fact]
    public void IsUnder_MatchesWholePathSegmentsOnly()
    {
        Assert.True(ManagedInstallsSecurity.IsUnder(Path.Combine(CimianPaths.SbinDir, "preflight.ps1"), CimianPaths.ManagedInstallsRoot));
        Assert.False(ManagedInstallsSecurity.IsUnder(CimianPaths.ManagedInstallsRoot + "Evil\\x.ps1", CimianPaths.ManagedInstallsRoot));
    }

    // ── Folders above an object, and the volume root ─────────────────────────

    internal static RawSecurityDescriptor Folder(SecurityIdentifier owner, params (SecurityIdentifier Sid, FileSystemRights Rights, AceFlags Flags)[] entries)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, entries.Length + 2);
        acl.InsertAce(acl.Count, new CommonAce(AceFlags.ObjectInherit | AceFlags.ContainerInherit, AceQualifier.AccessAllowed, (int)FileSystemRights.FullControl, System, false, null));
        acl.InsertAce(acl.Count, new CommonAce(AceFlags.ObjectInherit | AceFlags.ContainerInherit, AceQualifier.AccessAllowed, (int)FileSystemRights.FullControl, Admins, false, null));
        foreach (var e in entries)
        {
            acl.InsertAce(acl.Count, new CommonAce(e.Flags, AceQualifier.AccessAllowed, (int)e.Rights, e.Sid, false, null));
        }
        return new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, owner, null, null, acl);
    }

    [Fact]
    public void VolumeRoot_AsWindowsSetsIt_IsAcceptedWithoutAWarning()
    {
        var root = Folder(Admins,
            (Users, FileSystemRights.ReadAndExecute, AceFlags.ObjectInherit | AceFlags.ContainerInherit),
            (AuthenticatedUsers, FileSystemRights.Modify, AceFlags.ObjectInherit | AceFlags.ContainerInherit | AceFlags.InheritOnly),
            (AuthenticatedUsers, FileSystemRights.AppendData, AceFlags.None));
        var warnings = new List<string>();

        Assert.Null(ManagedInstallsSecurity.FindUntrustedAncestorWriter(root, isVolumeRoot: true, warnings.Add));
        Assert.Empty(warnings);
    }

    [Fact]
    public void VolumeRoot_GrantingUsersDelete_IsAcceptedWithAWarning()
    {
        // A volume root cannot be deleted or renamed, so Delete on it changes nothing below.
        var root = Folder(Admins, (AuthenticatedUsers, FileSystemRights.Delete, AceFlags.None));
        var warnings = new List<string>();

        Assert.Null(ManagedInstallsSecurity.FindUntrustedAncestorWriter(root, isVolumeRoot: true, warnings.Add));
        Assert.Contains("Delete", Assert.Single(warnings));
    }

    [Fact]
    public void OtherFolder_GrantingUsersDelete_IsStillRefused()
    {
        // Anything but the root can be renamed away and replaced.
        var folder = Folder(Admins, (AuthenticatedUsers, FileSystemRights.Delete, AceFlags.None));
        var warnings = new List<string>();

        Assert.Contains("Delete", ManagedInstallsSecurity.FindUntrustedAncestorWriter(folder, isVolumeRoot: false, warnings.Add));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.FullControl)]
    public void VolumeRoot_GrantingUsersAnyOtherAncestorRight_IsStillRefused(FileSystemRights rights)
    {
        var root = Folder(Admins, (AuthenticatedUsers, rights, AceFlags.None));

        Assert.NotNull(ManagedInstallsSecurity.FindUntrustedAncestorWriter(root, isVolumeRoot: true, _ => { }));
    }

    [Fact]
    public void VolumeRoot_GrantingGenericAll_IsStillRefused()
    {
        var acl = new RawAcl(GenericAcl.AclRevision, 1);
        acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x10000000, AuthenticatedUsers, false, null));
        var root = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, Admins, null, null, acl);

        Assert.NotNull(ManagedInstallsSecurity.FindUntrustedAncestorWriter(root, isVolumeRoot: true, _ => { }));
    }

    [Fact]
    public void VolumeRoot_OwnedByUsers_IsStillRefused()
    {
        var root = Folder(Users);

        Assert.Contains("owned by", ManagedInstallsSecurity.FindUntrustedAncestorWriter(root, isVolumeRoot: true, _ => { }));
    }

    private static byte[] ToBinary(RawAcl acl)
    {
        var sd = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            Admins, null, null, acl);
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
    }
}

/// <summary>
/// The root-Delete notice stays out of ordinary output: a debug line on every check, and
/// a warning only from the full repair.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public class RootDeleteNoticeTests : IDisposable
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly int _originalVerbosity = ConsoleLogger.Verbosity;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public RootDeleteNoticeTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        ConsoleLogger.SetSessionLogger(null);
    }

    public void Dispose()
    {
        ConsoleLogger.Verbosity = _originalVerbosity;
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }

    private static RawSecurityDescriptor RootGrantingDelete() =>
        ManagedInstallsSecurityTests.Folder(Admins, (AuthenticatedUsers, FileSystemRights.Delete, AceFlags.None));

    private string Output => _stdout.ToString() + _stderr.ToString();

    [Fact]
    public void ByDefault_TheNoticeIsNotPrintedInANormalRun()
    {
        ConsoleLogger.Verbosity = 0;

        Assert.Null(ManagedInstallsSecurity.FindUntrustedAncestorWriter(RootGrantingDelete(), isVolumeRoot: true));

        Assert.DoesNotContain("volume root", Output);
        Assert.DoesNotContain("WARN", Output);
    }

    [Fact]
    public void ByDefault_TheNoticeIsADebugLine()
    {
        ConsoleLogger.Verbosity = 3;

        ManagedInstallsSecurity.FindUntrustedAncestorWriter(RootGrantingDelete(), isVolumeRoot: true);

        Assert.Contains("volume root", Output);
        Assert.Contains("DEBUG", Output);
        Assert.DoesNotContain("WARN", Output);
    }

    [Fact]
    public void CheckingASystemFolder_NeverWarnsInANormalRun()
    {
        // Whatever this machine's root allows, an ordinary check prints no warning about it.
        ConsoleLogger.Verbosity = 0;

        ManagedInstallsSecurity.IsAdminOnly(Environment.SystemDirectory, out _);

        Assert.DoesNotContain("WARN", Output);
    }
}
