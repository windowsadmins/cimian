using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Models;
using CatalogItem = Cimian.CLI.managedsoftwareupdate.Models.CatalogItem;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// managed_updates means "patch if present": an item listed only there is updated
/// where it is installed and left alone where it is not. These tests drive the real
/// status checks against files in a temp directory, with item names no machine has
/// a ManagedInstalls registry entry for. The ManagedInstalls entry lookup is
/// injected, so a test can leave one behind without writing HKLM.
/// </summary>
public class ManagedUpdatesPresenceTests : IDisposable
{
    private const string WrongHash = "00000000000000000000000000000000";

    private readonly string _testDir;
    private readonly CimianConfig _config;
    private readonly UpdateEngine _engine;
    private readonly string _suffix = Guid.NewGuid().ToString("N");

    // Item names the injected lookup reports a ManagedInstalls entry for.
    private readonly HashSet<string> _managedInstallsEntries = new(StringComparer.OrdinalIgnoreCase);

    public ManagedUpdatesPresenceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "ManagedUpdates", _suffix);
        Directory.CreateDirectory(_testDir);

        _config = new CimianConfig { CachePath = Path.Combine(_testDir, "Cache") };
        Directory.CreateDirectory(_config.CachePath);
        _engine = new UpdateEngine(_config, new StatusService(
            managedInstallsEntryLookup: name => _managedInstallsEntries.Contains(name)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    // No hyphen: a requires entry is split on one into name and version.
    private string Unique(string name) => $"{name}{_suffix}";

    /// <summary>An item whose installs file is not on disk: not installed.</summary>
    private CatalogItem AbsentItem(string name) => new()
    {
        Name = name,
        Version = "2.0.0",
        Installs = new List<InstallCheckItem>
        {
            new() { Type = "file", Path = Path.Combine(_testDir, name + ".missing") }
        }
    };

    /// <summary>An item whose installs file exists with another hash: installed, out of date.</summary>
    private CatalogItem OutdatedItem(string name)
    {
        var path = Path.Combine(_testDir, name + ".bin");
        File.WriteAllText(path, "old build");
        return new CatalogItem
        {
            Name = name,
            Version = "2.0.0",
            Installs = new List<InstallCheckItem>
            {
                new() { Type = "file", Path = path, Md5Checksum = WrongHash }
            }
        };
    }

    private static Dictionary<string, CatalogItem> Catalog(params CatalogItem[] items) =>
        items.ToDictionary(i => i.Name.ToLowerInvariant());

    private static ManifestItem Entry(string name, string action) =>
        new() { Name = name, Action = action, SourceManifest = "test" };

    [Fact]
    public void ManagedUpdateOnly_NotInstalled_IsLeftAlone()
    {
        var item = AbsentItem(Unique("absent"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Contains(item.Name, _engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_InstallcheckScriptSaysInstallNeeded_IsLeftAlone()
    {
        var item = new CatalogItem
        {
            Name = Unique("scripted"),
            Version = "2.0.0",
            InstallcheckScript = "exit 0"
        };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Contains(item.Name, _engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_InstalledAndOutOfDate_StillUpdates()
    {
        var item = OutdatedItem(Unique("outdated"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Single(toUpdate, item);
        Assert.Empty(_engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedInstall_NotInstalled_StillInstalls()
    {
        var item = AbsentItem(Unique("install"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "install") };

        var (toInstall, _, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.Empty(_engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void InBothManagedInstallsAndManagedUpdates_NotInstalled_StillInstalls()
    {
        var item = AbsentItem(Unique("both"));
        var manifest = new ManifestService(_config).DeduplicateItems(new List<ManifestItem>
        {
            Entry(item.Name, "update"),
            Entry(item.Name, "install")
        });

        var (toInstall, _, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Single(toInstall, item);
        Assert.Empty(_engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_NotInstalled_DoesNotPullInItsRequires()
    {
        var dep = AbsentItem(Unique("dep"));
        var item = AbsentItem(Unique("absent"));
        item.Requires = new List<string> { dep.Name };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };
        var catalog = Catalog(item, dep);

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, catalog);
        _engine.ResolveDependencies(manifest, catalog, toUpdate);

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.DoesNotContain(manifest, m => m.Name == dep.Name);
    }

    [Fact]
    public void ManagedUpdateOnly_InstalledAndOutOfDate_StillPullsInItsRequires()
    {
        var dep = AbsentItem(Unique("dep"));
        var item = OutdatedItem(Unique("outdated"));
        item.Requires = new List<string> { dep.Name };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };
        var catalog = Catalog(item, dep);

        var (_, toUpdate, _, _) = _engine.IdentifyActions(manifest, catalog);
        _engine.ResolveDependencies(manifest, catalog, toUpdate);

        Assert.Contains(toUpdate, i => i.Name == item.Name);
        Assert.Contains(toUpdate, i => i.Name == dep.Name);
        Assert.Contains(manifest, m => m.Name == dep.Name);
    }
    private static readonly IReadOnlyDictionary<string, ItemOutcome> NoOutcomes =
        new Dictionary<string, ItemOutcome>();

    private static readonly IReadOnlyDictionary<string, (string Reason, string? Cause, string? InstalledVersion, bool WasUpdate, bool PendingRestart)> NoSuppressions =
        new Dictionary<string, (string Reason, string? Cause, string? InstalledVersion, bool WasUpdate, bool PendingRestart)>();

    /// <summary>An item whose installs array names one present file and one missing file.</summary>
    private CatalogItem HalfInstalledItem(string name)
    {
        var present = Path.Combine(_testDir, name + ".present");
        File.WriteAllText(present, "build");
        return new CatalogItem
        {
            Name = name,
            Version = "2.0.0",
            Installs = new List<InstallCheckItem>
            {
                new() { Type = "file", Path = present, Md5Checksum = WrongHash },
                new() { Type = "file", Path = Path.Combine(_testDir, name + ".missing") }
            }
        };
    }

    // --- Presence follows Munki 7's someVersionInstalled -----------------------------

    [Fact]
    public void ManagedUpdateOnly_OnDemand_IsLeftAlone()
    {
        var item = OutdatedItem(Unique("ondemand"));
        item.OnDemand = true;
        _managedInstallsEntries.Add(item.Name);
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Contains(item.Name, _engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_InstallcheckScriptSaysInstallNeeded_WithLeftoverManagedInstallsEntry_IsLeftAlone()
    {
        // The app was installed by Cimian once and has since been removed: the
        // ManagedInstalls entry is still there, the installcheck_script says install.
        var item = new CatalogItem
        {
            Name = Unique("removed"),
            Version = "2.0.0",
            InstallcheckScript = "exit 0"
        };
        _managedInstallsEntries.Add(item.Name);
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Contains(item.Name, _engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_InstallsArrayWithOneOfTwoFilesMissing_WithLeftoverManagedInstallsEntry_IsLeftAlone()
    {
        var item = HalfInstalledItem(Unique("half"));
        _managedInstallsEntries.Add(item.Name);
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Contains(item.Name, _engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_InstallerBlockProductCodeNotRegistered_WithLeftoverManagedInstallsEntry_IsLeftAlone()
    {
        var item = new CatalogItem
        {
            Name = Unique("msireceipt"),
            Version = "2.0.0",
            Installer = new InstallerInfo { Type = "msi", ProductCode = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}" }
        };
        _managedInstallsEntries.Add(item.Name);
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Contains(item.Name, _engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_CheckFilePresentAndOutOfDate_StillUpdates()
    {
        var path = Path.Combine(_testDir, "checkfile.txt");
        File.WriteAllText(path, "no version resource");
        var item = new CatalogItem
        {
            Name = Unique("checkfile"),
            Version = "2.0.0",
            Check = new CheckInfo { File = new FileCheck { Path = path, Hash = WrongHash } }
        };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Single(toUpdate, item);
        Assert.Empty(_engine.ManagedUpdatesSkippedAbsent);
    }

    [Fact]
    public void ManagedUpdateOnly_RealInstallerWithNothingToDetect_IsLeftAlone()
    {
        // No installs, no receipts, no check.*, no ManagedInstalls entry. Munki's
        // someVersionInstalled calls this installed, and Munki then installs nothing
        // because its installedState agrees. Cimian's status check calls it not installed, so
        // presence has to say absent for the same net result.
        var item = new CatalogItem
        {
            Name = Unique("nodetect"),
            Version = "2.0.0",
            Installer = new InstallerInfo { Type = "exe", Location = "apps/nodetect.exe" }
        };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Contains(item.Name, _engine.ManagedUpdatesSkippedAbsent);
    }

    // --- A broken status check is reported as broken --------------------------------

    [Fact]
    public void ManagedUpdateOnly_StatusCheckFailed_IsSkippedWithTheCheckReasonCode()
    {
        // An installs entry with neither a type nor an identity field cannot be evaluated.
        var item = new CatalogItem
        {
            Name = Unique("broken"),
            Version = "2.0.0",
            Installs = new List<InstallCheckItem> { new() }
        };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        var (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        var skip = Assert.Contains(item.Name, _engine.ManagedUpdatesSkipReasons);
        Assert.Equal(StatusReasonCode.CheckFailed, skip.ReasonCode);
        Assert.Contains("status check failed", skip.Reason);
    }

    [Fact]
    public void ManagedUpdateOnly_NotInstalled_IsSkippedAsNotInstalled()
    {
        var item = AbsentItem(Unique("absent"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        _engine.IdentifyActions(manifest, Catalog(item));

        var skip = Assert.Contains(item.Name, _engine.ManagedUpdatesSkipReasons);
        Assert.Equal(StatusReasonCode.NotInstalled, skip.ReasonCode);
    }

    // --- Reporting: an absent item appears nowhere, as in Munki ----------------------

    [Fact]
    public void ManagedUpdateOnly_NotInstalled_IsLeftOutOfItemsJson()
    {
        var absent = AbsentItem(Unique("absent"));
        var outdated = OutdatedItem(Unique("outdated"));
        var manifest = new List<ManifestItem> { Entry(absent.Name, "update"), Entry(outdated.Name, "update") };
        var catalog = Catalog(absent, outdated);

        var (toInstall, toUpdate, toUninstall, _) = _engine.IdentifyActions(manifest, catalog);
        var items = _engine.BuildSessionItems(manifest, toInstall, toUpdate, toUninstall, catalog, NoOutcomes, NoSuppressions);

        Assert.DoesNotContain(items, i => i.Name == absent.Name);
        var reported = Assert.Single(items, i => i.Name == outdated.Name);
        Assert.Equal("managed_updates", reported.ItemType);
    }

    [Fact]
    public void ManagedUpdateOnly_NotInstalled_IsLeftOutOfInstallInfo()
    {
        var absent = AbsentItem(Unique("absent"));
        var outdated = OutdatedItem(Unique("outdated"));
        var manifest = new List<ManifestItem> { Entry(absent.Name, "update"), Entry(outdated.Name, "update") };
        var catalog = Catalog(absent, outdated);

        var (toInstall, toUpdate, toUninstall, _) = _engine.IdentifyActions(manifest, catalog);
        var info = _engine.BuildInstallInfo(manifest, toInstall, toUpdate, toUninstall, catalog);

        Assert.DoesNotContain(absent.Name, info.ManagedUpdates);
        Assert.DoesNotContain(absent.Name, info.ProcessedInstalls);
        Assert.DoesNotContain(info.ManagedInstalls, i => i.Name == absent.Name);
        Assert.Contains(outdated.Name, info.ManagedUpdates);
        Assert.Contains(outdated.Name, info.ProcessedInstalls);
    }
    // --- SomeVersionInstalled, rule by rule ------------------------------------------

    private StatusService Presence(TimeSpan? installcheckTimeout = null) =>
        new(installcheckTimeout, name => _managedInstallsEntries.Contains(name));

    [Fact]
    public void SomeVersionInstalled_OnDemand_IsNotInstalled()
    {
        var item = OutdatedItem(Unique("ondemand"));
        item.OnDemand = true;

        Assert.False(Presence().SomeVersionInstalled(item).Installed);
    }

    [Fact]
    public void SomeVersionInstalled_InstallcheckScriptExitsZero_IsNotInstalled_EvenWithManagedInstallsEntry()
    {
        var item = new CatalogItem { Name = Unique("script0"), Version = "2.0.0", InstallcheckScript = "exit 0" };
        _managedInstallsEntries.Add(item.Name);

        Assert.False(Presence().SomeVersionInstalled(item).Installed);
    }

    [Fact]
    public void SomeVersionInstalled_InstallcheckScriptExitsNonZero_IsInstalled()
    {
        var item = new CatalogItem { Name = Unique("script1"), Version = "2.0.0", InstallcheckScript = "exit 1" };

        Assert.True(Presence().SomeVersionInstalled(item).Installed);
    }

    [Fact]
    public void SomeVersionInstalled_InstallcheckScriptErrors_IsInstalled()
    {
        var item = new CatalogItem { Name = Unique("scripterr"), Version = "2.0.0", InstallcheckScript = "Start-Sleep -Seconds 30" };

        var presence = Presence(TimeSpan.FromMilliseconds(500)).SomeVersionInstalled(item);

        Assert.True(presence.Installed);
        Assert.Null(presence.FailureReasonCode);
    }

    [Fact]
    public void SomeVersionInstalled_InstallcheckScript_ReusesTheResultItIsGiven()
    {
        // The script would say "install needed"; the result handed in says otherwise.
        var item = new CatalogItem { Name = Unique("reuse"), Version = "2.0.0", InstallcheckScript = "exit 0" };
        var prior = new StatusCheckResult
        {
            DetectionMethod = DetectionMethod.Script,
            ReasonCode = StatusReasonCode.ScriptConfirmed,
            Reason = "already run"
        };

        Assert.True(Presence().SomeVersionInstalled(item, prior).Installed);
    }

    [Fact]
    public void SomeVersionInstalled_VersionScriptPrintsAVersion_IsInstalled_EvenWithAnInstallsEntryMissing()
    {
        var item = AbsentItem(Unique("vscript"));
        item.VersionScript = "Write-Output '1.0.0'";

        Assert.True(Presence().SomeVersionInstalled(item).Installed);
    }

    [Fact]
    public void SomeVersionInstalled_VersionScriptPrintsNothing_IsNotInstalled_EvenWithEveryInstallsEntryPresent()
    {
        var item = OutdatedItem(Unique("vscript"));
        item.VersionScript = "exit 0";

        Assert.False(Presence().SomeVersionInstalled(item).Installed);
    }

    [Fact]
    public void SomeVersionInstalled_EveryInstallsEntryPresent_IsInstalled()
    {
        var item = OutdatedItem(Unique("present"));

        Assert.True(Presence().SomeVersionInstalled(item).Installed);
    }

    [Fact]
    public void SomeVersionInstalled_OneOfTwoInstallsEntriesMissing_IsNotInstalled_EvenWithManagedInstallsEntry()
    {
        var item = HalfInstalledItem(Unique("half"));
        _managedInstallsEntries.Add(item.Name);

        var presence = Presence().SomeVersionInstalled(item);

        Assert.False(presence.Installed);
        Assert.Null(presence.FailureReasonCode);
    }

    [Fact]
    public void SomeVersionInstalled_InstallsEntryCannotBeEvaluated_IsNotInstalled_WithAFailureCode()
    {
        var item = new CatalogItem { Name = Unique("broken"), Version = "2.0.0", Installs = new List<InstallCheckItem> { new() } };

        var presence = Presence().SomeVersionInstalled(item);

        Assert.False(presence.Installed);
        Assert.Equal(StatusReasonCode.CheckFailed, presence.FailureReasonCode);
    }

    [Fact]
    public void SomeVersionInstalled_ReceiptLikeCheckFile_FollowsTheFile()
    {
        var path = Path.Combine(_testDir, "receipt.txt");
        var item = new CatalogItem
        {
            Name = Unique("receipt"),
            Version = "2.0.0",
            Check = new CheckInfo { File = new FileCheck { Path = path } }
        };

        Assert.False(Presence().SomeVersionInstalled(item).Installed);
        File.WriteAllText(path, "here");
        Assert.True(Presence().SomeVersionInstalled(item).Installed);
    }

    [Fact]
    public void SomeVersionInstalled_ReceiptLikeInstallerProductCodeNotRegistered_IsNotInstalled()
    {
        var item = new CatalogItem
        {
            Name = Unique("msireceipt"),
            Version = "2.0.0",
            Installer = new InstallerInfo { Type = "msi", ProductCode = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}" }
        };
        _managedInstallsEntries.Add(item.Name);

        Assert.False(Presence().SomeVersionInstalled(item).Installed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nopkg")]
    [InlineData("script")]
    public void SomeVersionInstalled_NoChecks_ScriptOnlyItem_IsInstalled(string installerType)
    {
        var item = new CatalogItem { Name = Unique("nochecks"), Version = "2.0.0", Installer = new InstallerInfo { Type = installerType } };

        Assert.True(Presence().SomeVersionInstalled(item).Installed);
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("msi")]
    public void SomeVersionInstalled_NoChecks_RealInstaller_IsNotInstalled(string installerType)
    {
        var item = new CatalogItem { Name = Unique("nodetect"), Version = "2.0.0", Installer = new InstallerInfo { Type = installerType } };

        Assert.False(Presence().SomeVersionInstalled(item).Installed);
    }
    // --- Present, but the status check failed: Munki queues nothing ------------------

    private UpdateEngine EngineWithInstallcheckTimeout(TimeSpan timeout) =>
        new(_config, new StatusService(timeout, name => _managedInstallsEntries.Contains(name)));

    /// <summary>A script-only item whose installcheck_script times out: a failed check that asks for no action.</summary>
    private CatalogItem ScriptTimeoutItem(string name) => new()
    {
        Name = name,
        Version = "2.0.0",
        InstallcheckScript = "Start-Sleep -Seconds 30"
    };

    [Fact]
    public void ManagedUpdateOnly_InstallcheckScriptTimesOut_QueuesNothing_AndKeepsTheCheckReasonCode()
    {
        var engine = EngineWithInstallcheckTimeout(TimeSpan.FromMilliseconds(500));
        var dep = AbsentItem(Unique("dep"));
        var item = ScriptTimeoutItem(Unique("scripterr"));
        item.Requires = new List<string> { dep.Name };
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };
        var catalog = Catalog(item, dep);

        var (toInstall, toUpdate, _, _) = engine.IdentifyActions(manifest, catalog);
        engine.ResolveDependencies(manifest, catalog, toUpdate);

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.DoesNotContain(manifest, m => m.Name == dep.Name);
        Assert.Empty(engine.ManagedUpdatesSkippedAbsent);
        var failure = Assert.Contains(item.Name, engine.ManagedUpdatesCheckFailures);
        Assert.Equal(StatusReasonCode.ScriptError, failure.ReasonCode);
        Assert.Contains("timed out", failure.Reason);
    }

    [Fact]
    public void ManagedUpdateOnly_InstallcheckScriptTimesOut_IsReportedWithTheCheckError()
    {
        var engine = EngineWithInstallcheckTimeout(TimeSpan.FromMilliseconds(500));
        var item = ScriptTimeoutItem(Unique("scripterr"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };
        var catalog = Catalog(item);

        var (toInstall, toUpdate, toUninstall, _) = engine.IdentifyActions(manifest, catalog);
        var items = engine.BuildSessionItems(manifest, toInstall, toUpdate, toUninstall, catalog, NoOutcomes, NoSuppressions);
        var info = engine.BuildInstallInfo(manifest, toInstall, toUpdate, toUninstall, catalog);

        var reported = Assert.Single(items, i => i.Name == item.Name);
        Assert.Equal("Warning", reported.Status);
        Assert.Equal(StatusReasonCode.ScriptError, reported.StatusReasonCode);
        Assert.Contains("timed out", reported.WarningMessage);

        Assert.Contains(item.Name, info.ManagedUpdates);
        Assert.Contains(item.Name, info.ProcessedInstalls);
        Assert.DoesNotContain(info.ManagedInstalls, i => i.Name == item.Name);
    }

    [Fact]
    public void ManagedUpdateOnly_PresentButStatusCheckThrows_QueuesNothing_AndKeepsTheCheckReasonCode()
    {
        // The file is there, so the item is present, but it is locked, so hashing it
        // throws: the status check is an error that asks for action. Before this the
        // item fell through to a fresh install.
        var item = OutdatedItem(Unique("locked"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "update") };

        List<CatalogItem> toInstall, toUpdate;
        using (new FileStream(item.Installs[0].Path!, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (toInstall, toUpdate, _, _) = _engine.IdentifyActions(manifest, Catalog(item));
        }

        Assert.Empty(toInstall);
        Assert.Empty(toUpdate);
        Assert.Empty(_engine.ManagedUpdatesSkippedAbsent);
        var failure = Assert.Contains(item.Name, _engine.ManagedUpdatesCheckFailures);
        Assert.Equal(StatusReasonCode.CheckFailed, failure.ReasonCode);
    }

    [Fact]
    public void ManagedInstall_PresentButStatusCheckThrows_StillInstalls()
    {
        var item = OutdatedItem(Unique("locked"));
        var manifest = new List<ManifestItem> { Entry(item.Name, "install") };

        List<CatalogItem> toInstall;
        using (new FileStream(item.Installs[0].Path!, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (toInstall, _, _, _) = _engine.IdentifyActions(manifest, Catalog(item));
        }

        Assert.Single(toInstall, item);
        Assert.Empty(_engine.ManagedUpdatesCheckFailures);
    }
}

/// <summary>
/// The warning line for a managed_updates item whose status check failed. Shares a
/// collection with the other tests that swap Console.Out, so they never run at once.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public class ManagedUpdatesCheckFailureWarningTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly StringWriter _stdout = new();
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "ManagedUpdatesWarn", Guid.NewGuid().ToString("N"));

    public ManagedUpdatesCheckFailureWarningTests()
    {
        Directory.CreateDirectory(_testDir);
        Console.SetOut(_stdout);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        try { Directory.Delete(_testDir, recursive: true); } catch { /* Ignore cleanup errors */ }
    }

    [Fact]
    public void ManagedUpdateOnly_InstallcheckScriptTimesOut_LogsAWarningWithTheCheckReason()
    {
        var config = new CimianConfig { CachePath = Path.Combine(_testDir, "Cache") };
        Directory.CreateDirectory(config.CachePath);
        var engine = new UpdateEngine(config, new StatusService(TimeSpan.FromMilliseconds(500), _ => false));
        var item = new CatalogItem
        {
            Name = "scripterr" + Guid.NewGuid().ToString("N"),
            Version = "2.0.0",
            InstallcheckScript = "Start-Sleep -Seconds 30"
        };
        var manifest = new List<ManifestItem> { new() { Name = item.Name, Action = "update", SourceManifest = "test" } };

        engine.IdentifyActions(manifest, new Dictionary<string, CatalogItem> { [item.Name.ToLowerInvariant()] = item });

        var output = _stdout.ToString();
        Assert.Contains($"Leaving {item.Name} alone", output);
        Assert.Contains("timed out", output);
    }
}
