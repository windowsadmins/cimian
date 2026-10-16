using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>Runs only in an elevated process; the repair sets owners and protected DACLs.</summary>
public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "Needs an elevated administrator or SYSTEM";
        }
    }
}

/// <summary>
/// The permissions repair walking a real tree: a ManagedInstalls laid out under a fresh
/// folder in %ProgramData%, never the machine's own. The test account counts as a member of
/// Administrators; files a standard user would have made are given to BUILTIN\Users.
/// </summary>
public class RepairWalkerTests : IDisposable
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private readonly string _base;
    private readonly string _root;
    private readonly ManagedInstallsSecurity.RepairLayout _layout;
    private readonly SecurityIdentifier _me;
    private readonly List<string> _log = new();

    public RepairWalkerTests()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _me = identity.User!;
        _base = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CimianRepairTests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "ManagedInstalls");
        _layout = new ManagedInstallsSecurity.RepairLayout(_root);
        if (new ElevatedFactAttribute().Skip != null) return;

        // %ProgramData% lets users create folders, and hands their creator full control:
        // the folder above the test root is closed the way a real installation's parent is.
        Directory.CreateDirectory(_base);
        Icacls(_base, "/setowner", "*S-1-5-32-544");
        Icacls(_base, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_base)) { Icacls(_base, "/reset", "/T", "/C", "/Q"); Directory.Delete(_base, recursive: true); } } catch { }
    }

    private bool Repair(bool deep = true) =>
        ManagedInstallsSecurity.Repair(_layout, _log.Add, deep, sid => sid == _me);

    private static void Icacls(string path, params string[] args)
    {
        var psi = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(path);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"icacls {path} {string.Join(' ', args)}: {p.StandardOutput.ReadToEnd()}{p.StandardError.ReadToEnd()}");
    }

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static FileSystemSecurity Security(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();

    private static SecurityIdentifier Owner(string path) => (SecurityIdentifier)Security(path).GetOwner(typeof(SecurityIdentifier))!;

    private static bool IsProtected(string path) => Security(path).AreAccessRulesProtected;

    private static List<FileSystemAccessRule> Allows(string path) =>
        Security(path).GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow).ToList();

    private static bool Grants(string path, SecurityIdentifier sid) => Allows(path).Any(r => r.IdentityReference == sid);

    private static void AssertAdminOnly(string path)
    {
        Assert.True(IsProtected(path), $"{path} is not protected");
        Assert.All(Allows(path), r => Assert.True(r.IdentityReference == System || r.IdentityReference == Admins,
            $"{path} grants {r.IdentityReference.Value} {r.FileSystemRights}"));
    }

    [ElevatedFact]
    public void SpecialLocations_GetTheirOwnPermissions()
    {
        Write("Config.yaml", "SoftwareRepoURL: https://repo.example.org\n");

        Assert.True(Repair(), string.Join("\n", _log));

        Assert.True(IsProtected(_root));
        Assert.Contains(Allows(_root), r => r.IdentityReference == Users
            && (r.FileSystemRights & FileSystemRights.WriteData) == 0 && (r.FileSystemRights & FileSystemRights.ReadData) != 0);
        foreach (var dir in _layout.AdminOnlyDirs) AssertAdminOnly(dir);
        AssertAdminOnly(_layout.ConfigYaml);
        foreach (var dir in _layout.UserRequestFolders)
        {
            Assert.True(IsProtected(dir));
            Assert.Contains(Allows(dir), r => r.IdentityReference == Users && (r.FileSystemRights & FileSystemRights.CreateFiles) != 0);
        }
        Assert.Equal(Admins, Owner(_layout.ConfigYaml));
    }

    [ElevatedFact]
    public void ScriptAStandardUserOwns_InSbin_IsQuarantined()
    {
        var script = Write(@"sbin\preflight.ps1", "whoami > C:\\pwned.txt");
        Icacls(script, "/setowner", "*S-1-5-32-545");

        Repair();

        Assert.False(File.Exists(script));
        var quarantined = Directory.GetFiles(_layout.QuarantineDir, "preflight.ps1", SearchOption.AllDirectories);
        Assert.Single(quarantined);
        Assert.Contains(_log, l => l.Contains("quarantined") && l.Contains("preflight.ps1"));
    }

    [ElevatedFact]
    public void AdminLockedFiles_AreNotOpenedUp()
    {
        // An administrator keeps a locked backup of Config.yaml, holding the repository
        // token, and a folder of their own closed to users. Neither may become readable.
        var backup = Write("Config.yaml.20261006.bak", "SoftwareRepoURL: https://user:token@repo.example.org\n");
        Icacls(backup, "/inheritance:r", "/grant:r", "*S-1-5-18:F", "*S-1-5-32-544:F");
        var privateDir = Path.Combine(_root, "admin-notes");
        Directory.CreateDirectory(privateDir);
        Icacls(privateDir, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F");
        var note = Path.Combine(privateDir, "note.txt");
        File.WriteAllText(note, "x");

        Repair();

        AssertAdminOnly(backup);
        AssertAdminOnly(privateDir);
        Assert.False(Grants(note, Users));
    }

    [ElevatedFact]
    public void OrdinaryFileGrantingUsersWrite_IsCutBackToRead()
    {
        var report = Write(@"reports\items.json", "{}");
        Icacls(report, "/grant", "*S-1-5-32-545:(M)");

        Repair();

        Assert.All(Allows(report).Where(r => r.IdentityReference == Users),
            r => Assert.Equal((FileSystemRights)0, r.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete)));
    }

    [ElevatedFact]
    public void ConfigOwnedByAnAdministratorsMember_IsReownedNotQuarantined()
    {
        var config = Write("Config.yaml", "SoftwareRepoURL: https://repo.example.org\n");
        var script = Write(@"sbin\postflight.ps1", "exit 0");
        Icacls(config, "/setowner", $"*{_me.Value}");
        Icacls(script, "/setowner", $"*{_me.Value}");

        Repair();

        Assert.True(File.Exists(config));
        Assert.True(File.Exists(script));
        Assert.Equal(Admins, Owner(config));
        Assert.Equal(Admins, Owner(script));
        AssertAdminOnly(config);
    }

    [ElevatedFact]
    public void CachedInstallerReadableByUsers_IsClosed()
    {
        var installer = Write(@"Cache\apps\setup.ps1", "exit 0");
        Icacls(installer, "/inheritance:r", "/grant:r", "*S-1-5-18:F", "*S-1-5-32-544:F", "*S-1-5-32-545:R");

        Repair();

        Assert.True(File.Exists(installer));
        Assert.False(Grants(installer, Users));
        Assert.False(Grants(Path.GetDirectoryName(installer)!, Users));
    }

    [ElevatedFact]
    public void CachePathOutsideManagedInstalls_IsNeverTouched()
    {
        // An administrator who points CachePath at a shared folder must not have it locked
        // down or emptied: the repair changes nothing outside ManagedInstalls.
        var external = Path.Combine(_base, "Shared");
        Directory.CreateDirectory(external);
        Icacls(external, "/grant", "*S-1-5-32-545:(OI)(CI)M");
        var usersFile = Path.Combine(external, "notes.txt");
        File.WriteAllText(usersFile, "a user's file");
        Icacls(usersFile, "/setowner", "*S-1-5-32-545");
        var usersFolder = Path.Combine(external, "project");
        Directory.CreateDirectory(usersFolder);
        Icacls(usersFolder, "/setowner", "*S-1-5-32-545");
        var before = Security(external).GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        Write("Config.yaml", $"SoftwareRepoURL: https://repo.example.org\nCachePath: {external}\n");

        Repair();

        Assert.True(File.Exists(usersFile));
        Assert.True(Directory.Exists(usersFolder));
        Assert.Equal(before, Security(external).GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner));
        Assert.True(Grants(external, Users));
    }

    [ElevatedFact]
    public void ScriptWithAnAdministratorsMembersEntry_IsAcceptedAtRuntimeAfterTheRepair()
    {
        // The repair trusts a direct member's entry; the runtime check trusts only the
        // group. The repair hands the entry to the group so the two agree.
        Repair();
        var script = Write(@"sbin\preflight.ps1", "exit 0");
        Icacls(script, "/setowner", "*S-1-5-32-544");
        Icacls(script, "/grant", $"*{_me.Value}:(M)");
        Assert.False(ManagedInstallsSecurity.IsSafeToExecute(script, out _));

        Repair();

        Assert.True(File.Exists(script));
        Assert.True(ManagedInstallsSecurity.IsSafeToExecute(script, out var reason), reason);
        Assert.False(Grants(script, _me));
    }

    [ElevatedFact]
    public void Rollback_WithOneFileAUserCanChange_CopiesNothing()
    {
        Repair();
        var backup = _layout.At(Cimian.Core.CimianPaths.SelfUpdateBackupDir);
        var good = Path.Combine(backup, "a.exe");
        File.WriteAllText(good, "good");
        Icacls(good, "/setowner", "*S-1-5-32-544");
        var bad = Path.Combine(backup, "b.dll");
        File.WriteAllText(bad, "planted");
        Icacls(bad, "/grant", "*S-1-5-32-545:(M)");
        var install = Path.Combine(_base, "Program Files Cimian");
        Directory.CreateDirectory(install);

        Assert.False(SelfUpdateService.PerformRollback(backup, install));
        Assert.Empty(Directory.GetFiles(install));
    }
}
