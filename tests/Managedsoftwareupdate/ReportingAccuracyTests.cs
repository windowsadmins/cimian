using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Models;
using Cimian.Core.Services;
using CatalogItem = Cimian.CLI.managedsoftwareupdate.Models.CatalogItem;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// items.json reports what the status check found. An item the run took no action on
/// is reported as installed only when it is installed, as Munki reports from
/// installInfo, the status check's view of each item.
/// </summary>
public class ReportingAccuracyTests : IDisposable
{
    private readonly string _testDir;
    private readonly CimianConfig _config;
    private readonly UpdateEngine _engine;
    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private static readonly IReadOnlyDictionary<string, ItemOutcome> NoOutcomes =
        new Dictionary<string, ItemOutcome>();

    private static readonly IReadOnlyDictionary<string, (string Reason, string? Cause, string? InstalledVersion, bool WasUpdate, bool PendingRestart)> NoSuppressions =
        new Dictionary<string, (string Reason, string? Cause, string? InstalledVersion, bool WasUpdate, bool PendingRestart)>();

    public ReportingAccuracyTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "ReportingAccuracy", _suffix);
        Directory.CreateDirectory(_testDir);

        _config = new CimianConfig { CachePath = Path.Combine(_testDir, "Cache") };
        Directory.CreateDirectory(_config.CachePath);
        _engine = new UpdateEngine(_config, new StatusService(managedInstallsEntryLookup: _ => false));
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

    /// <summary>An item whose installs file is on disk: installed.</summary>
    private CatalogItem PresentItem(string name)
    {
        var path = Path.Combine(_testDir, name + ".bin");
        File.WriteAllText(path, "build");
        return new CatalogItem
        {
            Name = name,
            Version = "2.0.0",
            Installs = new List<InstallCheckItem> { new() { Type = "file", Path = path } }
        };
    }

    private static Dictionary<string, CatalogItem> Catalog(params CatalogItem[] items) =>
        items.ToDictionary(i => i.Name.ToLowerInvariant());

    private static ManifestItem Entry(string name, string action) =>
        new() { Name = name, Action = action, SourceManifest = "test" };

    private List<SessionPackageInfo> Report(List<ManifestItem> manifest, Dictionary<string, CatalogItem> catalog, ItemFilterService? filter = null)
    {
        var (toInstall, toUpdate, toUninstall, _) = _engine.IdentifyActions(manifest, catalog, filter);
        return _engine.BuildSessionItems(manifest, toInstall, toUpdate, toUninstall, catalog, NoOutcomes, NoSuppressions);
    }

    [Fact]
    public void OptionalInstall_NotInstalled_IsNotReportedInstalled()
    {
        var item = AbsentItem(Unique("optional"));

        var items = Report(new List<ManifestItem> { Entry(item.Name, "optional") }, Catalog(item));

        Assert.DoesNotContain(items, i => i.Name == item.Name && i.Status == "Installed");
    }

    [Fact]
    public void OptionalInstall_Installed_IsReportedInstalled()
    {
        var item = PresentItem(Unique("optional"));

        var items = Report(new List<ManifestItem> { Entry(item.Name, "optional") }, Catalog(item));

        Assert.Equal("Installed", Assert.Single(items, i => i.Name == item.Name).Status);
    }

    [Fact]
    public void DefaultInstallOnly_SeededButNotInstalled_IsNotReportedInstalled()
    {
        // default_installs only seeds Self Service. An item listed nowhere else is
        // never installed, so its entry stays a "default" marker.
        var item = AbsentItem(Unique("default"));

        var items = Report(new List<ManifestItem> { Entry(item.Name, "default") }, Catalog(item));

        Assert.DoesNotContain(items, i => i.Name == item.Name && i.Status == "Installed");
    }

    [Fact]
    public void RemovedInSelfService_OnTheFollowingRun_IsNotReportedInstalled()
    {
        // The removal run reported "Removed" and cleaned up the Self Service removal,
        // so on the next run the item is an optional install again, and still absent.
        var item = AbsentItem(Unique("removed"));

        var items = Report(new List<ManifestItem> { Entry(item.Name, "optional") }, Catalog(item));

        Assert.DoesNotContain(items, i => i.Name == item.Name && i.Status == "Installed");
    }

    [Fact]
    public void SelfServiceRequest_NotActedOn_IsReportedPending()
    {
        // An --item run for another item leaves this request unprocessed.
        var requested = AbsentItem(Unique("requested"));
        var other = PresentItem(Unique("other"));
        var manifest = new List<ManifestItem>
        {
            new() { Name = requested.Name, Action = "install", SourceManifest = "test", IsSelfServe = true, PromotedFromOptional = true },
            Entry(other.Name, "install")
        };

        var items = Report(manifest, Catalog(requested, other), new ItemFilterService(new[] { other.Name }));

        Assert.Equal("Pending Install", Assert.Single(items, i => i.Name == requested.Name).Status);
    }

    // --- version_script runs once per item per check -----------------------------------

    [Fact]
    public void ManagedUpdate_VersionScript_RunsOncePerCheck()
    {
        var counter = Path.Combine(_testDir, "runs.txt");
        var item = new CatalogItem
        {
            Name = Unique("vscript"),
            Version = "2.0.0",
            // Prints no version: not installed, so the presence check is reached too.
            VersionScript = $"Add-Content -LiteralPath '{counter}' -Value run"
        };

        _engine.IdentifyActions(new List<ManifestItem> { Entry(item.Name, "update") }, Catalog(item));

        Assert.Single(File.ReadAllLines(counter));
    }

    [Fact]
    public void SomeVersionInstalled_VersionScript_ReusesTheResultItIsGiven()
    {
        // The script would print nothing; the result handed in found a version.
        var item = new CatalogItem { Name = Unique("vreuse"), Version = "2.0.0", VersionScript = "exit 0" };
        var prior = new StatusCheckResult
        {
            Status = "pending",
            DetectionMethod = DetectionMethod.Script,
            InstalledVersion = "1.0.0",
            NeedsAction = true,
            IsUpdate = true
        };

        Assert.True(new StatusService(managedInstallsEntryLookup: _ => false).SomeVersionInstalled(item, prior).Installed);
    }

    // --- items.json is written every run ----------------------------------------------

    [Fact]
    public void ItemsReport_WithNoItems_ReplacesThePreviousRunsFile()
    {
        var itemsPath = Path.Combine(_testDir, "items.json");
        File.WriteAllText(itemsPath, """[{"item_name":"stale","current_status":"Installed"}]""");

        SessionLogger.WriteItemsReport(itemsPath, new List<SessionPackageInfo>(), "2026-10-05-1200", new DataExporter(_testDir));

        using var doc = JsonDocument.Parse(File.ReadAllText(itemsPath));
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // --- Each line is logged once -------------------------------------------------------

    [Fact]
    public void NoLineIsWrittenToTheSessionLogTwice()
    {
        // ConsoleLogger already writes every line to the attached session log, so a
        // console line followed by the same text to the session log writes it twice.
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "CimianTools.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var console = new Regex(@"ConsoleLogger\.\w+\((?<msg>.+)\);\s*$");
        var session = new Regex(@"_sessionLogger\?\.Log\(""\w+"",\s*(?<msg>.+)\);\s*$");
        var duplicates = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "cli", "managedsoftwareupdate"), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i + 1 < lines.Length; i++)
            {
                var c = console.Match(lines[i]);
                var s = session.Match(lines[i + 1]);
                if (c.Success && s.Success && c.Groups["msg"].Value == s.Groups["msg"].Value)
                    duplicates.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }

        Assert.Empty(duplicates);
    }
}

/// <summary>
/// The --checkonly tables. Shares a collection with the other tests that swap
/// Console.Out, so they never run at once.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public class CheckOnlyReportTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly StringWriter _stdout = new();
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "CheckOnlyReport", Guid.NewGuid().ToString("N"));
    private readonly UpdateEngine _engine;

    public CheckOnlyReportTests()
    {
        Directory.CreateDirectory(_testDir);
        var config = new CimianConfig { CachePath = Path.Combine(_testDir, "Cache") };
        Directory.CreateDirectory(config.CachePath);
        _engine = new UpdateEngine(config, new StatusService(managedInstallsEntryLookup: _ => false));
        Console.SetOut(_stdout);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        try { Directory.Delete(_testDir, recursive: true); } catch { /* Ignore cleanup errors */ }
    }

    private CatalogItem AbsentItem(string name) => new()
    {
        Name = name,
        Version = "2.0.0",
        Installs = new List<InstallCheckItem>
        {
            new() { Type = "file", Path = Path.Combine(_testDir, name + ".missing") }
        }
    };

    /// <summary>The status column of the table row for <paramref name="name"/>.</summary>
    private string RowStatus(string name)
    {
        // A redirected console stamps each line, so the row starts after the stamp.
        var row = _stdout.ToString().Split('\n').Single(l => l.Contains(name + " ") && l.Contains('|'));
        return row[row.IndexOf(name, StringComparison.Ordinal)..].Split('|')[2].Trim();
    }

    [Fact]
    public void ManagedUpdatesTable_SkippedItemNotInstalled_IsNotShownInstalled()
    {
        var item = AbsentItem("absent" + Guid.NewGuid().ToString("N")[..8]);
        var manifest = new List<ManifestItem> { new() { Name = item.Name, Action = "update", SourceManifest = "test" } };
        var catalog = new Dictionary<string, CatalogItem> { [item.Name.ToLowerInvariant()] = item };

        var (toInstall, toUpdate, toUninstall, _) = _engine.IdentifyActions(manifest, catalog);
        _engine.PrintCheckOnlyReport(manifest, toInstall, toUpdate, toUninstall, catalog);

        Assert.Equal("Not Installed", RowStatus(item.Name));
    }

    [Fact]
    public void CheckOnly_ListsPendingItemsInInstallOrder()
    {
        // Listed first, but it requires the second, so a run installs the second first.
        var app = AbsentItem("app" + Guid.NewGuid().ToString("N")[..8]);
        var runtime = AbsentItem("runtime" + Guid.NewGuid().ToString("N")[..8]);
        app.Requires = new List<string> { runtime.Name };
        var manifest = new List<ManifestItem>
        {
            new() { Name = app.Name, Action = "install", SourceManifest = "test" },
            new() { Name = runtime.Name, Action = "install", SourceManifest = "test" }
        };
        var catalog = new Dictionary<string, CatalogItem>
        {
            [app.Name.ToLowerInvariant()] = app,
            [runtime.Name.ToLowerInvariant()] = runtime
        };

        _engine.PrintCheckOnlyReport(manifest, new List<CatalogItem> { app, runtime }, new List<CatalogItem>(), new List<CatalogItem>(), catalog);

        var output = _stdout.ToString();
        var section = output[output.IndexOf("INSTALL ORDER", StringComparison.Ordinal)..];
        Assert.Contains($"1. {runtime.Name}", section);
        Assert.Contains($"2. {app.Name}", section);
    }
}
