using System.Net;
using System.Net.Http.Headers;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core;
using Cimian.Core.Services;
using Cimian.Engine.Predicates;
using Cimian.Infrastructure.System;
using SystemFacts = Cimian.Core.Models.SystemFacts;
// Use the Predicate expression types from Cimian.Engine
using ParsedExpression = Cimian.Engine.Predicates.ParsedExpression;
using LiteralExpression = Cimian.Engine.Predicates.LiteralExpression;
using ComparisonExpression = Cimian.Engine.Predicates.ComparisonExpression;
using LogicalExpression = Cimian.Engine.Predicates.LogicalExpression;
using NotExpression = Cimian.Engine.Predicates.NotExpression;
using AnyExpression = Cimian.Engine.Predicates.AnyExpression;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for retrieving and processing manifests
/// Migrated from Go pkg/manifest
/// </summary>
public class ManifestService
{
    private readonly HttpClient _httpClient;
    private readonly IDeserializer _deserializer;
    private readonly CimianConfig _config;
    private readonly Dictionary<string, string> _itemSources = new();
    private readonly PredicateEngine _predicateEngine;
    private readonly List<string> _featuredItems = new();

    /// <summary>
    /// Featured items collected across all processed manifests
    /// </summary>
    public IReadOnlyList<string> FeaturedItems => _featuredItems;
    private SystemFacts? _systemFacts;

    /// <summary>Opens the SelfServeManifest.yaml to merge; tests open a temp file instead.</summary>
    internal Func<SelfServiceManifestService> CreateSelfServeManifestService { get; init; } = () => new SelfServiceManifestService();

    /// <summary>Where users' Self Service drop files are taken in from; tests point it elsewhere.</summary>
    internal string SelfServiceDropDir { get; init; } = CimianPaths.SelfServiceDropDir;

    public ManifestService(CimianConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? CimianHttpClientFactory.CreateHttpClient(config);
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        _predicateEngine = new PredicateEngine(new Microsoft.Extensions.Logging.Abstractions.NullLogger<PredicateEngine>());
    }

    /// <summary>
    /// Retrieves all manifest items from server
    /// Uses two-pass approach: first collect catalogs, then process conditional items
    /// </summary>
    public async Task<List<ManifestItem>> GetManifestItemsAsync()
    {
        var items = new List<ManifestItem>();
        var manifestResults = new Dictionary<string, ManifestFetchResult>(StringComparer.OrdinalIgnoreCase);
        var pendingConditionals = new List<(List<ConditionalItem> Items, string SourceManifest)>();

        // PASS 1: Resolve and process the primary manifest, walking a 404 fallback
        // chain (configured identifier -> hostname -> serial -> Orphaned ->
        // site_default), collecting catalogs and deferring conditional items.
        await ResolvePrimaryManifestAsync(items, manifestResults, pendingConditionals);

        // Log collected catalogs before processing conditionals
        ConsoleLogger.Info($"    Collected catalogs for conditional evaluation: [{string.Join(", ", _config.Catalogs)}]");

        // PASS 2: Now process all conditional items with full catalog context
        foreach (var (conditionalItems, sourceManifest) in pendingConditionals)
        {
            ConsoleLogger.Info($"    Processing {conditionalItems.Count} conditional items from {sourceManifest}");
            var conditionalResults = ProcessConditionalItems(conditionalItems, sourceManifest);
            items.AddRange(conditionalResults);
        }

        // PASS 3: One-time SelfServe seed for manifest default_installs (Munki 7
        // processDefaultInstalls). Must run before the SelfServe merge so newly seeded managed_installs are
        // promoted in this same run.
        await SeedDefaultInstallsAsync(items);

        // PASS 4: Merge user-driven self-service requests (Munki parity: pkg/selfservice).
        // The GUI writes SelfServeManifest.yaml when a user clicks Install/Remove on an
        // optional item; without this merge MSU sees the item only as `optional` and
        // never queues an action.
        await MergeSelfServeManifestAsync(items);

        return items;
    }

    /// <summary>
    /// Loads a specific manifest from the server
    /// </summary>
    public async Task<List<ManifestItem>> LoadSpecificManifestAsync(string manifestName)
    {
        var items = new List<ManifestItem>();
        var manifestResults = new Dictionary<string, ManifestFetchResult>(StringComparer.OrdinalIgnoreCase);
        var pendingConditionals = new List<(List<ConditionalItem> Items, string SourceManifest)>();

        // Explicitly-requested manifest: a 404 should stay visible (quiet404: false),
        // unchanged from the pre-fallback-chain behavior.
        await ProcessManifestAsync(manifestName, items, manifestResults, pendingConditionals, quiet404: false);
        
        // Process deferred conditional items
        foreach (var (conditionalItems, sourceManifest) in pendingConditionals)
        {
            var conditionalResults = ProcessConditionalItems(conditionalItems, sourceManifest);
            items.AddRange(conditionalResults);
        }

        // Same SelfServe seed + merge as GetManifestItemsAsync so --manifest runs
        // honor default_installs and pending self-serve requests.
        await SeedDefaultInstallsAsync(items);
        await MergeSelfServeManifestAsync(items);

        return items;
    }

    /// <summary>
    /// Loads a local-only manifest from a file path
    /// </summary>
    public List<ManifestItem> LoadLocalOnlyManifest(string manifestPath) =>
        LoadLocalOnlyManifest(manifestPath,
            requireTrusted: ManagedInstallsSecurity.IsUnder(manifestPath, CimianPaths.ManagedInstallsRoot));

    /// <summary>
    /// Loads a local-only manifest. With <paramref name="requireTrusted"/>, one that anyone
    /// but SYSTEM or Administrators could have written is refused.
    /// </summary>
    internal List<ManifestItem> LoadLocalOnlyManifest(string manifestPath, bool requireTrusted)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Local manifest file not found: {manifestPath}");
        }

        if (requireTrusted && !ManagedInstallsSecurity.IsSafeToExecute(manifestPath, out var reason))
        {
            throw new UnauthorizedAccessException($"Refusing local manifest {manifestPath}: {reason}");
        }

        var yaml = File.ReadAllText(manifestPath);
        var manifest = _deserializer.Deserialize<ManifestFile>(yaml);

        return ConvertToManifestItems(manifest, Path.GetFileNameWithoutExtension(manifestPath));
    }

    /// <summary>
    /// Outcome of a single manifest fetch, used to drive the primary-manifest
    /// fallback chain. Only NotFound (HTTP 404) advances to the next candidate;
    /// Error (auth/5xx/network) aborts so a transient failure is never masked by
    /// silently degrading to a catch-all manifest.
    /// </summary>
    private enum ManifestFetchResult { Ok, NotFound, Error }

    /// <summary>
    /// Resolves the primary manifest by walking an ordered candidate chain and
    /// processing the first one the server returns:
    ///   configured identifier (cert CN &gt; ClientIdentifier &gt; hostname)
    ///   -&gt; serial number -&gt; Orphaned -&gt; site_default
    /// Only an HTTP 404 advances to the next candidate. A non-404 failure
    /// (auth, 5xx, network) aborts resolution immediately rather than degrading
    /// to a catch-all, so genuine server problems stay visible.
    /// </summary>
    private async Task ResolvePrimaryManifestAsync(
        List<ManifestItem> items,
        Dictionary<string, ManifestFetchResult> manifestResults,
        List<(List<ConditionalItem> Items, string SourceManifest)> pendingConditionals)
    {
        // Candidate kind drives log severity: a 404 on an explicitly configured
        // identifier is noteworthy (warn); a 404 on an opportunistic probe or a
        // catch-all is routine (detail).
        const string Configured = "configured";
        const string Probe = "probe";
        const string CatchAll = "catch-all";

        // Candidate names are resolved lazily so an expensive lookup (the serial
        // number, which queries WMI) only runs once the chain actually reaches it.
        // When the configured identifier resolves on the first try, or the chain
        // aborts on a non-404, the WMI query is never issued.
        var candidates = new List<(Func<string?> Resolve, string Kind)>
        {
            // Configured identity: certificate CN takes precedence over the explicit
            // ClientIdentifier, preserving the prior single-identifier selection.
            (() => CimianHttpClientFactory.GetClientCertificateCN(_config), Configured),
            (() => _config.ClientIdentifier, Configured),
            // Opportunistic probes.
            (() => Environment.MachineName, Probe),
            (GetSerialNumber, Probe),
            // Catch-all manifests of last resort.
            (() => "Orphaned", CatchAll),
            (() => "site_default", CatchAll),
        };

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var triedNames = new List<string>();
        var resolvedAny = false;

        foreach (var (resolve, kind) in candidates)
        {
            var name = resolve()?.Trim();
            // Skip blanks and de-duplicate candidates that resolve to the same name.
            if (string.IsNullOrWhiteSpace(name) || !tried.Add(name))
                continue;
            triedNames.Add(name);

            var result = await ProcessManifestAsync(name, items, manifestResults, pendingConditionals, quiet404: true);

            if (result == ManifestFetchResult.Ok)
            {
                if (resolvedAny)
                {
                    ConsoleLogger.Warn($"    Primary manifest resolved via fallback '{name}' ({kind}); " +
                        "device is running on a fallback/catch-all configuration.");
                }
                return;
            }

            if (result == ManifestFetchResult.Error)
            {
                // Non-404 failure already logged in ProcessManifestAsync. Abort the
                // chain so a transient server error is not mistaken for "no manifest".
                ConsoleLogger.Error($"    Aborting primary manifest resolution at '{name}' due to a non-404 error; not falling through to catch-all.");
                return;
            }

            // NotFound: advance to the next candidate.
            resolvedAny = true;
            if (kind == Configured)
                ConsoleLogger.Warn($"    Configured manifest '{name}' returned 404; trying next fallback candidate.");
            else
                ConsoleLogger.Detail($"    Manifest '{name}' ({kind}) returned 404; trying next fallback candidate.");
        }

        ConsoleLogger.Warn($"    No primary manifest could be resolved from candidates: [{string.Join(", ", triedNames)}]. Device will have no managed items this run.");
    }

    /// <summary>
    /// Reads the hardware serial number from the system BIOS for use as a
    /// manifest fallback identifier. Returns null if it cannot be determined.
    /// </summary>
    private static string? GetSerialNumber()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            foreach (var obj in searcher.Get())
            {
                var serial = obj["SerialNumber"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(serial))
                    return serial;
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"Serial number lookup for manifest fallback failed: {ex.Message}");
        }
        return null;
    }

    private async Task<ManifestFetchResult> ProcessManifestAsync(
        string manifestName,
        List<ManifestItem> items,
        Dictionary<string, ManifestFetchResult> manifestResults,
        List<(List<ConditionalItem> Items, string SourceManifest)> pendingConditionals,
        bool quiet404 = false)
    {
        // If we've already handled this manifest this run, return its actual prior
        // outcome rather than a blanket Ok — a manifest that previously 404'd or
        // errored must not be reported as successfully processed when referenced
        // again (e.g. as an include of a later catch-all). A tentative Ok is seeded
        // before the fetch below so circular includes still terminate.
        if (manifestResults.TryGetValue(manifestName, out var priorResult))
        {
            ConsoleLogger.Debug($"Skipping already-processed manifest: {manifestName} (prior result: {priorResult})");
            return priorResult;
        }
        // Seed a tentative Ok so a circular include resolves to Ok and recursion stops;
        // overwritten with the real result at each return path below.
        manifestResults[manifestName] = ManifestFetchResult.Ok;
        var remoteName = RepoPaths.ManifestFileName(manifestName, _config.RepositoryFormat);
        ConsoleLogger.Debug($"Processing manifest originalName: {manifestName} processedName: {remoteName}");

        // Try to download the manifest. The address follows the repository's format; the
        // cache is always YAML, so a downloaded plist is converted before it is saved.
        var manifestUrl = $"{_config.SoftwareRepoURL.TrimEnd('/')}/manifests/{remoteName}";
        var localPath = Path.Combine(_config.ManifestsPath, RepoPaths.ManifestFileName(manifestName, RepoFormat.Yaml));
        ConsoleLogger.Debug($"Downloading manifest url: {manifestUrl} localPath: {localPath}");

        try
        {
            ConsoleLogger.Debug($"Starting download url: {manifestUrl} destination: {localPath}");
            var response = await _httpClient.GetAsync(manifestUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = YamlUtils.AsYaml(await response.Content.ReadAsStringAsync(), RepoFileKind.Manifest);
                ConsoleLogger.Debug($"Download completed to temp file tempFile: {localPath}.downloading size: {content.Length}");
                
                // Save locally
                var dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(localPath, content);
                ConsoleLogger.Debug($"File saved successfully file: {localPath} size: {content.Length}");
                ConsoleLogger.Debug($"Download completed successfully file: {localPath}");
                ConsoleLogger.Debug($"Successfully downloaded manifest url: {manifestUrl}");
                ConsoleLogger.Debug($"Processed manifest: {Path.GetFileNameWithoutExtension(manifestName)}");

                var manifest = _deserializer.Deserialize<ManifestFile>(content);
                if (manifest != null)
                {
                    // Add catalogs to config FIRST (before processing anything else)
                    if (manifest.Catalogs != null && manifest.Catalogs.Count > 0)
                    {
                        ConsoleLogger.Debug($"Processing catalogs for manifest manifest: {Path.GetFileNameWithoutExtension(manifestName)} catalogs: [{string.Join(", ", manifest.Catalogs)}]");
                        foreach (var catalog in manifest.Catalogs)
                        {
                            if (!_config.Catalogs.Contains(catalog))
                            {
                                ConsoleLogger.Debug($"Added catalog to collection catalog: {catalog}");
                                _config.Catalogs.Add(catalog);
                            }
                        }
                    }
                    else
                    {
                        ConsoleLogger.Debug($"Processing catalogs for manifest manifest: {Path.GetFileNameWithoutExtension(manifestName)} catalogs: []");
                    }

                    // Process included manifests
                    if (manifest.IncludedManifests != null)
                    {
                        ConsoleLogger.Debug($"Processing included manifests from {manifestName} count: {manifest.IncludedManifests.Count}");
                        foreach (var include in manifest.IncludedManifests)
                        {
                            // Munki skips an empty include name instead of asking for the manifests folder.
                            if (string.IsNullOrWhiteSpace(include))
                                continue;

                            // Munki asks for an include by its exact name; in YAML mode one .yaml or .plist
                            // suffix is removed first. Backslashes become slashes.
                            var includeName = (_config.RepositoryFormat == RepoFormat.Plist ? include : RepoPaths.StripSuffix(include))
                                .Replace("\\", "/");
                            ConsoleLogger.Debug($"Processing included manifest: {includeName}");
                            
                            // Include paths are relative or absolute manifest references
                            // They should be passed as-is to ProcessManifestAsync. A 404 on
                            // an include stays visible (quiet404: false) — only the primary
                            // fallback chain probes quietly.
                            await ProcessManifestAsync(includeName, items, manifestResults, pendingConditionals);
                        }
                    }

                    // Collect featured_items from this manifest
                    if (manifest.FeaturedItems != null && manifest.FeaturedItems.Count > 0)
                    {
                        foreach (var fi in manifest.FeaturedItems)
                        {
                            if (!_featuredItems.Contains(fi, StringComparer.OrdinalIgnoreCase))
                                _featuredItems.Add(fi);
                        }
                        ConsoleLogger.Debug($"Collected {manifest.FeaturedItems.Count} featured items from {manifestName}");
                    }

                    // Convert to manifest items (excluding conditional items - they're deferred)
                    var manifestItems = ConvertToManifestItems(manifest, manifestName);
                    ConsoleLogger.Debug($"Processed manifest: {manifestName} itemCount: {manifestItems.Count}");
                    items.AddRange(manifestItems);
                    
                    // DEFER conditional items processing until all manifests are loaded
                    // This ensures catalogs are fully populated before conditional evaluation
                    if (manifest.ConditionalItems != null && manifest.ConditionalItems.Count > 0)
                    {
                        ConsoleLogger.Info($"    Deferring {manifest.ConditionalItems.Count} conditional items from {manifestName}");
                        pendingConditionals.Add((manifest.ConditionalItems, manifestName));
                    }
                }

                manifestResults[manifestName] = ManifestFetchResult.Ok;
                return ManifestFetchResult.Ok;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // 404 is the only status that advances the primary-manifest fallback chain.
                // Quiet (Debug) for primary-candidate probes; visible (Warn) for includes
                // and explicit LoadSpecificManifestAsync, matching pre-fallback behavior.
                if (quiet404)
                    ConsoleLogger.Debug($"Manifest not found (404): {manifestName}");
                else
                    ConsoleLogger.Warn($"Manifest not found (404): {manifestName}");
                manifestResults[manifestName] = ManifestFetchResult.NotFound;
                return ManifestFetchResult.NotFound;
            }

            // Non-404 (auth, 5xx, etc.): surface rather than treating it as missing.
            ConsoleLogger.Warn($"Failed to download manifest {manifestName}: {response.StatusCode}");
            manifestResults[manifestName] = ManifestFetchResult.Error;
            return ManifestFetchResult.Error;
        }
        catch (Exception ex)
        {
            // Network/transport failure: surface, do not mask by falling back.
            ConsoleLogger.Warn($"Error processing manifest {manifestName}: {ex.Message}");
            manifestResults[manifestName] = ManifestFetchResult.Error;
            return ManifestFetchResult.Error;
        }
    }

    /// <summary>
    /// Processes conditional items by evaluating conditions against system facts
    /// </summary>
    private List<ManifestItem> ProcessConditionalItems(List<ConditionalItem> conditionalItems, string sourceManifest)
    {
        var items = new List<ManifestItem>();
        
        foreach (var conditional in conditionalItems)
        {
            if (string.IsNullOrWhiteSpace(conditional.Condition))
            {
                continue;
            }
            
            // Evaluate the condition
            if (EvaluateCondition(conditional.Condition))
            {
                ConsoleLogger.Info($"    Conditional item matched: {conditional.Condition}");
                
                // Add managed_installs from this conditional
                if (conditional.ManagedInstalls != null)
                {
                    foreach (var name in conditional.ManagedInstalls)
                    {
                        items.Add(new ManifestItem
                        {
                            Name = name,
                            Action = "install",
                            SourceManifest = sourceManifest
                        });
                        SetItemSource(name, sourceManifest, "conditional_managed_installs");
                    }
                }
                
                // Add managed_uninstalls from this conditional
                if (conditional.ManagedUninstalls != null)
                {
                    foreach (var name in conditional.ManagedUninstalls)
                    {
                        items.Add(new ManifestItem
                        {
                            Name = name,
                            Action = "uninstall",
                            SourceManifest = sourceManifest
                        });
                        SetItemSource(name, sourceManifest, "conditional_managed_uninstalls");
                    }
                }
                
                // Add managed_updates from this conditional
                if (conditional.ManagedUpdates != null)
                {
                    foreach (var name in conditional.ManagedUpdates)
                    {
                        items.Add(new ManifestItem
                        {
                            Name = name,
                            Action = "update",
                            SourceManifest = sourceManifest
                        });
                        SetItemSource(name, sourceManifest, "conditional_managed_updates");
                    }
                }
                
                // Add optional_installs from this conditional
                if (conditional.OptionalInstalls != null)
                {
                    foreach (var name in conditional.OptionalInstalls)
                    {
                        items.Add(new ManifestItem
                        {
                            Name = name,
                            Action = "optional",
                            SourceManifest = sourceManifest
                        });
                        SetItemSource(name, sourceManifest, "conditional_optional_installs");
                    }
                }
            }
            else
            {
                ConsoleLogger.Info($"    Conditional item did not match: {conditional.Condition}");
            }
        }
        
        return items;
    }

    /// <summary>
    /// Ensures SystemFacts are populated for predicate evaluation
    /// </summary>
    private static readonly string ConditionsDir = CimianPaths.ConditionsDir;

    private void EnsureSystemFacts()
    {
        if (_systemFacts != null) return;

        // Use the full SystemFactsCollector (WMI-backed) so conditionals referencing
        // machine_model, gpu_names, cpu_*, ram_*, storage_*, npu_* evaluate against
        // real hardware values. Falls back to a minimal stub if collection fails so
        // the run still proceeds (conditionals just evaluate to false, same as before).
        try
        {
            var collector = new SystemFactsCollector(new ConsoleForwardingLogger<SystemFactsCollector>());
            collector.SetCatalogs(_config.Catalogs);
            _systemFacts = collector.CollectAsync().GetAwaiter().GetResult();
            ConsoleLogger.Info($"    SystemFacts: machine_model='{_systemFacts.MachineModel}' machine_type='{_systemFacts.MachineType}' gpu_names=[{string.Join(", ", _systemFacts.GpuNames)}] gpu_pci_ids=[{string.Join(", ", _systemFacts.GpuPciIds)}] gpu_vendors=[{string.Join(", ", _systemFacts.GpuVendors)}] gpu_driver_missing={_systemFacts.GpuDriverMissing} arch='{_systemFacts.Architecture}'");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"SystemFactsCollector failed ({ex.GetType().Name}) - falling back to minimal facts. Exception: {ex}");
            var osVersion = Environment.OSVersion.Version;
            // Preserve the pre-fix stub defaults on the unhappy path so predicates
            // like `machine_type == "desktop"` keep behaving the way they did before
            // this change, instead of silently flipping false when collection fails.
            _systemFacts = new SystemFacts
            {
                Hostname = Environment.MachineName,
                Architecture = GetSystemArchitecture(),
                OperatingSystem = "Windows",
                OperatingSystemVersion = osVersion.ToString(),
                OSVersMajor = osVersion.Major,
                OSVersMinor = osVersion.Minor,
                OSBuildNumber = osVersion.Build,
                Catalogs = _config.Catalogs,
                MachineType = "desktop",
                MachineModel = "Unknown",
                CollectedAt = DateTime.UtcNow
            };
        }

        // Load admin-provided custom conditions (Munki parity)
        LoadCustomConditions();
    }

    /// <summary>
    /// Scans the conditions folder for scripts and merges their key=value output into system facts.
    /// Scripts can be .ps1, .bat, .cmd, or .exe. Each line of stdout is parsed as key=value.
    /// </summary>
    private void LoadCustomConditions()
    {
        foreach (var script in ConditionScripts(ConditionsDir))
        {
            try
            {
                var ext = Path.GetExtension(script).ToLowerInvariant();
                string fileName, arguments;

                if (ext == ".ps1")
                {
                    fileName = "powershell.exe";
                    arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"";
                }
                else if (ext is ".bat" or ".cmd")
                {
                    fileName = "cmd.exe";
                    arguments = $"/c \"{script}\"";
                }
                else
                {
                    fileName = script;
                    arguments = "";
                }

                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                });
                if (process == null) continue;

                var outputTask = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(30_000))
                {
                    try { process.Kill(true); } catch { }
                    ConsoleLogger.Detail($"    Condition script {Path.GetFileName(script)} timed out");
                    continue;
                }

                var output = outputTask.Result;
                if (process.ExitCode != 0) continue;

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eqIndex = line.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var key = line[..eqIndex].Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(key)) continue;
                        var value = line[(eqIndex + 1)..].Trim();
                        _systemFacts!.CustomFacts[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleLogger.Detail($"    Condition script {Path.GetFileName(script)} failed: {ex.Message}");
            }
        }

        if (_systemFacts!.CustomFacts.Count > 0)
            ConsoleLogger.Info($"    Loaded {_systemFacts.CustomFacts.Count} custom condition(s)");
    }
    
    /// <summary>
    /// The condition scripts in <paramref name="dir"/>, in the order they run. A script that
    /// anyone but SYSTEM or Administrators could have written is skipped: these run as SYSTEM.
    /// </summary>
    internal static List<string> ConditionScripts(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();

        var scripts = new List<string>();
        foreach (var script in Directory.GetFiles(dir)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ps1" or ".bat" or ".cmd" or ".exe")
            .OrderBy(f => f))
        {
            // One someone is holding open would silently drop its facts and change what
            // conditional items evaluate to, so it stops the run instead.
            if (!ManagedInstallsSecurity.TryProbeRead(script, out var unreadable))
            {
                throw new InvalidOperationException($"Condition script {unreadable}");
            }

            if (ManagedInstallsSecurity.IsSafeToExecute(script, out var reason))
            {
                scripts.Add(script);
            }
            else
            {
                ConsoleLogger.Warn($"Skipping condition script {script}: {reason}");
            }
        }
        return scripts;
    }

    /// <summary>
    /// Evaluates a condition using the PredicateEngine for proper NSPredicate-style parsing
    /// Supports complex expressions with OR/AND/NOT operators, parentheses, and nested conditions
    /// </summary>
    private bool EvaluateCondition(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true; // No condition means always true
        }

        EnsureSystemFacts();
        
        try
        {
            // Use the sophisticated PredicateEngine for proper parsing and evaluation
            var result = _predicateEngine.EvaluateConditionAsync(condition, _systemFacts!).GetAwaiter().GetResult();
            
            if (result)
            {
                ConsoleLogger.Debug($"    Condition matched: {condition}");
            }
            else
            {
                ConsoleLogger.Debug($"    Condition did not match: {condition}");
            }
            
            return result;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Error evaluating condition '{condition}': {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Gets the system architecture in a format matching Go behavior
    /// </summary>
    private static string GetSystemArchitecture()
    {
        // Check PROCESSOR_IDENTIFIER first to detect native ARM64 hardware
        var procId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        if (procId.ToUpperInvariant().Contains("ARM"))
        {
            return "arm64";
        }
        
        // Check for WoW64 native architecture
        var nativeArch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? "";
        if (!string.IsNullOrEmpty(nativeArch))
        {
            return nativeArch.ToUpperInvariant() switch
            {
                "AMD64" or "X86_64" => "x64",
                "ARM64" => "arm64",
                _ => nativeArch.ToLowerInvariant()
            };
        }
        
        // Fall back to PROCESSOR_ARCHITECTURE
        var arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
        return arch.ToUpperInvariant() switch
        {
            "AMD64" or "X86_64" => "x64",
            "X86" or "386" => "x86",
            "ARM64" => "arm64",
            _ => Environment.Is64BitOperatingSystem ? "x64" : "x86"
        };
    }

    /// <summary>
    /// One-time SelfServe seed for manifest <c>default_installs</c> (Munki 7 parity:
    /// <c>processDefaultInstalls</c> in selfservice.swift). Names not yet recorded under SelfServe
    /// <c>default_installs</c> are appended there and to <c>managed_installs</c>.
    /// Subsequent runs see the SelfServe record and do not re-seed, so a user who
    /// removes the item in MSC keeps it removed.
    /// </summary>
    internal async Task SeedDefaultInstallsAsync(List<ManifestItem> items, ISelfServiceManifestService? selfServeService = null)
    {
        if (!items.Any(i => string.Equals(i.Action, "default", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ISelfServiceManifestService svc;
        SelfServiceManifest selfServe;
        try
        {
            svc = selfServeService ?? new SelfServiceManifestService();
            selfServe = await svc.LoadAsync();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to load SelfServeManifest for default_installs seed: {ex.Message}");
            return;
        }

        if (!SeedDefaultInstallsInto(items, selfServe, out var seeded))
        {
            return;
        }

        try
        {
            await svc.SaveAsync(selfServe);
            ConsoleLogger.Info($"    Seeded {seeded.Count} default_installs into SelfServeManifest: [{string.Join(", ", seeded)}]");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to save SelfServeManifest after default_installs seed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure seed logic matching Munki 7 <c>processDefaultInstalls</c>. Mutates
    /// <paramref name="selfServe"/> in place. Returns true when the SelfServe
    /// manifest changed and should be persisted.
    /// </summary>
    internal static bool SeedDefaultInstallsInto(
        List<ManifestItem> items,
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
            if (!string.Equals(item.Action, "default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = item.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Munki: if item not in SelfServe default_installs, append it and
            // also append to managed_installs when missing. Does not consult
            // optional_installs and does not touch managed_uninstalls.
            if (alreadyOffered.Contains(name))
            {
                continue;
            }

            selfServe.DefaultInstalls.Add(name);
            alreadyOffered.Add(name);

            if (!selfServe.ManagedInstalls.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                selfServe.ManagedInstalls.Add(name);
            }

            seededNames.Add(name);
            changed = true;
            ConsoleLogger.Debug($"SelfServe: seeded default_installs item: {name}");
        }

        return changed;
    }
    
    /// <summary>
    /// Takes in users' Self Service drop files, then merges the protected SelfServeManifest
    /// (written only by SYSTEM and Administrators) into the manifest item list.
    /// `managed_installs` entries promote a matching optional item to action=install; a name
    /// that no manifest offers as an optional install is ignored. `managed_uninstalls` entries
    /// flip an offered optional or default install to uninstall; any other removal is refused.
    ///
    /// Runs on every check, as Munki's processSelfServeManifest does. A user's choices
    /// persist in SelfServeManifest.yaml until the user cancels them through a new drop
    /// file; once the software is installed, normal status checks suppress further action.
    /// </summary>
    internal async Task MergeSelfServeManifestAsync(List<ManifestItem> items)
    {
        SelfServiceManifest selfServe;
        try
        {
            var svc = CreateSelfServeManifestService();
            selfServe = await svc.LoadAsync();

            // Munki 7 copy model: take in each user's drop file, then delete it. Only the
            // protected copy is ever read for decisions. Skipped when no manifest resolved,
            // so a failed fetch never discards users' choices as "not offered".
            if (items.Count > 0 && Directory.Exists(SelfServiceDropDir))
            {
                var offered = new HashSet<string>(
                    items.Where(i => string.Equals(i.Action, "optional", StringComparison.OrdinalIgnoreCase)).Select(i => i.Name),
                    StringComparer.OrdinalIgnoreCase);
                var firstSeen = SelfServiceSeenStore.Load();
                if (SelfServiceRequests.Ingest(SelfServiceDropDir, selfServe, offered, firstSeen,
                        DateTime.UtcNow, message => ConsoleLogger.Info($"    SelfServe: {message}")))
                {
                    await svc.SaveAsync(selfServe);
                }
                SelfServiceSeenStore.Save(firstSeen);
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to load SelfServeManifest: {ex.Message}");
            return;
        }

        MergeSelfServeInto(items, selfServe);
    }

    /// <summary>
    /// Applies a loaded SelfServeManifest's install and removal requests to the
    /// manifest item list. Split from <see cref="MergeSelfServeManifestAsync"/> so the
    /// merge can be exercised without the on-disk manifest.
    /// </summary>
    internal void MergeSelfServeInto(List<ManifestItem> items, SelfServiceManifest selfServe)
    {
        _refusedSelfServeRemovals.Clear();
        if (selfServe.ManagedInstalls.Count == 0 && selfServe.ManagedUninstalls.Count == 0)
        {
            return;
        }

        ConsoleLogger.Info($"    Merging SelfServeManifest: {selfServe.ManagedInstalls.Count} install request(s), {selfServe.ManagedUninstalls.Count} uninstall request(s)");

        const string selfServeSource = "SelfServeManifest";

        // Actions that mandate the item's presence/absence. A self-serve request
        // can never override these. "optional", "update", and "default" are
        // deliberately NOT here: optional_installs offers the item, managed_updates
        // only says "patch if present", and default_installs is a one-time SelfServe
        // seed — the user remains the authority on presence afterward.
        static bool IsPresenceMandating(ManifestItem i) =>
            i.Action?.ToLowerInvariant() is "install" or "uninstall" or "profile" or "app";

        foreach (var name in selfServe.ManagedInstalls)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Match against every entry for the name, not just the first: the same
            // item can be optional in one manifest and managed in another, and a
            // self-serve promotion must not outrank (or masquerade as) admin intent
            // when the lists are later deduplicated.
            var matches = items.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                // Only items this device is offered as optional_installs can be
                // requested (Munki 7 processSelfServeManifest). SelfServeManifest.yaml
                // is written by the user, and SYSTEM installs what it lists, so a
                // name that no manifest offers must never reach the install list.
                ConsoleLogger.Info($"SelfServe: ignoring install request for {name}; it is not an optional install for this device");
            }
            else if (!matches.Any(IsPresenceMandating))
            {
                var optional = matches.FirstOrDefault(i =>
                    string.Equals(i.Action, "optional", StringComparison.OrdinalIgnoreCase));
                if (optional != null)
                {
                    optional.Action = "install";
                    optional.IsSelfServe = true;
                    optional.PromotedFromOptional = true;
                    SetItemSource(name, selfServeSource, "managed_installs");
                    ConsoleLogger.Debug($"SelfServe: promoted optional to install item: {name} originalSource: {optional.SourceManifest}");
                }
                // Otherwise the item is not an optional install, so the request is left
                // alone. Munki 7's processSelfServeManifest likewise installs only the
                // SelfServe managed_installs that are available optional installs: an
                // item listed under default_installs alone is seeded but never installed,
                // and a pure managed_updates item is never offered in MSC.
            }
            // If the item is already managed_installs / uninstall by server policy,
            // leave it alone — admin policy wins.
        }

        // Removals follow Munki 7 processRemoval: only an item this device is offered as
        // an optional or default install, and that no admin policy requires, can be removed by a user.
        // Offering it is the admin's opt-in to user removal; the engine then checks it is in
        // the catalog, removable (not uninstallable: false) and installed.
        foreach (var name in selfServe.ManagedUninstalls)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            var matches = items.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                RefuseSelfServeRemoval(name, "it is not offered to this device");
            }
            else if (!matches.Any(IsPresenceMandating))
            {
                // Optional, default, and/or managed_updates — the user is the
                // authority on presence, so honor the removal request when we can
                // flip a list entry. Deduplication ranks uninstall above
                // update/default, so the flipped entry wins.
                var optional = matches.FirstOrDefault(i =>
                    string.Equals(i.Action, "optional", StringComparison.OrdinalIgnoreCase));
                if (optional != null)
                {
                    optional.Action = "uninstall";
                    optional.PromotedFromOptional = true;
                    optional.IsSelfServeRemoval = true;
                    SetItemSource(name, selfServeSource, "managed_uninstalls");
                    ConsoleLogger.Debug($"SelfServe: flipped optional to uninstall item: {name} originalSource: {optional.SourceManifest}");
                }
                else
                {
                    // A default_installs item is a one-time seed the user owns afterwards, so
                    // its removal is honoured too, still subject to the engine's catalog,
                    // uninstallable and installed checks.
                    var target = matches.FirstOrDefault(i =>
                        string.Equals(i.Action, "default", StringComparison.OrdinalIgnoreCase));
                    if (target != null)
                    {
                        target.Action = "uninstall";
                        target.PromotedFromOptional = false;
                        target.IsSelfServeRemoval = true;
                        SetItemSource(name, selfServeSource, "managed_uninstalls");
                        ConsoleLogger.Debug($"SelfServe: flipped default to uninstall item: {name} originalSource: {target.SourceManifest}");
                    }
                    else
                    {
                        RefuseSelfServeRemoval(name, "it is not offered as an optional install");
                    }
                }
            }
            else if (!matches.Any(i => string.Equals(i.Action, "uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                var blocking = matches.First(IsPresenceMandating);
                // install / profile / app from server policy — admin wins.
                RefuseSelfServeRemoval(name, $"admin policy requires {blocking.Action} (source: {blocking.SourceManifest})");
            }
        }
    }

    /// <summary>
    /// Self Service removals refused this run, with the reason. The engine drops them from
    /// SelfServeManifest.yaml so they are not retried every run.
    /// </summary>
    public IReadOnlyDictionary<string, string> RefusedSelfServeRemovals => _refusedSelfServeRemovals;

    private readonly Dictionary<string, string> _refusedSelfServeRemovals = new(StringComparer.OrdinalIgnoreCase);

    private void RefuseSelfServeRemoval(string name, string reason)
    {
        _refusedSelfServeRemovals[name] = reason;
        ConsoleLogger.Info($"SelfServe: refusing removal request for {name}: {reason}");
    }

    private List<ManifestItem> ConvertToManifestItems(ManifestFile manifest, string sourceManifest)
    {
        var items = new List<ManifestItem>();

        // Add managed_installs
        if (manifest.ManagedInstalls != null)
        {
            foreach (var name in manifest.ManagedInstalls)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "install",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "managed_installs");
            }
        }

        // Add managed_updates
        if (manifest.ManagedUpdates != null)
        {
            foreach (var name in manifest.ManagedUpdates)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "update",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "managed_updates");
            }
        }

        // Add managed_uninstalls
        if (manifest.ManagedUninstalls != null)
        {
            foreach (var name in manifest.ManagedUninstalls)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "uninstall",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "managed_uninstalls");
            }
        }

        // Add optional_installs
        if (manifest.OptionalInstalls != null)
        {
            foreach (var name in manifest.OptionalInstalls)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "optional",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "optional_installs");
            }
        }

        // Add managed_profiles
        if (manifest.ManagedProfiles != null)
        {
            foreach (var name in manifest.ManagedProfiles)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "profile",
                    SourceManifest = sourceManifest
                });
            }
        }

        // Add managed_apps
        if (manifest.ManagedApps != null)
        {
            foreach (var name in manifest.ManagedApps)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "app",
                    SourceManifest = sourceManifest
                });
            }
        }

        // Add default_installs — one-time SelfServe seed (see SeedDefaultInstallsAsync).
        // IdentifyActions does not force-install leftover Action=default entries;
        // presence after the first offer is owned by SelfServe / the user.
        if (manifest.DefaultInstalls != null)
        {
            foreach (var name in manifest.DefaultInstalls)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "default",
                    SourceManifest = sourceManifest
                });
                SetItemSource(name, sourceManifest, "default_installs");
            }
        }

        return items;
    }

    public void SetItemSource(string itemName, string sourceManifest, string sourceType)
    {
        var key = itemName.ToLowerInvariant();
        _itemSources[key] = $"{sourceManifest}:{sourceType}";
        ConsoleLogger.Debug($"Setting item source item: {itemName} sourceManifest: {sourceManifest} sourceType: {sourceType}");
    }

    public (string SourceManifest, string SourceType) GetItemSource(string itemName)
    {
        var key = itemName.ToLowerInvariant();
        if (_itemSources.TryGetValue(key, out var source))
        {
            var parts = source.Split(':');
            return (parts[0], parts.Length > 1 ? parts[1] : "unknown");
        }
        return ("Unknown", "unknown");
    }

    public void ClearItemSources()
    {
        _itemSources.Clear();
    }

    /// <summary>
    /// Deduplicates manifest items by name. When the same name appears with
    /// different actions across the manifest tree, the strongest action wins
    /// regardless of encounter order, so an item listed in both
    /// managed_installs and optional_installs is treated as a managed install.
    ///
    /// Action precedence (highest to lowest):
    ///   install &gt; uninstall &gt; update &gt; optional &gt; default &gt; profile/app
    /// (profile and app share the same rank.) A default_installs entry only seeds
    /// the SelfServe manifest, so it never hides the same item's optional_installs
    /// entry: the item stays offered in MSC after the user removes it.
    ///
    /// Within the same action, the highest version wins; otherwise the first
    /// occurrence's position is preserved.
    ///
    /// When update wins over an optional entry for the same name, the survivor is
    /// marked <see cref="ManifestItem.AlsoOptional"/>: managed_updates and
    /// optional_installs are independent lists, so the item stays offered in MSC.
    /// </summary>
    public List<ManifestItem> DeduplicateItems(List<ManifestItem> items)
    {
        var dedup = new Dictionary<string, ManifestItem>(StringComparer.OrdinalIgnoreCase);
        var orderedKeys = new List<string>();
        var optionalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Name))
                continue;

            var key = item.Name.ToLowerInvariant();
            if (string.Equals(item.Action, "optional", StringComparison.OrdinalIgnoreCase))
                optionalKeys.Add(key);

            if (dedup.TryGetValue(key, out var existing))
            {
                var existingRank = ActionPrecedence(existing.Action);
                var incomingRank = ActionPrecedence(item.Action);

                if (incomingRank > existingRank)
                {
                    // Stronger action supersedes (e.g. install supersedes optional)
                    dedup[key] = item;
                }
                else if (incomingRank == existingRank && IsOlderVersion(existing.Version, item.Version))
                {
                    // Same action, prefer the newer version
                    dedup[key] = item;
                }
                // Otherwise keep the existing entry
            }
            else
            {
                orderedKeys.Add(key);
                dedup[key] = item;
            }
        }

        var result = new List<ManifestItem>();
        foreach (var key in orderedKeys)
        {
            var winner = dedup[key];
            if (optionalKeys.Contains(key)
                && string.Equals(winner.Action, "update", StringComparison.OrdinalIgnoreCase))
            {
                winner.AlsoOptional = true;
            }
            result.Add(winner);
        }
        return result;
    }

    /// <summary>
    /// Ranks manifest actions by precedence so that stronger directives win
    /// when the same item name appears with conflicting actions across
    /// included manifests. Higher number = stronger directive.
    /// </summary>
    private static int ActionPrecedence(string? action) => action?.ToLowerInvariant() switch
    {
        "install" => 6,
        "uninstall" => 5,
        "update" => 4,
        "optional" => 3,
        "default" => 2,
        "profile" => 1,
        "app" => 1,
        _ => 0,
    };

    /// <summary>
    /// Compare versions to determine if v1 is older than v2.
    /// Go parity: pkg/status.IsOlderVersion
    /// </summary>
    private static bool IsOlderVersion(string? v1, string? v2)
    {
        if (string.IsNullOrEmpty(v1)) return true;
        if (string.IsNullOrEmpty(v2)) return false;
        
        // Try to parse as Version objects first
        if (Version.TryParse(v1.Replace("-", "."), out var ver1) && 
            Version.TryParse(v2.Replace("-", "."), out var ver2))
        {
            return ver1 < ver2;
        }

        // Fall back to string comparison
        return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase) < 0;
    }
}

/// <summary>
/// Forwards Microsoft.Extensions.Logging calls to ConsoleLogger so warnings and errors
/// from Cimian.Infrastructure services are visible in managedsoftwareupdate output.
/// </summary>
internal sealed class ConsoleForwardingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) =>
        logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception != null) message = $"{message}: {exception.GetType().Name}: {exception.Message}";
        switch (logLevel)
        {
            case Microsoft.Extensions.Logging.LogLevel.Error:
            case Microsoft.Extensions.Logging.LogLevel.Critical:
                ConsoleLogger.Error($"    [{typeof(T).Name}] {message}");
                break;
            default:
                ConsoleLogger.Warn($"    [{typeof(T).Name}] {message}");
                break;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
