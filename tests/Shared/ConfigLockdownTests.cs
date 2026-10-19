using System.Security.AccessControl;
using System.Security.Principal;
using Cimian.Core;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Config.yaml is closed to standard users: it can carry repository credentials, and a
/// user able to open it could hold it open and stop every run. What a non-administrator
/// sees when a tool needs it has to say why.
/// </summary>
public class ConfigLockdownTests : IDisposable
{
    private readonly string _dir;

    public ConfigLockdownTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "ConfigLockdown", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A config file the current account cannot read, as Config.yaml is for a standard user.</summary>
    private string UnreadableConfig()
    {
        var path = Path.Combine(_dir, "Config.yaml");
        File.WriteAllText(path, "software_repo_url: https://repo.example.org\nrepo_path: C:\\repo\n");
        var file = new FileInfo(path);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadData, AccessControlType.Deny));
        file.SetAccessControl(acl);
        return path;
    }

    [Fact]
    public void ConfigFileAcl_IsSystemAndAdministratorsOnly()
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var aces = ManagedInstallsSecurity.AdminOnlyFileAcl().Cast<CommonAce>().ToList();

        Assert.NotEmpty(aces);
        Assert.DoesNotContain(aces, a => a.SecurityIdentifier == users);
        Assert.All(aces, a => Assert.True(ManagedInstallsSecurity.IsTrustedPrincipal(a.SecurityIdentifier)));
        Assert.True(ManagedInstallsSecurity.IsAdminOnlyFile(CimianPaths.ConfigYaml));
        Assert.False(ManagedInstallsSecurity.IsAdminOnlyFile(CimianPaths.SelfServeManifestYaml));
    }

    [Fact]
    public void UserRequestFolders_AreTriggersAndSelfService()
    {
        Assert.True(ManagedInstallsSecurity.IsUserRequestFolder(CimianPaths.TriggersDir));
        Assert.True(ManagedInstallsSecurity.IsUserRequestFolder(CimianPaths.SelfServiceDropDir));
        Assert.False(ManagedInstallsSecurity.IsUserRequestFolder(CimianPaths.ManagedInstallsRoot));
    }

    [Fact]
    public void Managedsoftwareupdate_UnreadableConfig_SaysToElevate()
    {
        var path = UnreadableConfig();

        var ex = Assert.Throws<Cimian.CLI.managedsoftwareupdate.Services.ConfigurationRefusedException>(
            () => new Cimian.CLI.managedsoftwareupdate.Services.ConfigurationService().LoadConfig(path, requireTrusted: true));
        Assert.Contains("elevated", ex.Message);
    }

    [Fact]
    public void Manifestutil_UnreadableConfig_SaysToElevate()
    {
        var path = UnreadableConfig();

        var ex = Assert.Throws<UnauthorizedAccessException>(
            () => new Cimian.CLI.Manifestutil.Services.ManifestService().LoadConfig(path));
        Assert.Contains("elevated", ex.Message);
    }

    [Fact]
    public void Makepkginfo_UnreadableConfig_SaysToElevate()
    {
        var path = UnreadableConfig();

        var ex = Assert.Throws<UnauthorizedAccessException>(
            () => new Cimian.CLI.Makepkginfo.Services.PkgInfoBuilder().LoadConfig(path));
        Assert.Contains("elevated", ex.Message);
    }
}
