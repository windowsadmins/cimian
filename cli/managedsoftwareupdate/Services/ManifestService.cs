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
        var processedManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingConditionals = new List<(List<ConditionalItem> Items, string SourceManifest)>();

        // PASS 1: Resolve and process the primary manifest, walking a 404 fallback
        // chain (configured identifier -> hostname -> serial -> Orphaned ->
        // site_default), collecting catalogs and deferring conditional items.
        await ResolvePrimaryManifestAsync(items, processedManifests, pendingConditionals);

        // Log collected catalogs before processing conditionals
        ConsoleLogger.Info($"    Collected catalogs for conditional evaluation: [{string.Join(", ", _config.Catalogs)}]");

        // PASS 2: Now process all conditional items with full catalog context
        foreach (var (conditionalItems, sourceManifest) in pendingConditionals)
        {
            ConsoleLogger.Info($"    Processing {conditionalItems.Count} conditional items from {sourceManifest}");
            var conditionalResults = ProcessConditionalItems(conditionalItems, sourceManifest);
            items.AddRange(conditionalResults);
        }

        // PASS 3: Merge user-driven self-service requests (Munki parity: pkg/selfservice).
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
        var processedManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingConditionals = new List<(List<ConditionalItem> Items, string SourceManifest)>();

        await ProcessManifestAsync(manifestName, items, processedManifests, pendingConditionals);
        
        // Process deferred conditional items
        foreach (var (conditionalItems, sourceManifest) in pendingConditionals)
        {
            var conditionalResults = ProcessConditionalItems(conditionalItems, sourceManifest);
            items.AddRange(conditionalResults);
        }

        return items;
    }

    /// <summary>
    /// Loads a local-only manifest from a file path
    /// </summary>
    public List<ManifestItem> LoadLocalOnlyManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Local manifest file not found: {manifestPath}");
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
        HashSet<string> processedManifests,
        List<(List<ConditionalItem> Items, string SourceManifest)> pendingConditionals)
    {
        // Candidate kind drives log severity: a 404 on an explicitly configured
        // identifier is noteworthy (warn); a 404 on an opportunistic probe or a
        // catch-all is routine (detail).
        const string Configured = "configured";
        const string Probe = "probe";
        const string CatchAll = "catch-all";

        var candidates = new List<(string Name, string Kind)>();
        void Add(string? name, string kind)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var trimmed = name.Trim();
            if (candidates.Any(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                return;
            candidates.Add((trimmed, kind));
        }

        // Configured identity: certificate CN takes precedence over the explicit
        // ClientIdentifier, preserving the prior single-identifier selection.
        Add(CimianHttpClientFactory.GetClientCertificateCN(_config), Configured);
        Add(_config.ClientIdentifier, Configured);
        // Opportunistic probes.
        Add(Environment.MachineName, Probe);
        Add(GetSerialNumber(), Probe);
        // Catch-all manifests of last resort.
        Add("Orphaned", CatchAll);
        Add("site_default", CatchAll);

        for (var i = 0; i < candidates.Count; i++)
        {
            var (name, kind) = candidates[i];
            var result = await ProcessManifestAsync(name, items, processedManifests, pendingConditionals);

            if (result == ManifestFetchResult.Ok)
            {
                if (i > 0)
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
            if (kind == Configured)
                ConsoleLogger.Warn($"    Configured manifest '{name}' returned 404; trying next fallback candidate.");
            else
                ConsoleLogger.Detail($"    Manifest '{name}' ({kind}) returned 404; trying next fallback candidate.");
        }

        ConsoleLogger.Warn($"    No primary manifest could be resolved from candidates: [{string.Join(", ", candidates.Select(c => c.Name))}]. Device will have no managed items this run.");
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
        HashSet<string> processedManifests,
        List<(List<ConditionalItem> Items, string SourceManifest)> pendingConditionals)
    {
        // Avoid infinite loops from circular includes
        if (processedManifests.Contains(manifestName))
        {
            ConsoleLogger.Debug($"Skipping already-processed manifest: {manifestName}");
            return ManifestFetchResult.Ok;
        }
        processedManifests.Add(manifestName);
        ConsoleLogger.Debug($"Processing manifest originalName: {manifestName} processedName: {manifestName}.yaml");

        // Try to download the manifest
        var manifestUrl = $"{_config.SoftwareRepoURL.TrimEnd('/')}/manifests/{manifestName}.yaml";
        var localPath = Path.Combine(_config.ManifestsPath, $"{manifestName}.yaml");
        ConsoleLogger.Debug($"Downloading manifest url: {manifestUrl} localPath: {localPath}");

        try
        {
            ConsoleLogger.Debug($"Starting download url: {manifestUrl} destination: {localPath}");
            var response = await _httpClient.GetAsync(manifestUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
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
                            // Clean up the include path - normalize slashes and remove .yaml extension
                            var includeName = include.Replace(".yaml", "").Replace("\\", "/");
                            ConsoleLogger.Debug($"Processing included manifest: {includeName}");
                            
                            // Include paths are relative or absolute manifest references
                            // They should be passed as-is to ProcessManifestAsync
                            await ProcessManifestAsync(includeName, items, processedManifests, pendingConditionals);
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

                return ManifestFetchResult.Ok;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // 404 is the only status that advances the primary-manifest fallback chain.
                ConsoleLogger.Debug($"Manifest not found (404): {manifestName}");
                return ManifestFetchResult.NotFound;
            }

            // Non-404 (auth, 5xx, etc.): surface rather than treating it as missing.
            ConsoleLogger.Warn($"Failed to download manifest {manifestName}: {response.StatusCode}");
            return ManifestFetchResult.Error;
        }
        catch (Exception ex)
        {
            // Network/transport failure: surface, do not mask by falling back.
            ConsoleLogger.Warn($"Error processing manifest {manifestName}: {ex.Message}");
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
            ConsoleLogger.Info($"    SystemFacts: machine_model='{_systemFacts.MachineModel}' machine_type='{_systemFacts.MachineType}' gpu_names=[{string.Join(", ", _systemFacts.GpuNames)}] arch='{_systemFacts.Architecture}'");
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
        if (!Directory.Exists(ConditionsDir)) return;

        var scripts = Directory.GetFiles(ConditionsDir)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ps1" or ".bat" or ".cmd" or ".exe")
            .OrderBy(f => f);

        foreach (var script in scripts)
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
    /// Merges the user-writable SelfServeManifest into the manifest item list.
    /// `managed_installs` entries promote a matching optional item to action=install, or add a
    /// new install item if no server manifest references it. `managed_uninstalls` entries
    /// flip the action to uninstall (or add a new uninstall item).
    ///
    /// Honors Config.SkipSelfService so admins can disable self-service end-to-end. The user
    /// entries persist in SelfServeManifest.yaml until the user cancels the request; once the
    /// software is installed, normal status checks suppress further action.
    /// </summary>
    private async Task MergeSelfServeManifestAsync(List<ManifestItem> items)
    {
        if (_config.SkipSelfService)
        {
            ConsoleLogger.Debug("SelfServe merge skipped (SkipSelfService=true)");
            return;
        }

        SelfServiceManifest selfServe;
        try
        {
            var svc = new SelfServiceManifestService();
            selfServe = await svc.LoadAsync();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to load SelfServeManifest: {ex.Message}");
            return;
        }

        if (selfServe.ManagedInstalls.Count == 0 && selfServe.ManagedUninstalls.Count == 0)
        {
            return;
        }

        ConsoleLogger.Info($"    Merging SelfServeManifest: {selfServe.ManagedInstalls.Count} install request(s), {selfServe.ManagedUninstalls.Count} uninstall request(s)");

        const string selfServeSource = "SelfServeManifest";

        // Actions that mandate the item's presence/absence. A self-serve request
        // can never override these. "optional" and "update" are deliberately NOT
        // here: optional_installs offers the item, and managed_updates only says
        // "patch if present" — the user remains the authority on presence for
        // the common optional_installs + managed_updates combo.
        static bool IsPresenceMandating(ManifestItem i) =>
            i.Action?.ToLowerInvariant() is "install" or "uninstall" or "default" or "profile" or "app";

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
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "install",
                    SourceManifest = selfServeSource,
                    IsSelfServe = true
                });
                SetItemSource(name, selfServeSource, "managed_installs");
                ConsoleLogger.Debug($"SelfServe: added install request item: {name}");
            }
            else if (!matches.Any(IsPresenceMandating))
            {
                var optional = matches.FirstOrDefault(i =>
                    string.Equals(i.Action, "optional", StringComparison.OrdinalIgnoreCase));
                if (optional != null)
                {
                    optional.Action = "install";
                    optional.IsSelfServe = true;
                    SetItemSource(name, selfServeSource, "managed_installs");
                    ConsoleLogger.Debug($"SelfServe: promoted optional to install item: {name} originalSource: {optional.SourceManifest}");
                }
                // Only managed_updates entries: MSC never offers such an item,
                // so an install request here is stale state — leave it alone.
            }
            // If the item is already managed_installs / uninstall by server policy,
            // leave it alone — admin policy wins.
        }

        foreach (var name in selfServe.ManagedUninstalls)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            var matches = items.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                items.Add(new ManifestItem
                {
                    Name = name,
                    Action = "uninstall",
                    SourceManifest = selfServeSource
                });
                SetItemSource(name, selfServeSource, "managed_uninstalls");
                ConsoleLogger.Debug($"SelfServe: added uninstall request item: {name}");
            }
            else if (!matches.Any(IsPresenceMandating))
            {
                // Optional item (possibly also under managed_updates) — the user
                // is the authority on presence, so honor the removal request.
                // Deduplication ranks uninstall above update, so the flipped
                // entry wins even when an update entry remains.
                var optional = matches.FirstOrDefault(i =>
                    string.Equals(i.Action, "optional", StringComparison.OrdinalIgnoreCase));
                if (optional != null)
                {
                    optional.Action = "uninstall";
                    SetItemSource(name, selfServeSource, "managed_uninstalls");
                    ConsoleLogger.Debug($"SelfServe: flipped optional to uninstall item: {name} originalSource: {optional.SourceManifest}");
                }
            }
            else if (!matches.Any(i => string.Equals(i.Action, "uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                var blocking = matches.First(IsPresenceMandating);
                // install / default / profile / app from server policy — admin wins.
                ConsoleLogger.Info($"SelfServe: ignoring uninstall request for {name}; admin policy requires {blocking.Action} (source: {blocking.SourceManifest})");
            }
        }
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

        // Add default_installs — treated like managed_installs but only on first encounter
        // (not enforced if user has previously removed the item)
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
    ///   install &gt; uninstall &gt; update &gt; default &gt; optional &gt; profile/app
    /// (profile and app share the same rank.)
    ///
    /// Within the same action, the highest version wins; otherwise the first
    /// occurrence's position is preserved.
    /// </summary>
    public List<ManifestItem> DeduplicateItems(List<ManifestItem> items)
    {
        var dedup = new Dictionary<string, ManifestItem>(StringComparer.OrdinalIgnoreCase);
        var orderedKeys = new List<string>();

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Name))
                continue;

            var key = item.Name.ToLowerInvariant();

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
            result.Add(dedup[key]);
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
        "default" => 3,
        "optional" => 2,
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
