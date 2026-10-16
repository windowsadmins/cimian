// ManagedInstallsSecurity.cs - Permissions for %ProgramData%\ManagedInstalls and the
// trust checks applied to anything there that SYSTEM executes.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Cimian.Core.Services;

/// <summary>
/// Owns the access control on <see cref="CimianPaths.ManagedInstallsRoot"/>.
/// </summary>
/// <remarks>
/// ManagedInstalls used to inherit the %ProgramData% ACL, which lets every user create
/// files and folders and then own them. Several things running as SYSTEM trust what
/// they find there (scripts in sbin\ and conditions\, cached installers, the self-update
/// flag), so a standard user could run code as SYSTEM.
///
/// The layout this class enforces:
/// <list type="bullet">
/// <item>Root: protected; SYSTEM and Administrators full control, Users read and execute.
/// Everything below inherits that unless listed here.</item>
/// <item>Cache, sbin, conditions, SelfUpdateBackup, quarantine: SYSTEM and Administrators only.</item>
/// <item>triggers: Users may add files and manage the ones they added. Managed Software
/// Center, cimitrigger and CimianStatus drop run requests here.</item>
/// <item>selfservice\: the same as triggers\, for each user's Self Service drop file.</item>
/// <item>SelfServeManifest.yaml: written only by SYSTEM and Administrators (the Munki 7 copy
/// model; see <see cref="SelfServiceRequests"/>). Users read it.</item>
/// <item>LastRunTime.txt: Users may read and write it, not delete it.</item>
/// <item>Config.yaml: SYSTEM and Administrators only. It can carry repository credentials,
/// and a user who could open it could hold it open and stop every run.</item>
/// </list>
/// </remarks>
public static class ManagedInstallsSecurity
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>Folders under the root that only SYSTEM and Administrators may open.</summary>
    private static readonly string[] AdminOnlyDirs =
    {
        CimianPaths.CacheDir, CimianPaths.SbinDir, CimianPaths.ConditionsDir, CimianPaths.CatalogsDir,
        CimianPaths.SelfUpdateBackupDir, CimianPaths.QuarantineDir,
    };

    /// <summary>Folders whose contents SYSTEM executes or copies into Program Files.</summary>
    private static readonly string[] ExecutedDirs =
    {
        CimianPaths.SbinDir, CimianPaths.ConditionsDir, CimianPaths.SelfUpdateBackupDir,
    };

    /// <summary>Root files a standard user writes in place.</summary>
    private static readonly string[] UserWritableFiles =
    {
        CimianPaths.LastRunTimeFile,
    };

    /// <summary>
    /// Root files user-context tools wrote before this layout and that SYSTEM never reads;
    /// adopted rather than quarantined. Old run requests at the root are quarantined instead.
    /// </summary>
    private static readonly string[] ExpectedUserOwnedRootFiles =
    {
        CimianPaths.LegacyMscCrashLog,
    };

    // Rights that let a principal change an object's content, its name, or its security.
    // Generic bits are included because inherited entries can carry them unmapped.
    private const int WriteLikeRights =
        (int)(FileSystemRights.WriteData | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)
        | 0x10000000 /* GENERIC_ALL */ | 0x40000000 /* GENERIC_WRITE */;

    /// <summary>
    /// The only root files the repair adopts when a non-administrator owns them: the ones
    /// standard users write by design. Anything else such a user owns is quarantined.
    /// </summary>
    internal static bool IsExpectedUserOwned(string path) =>
        UserWritableFiles.Concat(ExpectedUserOwnedRootFiles).Any(f => Same(path, f));

    /// <summary>True for a folder the repair closes to everyone but SYSTEM and Administrators.</summary>
    internal static bool IsAdminOnlyLocation(string dir) => AdminOnlyDirs.Any(d => Same(dir, d));

    /// <summary>True for SYSTEM, BUILTIN\Administrators and TrustedInstaller.</summary>
    public static bool IsTrustedPrincipal(SecurityIdentifier? sid) =>
        sid != null && (sid == LocalSystem || sid == Administrators || sid == TrustedInstaller);

    // On a folder above the object, adding new entries is harmless: the path already
    // exists, and nothing can take its place without deleting it or changing permissions.
    private const int AncestorWriteLikeRights =
        (int)(FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)
        | 0x10000000 /* GENERIC_ALL */;

    /// <summary>
    /// Describes the first way a principal other than SYSTEM, Administrators or
    /// TrustedInstaller can modify an object with this security, or returns null when
    /// none can. An untrusted owner counts, because an owner can always rewrite the DACL.
    /// </summary>
    public static string? FindUntrustedWriter(FileSystemSecurity security)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        return FindUntrustedWriter(raw, WriteLikeRights);
    }

    private static string? FindUntrustedWriter(RawSecurityDescriptor security, int rights)
    {
        if (!IsTrustedPrincipal(security.Owner))
        {
            return $"owned by {Describe(security.Owner)}";
        }

        foreach (var ace in security.DiscretionaryAcl?.OfType<CommonAce>() ?? Enumerable.Empty<CommonAce>())
        {
            if (ace.AceQualifier != AceQualifier.AccessAllowed) continue;
            // Inherit-only entries describe children, not this object.
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if ((ace.AccessMask & rights) == 0) continue;

            var sid = ace.SecurityIdentifier;
            // CREATOR OWNER never matches a token; OWNER RIGHTS is the owner, checked above.
            if (sid == CreatorOwner || sid == OwnerRights) continue;
            if (IsTrustedPrincipal(sid)) continue;

            return $"writable by {Describe(sid)} ({(FileSystemRights)(ace.AccessMask & rights)})";
        }

        return null;
    }

    /// <summary>
    /// The check on one folder above an object. A volume root cannot be deleted or renamed,
    /// so an untrusted Delete entry on the root itself is not held against what is below it.
    /// It is reported through <paramref name="note"/>, a debug line by default; the full
    /// repair warns about it once (see <see cref="WarnAboutRootDelete"/>). Delete on any
    /// other folder, and delete-child, permission, ownership and full-control rights on the
    /// root, still refuse.
    /// </summary>
    internal static string? FindUntrustedAncestorWriter(RawSecurityDescriptor security, bool isVolumeRoot, Action<string>? note = null)
    {
        if (!isVolumeRoot)
        {
            return FindUntrustedWriter(security, AncestorWriteLikeRights);
        }

        var writer = FindUntrustedWriter(security, AncestorWriteLikeRights & ~(int)FileSystemRights.Delete);
        if (writer == null && FindUntrustedWriter(security, (int)FileSystemRights.Delete) is { } delete)
        {
            (note ?? ConsoleLogger.Debug)(
                $"The volume root is {delete}. Ignored: a volume root cannot be deleted or renamed. Remove that entry from the root's permissions.");
        }
        return writer;
    }

    /// <summary>
    /// Warns, in <paramref name="log"/> and as an Application event, when the volume root
    /// under <paramref name="path"/> lets someone other than SYSTEM or Administrators delete
    /// it. Harmless to Cimian, which ignores it, but not a stock Windows ACL.
    /// </summary>
    internal static void WarnAboutRootDelete(string path, Action<string> log)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return;
            using var handle = Native.Open(root, forWrite: false);
            FindUntrustedAncestorWriter(Native.GetSecurity(handle), isVolumeRoot: true, message =>
            {
                log($"WARNING: {message}");
                WriteSecurityEvent(message, System.Diagnostics.EventLogEntryType.Warning, 4002);
            });
        }
        catch (Exception ex)
        {
            log($"WARNING: could not check the volume root's permissions: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks that only SYSTEM, Administrators and TrustedInstaller can change
    /// <paramref name="path"/>, or replace it by changing any folder above it, all the way
    /// to the volume root. The object must not be a link or have a second name, and no
    /// folder above it may be a link.
    /// </summary>
    public static bool IsAdminOnly(string path, out string? reason)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var isTarget = true;
        try
        {
            while (true)
            {
                using var handle = Native.Open(current, forWrite: false);
                var info = Native.GetInfo(handle);
                var isDirectory = (info.Attributes & (uint)FileAttributes.Directory) != 0;

                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                {
                    reason = $"{current} is a link";
                    return false;
                }
                if (isTarget && !isDirectory && info.LinkCount > 1)
                {
                    reason = $"{current} has {info.LinkCount} names (hard link)";
                    return false;
                }

                var parent = Path.GetDirectoryName(current);
                var security = Native.GetSecurity(handle);
                var writer = isTarget
                    ? FindUntrustedWriter(security, WriteLikeRights)
                    : FindUntrustedAncestorWriter(security, isVolumeRoot: parent == null);
                if (writer != null)
                {
                    reason = $"{current} is {writer}";
                    return false;
                }

                if (parent == null) break;
                current = parent;
                isTarget = false;
            }

            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"could not check {current}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Checks a script, installer or flag file that SYSTEM is about to act on.
    /// See <see cref="IsAdminOnly"/>.
    /// </summary>
    public static bool IsSafeToExecute(string path, out string? reason) => IsAdminOnly(path, out reason);

    /// <summary>
    /// Reads a run request for CimianWatcher. The file is opened once without following
    /// links; a link, a file with a second name, or one larger than
    /// <see cref="TriggerRequests.MaxRequestBytes"/> is refused, and at most that much is
    /// read from the handle that was checked.
    /// </summary>
    /// <param name="requireTrustedOwner">
    /// True for the fixed names at the ManagedInstalls root, which only SYSTEM and
    /// administrators may write.
    /// </param>
    public static bool TryReadRequest(string path, bool requireTrustedOwner, out string? content, out string? reason)
    {
        content = null;
        try
        {
            using var handle = Native.OpenForRead(path);
            var info = Native.GetInfo(handle);
            if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                reason = "it is a link or a folder";
                return false;
            }
            if (info.LinkCount > 1)
            {
                reason = "it is a hard link";
                return false;
            }
            if (requireTrustedOwner && !IsTrustedPrincipal(Native.GetSecurity(handle).Owner))
            {
                reason = "it is not owned by SYSTEM or Administrators";
                return false;
            }

            using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            var buffer = new byte[TriggerRequests.MaxRequestBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }
            if (total > TriggerRequests.MaxRequestBytes)
            {
                reason = $"it is larger than {TriggerRequests.MaxRequestBytes} bytes";
                return false;
            }

            content = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"it could not be read ({ex.GetType().Name})";
            return false;
        }
    }

    /// <summary>
    /// Makes <paramref name="dir"/> a quarantine folder only SYSTEM and Administrators
    /// control, before anything is moved into it. A link or file in its place is removed
    /// (a link's target is untouched); a folder a non-administrator owns is moved aside into
    /// <paramref name="asideParent"/> and replaced. Returns true only when the folder is
    /// then admin-only all the way to the volume root.
    /// </summary>
    internal static bool PrepareQuarantineDir(string dir, string asideParent, Func<string, bool> secure,
        Action<string> warn, out string? problem)
    {
        if (Directory.Exists(dir) || File.Exists(dir))
        {
            var info = ObjectInfo.Read(dir);
            if (info.IsReparsePoint || !info.IsDirectory)
            {
                // Removing a link removes the link only.
                if (info.IsDirectory) Directory.Delete(dir, recursive: false);
                else File.Delete(dir);
                warn($"removed {dir}: it was a {(info.IsReparsePoint ? "link" : "file")}");
            }
            else if (!IsTrustedPrincipal(info.Owner))
            {
                var aside = Path.Combine(asideParent, $"quarantine.untrusted-{Guid.NewGuid():N}");
                Directory.Move(dir, aside);
                warn($"moved {dir} (owned by {Describe(info.Owner)}) to {aside}");
            }
        }

        Directory.CreateDirectory(dir);
        if (!secure(dir))
        {
            problem = "its permissions could not be set";
            return false;
        }
        return IsAdminOnly(dir, out problem);
    }

    /// <summary>
    /// Opens <paramref name="path"/> for reading the way a script host or parser would, to
    /// tell "cannot be read right now" (a user holding it open, say) apart from "untrusted".
    /// </summary>
    public static bool TryProbeRead(string path, out string? reason)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"could not read {path}: {ex.Message}";
            return false;
        }
    }

    /// <summary>The NTFS file ID, read without following links, or null if it cannot be read.</summary>
    public static ulong? TryGetFileId(string path)
    {
        try
        {
            using var handle = Native.Open(path, forWrite: false);
            return Native.GetInfo(handle).FileId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates <paramref name="path"/> and writes <paramref name="content"/>, failing if
    /// anything already has that name. A link in its place is never followed.
    /// </summary>
    public static bool TryWriteNewFile(string path, byte[] content)
    {
        try
        {
            using var handle = Native.CreateNew(path);
            using var stream = new FileStream(handle, FileAccess.Write, bufferSize: 0);
            stream.Write(content);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The owner of a file, read without following links, or null if it cannot be read.</summary>
    public static SecurityIdentifier? TryGetOwner(string path)
    {
        try
        {
            using var handle = Native.Open(path, forWrite: false);
            return Native.GetSecurity(handle).Owner;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a new, uniquely named session folder in <paramref name="quarantineDir"/>
    /// and returns it, or returns null when that folder is not admin-only all the way up
    /// or the new folder could not be created fresh.
    /// </summary>
    internal static string? CreateQuarantineSession(string quarantineDir, out string? reason)
    {
        if (!IsAdminOnly(quarantineDir, out reason))
        {
            return null;
        }

        var session = Path.Combine(quarantineDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        if (!TryCreateDirectoryExclusive(session))
        {
            reason = $"{session} already exists";
            return null;
        }
        return session;
    }

    /// <summary>Creates a folder, failing if anything already has that name.</summary>
    internal static bool TryCreateDirectoryExclusive(string path) =>
        Native.CreateDirectoryW(@"\\?\" + Path.GetFullPath(path), IntPtr.Zero);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/>.</summary>
    public static bool IsUnder(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return p.Length > r.Length
            && p.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            && p[r.Length] == Path.DirectorySeparatorChar;
    }

    // ── Wanted security descriptors ──────────────────────────────────────────

    private const InheritanceFlags ContainerAndObject = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    internal static RawAcl RootAcl() => BuildAcl(
        (LocalSystem, FileSystemRights.FullControl, ContainerAndObject, PropagationFlags.None),
        (Administrators, FileSystemRights.FullControl, ContainerAndObject, PropagationFlags.None),
        (Users, FileSystemRights.ReadAndExecute, ContainerAndObject, PropagationFlags.None));

    internal static RawAcl AdminOnlyAcl() => BuildAcl(
        (LocalSystem, FileSystemRights.FullControl, ContainerAndObject, PropagationFlags.None),
        (Administrators, FileSystemRights.FullControl, ContainerAndObject, PropagationFlags.None));

    internal static RawAcl TriggersAcl() => BuildAcl(
        (LocalSystem, FileSystemRights.FullControl, ContainerAndObject, PropagationFlags.None),
        (Administrators, FileSystemRights.FullControl, ContainerAndObject, PropagationFlags.None),
        // List the folder and add files to it. Nothing inherits this entry.
        (Users, FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None),
        // Whoever adds a request can rewrite or withdraw it, and nobody else's.
        (CreatorOwner, FileSystemRights.Modify, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly));

    /// <summary>Folders where users drop requests for SYSTEM to take in.</summary>
    private static readonly string[] UserRequestFolders = { CimianPaths.TriggersDir, CimianPaths.SelfServiceDropDir };

    /// <summary>True for triggers\ and selfservice\.</summary>
    internal static bool IsUserRequestFolder(string dir) => UserRequestFolders.Any(d => Same(dir, d));

    /// <summary>Root files closed to standard users entirely.</summary>
    private static readonly string[] AdminOnlyFiles = { CimianPaths.ConfigYaml };

    /// <summary>True for a root file the repair closes to everyone but SYSTEM and Administrators.</summary>
    internal static bool IsAdminOnlyFile(string path) => AdminOnlyFiles.Any(f => Same(path, f));

    /// <summary>What a standard user is told when a tool needs Config.yaml.</summary>
    public const string ConfigNeedsElevation =
        "Config.yaml is readable only by SYSTEM and administrators. Run this from an elevated prompt.";

    internal static RawAcl AdminOnlyFileAcl() => BuildAcl(
        (LocalSystem, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None),
        (Administrators, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None));

    internal static RawAcl UserWritableFileAcl() => BuildAcl(
        (Users, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize, InheritanceFlags.None, PropagationFlags.None));

    private static RawAcl BuildAcl(params (SecurityIdentifier Sid, FileSystemRights Rights, InheritanceFlags Inherit, PropagationFlags Propagate)[] entries)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, entries.Length);
        foreach (var e in entries)
        {
            var flags = AceFlags.None;
            if ((e.Inherit & InheritanceFlags.ObjectInherit) != 0) flags |= AceFlags.ObjectInherit;
            if ((e.Inherit & InheritanceFlags.ContainerInherit) != 0) flags |= AceFlags.ContainerInherit;
            if ((e.Propagate & PropagationFlags.InheritOnly) != 0) flags |= AceFlags.InheritOnly;
            if ((e.Propagate & PropagationFlags.NoPropagateInherit) != 0) flags |= AceFlags.NoPropagateInherit;
            // FileSystemAccessRule adds SYNCHRONIZE to allow entries automatically. Raw
            // ACEs need it explicitly for normal synchronous file and directory opens.
            var rights = e.Rights | FileSystemRights.Synchronize;
            acl.InsertAce(acl.Count, new CommonAce(flags, AceQualifier.AccessAllowed, (int)rights, e.Sid, false, null));
        }
        return acl;
    }

    /// <summary>
    /// True when a descriptor already has a trusted owner and the wanted protection and
    /// explicit entries. Inherited entries are the parent's business and are not compared.
    /// </summary>
    internal static bool Matches(RawSecurityDescriptor current, RawAcl wanted, bool isProtected)
    {
        if (!IsTrustedPrincipal(current.Owner)) return false;
        var currentlyProtected = (current.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0;
        if (currentlyProtected != isProtected) return false;

        var explicitAces = (current.DiscretionaryAcl?.Cast<GenericAce>() ?? Enumerable.Empty<GenericAce>())
            .Where(a => (a.AceFlags & AceFlags.Inherited) == 0)
            .Select(Binary)
            .ToList();
        var wantedAces = wanted.Cast<GenericAce>().Select(Binary).ToList();
        if (explicitAces.Count != wantedAces.Count) return false;
        for (var i = 0; i < explicitAces.Count; i++)
        {
            if (!explicitAces[i].AsSpan().SequenceEqual(wantedAces[i])) return false;
        }
        return true;

        static byte[] Binary(GenericAce ace)
        {
            var bytes = new byte[ace.BinaryLength];
            ace.GetBinaryForm(bytes, 0);
            return bytes;
        }
    }

    // ── Repair ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The files and folders the repair handles, all placed under one root. The repair runs
    /// over <see cref="Default"/>; tests build one over a temporary folder.
    /// </summary>
    internal sealed class RepairLayout
    {
        public RepairLayout(string root)
        {
            Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }

        public static RepairLayout Default => new(CimianPaths.ManagedInstallsRoot);

        public string Root { get; }

        /// <summary>Where <paramref name="defaultPath"/>, a path under the real root, sits under this one.</summary>
        public string At(string defaultPath) =>
            Path.Combine(Root, Path.GetRelativePath(CimianPaths.ManagedInstallsRoot, defaultPath));

        public string CacheDir => At(CimianPaths.CacheDir);
        public string CatalogsDir => At(CimianPaths.CatalogsDir);
        public string QuarantineDir => At(CimianPaths.QuarantineDir);
        public string ConfigYaml => At(CimianPaths.ConfigYaml);
        public string SelfServeManifestYaml => At(CimianPaths.SelfServeManifestYaml);
        public IEnumerable<string> AdminOnlyDirs => ManagedInstallsSecurity.AdminOnlyDirs.Select(At);
        public IEnumerable<string> ExecutedDirs => ManagedInstallsSecurity.ExecutedDirs.Select(At);
        public IEnumerable<string> UserRequestFolders => ManagedInstallsSecurity.UserRequestFolders.Select(At);
        public IEnumerable<string> UserWritableFiles => ManagedInstallsSecurity.UserWritableFiles.Select(At);
        public IEnumerable<string> AdminOnlyFiles => ManagedInstallsSecurity.AdminOnlyFiles.Select(At);

        public bool IsExpectedUserOwned(string path) =>
            ManagedInstallsSecurity.UserWritableFiles.Concat(ExpectedUserOwnedRootFiles).Select(At).Any(f => Same(path, f));
    }

    /// <summary>
    /// Brings ManagedInstalls to the layout described on this class.
    /// </summary>
    /// <param name="log">Receives one line per change and per problem found.</param>
    /// <param name="deep">
    /// Walk every file and folder; the installer does this on every install and upgrade.
    /// A shallow pass fixes the root, its direct contents and everything inside the special
    /// folders, and is cheap enough for the start of every session.
    /// </param>
    /// <returns>
    /// False when something security-critical could not be secured: the root, an admin-only
    /// folder, triggers\ or a usable quarantine. A single file that could not be handled
    /// (held open by a user, say) is a warning in the log and does not make this false, or
    /// any user could keep it false. A false result is also written to the Application
    /// event log under the source "Cimian".
    /// </returns>
    public static bool Repair(Action<string> log, bool deep)
    {
        // Only SYSTEM or an elevated administrator can set these permissions. Anyone else
        // running managedsoftwareupdate would just fill the log with access-denied errors.
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return false;
        }

        // The full repair runs on install and upgrade only, so this is said once there,
        // not at the start of every session.
        if (deep)
        {
            WarnAboutRootDelete(CimianPaths.ManagedInstallsRoot, log);
        }

        var walker = new Walker(RepairLayout.Default, log, deep, CreateAdministratorsMemberCheck());
        var secured = walker.Run();
        if (!secured)
        {
            WriteSecurityEvent(
                "Cimian could not secure the permissions on " + CimianPaths.ManagedInstallsRoot +
                ". Run requests from users are held until this succeeds.\r\n\r\n" +
                string.Join("\r\n", walker.CriticalProblems));
        }
        return secured;
    }

    /// <summary>
    /// Runs the repair over <paramref name="layout"/>, with <paramref name="isAdministratorsMember"/>
    /// deciding which accounts are members of the local Administrators group. For tests.
    /// </summary>
    internal static bool Repair(RepairLayout layout, Action<string> log, bool deep,
        Func<SecurityIdentifier, bool> isAdministratorsMember) =>
        new Walker(layout, log, deep, isAdministratorsMember).Run();

    /// <summary>
    /// The direct members of the local Administrators group, read from the local account
    /// database. Nothing is looked up over the network, so an account that is an
    /// administrator only through a domain group is not counted; its objects are treated
    /// as untrusted, as before.
    /// </summary>
    internal static Func<SecurityIdentifier, bool> CreateAdministratorsMemberCheck()
    {
        var members = new Lazy<HashSet<SecurityIdentifier>>(Native.GetAdministratorsMembers);
        return sid => members.Value.Contains(sid);
    }

    // What an untrusted principal may keep on an object the repair does not own the layout
    // of: BUILTIN\Users may read and run what the root hands down, nobody else anything.
    private const int OrdinaryUsersAllowance =
        (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)
        | unchecked((int)0x80000000) /* GENERIC_READ */ | 0x20000000 /* GENERIC_EXECUTE */;

    /// <summary>
    /// The explicit entries of <paramref name="dacl"/> with every allow entry for an
    /// untrusted principal cut back: BUILTIN\Users to <paramref name="usersAllowance"/>,
    /// anyone else removed. Deny entries, entries for trusted principals and inherited
    /// entries (the parent's business) are kept as they are. Returns null when nothing has
    /// to change, so the repair never widens or rewrites a DACL that is already tight
    /// enough. A NULL DACL, which grants everyone everything, becomes an empty one.
    /// An allow entry for an account <paramref name="handToAdministrators"/> picks (a direct
    /// member of Administrators) is given to the Administrators group instead, where the
    /// runtime checks would otherwise refuse the object.
    /// </summary>
    internal static RawAcl? TightenAcl(RawAcl? dacl, int usersAllowance, Func<SecurityIdentifier, bool> isTrusted,
        Func<SecurityIdentifier, bool>? handToAdministrators = null)
    {
        if (dacl == null)
        {
            return new RawAcl(GenericAcl.AclRevision, 0);
        }

        var result = new RawAcl(dacl.Revision, dacl.Count);
        var changed = false;
        foreach (GenericAce ace in dacl)
        {
            if ((ace.AceFlags & AceFlags.Inherited) != 0)
            {
                continue;
            }

            if (ace is CommonAce member && member.AceQualifier == AceQualifier.AccessAllowed
                && handToAdministrators != null && !IsTrustedPrincipal(member.SecurityIdentifier)
                && handToAdministrators(member.SecurityIdentifier))
            {
                changed = true;
                result.InsertAce(result.Count, new CommonAce(member.AceFlags, AceQualifier.AccessAllowed, member.AccessMask, Administrators, false, null));
                continue;
            }

            if (ace is QualifiedAce q && q.AceQualifier == AceQualifier.AccessAllowed)
            {
                var sid = q.SecurityIdentifier;
                var acceptable = sid == CreatorOwner || sid == OwnerRights || isTrusted(sid)
                    || (sid == Users && (q.AccessMask & ~usersAllowance) == 0);
                if (!acceptable)
                {
                    changed = true;
                    var keep = sid == Users && q is CommonAce ? q.AccessMask & usersAllowance : 0;
                    if (keep != 0)
                    {
                        result.InsertAce(result.Count, new CommonAce(q.AceFlags, AceQualifier.AccessAllowed, keep, sid, false, null));
                    }
                    continue;
                }
            }

            result.InsertAce(result.Count, ace);
        }

        // Inherited entries are left out: they are recomputed from the parent.
        return changed ? result : null;
    }

    /// <summary>
    /// True when <paramref name="cachePath"/>, a CachePath outside ManagedInstalls, can be
    /// used. Nothing is created or changed: the folder must already exist, must not be a
    /// system or shared folder (see <see cref="IsForbiddenCacheLocation"/>), and must be
    /// protected and closed to everyone but SYSTEM and Administrators, with nothing above it
    /// to the volume root that anyone else could change or that is a link. Otherwise
    /// managedsoftwareupdate keeps its downloads in Cache\.
    /// </summary>
    public static bool IsUsableExternalCache(string cachePath, out string? reason)
    {
        if (IsForbiddenCacheLocation(cachePath, out reason)) return false;
        if (!Directory.Exists(cachePath))
        {
            // Never created: SYSTEM creating it could follow a junction a user planted above it.
            reason = $"{cachePath} does not exist";
            return false;
        }
        if (!IsAdminOnly(cachePath, out reason)) return false;

        // The folder itself must also be closed to reading, as Cache\ is.
        try
        {
            using var handle = Native.Open(cachePath, forWrite: false);
            var security = Native.GetSecurity(handle);
            if ((security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0
                || TightenAcl(security.DiscretionaryAcl, 0, IsTrustedPrincipal) != null)
            {
                reason = $"{cachePath} is not protected and closed to everyone but SYSTEM and Administrators";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"could not check {cachePath}: {ex.Message}";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Folders that are never a cache outside ManagedInstalls: a network path, a drive root,
    /// %ProgramData% itself or anything containing ManagedInstalls, and %SystemRoot%,
    /// %ProgramFiles%, %ProgramFiles(x86)% and the profiles folder with everything below them.
    /// </summary>
    internal static bool IsForbiddenCacheLocation(string cachePath, out string? reason)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cachePath));
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = $"{cachePath} is a network path";
            return true;
        }
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            reason = $"{cachePath} is a drive root";
            return true;
        }
        if (Same(full, CimianPaths.ManagedInstallsRoot) || IsUnder(CimianPaths.ManagedInstallsRoot, full))
        {
            reason = $"{cachePath} contains ManagedInstalls";
            return true;
        }
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData) && Same(full, programData))
        {
            reason = $"{cachePath} is %ProgramData%";
            return true;
        }

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var protectedTrees = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(systemDrive, "Users"),
        };
        foreach (var tree in protectedTrees.Where(t => !string.IsNullOrEmpty(t)))
        {
            if (Same(full, tree) || IsUnder(full, tree))
            {
                reason = $"{cachePath} is in {tree}";
                return true;
            }
        }

        reason = null;
        return false;
    }

    private const string EventSource = "Cimian";

    private static void WriteSecurityEvent(string message,
        System.Diagnostics.EventLogEntryType type = System.Diagnostics.EventLogEntryType.Error, int eventId = 4001)
    {
        try
        {
            if (!System.Diagnostics.EventLog.SourceExists(EventSource))
            {
                System.Diagnostics.EventLog.CreateEventSource(EventSource, "Application");
            }
            System.Diagnostics.EventLog.WriteEntry(EventSource, message, type, eventId);
        }
        catch
        {
            // The log line above already says what failed; the event is a second channel.
        }
    }

    private sealed class Walker
    {
        private enum Role { Root, Inherit, AdminOnly, Executed, Cache, Triggers, Quarantine }

        private readonly RepairLayout _layout;
        private readonly Action<string> _log;
        private readonly bool _deep;
        private readonly Func<SecurityIdentifier, bool> _isAdministratorsMember;
        private string? _quarantineSession;
        private bool _quarantineUsable;
        private bool _secured = true;
        private readonly List<string> _critical = new();

        /// <summary>What made <see cref="Run"/> return false.</summary>
        public IReadOnlyList<string> CriticalProblems => _critical;

        public Walker(RepairLayout layout, Action<string> log, bool deep, Func<SecurityIdentifier, bool> isAdministratorsMember)
        {
            _layout = layout;
            _log = log;
            _deep = deep;
            _isAdministratorsMember = isAdministratorsMember;
        }

        public bool Run()
        {
            try
            {
                RunCore();
            }
            catch (Exception ex)
            {
                FailCritical($"Repair stopped: {ex.Message}");
            }
            return _secured;
        }

        /// <summary>
        /// SYSTEM, Administrators and TrustedInstaller, plus an account that is itself a
        /// member of the local Administrators group. Whether an administrator's new files
        /// are owned by the account or by the group depends on the edition, UAC and the
        /// logon type, so either is an administrator's file; the repair gives the account's
        /// to the group rather than quarantining them.
        /// </summary>
        private bool IsAdministrative(SecurityIdentifier? sid) =>
            sid != null && (IsTrustedPrincipal(sid) || IsAdministratorsMember(sid));

        private bool IsAdministratorsMember(SecurityIdentifier sid)
        {
            try { return _isAdministratorsMember(sid); }
            catch { return false; }
        }

        private void RunCore()
        {
            var root = _layout.Root;
            Directory.CreateDirectory(root);

            // %ProgramData% lets any user create folders, so the root itself may have been
            // made, or replaced by a link, before Cimian was installed.
            var rootInfo = ObjectInfo.Read(root);
            if (rootInfo.IsReparsePoint)
            {
                if (!IsAdministrative(rootInfo.Owner))
                {
                    Remove(root, isDirectory: true, $"link owned by {Describe(rootInfo.Owner)}");
                    Directory.CreateDirectory(root);
                }
                else
                {
                    FailCritical($"{root} is a link; not securing what it points at");
                    return;
                }
            }
            else if (!IsAdministrative(rootInfo.Owner))
            {
                _log($"WARNING: {root} was owned by {Describe(rootInfo.Owner)}; taking ownership and checking everything in it.");
            }

            // The root goes first: once it is protected, users can no longer add
            // anything beside what this pass is about to inspect.
            if (!SecureDirectory(root, Role.Root))
            {
                return;
            }

            // Quarantine is secured before anything is moved into it.
            PrepareQuarantine();

            // Users cannot create files at the root any more, so the ones they write in
            // place have to exist already.
            foreach (var file in _layout.UserWritableFiles)
            {
                if (!File.Exists(file) && !Directory.Exists(file))
                {
                    using (File.Create(file)) { }
                    _log($"Created {file}");
                }
            }

            ProcessChildren(root, Role.Root);

            // Create any special folder that is missing, including one that was just
            // quarantined because a user had made it.
            foreach (var dir in _layout.AdminOnlyDirs.Concat(_layout.UserRequestFolders))
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    SecureDirectory(dir, RoleOf(dir, Role.Root));
                }
            }
        }

        private Role RoleOf(string path, Role parent)
        {
            if (Same(path, _layout.CacheDir)) return Role.Cache;
            if (Same(path, _layout.QuarantineDir)) return Role.Quarantine;
            if (_layout.UserRequestFolders.Any(d => Same(path, d))) return Role.Triggers;
            // Catalogs are read by SYSTEM; a user able to open one could hold it open.
            if (Same(path, _layout.CatalogsDir)) return Role.AdminOnly;
            if (_layout.ExecutedDirs.Any(d => Same(path, d))) return Role.Executed;
            return parent == Role.Root ? Role.Inherit : parent;
        }

        /// <summary>
        /// True for the folders whose permissions this class sets outright: the root and the
        /// special folders. Everything else is only tightened.
        /// </summary>
        private bool IsNamedLocation(string path) =>
            Same(path, _layout.Root) || Same(path, _layout.QuarantineDir)
            || _layout.AdminOnlyDirs.Concat(_layout.UserRequestFolders).Any(d => Same(path, d));

        private void ProcessChildren(string dir, Role dirRole)
        {
            // Quarantined files are kept exactly as they were found.
            if (dirRole == Role.Quarantine) return;

            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception ex)
            {
                Fail($"Could not list {dir}: {ex.Message}");
                return;
            }

            foreach (var entry in entries)
            {
                ProcessEntry(entry, dirRole);
            }
        }

        private void ProcessEntry(string path, Role parentRole)
        {
            ObjectInfo info;
            try
            {
                info = ObjectInfo.Read(path);
            }
            catch (Exception ex)
            {
                Fail($"Could not inspect {path}: {ex.Message}");
                return;
            }

            var role = RoleOf(path, parentRole);
            var isRootFile = parentRole == Role.Root && !info.IsDirectory;
            var userFile = isRootFile && _layout.UserWritableFiles.Any(f => Same(path, f));
            var expectedUserOwned = isRootFile && _layout.IsExpectedUserOwned(path);
            var trustedOwner = IsAdministrative(info.Owner);

            // A link is never followed. One an administrator made (a relocated Cache, say)
            // is left exactly as it is; one anybody else made is removed.
            if (info.IsReparsePoint)
            {
                if (trustedOwner)
                {
                    _log($"Leaving administrator-made link alone: {path}");
                }
                else
                {
                    Remove(path, info.IsDirectory, $"link owned by {Describe(info.Owner)}");
                }
                return;
            }

            // A second name for a file somewhere else. Changing its security would change
            // that file's too, so it is never adopted.
            if (!info.IsDirectory && info.LinkCount > 1)
            {
                if (trustedOwner)
                {
                    _log($"Skipping hard-linked file: {path}");
                }
                else
                {
                    Remove(path, isDirectory: false, $"hard link owned by {Describe(info.Owner)}");
                }
                return;
            }

            // Run requests are the one thing users are meant to own.
            if (parentRole == Role.Triggers)
            {
                return;
            }

            if (!trustedOwner)
            {
                var why = $"owned by {Describe(info.Owner)}";
                switch (role)
                {
                    case Role.Executed:
                        Quarantine(path, $"{why}, in a folder SYSTEM runs files from");
                        return;
                    case Role.Cache:
                        // A folder is moved rather than deleted, so SYSTEM never recurses
                        // through a tree a user built.
                        if (info.IsDirectory) Quarantine(path, $"{why}, in the download cache");
                        else Remove(path, isDirectory: false, $"{why}, in the download cache");
                        return;
                }

                // A SelfServeManifest.yaml a user wrote before the copy model: keep the
                // choices, as requests the merge filters like any other, not the file.
                if (isRootFile && Same(path, _layout.SelfServeManifestYaml))
                {
                    MigrateUserSelfServeManifest(path, why);
                    return;
                }

                // Anything else a user made is set aside rather than adopted: taking ownership
                // would make it look like an administrator's, and SYSTEM reads much of what
                // lives here (Config.yaml, catalogs, the self-update flag...).
                if (!expectedUserOwned)
                {
                    Quarantine(path, why);
                    return;
                }
            }
            else if (!IsTrustedPrincipal(info.Owner))
            {
                _log($"{path} is owned by {Describe(info.Owner)}, a member of Administrators; giving ownership to the Administrators group");
            }

            if (info.IsDirectory)
            {
                var secured = IsNamedLocation(path)
                    ? SecureDirectory(path, role)
                    : Tighten(path, UsersAllowance(role), IsRuntimeChecked(role, path, isRootFile: false));
                if (!secured) return;
                // A shallow pass leaves ordinary folders (logs, reports...) to the installer's
                // deep pass; the special folders are always walked.
                if (_deep || role != Role.Inherit)
                {
                    ProcessChildren(path, role);
                }
                return;
            }

            if (isRootFile && _layout.AdminOnlyFiles.Any(f => Same(path, f)))
            {
                // Protected, so the root's Users read entry does not reach it.
                if (!Apply(path, AdminOnlyFileAcl(), isProtected: true))
                {
                    FailCritical($"{path} is not secured");
                }
                return;
            }

            if (userFile)
            {
                Apply(path, UserWritableFileAcl(), isProtected: false);
                return;
            }

            Tighten(path, UsersAllowance(role), IsRuntimeChecked(role, path, isRootFile));
        }

        /// <summary>
        /// True where SYSTEM refuses an object anyone but SYSTEM, Administrators or
        /// TrustedInstaller can change: everything in the admin-only folders, and the root
        /// files it reads under that check.
        /// </summary>
        private bool IsRuntimeChecked(Role role, string path, bool isRootFile) =>
            role is Role.AdminOnly or Role.Executed or Role.Cache
            || (isRootFile && (Same(path, _layout.SelfServeManifestYaml) || Same(path, _layout.At(CimianPaths.SelfServiceSeenFile))));

        /// <summary>What BUILTIN\Users may keep on an object inside a folder of <paramref name="role"/>.</summary>
        private static int UsersAllowance(Role role) =>
            role is Role.Root or Role.Inherit ? OrdinaryUsersAllowance : 0;

        private bool SecureDirectory(string path, Role role)
        {
            var (acl, isProtected) = role switch
            {
                Role.Root => (RootAcl(), true),
                Role.AdminOnly or Role.Executed or Role.Cache or Role.Quarantine => (AdminOnlyAcl(), true),
                Role.Triggers => (TriggersAcl(), true),
                _ => ((RawAcl?)null, false),
            };
            var applied = acl == null ? Tighten(path, UsersAllowance(role), runtimeChecked: false) : Apply(path, acl, isProtected);
            // An ordinary folder is a warning like any file; the special ones are what the
            // rest of the protection rests on.
            if (!applied && role != Role.Inherit)
            {
                FailCritical($"{path} is not secured");
            }
            return applied;
        }

        private bool Apply(string path, RawAcl acl, bool isProtected)
        {
            try
            {
                using var handle = OpenUnlinked(path);
                if (handle == null) return false;

                // Only write when something differs. Setting a folder's DACL makes Windows
                // re-propagate it through everything below, which is slow on a large logs tree.
                if (Matches(Native.GetSecurity(handle), acl, isProtected))
                {
                    return true;
                }

                Native.SetSecurity(handle, Administrators, acl, isProtected);
                _log($"Secured {path}");
                return true;
            }
            catch (Exception ex)
            {
                Fail($"Could not secure {path}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Takes away what untrusted principals hold on an object whose permissions this
        /// class does not set outright, and gives an administrator's object to the
        /// Administrators group. It never adds access, never removes protection, and leaves
        /// a DACL alone when it already grants untrusted principals nothing more than
        /// <paramref name="usersAllowance"/> to BUILTIN\Users. Where SYSTEM checks the object
        /// before using it (<paramref name="runtimeChecked"/>), a direct Administrators
        /// member's entries go to the group, so the repair and that check agree.
        /// </summary>
        private bool Tighten(string path, int usersAllowance, bool runtimeChecked)
        {
            try
            {
                using var handle = OpenUnlinked(path);
                if (handle == null) return false;

                var current = Native.GetSecurity(handle);
                var reown = !IsTrustedPrincipal(current.Owner);
                var tightened = TightenAcl(current.DiscretionaryAcl, usersAllowance, IsAdministrative,
                    runtimeChecked ? IsAdministratorsMember : null);
                if (!reown && tightened == null)
                {
                    return true;
                }

                var isProtected = (current.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0;
                Native.SetSecurity(handle, reown ? Administrators : null, tightened, isProtected);
                _log($"Tightened {path}");
                return true;
            }
            catch (Exception ex)
            {
                Fail($"Could not secure {path}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Opens an object to change its security, or returns null if it became a link.</summary>
        private SafeFileHandle? OpenUnlinked(string path)
        {
            var handle = Native.Open(path, forWrite: true);
            var info = Native.GetInfo(handle);
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
                || ((info.Attributes & (uint)FileAttributes.Directory) == 0 && info.LinkCount > 1))
            {
                handle.Dispose();
                Fail($"Not securing {path}: it became a link while being repaired");
                return null;
            }
            return handle;
        }

        /// <summary>
        /// Makes quarantine\ a folder only SYSTEM and Administrators control, before anything
        /// is moved into it. One that is a link is removed; one a user made is moved aside
        /// (it is then quarantined like any other user-made folder) and replaced.
        /// </summary>
        private void PrepareQuarantine()
        {
            var dir = _layout.QuarantineDir;
            try
            {
                _quarantineUsable = PrepareQuarantineDir(dir, _layout.Root,
                    d => SecureDirectory(d, Role.Quarantine), message => _log($"WARNING: {message}"),
                    out var problem);
                if (!_quarantineUsable)
                {
                    FailCritical($"{dir} is not usable ({problem}); nothing will be quarantined");
                }
            }
            catch (Exception ex)
            {
                FailCritical($"Could not prepare {dir}: {ex.Message}");
            }
        }

        private void MigrateUserSelfServeManifest(string path, string why)
        {
            SelfServiceManifest? choices = null;
            if (TryReadRequest(path, requireTrustedOwner: false, out var content, out var problem))
            {
                choices = SelfServiceRequests.ParseLegacyManifest(content!, out problem);
            }
            if (choices == null)
            {
                _log($"WARNING: not keeping the choices in {path}: {problem}");
            }

            Quarantine(path, $"{why}; Self Service now keeps this file for SYSTEM to write");
            if (choices == null || File.Exists(path)) return;

            var yaml = new YamlDotNet.Serialization.SerializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                .Build().Serialize(choices);
            // Created exclusively and without following links: if anything has taken the
            // name since the quarantine, nothing is written.
            if (TryWriteNewFile(path, System.Text.Encoding.UTF8.GetBytes(yaml)))
            {
                _log($"Rewrote {path} as SYSTEM with the {choices.ManagedInstalls.Count + choices.ManagedUninstalls.Count} choice(s) it held");
            }
            else
            {
                Fail($"Could not rewrite {path}: something already has that name");
            }
        }

        private void Quarantine(string path, string why)
        {
            try
            {
                if (!_quarantineUsable)
                {
                    throw new InvalidOperationException("quarantine is not usable");
                }

                if (_quarantineSession == null)
                {
                    _quarantineSession = CreateQuarantineSession(_layout.QuarantineDir, out var problem)
                        ?? throw new InvalidOperationException(problem);
                }

                var relative = Path.GetRelativePath(_layout.Root, path);
                var target = Path.Combine(_quarantineSession, relative);
                var targetDir = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(targetDir);

                // Checked before every move, so nothing is ever moved where a user points.
                if (!IsAdminOnly(targetDir, out var unsafeTarget))
                {
                    throw new InvalidOperationException(unsafeTarget);
                }

                if (Directory.Exists(path)) Directory.Move(path, target);
                else File.Move(path, target);
                _log($"WARNING: quarantined {path} ({why}) to {target}");
            }
            catch (Exception ex)
            {
                // Left where it is: never adopted, and the runtime checks still refuse it.
                Fail($"Could not quarantine {path} ({why}): {ex.Message}");
            }
        }

        private void Remove(string path, bool isDirectory, string why)
        {
            try
            {
                // For a link this removes the link, never what it points at.
                if (isDirectory) Directory.Delete(path, recursive: !new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint));
                else File.Delete(path);
                _log($"WARNING: removed {path} ({why})");
            }
            catch (Exception ex)
            {
                Fail($"Could not remove {path} ({why}): {ex.Message}");
            }
        }

        /// <summary>One item could not be handled. Logged; the repair as a whole still counts.</summary>
        private void Fail(string message)
        {
            _log($"WARNING: {message}");
        }

        /// <summary>Something the protection rests on could not be secured.</summary>
        private void FailCritical(string message)
        {
            _secured = false;
            _critical.Add(message);
            _log($"ERROR: {message}");
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid == null) return "nobody";
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch { return sid.Value; }
    }

    private readonly record struct ObjectInfo(bool IsDirectory, bool IsReparsePoint, uint LinkCount, SecurityIdentifier? Owner)
    {
        public static ObjectInfo Read(string path)
        {
            using var handle = Native.Open(path, forWrite: false);
            var info = Native.GetInfo(handle);
            return new ObjectInfo(
                (info.Attributes & (uint)FileAttributes.Directory) != 0,
                (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0,
                info.LinkCount,
                Native.GetSecurity(handle).Owner);
        }
    }

    /// <summary>
    /// Handle-based calls, so what is inspected is exactly what gets changed: the object is
    /// opened without following links, checked, and secured through the same handle.
    /// </summary>
    private static class Native
    {
        private const uint READ_CONTROL = 0x00020000;
        private const uint WRITE_DAC = 0x00040000;
        private const uint WRITE_OWNER = 0x00080000;
        private const uint FILE_READ_ATTRIBUTES = 0x0080;
        private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const int SE_FILE_OBJECT = 1;
        private const uint OWNER_SECURITY_INFORMATION = 0x1;
        private const uint DACL_SECURITY_INFORMATION = 0x4;
        private const uint PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;
        private const uint UNPROTECTED_DACL_SECURITY_INFORMATION = 0x20000000;

        // FILETIMEs are two DWORDs with 4-byte alignment; Pack = 4 keeps the longs from adding padding.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public long CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        public readonly record struct Info(uint Attributes, uint LinkCount, ulong FileId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint info,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

        [DllImport("advapi32.dll")]
        private static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint info,
            byte[]? owner, byte[]? group, byte[]? dacl, byte[]? sacl);

        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateDirectoryW(string path, IntPtr security);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        public static SafeFileHandle Open(string path, bool forWrite)
        {
            var access = READ_CONTROL | FILE_READ_ATTRIBUTES | (forWrite ? WRITE_DAC | WRITE_OWNER : 0);
            var handle = CreateFileW(@"\\?\" + Path.GetFullPath(path), access, FILE_SHARE_ALL, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return handle;
        }

        public static SafeFileHandle OpenForRead(string path)
        {
            const uint GENERIC_READ = 0x80000000;
            var handle = CreateFileW(@"\\?\" + Path.GetFullPath(path), GENERIC_READ | READ_CONTROL | FILE_READ_ATTRIBUTES,
                FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return handle;
        }

        public static SafeFileHandle CreateNew(string path)
        {
            const uint GENERIC_WRITE = 0x40000000;
            const uint CREATE_NEW = 1;
            var handle = CreateFileW(@"\\?\" + Path.GetFullPath(path), GENERIC_WRITE, 0, IntPtr.Zero,
                CREATE_NEW, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return handle;
        }

        public static Info GetInfo(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return new Info(info.FileAttributes, info.NumberOfLinks, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }

        public static RawSecurityDescriptor GetSecurity(SafeFileHandle handle)
        {
            var rc = GetSecurityInfo(handle, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                out _, out _, out _, out _, out var descriptor);
            if (rc != 0) throw new Win32Exception((int)rc);
            try
            {
                var length = (int)GetSecurityDescriptorLength(descriptor);
                var bytes = new byte[length];
                Marshal.Copy(descriptor, bytes, 0, length);
                return new RawSecurityDescriptor(bytes, 0);
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        /// <summary>
        /// Sets the owner, the DACL with the given protection, or both; a null leaves that
        /// part as it is.
        /// </summary>
        public static void SetSecurity(SafeFileHandle handle, SecurityIdentifier? owner, RawAcl? acl, bool isProtected)
        {
            byte[]? ownerBytes = null;
            byte[]? aclBytes = null;
            uint flags = 0;
            if (owner != null)
            {
                ownerBytes = new byte[owner.BinaryLength];
                owner.GetBinaryForm(ownerBytes, 0);
                flags |= OWNER_SECURITY_INFORMATION;
            }
            if (acl != null)
            {
                aclBytes = new byte[acl.BinaryLength];
                acl.GetBinaryForm(aclBytes, 0);
                flags |= DACL_SECURITY_INFORMATION
                    | (isProtected ? PROTECTED_DACL_SECURITY_INFORMATION : UNPROTECTED_DACL_SECURITY_INFORMATION);
            }
            if (flags == 0) return;

            var rc = SetSecurityInfo(handle, SE_FILE_OBJECT, flags, ownerBytes, null, aclBytes, null);
            if (rc != 0) throw new Win32Exception((int)rc);
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer,
            int maxLength, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        /// <summary>
        /// The SIDs of the direct members of BUILTIN\Administrators, from the local account
        /// database. Level 0 returns SIDs only, so no name is resolved and no domain
        /// controller is asked anything. Empty when the group cannot be read.
        /// </summary>
        public static HashSet<SecurityIdentifier> GetAdministratorsMembers()
        {
            var members = new HashSet<SecurityIdentifier>();
            try
            {
                // The group's name is localized; a builtin alias resolves locally.
                var name = Administrators.Translate(typeof(NTAccount)).Value;
                name = name[(name.IndexOf('\\') + 1)..];

                const int MAX_PREFERRED_LENGTH = -1;
                if (NetLocalGroupGetMembers(null, name, 0, out var buffer, MAX_PREFERRED_LENGTH, out var read, out _, IntPtr.Zero) != 0)
                {
                    return members;
                }
                try
                {
                    for (var i = 0; i < read; i++)
                    {
                        var sidPointer = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                        members.Add(new SecurityIdentifier(sidPointer));
                    }
                }
                finally
                {
                    NetApiBufferFree(buffer);
                }
            }
            catch
            {
                members.Clear();
            }
            return members;
        }
    }
}
