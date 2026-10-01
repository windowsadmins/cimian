// InstallInfoAnalyzer.cs — Munki-aligned InstallInfo builder
// Mirrors updatecheck/core.py pass order and analyze.py process_* membership rules.
// InstallInfo is the source of truth; install queues are derived from it.

using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core;
using Cimian.Core.Models;
using Cimian.Core.Services;
using CatalogItem = Cimian.CLI.managedsoftwareupdate.Models.CatalogItem;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Result of a Munki-style analysis pass: InstallInfo plus derived action queues
/// and the effective ManifestItem list for session reporting.
/// </summary>
public sealed class AnalysisResult
{
    public InstallInfoFile InstallInfo { get; init; } = new();
    public List<CatalogItem> ToInstall { get; init; } = [];
    public List<CatalogItem> ToUpdate { get; init; } = [];
    public List<CatalogItem> ToUninstall { get; init; } = [];
    public List<ManifestItem> ManifestItems { get; init; } = [];
}

/// <summary>
/// Builds <see cref="InstallInfoFile"/> using Munki's updatecheck pass model:
/// managed_installs → managed_uninstalls → managed_updates → optional_installs →
/// featured validation → default_installs seed → SelfServe (filtered to available
/// optionals, plus default_installs-seeded names) → will_be_* overlays. Uses an
/// already_processed-style ledger so managed_updates and optional_installs stay
/// orthogonal.
///
/// The one-time <c>default_installs</c> SelfServe seed (Munki 6.1
/// <c>process_default_installs</c>) lives here — same semantics as the earlier
/// ManifestService-based fix in windowsadmins/cimian#188, relocated into this
/// analyzer so seed + SelfServe run after optional_installs exist.
/// </summary>
public sealed class InstallInfoAnalyzer
{
    private readonly CimianConfig _config;
    private readonly StatusService _statusService;
    private readonly DownloadService? _downloadService;

    public InstallInfoAnalyzer(
        CimianConfig config,
        StatusService statusService,
        DownloadService? downloadService = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
        _downloadService = downloadService;
    }

    /// <summary>
    /// Analyze raw (non-SelfServe-merged) manifest items against the catalog map.
    /// <paramref name="items"/> may contain both update and optional entries for the
    /// same name — that is intentional Munki parity.
    /// </summary>
    public async Task<AnalysisResult> AnalyzeAsync(
        IReadOnlyList<ManifestItem> items,
        Dictionary<string, CatalogItem> catalogMap,
        IReadOnlyList<string>? featuredItems = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(catalogMap);

        var info = new InstallInfoFile { LastCheck = DateTime.Now };
        var toInstall = new List<CatalogItem>();
        var toUpdate = new List<CatalogItem>();
        var toUninstall = new List<CatalogItem>();
        var effectiveItems = new List<ManifestItem>();

        // --- Pass: managed_installs ---
        ConsoleLogger.Info("**Checking for installs**");
        foreach (var mi in items.Where(i => IsAction(i, "install")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessInstall(mi, catalogMap, info, toInstall, toUpdate, effectiveItems, isManagedUpdate: false, isOptionalInstall: false);
        }

        // --- Pass: managed_uninstalls ---
        ConsoleLogger.Info("**Checking for removals**");
        foreach (var mi in items.Where(i => IsAction(i, "uninstall")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessRemoval(mi, catalogMap, info, toUninstall, effectiveItems);
        }

        // --- Pass: managed_updates ---
        ConsoleLogger.Info("**Checking for managed updates**");
        foreach (var mi in items.Where(i => IsAction(i, "update")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessManagedUpdate(mi, catalogMap, info, toInstall, toUpdate, effectiveItems);
        }

        // --- Pass: optional_installs ---
        ConsoleLogger.Info("**Checking for optional installs**");
        foreach (var mi in items.Where(i => IsAction(i, "optional")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessOptionalInstall(mi, catalogMap, info, toInstall, toUpdate, effectiveItems);
        }

        // --- Featured items (must be ⊆ optional_installs) ---
        if (featuredItems != null)
        {
            var optionalNames = new HashSet<string>(
                info.OptionalInstalls.Select(o => o.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var featured in featuredItems)
            {
                if (string.IsNullOrWhiteSpace(featured)) continue;
                if (!info.FeaturedItems.Contains(featured, StringComparer.OrdinalIgnoreCase))
                    info.FeaturedItems.Add(featured);
                if (!optionalNames.Contains(featured))
                    ConsoleLogger.Warn($"{featured} is a featured item but not an optional install");
            }
        }

        // --- default_installs: one-time SelfServe seed (Munki 6.1 / #188) ---
        // Must run before SelfServe processing so newly seeded managed_installs
        // promote in this same run. Tracking list is SelfServe default_installs
        // (not merely managed_installs) so user removal sticks across runs.
        if (!_config.SkipSelfService &&
            items.Any(i => IsAction(i, "default")))
        {
            await SeedDefaultInstallsAsync(items).ConfigureAwait(false);
        }

        // --- SelfServe: only after optional list exists; filter to available
        // optionals, and also honor default_installs-seeded managed_installs
        // even when the title is not in optional_installs (Munki still seeds;
        // Cimian promotes those SelfServe installs without an optional gate).
        if (!_config.SkipSelfService)
        {
            await ProcessSelfServeAsync(catalogMap, info, toInstall, toUpdate, toUninstall, effectiveItems, cancellationToken)
                .ConfigureAwait(false);
        }

        // --- Overlay will_be_* on optional rows (Munki core.py) ---
        OverlayWillBeFlags(info);

        // Leftover Action=default markers (already seeded, or seed skipped):
        // keep for StaleUsage / session reporting. They do not queue installs.
        foreach (var mi in items.Where(i => IsAction(i, "default")))
        {
            if (!effectiveItems.Any(e => NameEquals(e.Name, mi.Name)))
                effectiveItems.Add(mi);
        }

        // Profiles / apps: keep as effective items for session reporting without InstallInfo rows yet
        foreach (var mi in items.Where(i => IsAction(i, "profile") || IsAction(i, "app")))
        {
            if (!effectiveItems.Any(e =>
                    string.Equals(e.Name, mi.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Action, mi.Action, StringComparison.OrdinalIgnoreCase)))
            {
                effectiveItems.Add(mi);
            }
        }

        return new AnalysisResult
        {
            InstallInfo = info,
            ToInstall = toInstall,
            ToUpdate = toUpdate,
            ToUninstall = toUninstall,
            ManifestItems = effectiveItems
        };
    }

    /// <summary>
    /// Same-action version dedupe only, plus true presence conflicts.
    /// update + optional for the same name both survive (Munki orthogonal lists).
    /// </summary>
    public static List<ManifestItem> DeduplicatePreservingOrthogonalLists(IReadOnlyList<ManifestItem> items)
    {
        // Group by name; resolve within each name.
        var groups = new Dictionary<string, List<ManifestItem>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Name)) continue;
            var key = item.Name.ToLowerInvariant();
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
                order.Add(key);
            }
            list.Add(item);
        }

        var result = new List<ManifestItem>();
        foreach (var key in order)
        {
            var group = groups[key];
            var hasInstall = group.Any(i => IsAction(i, "install"));
            var hasUninstall = group.Any(i => IsAction(i, "uninstall"));

            if (hasInstall)
            {
                // managed_installs blocks optional (and wins over update/uninstall)
                result.Add(PickBestVersion(group.Where(i => IsAction(i, "install"))));
                continue;
            }

            if (hasUninstall)
            {
                result.Add(PickBestVersion(group.Where(i => IsAction(i, "uninstall"))));
                continue;
            }

            // Orthogonal: keep update and optional both (version-deduped within action)
            var update = group.Where(i => IsAction(i, "update")).ToList();
            if (update.Count > 0)
                result.Add(PickBestVersion(update));

            var optional = group.Where(i => IsAction(i, "optional")).ToList();
            if (optional.Count > 0)
                result.Add(PickBestVersion(optional));

            var defaults = group.Where(i => IsAction(i, "default")).ToList();
            if (defaults.Count > 0)
                result.Add(PickBestVersion(defaults));

            foreach (var other in group.Where(i =>
                         !IsAction(i, "update") && !IsAction(i, "optional") && !IsAction(i, "default")))
            {
                result.Add(other);
            }
        }

        return result;
    }

    private static ManifestItem PickBestVersion(IEnumerable<ManifestItem> items)
    {
        ManifestItem? best = null;
        foreach (var item in items)
        {
            if (best == null || IsOlderVersion(best.Version, item.Version))
                best = item;
        }
        return best!;
    }

    private static bool IsOlderVersion(string? v1, string? v2)
    {
        if (string.IsNullOrEmpty(v1)) return true;
        if (string.IsNullOrEmpty(v2)) return false;
        if (System.Version.TryParse(v1.Replace("-", "."), out var ver1) &&
            System.Version.TryParse(v2.Replace("-", "."), out var ver2))
            return ver1 < ver2;
        return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool IsAction(ManifestItem item, string action) =>
        string.Equals(item.Action, action, StringComparison.OrdinalIgnoreCase);

    private static bool NameEquals(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Munki already_processed: true if name is already in any of the given InstallInfo sections.
    /// </summary>
    private static bool AlreadyProcessed(string name, InstallInfoFile info, params string[] sections)
    {
        foreach (var section in sections)
        {
            switch (section)
            {
                case "processed_installs":
                    if (info.ProcessedInstalls.Any(n => NameEquals(n, name))) return true;
                    break;
                case "processed_uninstalls":
                    if (info.ProcessedUninstalls.Any(n => NameEquals(n, name))) return true;
                    break;
                case "managed_updates":
                    if (info.ManagedUpdates.Any(n => NameEquals(n, name))) return true;
                    break;
                case "optional_installs":
                    if (info.OptionalInstalls.Any(o => NameEquals(o.Name, name))) return true;
                    break;
            }
        }
        return false;
    }

    private bool TryGetCatalog(string name, Dictionary<string, CatalogItem> catalogMap, out CatalogItem? cat)
    {
        if (catalogMap.TryGetValue(name.ToLowerInvariant(), out var found))
        {
            cat = found;
            return true;
        }
        cat = null;
        return false;
    }

    private void ProcessInstall(
        ManifestItem mi,
        Dictionary<string, CatalogItem> catalogMap,
        InstallInfoFile info,
        List<CatalogItem> toInstall,
        List<CatalogItem> toUpdate,
        List<ManifestItem> effectiveItems,
        bool isManagedUpdate,
        bool isOptionalInstall)
    {
        if (string.IsNullOrEmpty(mi.Name)) return;

        if (!isManagedUpdate)
        {
            if (AlreadyProcessed(mi.Name, info, "processed_installs", "processed_uninstalls"))
            {
                ConsoleLogger.Detail($"    Skipping {mi.Name}: already processed for install/uninstall");
                return;
            }
        }

        if (!TryGetCatalog(mi.Name, catalogMap, out var cat) || cat == null)
        {
            ConsoleLogger.Detail($"    Item not in catalog: {mi.Name} (action: install)");
            if (!isManagedUpdate)
                info.ProcessedInstalls.Add(mi.Name);
            effectiveItems.Add(mi);
            return;
        }

        if (!UpdateEngine.IsEligibleForOsVersion(cat, out var osReason, out _))
        {
            ConsoleLogger.Info($"Skipping {mi.Name}: {osReason}");
            if (!isManagedUpdate)
                info.ProcessedInstalls.Add(mi.Name);
            effectiveItems.Add(mi);
            return;
        }

        if (!UpdateEngine.IsEligibleForAgentVersion(cat, out var agentReason, out _))
        {
            ConsoleLogger.Info($"Skipping {mi.Name}: {agentReason}");
            if (!isManagedUpdate)
                info.ProcessedInstalls.Add(mi.Name);
            effectiveItems.Add(mi);
            return;
        }

        var action = isManagedUpdate ? "update" : "install";
        var status = _statusService.CheckStatus(cat, action, _config.CachePath);

        if (!isManagedUpdate)
            info.ProcessedInstalls.Add(mi.Name);

        effectiveItems.Add(mi);

        if (!status.NeedsAction)
            return;

        if (status.IsUpdate)
        {
            if (!toUpdate.Any(c => NameEquals(c.Name, cat.Name)))
                toUpdate.Add(cat);
        }
        else
        {
            if (!toInstall.Any(c => NameEquals(c.Name, cat.Name)))
                toInstall.Add(cat);
        }

        // Pending managed_installs record (Munki keeps these until filtered at write time)
        if (!info.ManagedInstalls.Any(m => NameEquals(m.Name, cat.Name)))
        {
            var item = BuildInstallInfoItem(cat.Name, cat);
            item.Status = status.IsUpdate ? "update-available" : "will-be-installed";
            item.WillBeInstalled = true;
            item.NeedsUpdate = status.IsUpdate;
            item.InstalledVersion = status.InstalledVersion;
            item.Installed = !string.IsNullOrEmpty(status.InstalledVersion);
            info.ManagedInstalls.Add(item);
        }

        _ = isOptionalInstall; // reserved for force_install_after_date / note filtering parity
    }

    private void ProcessManagedUpdate(
        ManifestItem mi,
        Dictionary<string, CatalogItem> catalogMap,
        InstallInfoFile info,
        List<CatalogItem> toInstall,
        List<CatalogItem> toUpdate,
        List<ManifestItem> effectiveItems)
    {
        if (string.IsNullOrEmpty(mi.Name)) return;

        // Munki: skip if already in managed_updates / processed_installs / processed_uninstalls
        // Does NOT skip for optional_installs.
        if (AlreadyProcessed(mi.Name, info, "managed_updates", "processed_installs", "processed_uninstalls"))
            return;

        if (!TryGetCatalog(mi.Name, catalogMap, out var cat) || cat == null)
        {
            ConsoleLogger.Detail($"    Item not in catalog: {mi.Name} (action: update)");
            return;
        }

        // Only update if some version is installed
        var probe = _statusService.CheckStatus(cat, "update", _config.CachePath);
        var someVersionInstalled = !string.IsNullOrEmpty(probe.InstalledVersion) || probe.IsUpdate || !probe.NeedsAction;

        // NeedsAction=false with empty version can mean "not installed" for update action —
        // prefer IsUpdate or InstalledVersion. If NeedsAction and IsUpdate, installed outdated.
        // If NeedsAction and !IsUpdate with no version, treat as not installed (Munki some_version_installed false).
        if (probe.NeedsAction && !probe.IsUpdate && string.IsNullOrEmpty(probe.InstalledVersion))
        {
            ConsoleLogger.Detail($"    {mi.Name} does not appear to be installed, so no managed updates...");
            return;
        }

        // Record managed_updates name — does NOT add to processed_installs
        if (!info.ManagedUpdates.Any(n => NameEquals(n, mi.Name)))
            info.ManagedUpdates.Add(mi.Name);

        // Process as install with is_managed_update=true (won't stamp processed_installs again)
        var updateItem = new ManifestItem
        {
            Name = mi.Name,
            Action = "update",
            SourceManifest = mi.SourceManifest,
            Version = mi.Version
        };
        ProcessInstall(updateItem, catalogMap, info, toInstall, toUpdate, effectiveItems,
            isManagedUpdate: true, isOptionalInstall: false);

        _ = someVersionInstalled;
    }

    private void ProcessOptionalInstall(
        ManifestItem mi,
        Dictionary<string, CatalogItem> catalogMap,
        InstallInfoFile info,
        List<CatalogItem> toInstall,
        List<CatalogItem> toUpdate,
        List<ManifestItem> effectiveItems)
    {
        if (string.IsNullOrEmpty(mi.Name)) return;

        // Munki: skip optional_installs / processed_installs / processed_uninstalls
        // Does NOT skip for managed_updates.
        if (AlreadyProcessed(mi.Name, info, "optional_installs", "processed_installs", "processed_uninstalls"))
            return;

        TryGetCatalog(mi.Name, catalogMap, out var cat);

        var optRecord = BuildOptionalRecord(mi.Name, cat, null);
        info.OptionalInstalls.Add(optRecord);
        effectiveItems.Add(mi);

        // force_install_after_date on optional (Cimian existing behavior)
        if (cat?.ForceInstallAfterDate != null && DateTime.Now >= cat.ForceInstallAfterDate.Value)
        {
            if (UpdateEngine.IsEligibleForOsVersion(cat, out _, out _) &&
                UpdateEngine.IsEligibleForAgentVersion(cat, out _, out _))
            {
                var status = _statusService.CheckStatus(cat, "install", _config.CachePath);
                if (status.NeedsAction)
                {
                    ConsoleLogger.Info(
                        $"    -> force_install_after_date {cat.ForceInstallAfterDate.Value:yyyy-MM-dd} has passed, forcing install of optional item {mi.Name}");
                    if (status.IsUpdate)
                    {
                        if (!toUpdate.Any(c => NameEquals(c.Name, cat.Name)))
                            toUpdate.Add(cat);
                    }
                    else if (!toInstall.Any(c => NameEquals(c.Name, cat.Name)))
                    {
                        toInstall.Add(cat);
                    }
                }
            }
        }
    }

    private void ProcessRemoval(
        ManifestItem mi,
        Dictionary<string, CatalogItem> catalogMap,
        InstallInfoFile info,
        List<CatalogItem> toUninstall,
        List<ManifestItem> effectiveItems)
    {
        if (string.IsNullOrEmpty(mi.Name)) return;

        // Munki: refuse removal if in processed_installs
        if (AlreadyProcessed(mi.Name, info, "processed_installs"))
        {
            ConsoleLogger.Warn(
                $"Will not attempt to remove {mi.Name} because some version of it is in the list of managed installs");
            return;
        }

        if (AlreadyProcessed(mi.Name, info, "processed_uninstalls"))
            return;

        info.ProcessedUninstalls.Add(mi.Name);
        effectiveItems.Add(mi);

        if (!TryGetCatalog(mi.Name, catalogMap, out var cat) || cat == null)
            return;

        var removalStatus = _statusService.CheckUninstallStatus(cat, _config.CachePath);
        if (!removalStatus.NeedsAction)
            return;

        if (!toUninstall.Any(c => NameEquals(c.Name, cat.Name)))
            toUninstall.Add(cat);

        if (!info.Removals.Any(r => NameEquals(r.Name, cat.Name)))
        {
            var item = BuildInstallInfoItem(cat.Name, cat);
            item.Status = "will-be-removed";
            item.WillBeRemoved = true;
            item.Installed = true;
            info.Removals.Add(item);
        }
    }

    /// <summary>
    /// One-time SelfServe seed for manifest <c>default_installs</c> (Munki 6.1 parity:
    /// <c>process_default_installs</c>). Names not yet recorded under SelfServe
    /// <c>default_installs</c> are appended there and to <c>managed_installs</c>.
    /// Subsequent runs see the SelfServe record and do not re-seed, so a user who
    /// removes the item in MSC keeps it removed.
    ///
    /// Semantics match windowsadmins/cimian#188; this PR relocates the seed into
    /// the InstallInfo analyzer pass order (after optional_installs).
    /// </summary>
    private async Task SeedDefaultInstallsAsync(IReadOnlyList<ManifestItem> items)
    {
        SelfServiceManifestService svc;
        SelfServiceManifest selfServe;
        try
        {
            svc = new SelfServiceManifestService();
            selfServe = await svc.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to load SelfServeManifest for default_installs seed: {ex.Message}");
            return;
        }

        if (!SeedDefaultInstallsInto(items, selfServe, out var seeded))
            return;

        try
        {
            await svc.SaveAsync(selfServe).ConfigureAwait(false);
            ConsoleLogger.Info($"    Seeded {seeded.Count} default_installs into SelfServeManifest: [{string.Join(", ", seeded)}]");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to save SelfServeManifest after default_installs seed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure seed logic matching Munki <c>process_default_installs</c>. Mutates
    /// <paramref name="selfServe"/> in place. Returns true when the SelfServe
    /// manifest changed and should be persisted.
    /// </summary>
    internal static bool SeedDefaultInstallsInto(
        IReadOnlyList<ManifestItem> items,
        SelfServiceManifest selfServe,
        out List<string> seededNames)
    {
        seededNames = new List<string>();
        selfServe.DefaultInstalls ??= [];
        selfServe.ManagedInstalls ??= [];

        var alreadyOffered = new HashSet<string>(
            selfServe.DefaultInstalls.Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);

        var changed = false;

        foreach (var item in items)
        {
            if (!IsAction(item, "default"))
                continue;

            var name = item.Name;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Munki: if item not in SelfServe default_installs, append it and
            // also append to managed_installs when missing. Does not consult
            // optional_installs and does not touch managed_uninstalls.
            if (alreadyOffered.Contains(name))
                continue;

            selfServe.DefaultInstalls.Add(name);
            alreadyOffered.Add(name);

            if (!selfServe.ManagedInstalls.Any(x => NameEquals(x, name)))
                selfServe.ManagedInstalls.Add(name);

            seededNames.Add(name);
            changed = true;
            ConsoleLogger.Debug($"SelfServe: seeded default_installs item: {name}");
        }

        return changed;
    }

    private async Task ProcessSelfServeAsync(
        Dictionary<string, CatalogItem> catalogMap,
        InstallInfoFile info,
        List<CatalogItem> toInstall,
        List<CatalogItem> toUpdate,
        List<CatalogItem> toUninstall,
        List<ManifestItem> effectiveItems,
        CancellationToken cancellationToken)
    {
        SelfServiceManifest selfServe;
        try
        {
            var svc = new SelfServiceManifestService();
            selfServe = await svc.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to load SelfServeManifest: {ex.Message}");
            return;
        }

        // Available optionals: offered, no blocking note (Munki available_optional_installs)
        var availableOptionals = new HashSet<string>(
            info.OptionalInstalls
                .Where(o => string.IsNullOrEmpty(o.Notes))
                .Select(o => o.Name),
            StringComparer.OrdinalIgnoreCase);

        // default_installs-seeded names may install via SelfServe managed_installs
        // even when not listed in optional_installs (Munki seeds without that gate;
        // #188 promoted Action=default the same way).
        var defaultSeeded = new HashSet<string>(
            (selfServe.DefaultInstalls ?? []).Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);

        ConsoleLogger.Info("**Processing self-serve choices**");

        var installRequests = selfServe.ManagedInstalls
            .Where(n => !string.IsNullOrWhiteSpace(n) &&
                        (availableOptionals.Contains(n) || defaultSeeded.Contains(n)))
            .ToList();

        foreach (var name in installRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Skip if admin already mandated install / uninstall
            if (AlreadyProcessed(name, info, "processed_installs", "processed_uninstalls"))
                continue;

            var fromOptional = availableOptionals.Contains(name);
            var mi = new ManifestItem
            {
                Name = name,
                Action = "install",
                SourceManifest = "SelfServeManifest",
                IsSelfServe = true,
                PromotedFromOptional = fromOptional
            };
            ProcessInstall(mi, catalogMap, info, toInstall, toUpdate, effectiveItems,
                isManagedUpdate: false, isOptionalInstall: fromOptional);
        }

        foreach (var name in selfServe.ManagedUninstalls.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Admin managed_installs wins; leftover default / optional / update do not
            // block SelfServe uninstall (#188 IsPresenceMandating parity).
            if (AlreadyProcessed(name, info, "processed_installs"))
            {
                ConsoleLogger.Info($"SelfServe: ignoring uninstall request for {name}; admin policy requires install");
                continue;
            }

            var mi = new ManifestItem
            {
                Name = name,
                Action = "uninstall",
                SourceManifest = "SelfServeManifest",
                PromotedFromOptional = true
            };
            ProcessRemoval(mi, catalogMap, info, toUninstall, effectiveItems);
        }
    }

    private static void OverlayWillBeFlags(InstallInfoFile info)
    {
        var pendingInstalls = new HashSet<string>(
            info.ManagedInstalls.Select(m => m.Name),
            StringComparer.OrdinalIgnoreCase);
        var pendingRemovals = new HashSet<string>(
            info.Removals.Select(r => r.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var opt in info.OptionalInstalls)
        {
            if (!opt.Installed && pendingInstalls.Contains(opt.Name))
            {
                opt.WillBeInstalled = true;
                if (string.IsNullOrEmpty(opt.Status) || opt.Status == "not-installed" || opt.Status == "update-available")
                    opt.Status = "will-be-installed";
            }
            else if (opt.Installed && pendingRemovals.Contains(opt.Name))
            {
                opt.WillBeRemoved = true;
                opt.Status = "will-be-removed";
            }
        }
    }

    private InstallInfoItem BuildOptionalRecord(string name, CatalogItem? cat, string? pendingStatus)
    {
        var optItem = BuildInstallInfoItem(name, cat);
        if (cat != null)
        {
            var status = _statusService.CheckStatus(cat, "install", _config.CachePath);
            optItem.Installed = !status.NeedsAction;
            optItem.InstalledVersion = status.InstalledVersion;
            optItem.NeedsUpdate = status.IsUpdate;
            optItem.Status = pendingStatus ?? (status.NeedsAction
                ? (status.IsUpdate ? "update-available" : "not-installed")
                : "installed");

            if (_downloadService != null && !string.IsNullOrEmpty(cat.Installer?.Location))
            {
                var cachePath = _downloadService.GetCachePath(cat);
                optItem.Precached = File.Exists(cachePath);
            }
        }
        else
        {
            optItem.Status = pendingStatus ?? "not-installed";
            optItem.Notes = "No pkginfo found in catalogs";
        }

        optItem.WillBeInstalled = pendingStatus == "will-be-installed";
        optItem.WillBeRemoved = pendingStatus == "will-be-removed";
        return optItem;
    }

    private static InstallInfoItem BuildInstallInfoItem(string name, CatalogItem? cat) => new()
    {
        Name = name,
        DisplayName = cat?.DisplayName ?? name,
        Version = cat?.Version ?? string.Empty,
        Description = cat?.Description,
        Category = cat?.Category,
        Developer = cat?.Developer,
        Icon = cat?.IconName,
        InstallerItemSize = cat?.Installer?.Size ?? 0,
        Uninstallable = cat?.IsUninstallable() ?? false,
        RestartAction = cat?.RestartAction,
        ForceInstallAfterDate = cat?.ForceInstallAfterDate,
    };
}
