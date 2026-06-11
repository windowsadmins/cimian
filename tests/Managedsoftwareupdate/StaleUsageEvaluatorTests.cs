using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Pins the stale-usage decision ladder. The ordering of the guards matters:
/// a package must fall out at the FIRST failed gate (opt-in → unattended →
/// uninstallable → tracked exes → history → data → threshold) so reports
/// attribute the skip to the real cause.
/// </summary>
public class StaleUsageEvaluatorTests
{
    private const string Exe = @"C:\Program Files\StaleApp\staleapp.exe";

    private sealed class FakeUsageSource : IUsageDataSource
    {
        public Dictionary<string, DateTime> Data { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int HistoryDays { get; set; } = 60;
        public int FreshnessDays { get; set; } = 0;

        public bool IsAvailable => true;
        public string SourceName => "fake";
        public int GetHistoryDays() => HistoryDays;
        public int GetDataFreshnessDays() => FreshnessDays;

        public bool TryGetLastUsed(string executablePath, out DateTime lastUsedUtc) =>
            Data.TryGetValue(executablePath, out lastUsedUtc);
    }

    /// <summary>Opted-in, unattended, MSI-uninstallable item tracking one exe.</summary>
    private static CatalogItem Item(int? threshold = 30, bool unattended = true) => new()
    {
        Name = "StaleApp",
        Version = "1.0",
        UnattendedUninstall = unattended,
        Uninstallable = true,
        DaysUntouchedBeforeUninstall = threshold,
        UsageTrackedPaths = new List<string> { Exe },
        Installs = new List<InstallCheckItem>
        {
            new() { Type = "msi", ProductCode = "{11111111-2222-3333-4444-555555555555}" },
        },
    };

    private static FakeUsageSource SourceWithUsage(int daysAgo)
    {
        var s = new FakeUsageSource();
        s.Data[Exe] = DateTime.UtcNow.AddDays(-daysAgo);
        return s;
    }

    // ── guard ladder ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void NotOptedIn_WhenThresholdAbsentOrNonPositive(int? threshold)
    {
        var decision = StaleUsageEvaluator.Evaluate(Item(threshold), SourceWithUsage(100), 14);
        Assert.Equal(StaleUsageOutcome.NotOptedIn, decision.Outcome);
    }

    [Fact]
    public void NotUnattended_BlocksRemoval_EvenWhenAncient()
    {
        var decision = StaleUsageEvaluator.Evaluate(Item(unattended: false), SourceWithUsage(365), 14);
        Assert.Equal(StaleUsageOutcome.NotUnattended, decision.Outcome);
    }

    [Fact]
    public void NotUninstallable_WhenNoRemovalMethodDefined()
    {
        var item = Item();
        item.Installs.Clear(); // drop the MSI product code — nothing left to run
        var decision = StaleUsageEvaluator.Evaluate(item, SourceWithUsage(365), 14);
        Assert.Equal(StaleUsageOutcome.NotUninstallable, decision.Outcome);
    }

    [Fact]
    public void NoTrackedExecutables_WhenNoPathsAndNoExeInstalls()
    {
        var item = Item();
        item.UsageTrackedPaths = null; // fall back to installs — which has only the MSI row
        var decision = StaleUsageEvaluator.Evaluate(item, SourceWithUsage(365), 14);
        Assert.Equal(StaleUsageOutcome.NoTrackedExecutables, decision.Outcome);
    }

    [Fact]
    public void InsufficientHistory_UsesGlobalDefault()
    {
        var source = SourceWithUsage(100);
        source.HistoryDays = 5; // device imaged last week
        var decision = StaleUsageEvaluator.Evaluate(Item(), source, 14);
        Assert.Equal(StaleUsageOutcome.InsufficientHistory, decision.Outcome);
    }

    [Fact]
    public void InsufficientHistory_PackageOverride_BeatsGlobal()
    {
        var source = SourceWithUsage(100);
        source.HistoryDays = 20; // passes the global 14, fails the package's 45
        var item = Item();
        item.MinimumUsageHistoryDays = 45;
        var decision = StaleUsageEvaluator.Evaluate(item, source, 14);
        Assert.Equal(StaleUsageOutcome.InsufficientHistory, decision.Outcome);
    }

    [Fact]
    public void NoUsageData_NeverRemoves()
    {
        var source = new FakeUsageSource(); // tracker never saw this exe
        var decision = StaleUsageEvaluator.Evaluate(Item(), source, 14);
        Assert.Equal(StaleUsageOutcome.NoUsageData, decision.Outcome);
    }

    // ── threshold ───────────────────────────────────────────────────────

    [Fact]
    public void RecentlyUsed_WhenWithinThreshold()
    {
        var decision = StaleUsageEvaluator.Evaluate(Item(threshold: 30), SourceWithUsage(10), 14);
        Assert.Equal(StaleUsageOutcome.RecentlyUsed, decision.Outcome);
        Assert.InRange(decision.DaysSinceLastUsed, 9, 11);
    }

    [Fact]
    public void Stale_WhenPastThreshold()
    {
        var decision = StaleUsageEvaluator.Evaluate(Item(threshold: 30), SourceWithUsage(45), 14);
        Assert.Equal(StaleUsageOutcome.Stale, decision.Outcome);
        Assert.InRange(decision.DaysSinceLastUsed, 44, 46);
        Assert.Equal(30, decision.ThresholdDays);
    }

    [Fact]
    public void NewestUsage_AcrossTrackedExes_Wins()
    {
        // Main exe untouched for 90 days, helper used 5 days ago: the package
        // is in use — one live exe vetoes removal.
        const string helper = @"C:\Program Files\StaleApp\helper.exe";
        var source = SourceWithUsage(90);
        source.Data[helper] = DateTime.UtcNow.AddDays(-5);

        var item = Item(threshold: 30);
        item.UsageTrackedPaths = new List<string> { Exe, helper };

        var decision = StaleUsageEvaluator.Evaluate(item, source, 14);
        Assert.Equal(StaleUsageOutcome.RecentlyUsed, decision.Outcome);
    }

    // ── tracked-exe resolution ──────────────────────────────────────────

    [Fact]
    public void ResolveTrackedExecutables_FallsBackToInstallsExes()
    {
        var item = Item();
        item.UsageTrackedPaths = null;
        item.Installs.Add(new InstallCheckItem { Type = "file", Path = Exe });
        item.Installs.Add(new InstallCheckItem
        {
            Type = "msi",
            ProductCode = "{66666666-7777-8888-9999-000000000000}",
            KeyPath = @"C:\Program Files\StaleApp\fromkeypath.exe",
        });
        item.Installs.Add(new InstallCheckItem { Type = "file", Path = @"C:\Program Files\StaleApp\readme.txt" });

        var exes = StaleUsageEvaluator.ResolveTrackedExecutables(item);

        Assert.Contains(Exe, exes);
        Assert.Contains(@"C:\Program Files\StaleApp\fromkeypath.exe", exes);
        Assert.DoesNotContain(@"C:\Program Files\StaleApp\readme.txt", exes);
    }

    [Fact]
    public void ResolveTrackedExecutables_ExplicitPaths_TakePrecedence()
    {
        var item = Item();
        item.Installs.Add(new InstallCheckItem { Type = "file", Path = @"C:\Other\other.exe" });

        var exes = StaleUsageEvaluator.ResolveTrackedExecutables(item);

        Assert.Equal(new[] { Exe }, exes);
    }

    // ── manifest scoping (Munki parity) ─────────────────────────────────
    // Unused-software removal acts on self-serve/optional installs and
    // orphans, never on anything an admin manifest mandates.

    [Fact]
    public void ClassifyScope_Orphan_WhenNoManifestEntry()
        => Assert.Equal(StaleUsageScope.Orphan, StaleUsageEvaluator.ClassifyScope(null));

    [Fact]
    public void ClassifyScope_Optional_WhenUnsubscribedOptionalInstall()
    {
        var entry = new ManifestItem { Name = "StaleApp", Action = "optional" };
        Assert.Equal(StaleUsageScope.Optional, StaleUsageEvaluator.ClassifyScope(entry));
    }

    [Fact]
    public void ClassifyScope_SelfServe_WhenUserInstalledViaSelfServeManifest()
    {
        var entry = new ManifestItem { Name = "StaleApp", Action = "install", IsSelfServe = true };
        Assert.Equal(StaleUsageScope.SelfServe, StaleUsageEvaluator.ClassifyScope(entry));
    }

    [Theory]
    [InlineData("install")]   // managed_installs — admin mandates presence
    [InlineData("update")]    // managed_updates — presence is user/other-channel managed
    [InlineData("default")]   // default_installs — enforced like an install
    [InlineData("uninstall")] // already being removed
    [InlineData("profile")]
    [InlineData("app")]
    public void ClassifyScope_Protected_ForAdminManagedActions(string action)
    {
        var entry = new ManifestItem { Name = "StaleApp", Action = action };
        Assert.Equal(StaleUsageScope.Protected, StaleUsageEvaluator.ClassifyScope(entry));
    }

    [Fact]
    public void ClassifyScope_Protected_WhenAdminInstall_EvenIfUserAlsoRequestedIt()
    {
        // The merge never sets IsSelfServe on an item the server already
        // manages; this pins the contract from the classifier's side too.
        var entry = new ManifestItem { Name = "StaleApp", Action = "install", IsSelfServe = false };
        Assert.Equal(StaleUsageScope.Protected, StaleUsageEvaluator.ClassifyScope(entry));
    }
}
