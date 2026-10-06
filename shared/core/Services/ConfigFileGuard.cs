using System.Security.AccessControl;
using System.Security.Principal;

namespace Cimian.Core.Services;

/// <summary>
/// Decides whether a settings file is safe for a SYSTEM process to trust, and locks one
/// down when Cimian writes it.
/// </summary>
/// <remarks>
/// managedsoftwareupdate runs as SYSTEM and acts on Config.yaml, so a Config.yaml that a
/// standard user could have written is not used. A file is trusted only when it is a
/// plain file (not a link), its owner is SYSTEM, Administrators or TrustedInstaller, and
/// no other account is allowed to change its contents or its permissions. The installer
/// and <see cref="Lock"/> give it SYSTEM and Administrators full control and Users read,
/// with inheritance off.
/// </remarks>
public static class ConfigFileGuard
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    // GENERIC_ALL and GENERIC_WRITE, which an ACE can carry unmapped.
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Owners whose files a SYSTEM process may trust.</summary>
    public static bool IsTrustedOwner(SecurityIdentifier? owner) =>
        owner is not null && (owner == LocalSystem || owner == Administrators || owner == TrustedInstaller);

    /// <summary>
    /// True when <paramref name="path"/> can be trusted; otherwise false, with
    /// <paramref name="reason"/> saying why. A missing file is not trusted.
    /// </summary>
    public static bool IsTrusted(string path, out string reason)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                reason = "it does not exist";
                return false;
            }
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                reason = "it is a link, not a file";
                return false;
            }

            var security = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var problem = FindProblem(security);
            reason = problem ?? string.Empty;
            return problem is null;
        }
        catch (Exception ex)
        {
            reason = $"its permissions could not be read ({ex.Message})";
            return false;
        }
    }

    /// <summary>
    /// The reason <paramref name="security"/> lets someone other than SYSTEM or an
    /// administrator change the file, or null when it does not.
    /// </summary>
    public static string? FindProblem(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!IsTrustedOwner(owner))
        {
            // The owner can always rewrite the ACL, whatever it says now.
            return $"it is owned by {owner?.Value ?? "an unknown account"}, not SYSTEM or Administrators";
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                IsTrustedOwner(sid) || sid == CreatorOwner)
            {
                continue;
            }

            var mask = (int)rule.FileSystemRights;
            if ((rule.FileSystemRights & WriteRights) != 0 || (mask & (GenericAll | GenericWrite)) != 0)
            {
                return $"{sid.Value} is allowed to change it";
            }
        }

        return null;
    }

    /// <summary>
    /// SYSTEM and Administrators full control, Users read, not inherited. Must run as
    /// SYSTEM or elevated; failures are returned for the caller to log, not thrown.
    /// </summary>
    public static string? Lock(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var security = LockedSecurity();
            using (var identity = WindowsIdentity.GetCurrent())
            {
                // An elevated administrator may only assign the Administrators group.
                security.SetOwner(identity.User == LocalSystem ? LocalSystem : Administrators);
            }
            try
            {
                file.SetAccessControl(security);
            }
            catch (Exception)
            {
                // Not allowed to take ownership here: apply the ACL and keep the owner.
                file.SetAccessControl(LockedSecurity());
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not set permissions on {path}: {ex.Message}";
        }
    }

    /// <summary>The ACL <see cref="Lock"/> applies.</summary>
    public static FileSecurity LockedSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return security;
    }
}
