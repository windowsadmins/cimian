// DataExporter.cs - Data reporting service for external monitoring tools
// Migrated from Go pkg/reporting/reporting.go

using System.Text.Json;
using System.Text.RegularExpressions;
using Cimian.Core.Models;
using Microsoft.Win32;

namespace Cimian.Core.Services;

/// <summary>
/// DataExporter provides methods to export Cimian logs for external monitoring tool consumption.
/// This is the C# equivalent of Go's DataExporter struct with all its methods.
/// </summary>
public class DataExporter
{
    private readonly string _baseDir;
    private readonly Dictionary<string, int> _manifestPackageCache = new();
    private readonly Dictionary<string, string> _currentItemErrors = new();
    private readonly Dictionary<string, string> _processedItemResults = new();
    private List<SessionPackageInfo> _currentSessionPackagesInfo = new();
    private List<string> _currentSessionPackages = new();

    private static readonly string DefaultBaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls", "Logs");

    private static readonly string ReportsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls", "reports");

    private static readonly string ManifestsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls", "manifests");

    private static readonly string CatalogsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls", "catalogs");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Creates a new DataExporter with the default base directory
    /// </summary>
    public DataExporter() : this(DefaultBaseDir) { }

    /// <summary>
    /// Creates a new DataExporter with a custom base directory
    /// </summary>
    public DataExporter(string baseDir)
    {
        _baseDir = baseDir;
    }

    #region Session Package Tracking

    /// <summary>
    /// Records an error message for a specific item (for dynamic error tracking)
    /// </summary>
    public void RecordItemError(string itemName, string errorMsg)
    {
        if (string.IsNullOrEmpty(itemName) || string.IsNullOrEmpty(errorMsg))
            return;

        _currentItemErrors[itemName] = errorMsg;

        // Update SessionPackageInfo if it exists
        for (int i = 0; i < _currentSessionPackagesInfo.Count; i++)
        {
            if (_currentSessionPackagesInfo[i].Name == itemName)
            {
                _currentSessionPackagesInfo[i].ErrorMessage = errorMsg;
                break;
            }
        }
    }

    /// <summary>
    /// Records a warning message for a specific item (for dynamic warning tracking)
    /// </summary>
    public void RecordItemWarning(string itemName, string warningMsg)
    {
        if (string.IsNullOrEmpty(itemName) || string.IsNullOrEmpty(warningMsg))
            return;

        for (int i = 0; i < _currentSessionPackagesInfo.Count; i++)
        {
            if (_currentSessionPackagesInfo[i].Name == itemName)
            {
                _currentSessionPackagesInfo[i].WarningMessage = warningMsg;
                break;
            }
        }
    }

    /// <summary>
    /// Records the result of processing an item
    /// </summary>
    public void RecordItemResult(string itemName, string status)
    {
        if (!string.IsNullOrEmpty(itemName))
        {
            _processedItemResults[itemName] = status;
        }
    }

    /// <summary>
    /// Sets the current session packages list
    /// </summary>
    public void SetCurrentSessionPackages(List<string> packages)
    {
        _currentSessionPackages = packages ?? new List<string>();
    }

    /// <summary>
    /// Sets the current session packages info list
    /// </summary>
    public void SetCurrentSessionPackagesInfo(List<SessionPackageInfo> packagesInfo)
    {
        _currentSessionPackagesInfo = packagesInfo ?? new List<SessionPackageInfo>();
    }

    /// <summary>
    /// Gets session statistics from processed items
    /// </summary>
    public SessionStats GetSessionStats()
    {
        var stats = new SessionStats
        {
            TotalCount = _processedItemResults.Count
        };

        foreach (var status in _processedItemResults.Values)
        {
            switch (status.ToLowerInvariant())
            {
                case "success":
                case "completed":
                case "installed":
                    stats.SuccessCount++;
                    break;
                case "failed":
                case "error":
                    stats.FailureCount++;
                    break;
                case "warning":
                    stats.WarningCount++;
                    break;
            }
        }

        return stats;
    }

    #endregion

    #region Table Generation

    /// <summary>
    /// Generates session records for external reporting tools
    /// </summary>
    public List<SessionRecord> GenerateSessionsTable(int limitDays = 30)
    {
        var records = new List<SessionRecord>();
        var sessions = GetRecentSessions(limitDays);
        var sessionConfig = LoadCimianConfiguration();
        var cacheSize = sessionConfig?.CachePath != null ? CalculateCacheSize(sessionConfig.CachePath) : 0;
        var totalManagedPackages = sessionConfig != null ? GetTotalManagedPackagesFromManifest(sessionConfig) : 0;

        foreach (var sessionDir in sessions)
        {
            var sessionPath = Path.Combine(_baseDir, sessionDir, "session.json");
            
            if (File.Exists(sessionPath))
            {
                try
                {
                    var json = File.ReadAllText(sessionPath);
                    var session = JsonSerializer.Deserialize<LogSession>(json, JsonOptions);
                    
                    if (session != null)
                    {
                        var record = new SessionRecord
                        {
                            SessionId = session.SessionId,
                            StartTime = session.StartTime.ToString("o"),
                            EndTime = session.EndTime?.ToString("o"),
                            RunType = session.RunType,
                            Status = session.Status,
                            Duration = session.DurationSeconds ?? 0,
                            TotalActions = session.Summary?.TotalActions ?? 0,
                            Installs = session.Summary?.Installs ?? 0,
                            Updates = session.Summary?.Updates ?? 0,
                            Removals = session.Summary?.Removals ?? 0,
                            Successes = session.Summary?.Successes ?? 0,
                            Failures = session.Summary?.Failures ?? 0,
                            PackagesHandled = session.Summary?.PackagesHandled,
                            Config = sessionConfig
                        };

                        // Extract environment info
                        if (session.Environment != null)
                        {
                            if (session.Environment.TryGetValue("hostname", out var hostname))
                                record.Hostname = hostname?.ToString() ?? "";
                            if (session.Environment.TryGetValue("user", out var user))
                                record.User = user?.ToString() ?? "";
                            if (session.Environment.TryGetValue("log_version", out var logVersion))
                                record.LogVersion = logVersion?.ToString() ?? "";
                            if (session.Environment.TryGetValue("process_id", out var pid))
                                record.ProcessId = Convert.ToInt32(pid);
                        }

                        // Create enhanced summary
                        var finalTotalManaged = totalManagedPackages > 0 ? totalManagedPackages : (record.PackagesHandled?.Count ?? 0);
                        record.Summary = new SessionSummary
                        {
                            TotalPackagesManaged = finalTotalManaged,
                            PackagesInstalled = record.Successes,
                            PackagesPending = finalTotalManaged - record.Successes - record.Failures,
                            PackagesFailed = record.Failures,
                            CacheSizeMb = cacheSize
                        };

                        if (record.Failures > 0)
                        {
                            record.Summary.FailedPackages = GetFailedPackagesForSession(sessionDir);
                        }

                        records.Add(record);
                    }
                }
                catch
                {
                    // Try to generate from events if session.json fails
                    var eventRecord = GenerateSessionFromEvents(sessionDir);
                    if (eventRecord != null)
                        records.Add(eventRecord);
                }
            }
            else
            {
                // No session.json, generate from events
                var eventRecord = GenerateSessionFromEvents(sessionDir);
                if (eventRecord != null)
                    records.Add(eventRecord);
            }
        }

        return records;
    }

    /// <summary>
    /// Generates event records for a specific session
    /// </summary>
    public List<EventRecord> GenerateEventsTable(string sessionId, int limitHours = 24)
    {
        var records = new List<EventRecord>();
        var eventsPath = Path.Combine(_baseDir, sessionId, "events.jsonl");
        
        if (!File.Exists(eventsPath))
            return records;

        var cutoffTime = DateTime.UtcNow.AddHours(-limitHours);

        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var eventData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (eventData == null)
                    continue;

                var record = new EventRecord
                {
                    SessionId = sessionId,
                    EventId = Guid.NewGuid().ToString("N")[..8]
                };

                if (eventData.TryGetValue("timestamp", out var ts))
                    record.Timestamp = ts.GetString() ?? "";
                if (eventData.TryGetValue("level", out var level))
                    record.Level = level.GetString() ?? "";
                if (eventData.TryGetValue("msg", out var msg))
                    record.Message = msg.GetString() ?? "";
                if (eventData.TryGetValue("event_type", out var eventType))
                    record.EventType = eventType.GetString() ?? "";
                if (eventData.TryGetValue("package", out var pkg))
                    record.Package = pkg.GetString();
                if (eventData.TryGetValue("version", out var ver))
                    record.Version = ver.GetString();
                if (eventData.TryGetValue("action", out var action))
                    record.Action = action.GetString() ?? "";
                if (eventData.TryGetValue("status", out var status))
                    record.Status = status.GetString() ?? "";
                if (eventData.TryGetValue("error", out var error))
                    record.Error = error.GetString();

                // Source information
                if (eventData.TryGetValue("source_file", out var srcFile))
                    record.SourceFile = srcFile.GetString() ?? "";
                if (eventData.TryGetValue("source_function", out var srcFunc))
                    record.SourceFunc = srcFunc.GetString() ?? "";
                if (eventData.TryGetValue("source_line", out var srcLine))
                    record.SourceLine = srcLine.GetInt32();

                records.Add(record);
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return records;
    }

    /// <summary>
    /// Generates item records from historical sessions
    /// </summary>
    public List<ItemRecord> GenerateItemsTable(int limitDays = 30)
    {
        var itemStats = new Dictionary<string, ComprehensiveItemStat>();

        // First populate from current manifests
        PopulateFromCurrentManifests(itemStats);

        // Then update with historical session data
        var sessions = GetRecentSessions(limitDays);
        foreach (var sessionDir in sessions)
        {
            ProcessSessionForItems(sessionDir, itemStats);
        }

        // Convert to ItemRecords
        var records = new List<ItemRecord>();
        foreach (var stats in itemStats.Values)
        {
            var record = ConvertStatsToItemRecord(stats);
            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Generates item records from current session packages info
    /// </summary>
    public List<ItemRecord> GenerateCurrentItemsFromPackagesInfo(List<SessionPackageInfo> packagesInfo)
    {
        var records = new List<ItemRecord>();
        var now = DateTime.UtcNow.ToString("o");

        foreach (var pkg in packagesInfo)
        {
            var record = new ItemRecord
            {
                Id = GeneratePackageId(pkg.Name),
                ItemName = pkg.Name,
                DisplayName = pkg.DisplayName,
                ItemType = pkg.ItemType,
                CurrentStatus = pkg.Status,
                LatestVersion = pkg.Version,
                InstalledVersion = pkg.InstalledVersion,
                LastUpdate = now,
                LastAttemptTime = now,
                LastAttemptStatus = pkg.Status,
                Type = "cimian"
            };

            if (!string.IsNullOrEmpty(pkg.ErrorMessage))
            {
                record.LastError = pkg.ErrorMessage;
                record.FailureCount = 1;
            }

            if (!string.IsNullOrEmpty(pkg.WarningMessage))
            {
                record.LastWarning = pkg.WarningMessage;
                record.WarningCount = 1;
            }

            records.Add(record);
        }

        return records;
    }

    #endregion

    #region Export Methods

    /// <summary>
    /// Exports all tables to a JSON file for external tool consumption
    /// </summary>
    public void ExportDataJson(string outputPath, int limitDays = 30)
    {
        var sessions = GenerateSessionsTable(limitDays);
        var items = GenerateItemsTable(limitDays);

        // Get events from most recent session
        var events = new List<EventRecord>();
        if (sessions.Count > 0)
        {
            events = GenerateEventsTable(sessions[0].SessionId, 24);
        }

        var data = new DataTables
        {
            CimianSessions = sessions,
            CimianEvents = events,
            CimianItems = items
        };

        var json = JsonSerializer.Serialize(data, JsonOptions);
        
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
            
        File.WriteAllText(outputPath, json);
    }

    /// <summary>
    /// Exports data to the standard reports directory
    /// </summary>
    public void ExportToReportsDirectory(int limitDays = 30)
    {
        EnsureReportsDirectoryExists();

        // Export sessions
        var sessions = GenerateSessionsTable(limitDays);
        var sessionsPath = Path.Combine(ReportsDir, "sessions.json");
        File.WriteAllText(sessionsPath, JsonSerializer.Serialize(sessions, JsonOptions));

        // Export items
        var items = GenerateItemsTable(limitDays);
        var itemsPath = Path.Combine(ReportsDir, "items.json");
        File.WriteAllText(itemsPath, JsonSerializer.Serialize(items, JsonOptions));

        // Export events from latest session
        if (sessions.Count > 0)
        {
            var events = GenerateEventsTable(sessions[0].SessionId, 24);
            var eventsPath = Path.Combine(ReportsDir, "events.json");
            File.WriteAllText(eventsPath, JsonSerializer.Serialize(events, JsonOptions));
        }
    }

    /// <summary>
    /// Exports progressive reports during a session
    /// </summary>
    public void ExportProgressiveReports(int limitDays, string phase)
    {
        EnsureReportsDirectoryExists();

        // Export current items
        var items = _currentSessionPackagesInfo.Count > 0
            ? GenerateCurrentItemsFromPackagesInfo(_currentSessionPackagesInfo)
            : GenerateItemsTable(limitDays);

        var itemsPath = Path.Combine(ReportsDir, "items.json");
        File.WriteAllText(itemsPath, JsonSerializer.Serialize(items, JsonOptions));
    }

    /// <summary>
    /// Exports an item progress update
    /// </summary>
    public void ExportItemProgressUpdate(int limitDays, string completedItem, string status, string? errorMsg = null, string? warningMsg = null)
    {
        // Record the result
        RecordItemResult(completedItem, status);

        if (!string.IsNullOrEmpty(errorMsg))
            RecordItemError(completedItem, errorMsg);
        if (!string.IsNullOrEmpty(warningMsg))
            RecordItemWarning(completedItem, warningMsg);

        // Export updated reports
        ExportProgressiveReports(limitDays, "item_update");
    }

    /// <summary>
    /// Ensures the reports directory exists
    /// </summary>
    public void EnsureReportsDirectoryExists()
    {
        if (!Directory.Exists(ReportsDir))
            Directory.CreateDirectory(ReportsDir);
    }

    /// <summary>
    /// Copies the latest run log to the reports directory
    /// </summary>
    public void CopyLatestRunLog()
    {
        EnsureReportsDirectoryExists();

        var sessions = GetRecentSessions(1);
        if (sessions.Count == 0)
            return;

        var latestSession = sessions[0];
        var eventsPath = Path.Combine(_baseDir, latestSession, "events.jsonl");
        
        if (File.Exists(eventsPath))
        {
            var destPath = Path.Combine(ReportsDir, "latest_run.jsonl");
            File.Copy(eventsPath, destPath, overwrite: true);
        }
    }

    #endregion

    #region Configuration

    /// <summary>
    /// Loads the current Cimian configuration for session enhancement
    /// </summary>
    public SessionConfig? LoadCimianConfiguration()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ManagedInstalls", "preferences.yaml");

        if (!File.Exists(configPath))
            return null;

        try
        {
            // Simple YAML parsing for key fields
            var lines = File.ReadAllLines(configPath);
            var config = new SessionConfig();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("software_repo_url:"))
                    config.SoftwareRepoUrl = ExtractYamlValue(trimmed);
                else if (trimmed.StartsWith("client_identifier:"))
                    config.ClientIdentifier = ExtractYamlValue(trimmed);
                else if (trimmed.StartsWith("cache_path:"))
                    config.CachePath = ExtractYamlValue(trimmed);
                else if (trimmed.StartsWith("default_catalog:"))
                    config.DefaultCatalog = ExtractYamlValue(trimmed);
                else if (trimmed.StartsWith("log_level:"))
                    config.LogLevel = ExtractYamlValue(trimmed);
                else if (trimmed.StartsWith("local_only_manifest:"))
                    config.Manifest = ExtractYamlValue(trimmed);
            }

            if (string.IsNullOrEmpty(config.Manifest) && !string.IsNullOrEmpty(config.ClientIdentifier))
                config.Manifest = config.ClientIdentifier;

            return config;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractYamlValue(string line)
    {
        var colonIndex = line.IndexOf(':');
        if (colonIndex < 0)
            return "";
        return line[(colonIndex + 1)..].Trim().Trim('"', '\'');
    }

    #endregion

    #region Helper Methods

    private List<string> GetRecentSessions(int limitDays)
    {
        var sessions = new List<string>();
        
        if (!Directory.Exists(_baseDir))
            return sessions;

        var cutoffDate = DateTime.UtcNow.AddDays(-limitDays);

        foreach (var dir in Directory.GetDirectories(_baseDir))
        {
            var dirName = Path.GetFileName(dir);
            if (IsValidSessionDir(dir))
            {
                // Try to parse date from directory name (format: YYYYMMDD-HHMMSS or similar)
                if (TryParseSessionDate(dirName, out var sessionDate))
                {
                    if (sessionDate >= cutoffDate)
                        sessions.Add(dirName);
                }
                else
                {
                    // Include if we can't parse the date
                    sessions.Add(dirName);
                }
            }
        }

        // Sort by name descending (most recent first)
        sessions.Sort((a, b) => string.Compare(b, a, StringComparison.Ordinal));
        return sessions;
    }

    private bool IsValidSessionDir(string sessionPath)
    {
        // A valid session directory should have either session.json or events.jsonl
        return File.Exists(Path.Combine(sessionPath, "session.json")) ||
               File.Exists(Path.Combine(sessionPath, "events.jsonl"));
    }

    private static bool TryParseSessionDate(string dirName, out DateTime date)
    {
        date = DateTime.MinValue;
        
        // Try common formats: YYYYMMDD-HHMMSS, YYYY-MM-DD-HH-MM-SS
        var formats = new[]
        {
            "yyyyMMdd-HHmmss",
            "yyyy-MM-dd-HH-mm-ss",
            "yyyyMMddHHmmss"
        };

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(dirName, format, null, System.Globalization.DateTimeStyles.None, out date))
                return true;
        }

        return false;
    }

    private SessionRecord? GenerateSessionFromEvents(string sessionDir)
    {
        var eventsPath = Path.Combine(_baseDir, sessionDir, "events.jsonl");
        if (!File.Exists(eventsPath))
            return null;

        var record = new SessionRecord
        {
            SessionId = sessionDir,
            Status = "unknown"
        };

        DateTime? startTime = null;
        DateTime? endTime = null;

        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var eventData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (eventData == null)
                    continue;

                if (eventData.TryGetValue("timestamp", out var ts))
                {
                    if (DateTime.TryParse(ts.GetString(), out var eventTime))
                    {
                        startTime ??= eventTime;
                        endTime = eventTime;
                    }
                }

                if (eventData.TryGetValue("run_type", out var runType))
                    record.RunType = runType.GetString() ?? "";

                if (eventData.TryGetValue("hostname", out var hostname))
                    record.Hostname = hostname.GetString() ?? "";

                if (eventData.TryGetValue("user", out var user))
                    record.User = user.GetString() ?? "";
            }
            catch
            {
                // Skip malformed lines
            }
        }

        if (startTime.HasValue)
        {
            record.StartTime = startTime.Value.ToString("o");
            if (endTime.HasValue)
            {
                record.EndTime = endTime.Value.ToString("o");
                record.Duration = (long)(endTime.Value - startTime.Value).TotalSeconds;
            }
        }

        return record;
    }

    private double CalculateCacheSize(string cachePath)
    {
        if (string.IsNullOrEmpty(cachePath) || !Directory.Exists(cachePath))
            return 0;

        try
        {
            long totalSize = 0;
            foreach (var file in Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    totalSize += new FileInfo(file).Length;
                }
                catch
                {
                    // Skip inaccessible files
                }
            }
            return totalSize / (1024.0 * 1024.0);
        }
        catch
        {
            return 0;
        }
    }

    private int GetTotalManagedPackagesFromManifest(SessionConfig config)
    {
        if (string.IsNullOrEmpty(config.Manifest))
            return 0;

        if (_manifestPackageCache.TryGetValue(config.Manifest, out var cachedCount))
            return cachedCount;

        var count = CountPackagesInManifest(config.Manifest, new HashSet<string>());
        _manifestPackageCache[config.Manifest] = count;
        return count;
    }

    private int CountPackagesInManifest(string manifestName, HashSet<string> visited)
    {
        if (visited.Contains(manifestName))
            return 0;
        visited.Add(manifestName);

        var manifestPath = FindManifestFile(manifestName);
        if (manifestPath == null)
            return 0;

        try
        {
            var lines = File.ReadAllLines(manifestPath);
            int count = 0;
            var inArray = false;
            var currentArray = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                if (trimmed.StartsWith("managed_installs:") ||
                    trimmed.StartsWith("managed_uninstalls:") ||
                    trimmed.StartsWith("managed_updates:") ||
                    trimmed.StartsWith("optional_installs:"))
                {
                    inArray = true;
                    currentArray = trimmed.Split(':')[0];
                }
                else if (trimmed.StartsWith("included_manifests:"))
                {
                    inArray = true;
                    currentArray = "included_manifests";
                }
                else if (inArray && trimmed.StartsWith("- "))
                {
                    var value = trimmed[2..].Trim().Trim('"', '\'');
                    if (currentArray == "included_manifests")
                    {
                        count += CountPackagesInManifest(value, visited);
                    }
                    else
                    {
                        count++;
                    }
                }
                else if (inArray && !trimmed.StartsWith("-") && trimmed.Contains(':'))
                {
                    inArray = false;
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private string? FindManifestFile(string manifestName)
    {
        var possiblePaths = new[]
        {
            Path.Combine(ManifestsDir, manifestName + ".yaml"),
            Path.Combine(ManifestsDir, manifestName),
            Path.Combine(ManifestsDir, manifestName.Replace("/", "\\") + ".yaml"),
            Path.Combine(ManifestsDir, manifestName.Replace("/", "\\"))
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private List<FailedPackageInfo> GetFailedPackagesForSession(string sessionDir)
    {
        var failed = new List<FailedPackageInfo>();
        var eventsPath = Path.Combine(_baseDir, sessionDir, "events.jsonl");
        
        if (!File.Exists(eventsPath))
            return failed;

        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var eventData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (eventData == null)
                    continue;

                var status = eventData.TryGetValue("status", out var s) ? s.GetString() : null;
                if (status?.ToLowerInvariant() != "failed")
                    continue;

                var packageName = eventData.TryGetValue("package", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(packageName))
                    continue;

                var timestamp = eventData.TryGetValue("timestamp", out var ts) ? ts.GetString() : "";
                var error = eventData.TryGetValue("error", out var e) ? e.GetString() : null;

                failed.Add(new FailedPackageInfo
                {
                    PackageId = GeneratePackageId(packageName),
                    PackageName = packageName,
                    ErrorType = CategorizeError(error ?? "", ""),
                    LastAttempt = timestamp ?? "",
                    ErrorMessage = error
                });
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return failed;
    }

    private void PopulateFromCurrentManifests(Dictionary<string, ComprehensiveItemStat> itemStats)
    {
        var config = LoadCimianConfiguration();
        if (config?.Manifest == null)
            return;

        var items = GetItemsFromManifest(config.Manifest, new HashSet<string>());
        var catalogVersions = LoadCatalogVersions();
        var catalogDisplayNames = LoadCatalogDisplayNames();

        foreach (var item in items)
        {
            if (!itemStats.ContainsKey(item.Name))
            {
                itemStats[item.Name] = new ComprehensiveItemStat
                {
                    Name = item.Name,
                    ItemType = item.Type,
                    CurrentStatus = "pending",
                    LatestVersion = catalogVersions.GetValueOrDefault(item.Name, ""),
                    DisplayName = catalogDisplayNames.GetValueOrDefault(item.Name, item.Name)
                };
            }
        }
    }

    private List<ManagedItem> GetItemsFromManifest(string manifestName, HashSet<string> visited)
    {
        var items = new List<ManagedItem>();

        if (visited.Contains(manifestName))
            return items;
        visited.Add(manifestName);

        var manifestPath = FindManifestFile(manifestName);
        if (manifestPath == null)
            return items;

        try
        {
            var lines = File.ReadAllLines(manifestPath);
            var inArray = false;
            var currentArray = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("managed_installs:"))
                    { inArray = true; currentArray = "managed_installs"; }
                else if (trimmed.StartsWith("managed_uninstalls:"))
                    { inArray = true; currentArray = "managed_uninstalls"; }
                else if (trimmed.StartsWith("managed_updates:"))
                    { inArray = true; currentArray = "managed_updates"; }
                else if (trimmed.StartsWith("optional_installs:"))
                    { inArray = true; currentArray = "optional_installs"; }
                else if (trimmed.StartsWith("included_manifests:"))
                    { inArray = true; currentArray = "included_manifests"; }
                else if (inArray && trimmed.StartsWith("- "))
                {
                    var value = trimmed[2..].Trim().Trim('"', '\'');
                    if (currentArray == "included_manifests")
                    {
                        items.AddRange(GetItemsFromManifest(value, visited));
                    }
                    else
                    {
                        items.Add(new ManagedItem { Name = value, Type = currentArray });
                    }
                }
                else if (inArray && !trimmed.StartsWith("-") && trimmed.Contains(':'))
                {
                    inArray = false;
                }
            }
        }
        catch
        {
            // Ignore errors reading manifest
        }

        return items;
    }

    private void ProcessSessionForItems(string sessionDir, Dictionary<string, ComprehensiveItemStat> itemStats)
    {
        var eventsPath = Path.Combine(_baseDir, sessionDir, "events.jsonl");
        if (!File.Exists(eventsPath))
            return;

        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var eventData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (eventData == null)
                    continue;

                var packageName = eventData.TryGetValue("package", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(packageName))
                    continue;

                if (!itemStats.TryGetValue(packageName, out var stats))
                {
                    stats = new ComprehensiveItemStat
                    {
                        Name = packageName,
                        Sessions = new HashSet<string>(),
                        RecentAttempts = new List<ItemAttempt>()
                    };
                    itemStats[packageName] = stats;
                }

                stats.Sessions.Add(sessionDir);

                var action = eventData.TryGetValue("action", out var a) ? a.GetString() : "";
                var status = eventData.TryGetValue("status", out var s) ? s.GetString() : "";
                var timestamp = eventData.TryGetValue("timestamp", out var ts) ? ts.GetString() : "";
                var version = eventData.TryGetValue("version", out var v) ? v.GetString() : "";
                var error = eventData.TryGetValue("error", out var e) ? e.GetString() : "";

                // Update statistics
                switch (action?.ToLowerInvariant())
                {
                    case "install":
                        stats.InstallCount++;
                        break;
                    case "update":
                        stats.UpdateCount++;
                        break;
                    case "remove":
                        stats.RemovalCount++;
                        break;
                }

                switch (status?.ToLowerInvariant())
                {
                    case "failed":
                        stats.FailureCount++;
                        stats.LastError = error ?? "";
                        break;
                    case "warning":
                        stats.WarningCount++;
                        stats.LastWarning = error ?? "";
                        break;
                    case "success":
                    case "completed":
                        stats.LastSuccessfulTime = timestamp ?? "";
                        break;
                }

                stats.LastAttemptTime = timestamp ?? "";
                stats.LastAttemptStatus = status ?? "";
                stats.LastSeenInSession = sessionDir;
                stats.LastUpdate = timestamp ?? "";

                // Track recent attempts for loop detection
                stats.RecentAttempts.Add(new ItemAttempt
                {
                    SessionId = sessionDir,
                    Timestamp = timestamp ?? "",
                    Action = action ?? "",
                    Status = status ?? "",
                    Version = version
                });

                // Keep only last 10 attempts
                if (stats.RecentAttempts.Count > 10)
                    stats.RecentAttempts.RemoveAt(0);
            }
            catch
            {
                // Skip malformed lines
            }
        }
    }

    private ItemRecord ConvertStatsToItemRecord(ComprehensiveItemStat stats)
    {
        var (loopDetected, loopDetails) = DetectInstallLoopEnhanced(stats.RecentAttempts, stats.Name);

        return new ItemRecord
        {
            Id = GeneratePackageId(stats.Name),
            ItemName = stats.Name,
            DisplayName = string.IsNullOrEmpty(stats.DisplayName) ? stats.Name : stats.DisplayName,
            ItemType = stats.ItemType,
            CurrentStatus = stats.CurrentStatus,
            LatestVersion = stats.LatestVersion,
            InstalledVersion = stats.InstalledVersion,
            LastSeenInSession = stats.LastSeenInSession,
            LastSuccessfulTime = stats.LastSuccessfulTime,
            LastAttemptTime = stats.LastAttemptTime,
            LastAttemptStatus = stats.LastAttemptStatus,
            LastUpdate = stats.LastUpdate,
            InstallCount = stats.InstallCount,
            UpdateCount = stats.UpdateCount,
            RemovalCount = stats.RemovalCount,
            FailureCount = stats.FailureCount,
            WarningCount = stats.WarningCount,
            TotalSessions = stats.Sessions.Count,
            InstallLoopDetected = loopDetected,
            LoopDetails = loopDetails,
            LastError = stats.LastError,
            LastWarning = stats.LastWarning,
            RecentAttempts = stats.RecentAttempts.TakeLast(5).ToList(),
            Type = "cimian"
        };
    }

    private Dictionary<string, string> LoadCatalogVersions()
    {
        var versions = new Dictionary<string, string>();
        
        if (!Directory.Exists(CatalogsDir))
            return versions;

        foreach (var file in Directory.GetFiles(CatalogsDir, "*.yaml"))
        {
            try
            {
                var lines = File.ReadAllLines(file);
                string? currentName = null;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("name:"))
                        currentName = ExtractYamlValue(trimmed);
                    else if (trimmed.StartsWith("version:") && currentName != null)
                    {
                        versions[currentName] = ExtractYamlValue(trimmed);
                        currentName = null;
                    }
                }
            }
            catch
            {
                // Ignore errors reading catalog
            }
        }

        return versions;
    }

    private Dictionary<string, string> LoadCatalogDisplayNames()
    {
        var names = new Dictionary<string, string>();

        if (!Directory.Exists(CatalogsDir))
            return names;

        foreach (var file in Directory.GetFiles(CatalogsDir, "*.yaml"))
        {
            try
            {
                var lines = File.ReadAllLines(file);
                string? currentName = null;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("name:"))
                        currentName = ExtractYamlValue(trimmed);
                    else if (trimmed.StartsWith("display_name:") && currentName != null)
                    {
                        names[currentName] = ExtractYamlValue(trimmed);
                        currentName = null;
                    }
                }
            }
            catch
            {
                // Ignore errors reading catalog
            }
        }

        return names;
    }

    private static string GeneratePackageId(string packageName)
    {
        // Simple hash-based ID generation
        var hash = packageName.GetHashCode();
        return $"pkg_{Math.Abs(hash):x8}";
    }

    private static string CategorizeError(string errorMsg, string message)
    {
        var combined = (errorMsg + " " + message).ToLowerInvariant();

        if (combined.Contains("permission") || combined.Contains("access denied"))
            return "permission_error";
        if (combined.Contains("network") || combined.Contains("connection") || combined.Contains("timeout"))
            return "network_error";
        if (combined.Contains("disk") || combined.Contains("space"))
            return "disk_error";
        if (combined.Contains("dependency") || combined.Contains("prerequisite"))
            return "dependency_error";
        if (combined.Contains("signature") || combined.Contains("certificate"))
            return "signature_error";
        if (combined.Contains("corrupt") || combined.Contains("invalid"))
            return "corruption_error";

        return "unknown_error";
    }

    private (bool detected, InstallLoopDetail? details) DetectInstallLoopEnhanced(List<ItemAttempt> attempts, string packageName)
    {
        if (attempts.Count < 3)
            return (false, null);

        // Check for repeated installs
        var installAttempts = attempts.Where(a => a.Action == "install").ToList();
        if (installAttempts.Count < 3)
            return (false, null);

        // Check if multiple installs happened in recent sessions
        var recentInstalls = installAttempts.TakeLast(5).ToList();
        var uniqueSessions = recentInstalls.Select(a => a.SessionId).Distinct().Count();

        if (recentInstalls.Count >= 3 && uniqueSessions >= 3)
        {
            var suspectedCause = AnalyzeSuspectedCause(attempts, packageName);
            var recommendation = GetLoopRecommendation(attempts, packageName);

            return (true, new InstallLoopDetail
            {
                DetectionCriteria = $"Package installed {recentInstalls.Count} times across {uniqueSessions} sessions",
                LoopStartSession = recentInstalls.First().SessionId,
                SuspectedCause = suspectedCause,
                Recommendation = recommendation
            });
        }

        return (false, null);
    }

    private static string AnalyzeSuspectedCause(List<ItemAttempt> attempts, string packageName)
    {
        var successfulInstalls = attempts.Count(a => a.Action == "install" && a.Status == "success");
        var failedInstalls = attempts.Count(a => a.Action == "install" && a.Status == "failed");

        if (successfulInstalls >= 2)
            return "installer_reports_success_but_app_not_detected";

        if (failedInstalls >= 2 && successfulInstalls == 0)
        {
            var nameLower = packageName.ToLowerInvariant();
            if (nameLower.Contains("adobe"))
                return "adobe_licensing_or_creative_cloud_conflict";
            if (nameLower.Contains("office") || nameLower.Contains("microsoft"))
                return "microsoft_installer_service_or_office_conflict";
            if (nameLower.Contains("java"))
                return "java_version_conflict_or_registry_corruption";
            return "installer_permission_dependency_or_conflict_issues";
        }

        if (failedInstalls > 0 && successfulInstalls > 0)
            return "intermittent_system_conditions_or_timing_issues";

        return "unknown_loop_cause_requires_manual_investigation";
    }

    private static string GetLoopRecommendation(List<ItemAttempt> attempts, string packageName)
    {
        var suspectedCause = AnalyzeSuspectedCause(attempts, packageName);

        return suspectedCause switch
        {
            "installer_reports_success_but_app_not_detected" =>
                "Verify installer exit codes and app detection logic in pkginfo; check if silent install parameters are correct",
            "adobe_licensing_or_creative_cloud_conflict" =>
                "Clear Adobe licensing cache, restart Creative Cloud services, or temporarily disable real-time AV scanning",
            "microsoft_installer_service_or_office_conflict" =>
                "Restart Windows Installer service, clear MSI cache, or run system in safe mode for troubleshooting",
            "java_version_conflict_or_registry_corruption" =>
                "Clean Java registry entries, remove conflicting Java versions, or use Java offline installer",
            "intermittent_system_conditions_or_timing_issues" =>
                "Schedule installations during maintenance windows or implement pre-flight system health checks",
            _ => "Review installer logs, verify system requirements, check for conflicts with AV/security software"
        };
    }

    #endregion

    #region Registry Access

    /// <summary>
    /// Gets the installed version of a package from Windows registry
    /// </summary>
    public string GetInstalledVersionFromRegistry(string packageName)
    {
        // Try Cimian's tracking registry first
        var cimianVersion = GetCimianManagedVersion(packageName);
        if (!string.IsNullOrEmpty(cimianVersion))
            return cimianVersion;

        // Try standard uninstall registry
        return GetUninstallRegistryVersion(packageName);
    }

    private string GetCimianManagedVersion(string packageName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Cimian\InstalledPackages\{packageName}");
            return key?.GetValue("Version")?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private string GetUninstallRegistryVersion(string packageName)
    {
        var registryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var basePath in registryPaths)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(basePath);
                if (baseKey == null)
                    continue;

                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = baseKey.OpenSubKey(subKeyName);
                        var displayName = subKey?.GetValue("DisplayName")?.ToString();

                        if (displayName != null &&
                            displayName.Contains(packageName, StringComparison.OrdinalIgnoreCase))
                        {
                            return subKey?.GetValue("DisplayVersion")?.ToString() ?? "";
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            catch
            {
                continue;
            }
        }

        return "";
    }

    /// <summary>
    /// Gets enhanced .pkg package metadata from registry
    /// </summary>
    public PkgRegistryMetadata GetPkgRegistryMetadata(string itemName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Cimian\InstalledPackages\{itemName}");
            if (key == null)
                return new PkgRegistryMetadata();

            return new PkgRegistryMetadata
            {
                Version = key.GetValue("Version")?.ToString() ?? "",
                PackageFormat = key.GetValue("PackageFormat")?.ToString() ?? "",
                PackageId = key.GetValue("PackageId")?.ToString() ?? "",
                Developer = key.GetValue("Developer")?.ToString() ?? "",
                Description = key.GetValue("Description")?.ToString() ?? "",
                SignatureStatus = key.GetValue("SignatureStatus")?.ToString() ?? "",
                SignerCertificate = key.GetValue("SignerCertificate")?.ToString() ?? "",
                SignerCommonName = key.GetValue("SignerCommonName")?.ToString() ?? "",
                SignatureTimestamp = key.GetValue("SignatureTimestamp")?.ToString() ?? "",
                Architecture = key.GetValue("Architecture")?.ToString() ?? "",
                InstallLocation = key.GetValue("InstallLocation")?.ToString() ?? ""
            };
        }
        catch
        {
            return new PkgRegistryMetadata();
        }
    }

    #endregion
}
