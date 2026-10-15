using System.Security.AccessControl;
using System.Security.Principal;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// A settings file is trusted only when SYSTEM, Administrators or TrustedInstaller own it
/// and nobody else may change it.
/// </summary>
public class ConfigFileGuardTests : IDisposable
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier SomeUser = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    private readonly string _dir;

    public ConfigFileGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static FileSecurity Locked(SecurityIdentifier owner)
    {
        var security = ConfigFileGuard.LockedSecurity();
        security.SetOwner(owner);
        return security;
    }

    [Fact]
    public void LockedAcl_OwnedByAdministrators_IsTrusted()
    {
        Assert.Null(ConfigFileGuard.FindProblem(Locked(Administrators)));
    }

    [Fact]
    public void LockedAcl_OwnedBySystem_IsTrusted()
    {
        Assert.Null(ConfigFileGuard.FindProblem(Locked(LocalSystem)));
    }

    [Fact]
    public void LockedAcl_HasInheritanceOffAndUsersReadOnly()
    {
        var security = ConfigFileGuard.LockedSecurity();

        Assert.True(security.AreAccessRulesProtected);
        var users = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Single(r => r.IdentityReference.Equals(Users));
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, users.FileSystemRights);
    }

    [Fact]
    public void OwnedByStandardUser_IsNotTrusted()
    {
        var problem = ConfigFileGuard.FindProblem(Locked(SomeUser));

        Assert.NotNull(problem);
        Assert.Contains(SomeUser.Value, problem);
    }

    [Theory]
    [InlineData(FileSystemRights.Write)]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.FullControl)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.Delete)]
    public void WriteRightForNonAdmin_IsNotTrusted(FileSystemRights rights)
    {
        var security = Locked(Administrators);
        security.AddAccessRule(new FileSystemAccessRule(AuthenticatedUsers, rights, AccessControlType.Allow));

        Assert.NotNull(ConfigFileGuard.FindProblem(security));
    }

    [Fact]
    public void GenericWriteForNonAdmin_IsNotTrusted()
    {
        // An unmapped GENERIC_WRITE can only be expressed in SDDL.
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm($"O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GW;;;{SomeUser.Value})");

        Assert.NotNull(ConfigFileGuard.FindProblem(security));
    }

    [Fact]
    public void DenyRuleForNonAdmin_IsTrusted()
    {
        var security = Locked(Administrators);
        security.AddAccessRule(new FileSystemAccessRule(SomeUser, FileSystemRights.Write, AccessControlType.Deny));

        Assert.Null(ConfigFileGuard.FindProblem(security));
    }

    [Fact]
    public void RealFile_WithUsersModify_IsNotTrusted()
    {
        var path = Path.Combine(_dir, "Config.yaml");
        File.WriteAllText(path, "x: 1\n");
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, AccessControlType.Allow));
        file.SetAccessControl(security);

        Assert.False(ConfigFileGuard.IsTrusted(path, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void MissingFile_IsNotTrusted()
    {
        Assert.False(ConfigFileGuard.IsTrusted(Path.Combine(_dir, "absent.yaml"), out _));
    }

    [Fact]
    public void Lock_LeavesNoNonAdminWriter()
    {
        var path = Path.Combine(_dir, "Config.yaml");
        File.WriteAllText(path, "x: 1\n");

        Assert.Null(ConfigFileGuard.Lock(path));

        // Unelevated, the owner stays this account; only the DACL is checked here.
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        security.SetOwner(Administrators);
        Assert.Null(ConfigFileGuard.FindProblem(security));
    }
}
