using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cimian.Core.Models;

namespace Cimian.Core.Services;

/// <summary>
/// SessionLogger provides structured logging with day-nested timestamped directories
/// compatible with external monitoring and reporting tools.
/// Ported from Go: pkg/logging/logging.go and pkg/logging/events.go
/// 
/// Features:
/// - Day-nested directories: logs/YYYY-MM-DD/HHMM/ for easy navigation
/// - Creates session.json, events.jsonl, install.log, and run.log files
/// - 7-day rolling retention with automatic cleanup
/// - Writes reports to C:\ProgramData\ManagedInstalls\reports
/// - Structured data formats for external tool integration
/// </summary>
public class SessionLogger : IDisposable
{
    private const string BaseLogsDir = @"C:\ProgramData\ManagedInstalls\logs";
    private const string ReportsDir = @"C:\ProgramData\ManagedInstalls\reports";

    // Retention policy: 30-day rolling window (~220MB at typical usage)
    private const int DefaultMaxAgeDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions JsonLinesOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string _sessionId = "";
    private string _sessionDir = "";
    private DateTime _sessionStart;
    private string _runType = "manual";

    private StreamWriter? _logFile;        // install.log
    private StreamWriter? _runLogFile;     // run.log (session copy)
    private StreamWriter? _reportRunLog;   // reports/run.log
    private StreamWriter? _eventsFile;     // events.jsonl

    private readonly ConcurrentQueue<LogEvent> _events = new();
    private SessionData _sessionData = new();
    private bool _disposed;

    private readonly object _logLock = new();

    /// <summary>
    /// Gets the current session ID
    /// </summary>
    public string SessionId => _sessionId;

    /// <summary>
    /// Gets the current session directory path
    /// </summary>
    public string SessionDir => _sessionDir;

    /// <summary>
    /// Initializes a new session with timestamped directory structure
    /// </summary>
    /// <param name="runType">Type of run: auto, manual, bootstrap, checkonly, installonly</param>
    /// <param name="metadata">Optional metadata to include in session</param>
    /// <returns>The session ID</returns>
    public string StartSession(string runType, Dictionary<string, object>? metadata = null)
    {
        _sessionStart = DateTime.Now;
        _runType = runType;
        
        // Generate session ID as YYYY-MM-DD-HHMM for reports
        _sessionId = _sessionStart.ToString("yyyy-MM-dd-HHmm");
        
        // Create day-nested directory: logs/YYYY-MM-DD/HHMM/
        var dayDir = Path.Combine(BaseLogsDir, _sessionStart.ToString("yyyy-MM-dd"));
        var timeDir = _sessionStart.ToString("HHmm");
        _sessionDir = Path.Combine(dayDir, timeDir);
        
        // Handle rare same-minute collision by appending suffix
        if (Directory.Exists(_sessionDir))
        {
            for (var i = 2; i <= 9; i++)
            {
                var candidate = Path.Combine(dayDir, $"{timeDir}_{i}");
                if (!Directory.Exists(candidate))
                {
                    _sessionDir = candidate;
                    _sessionId = $"{_sessionStart:yyyy-MM-dd}-{timeDir}_{i}";
                    break;
                }
            }
        }
        
        Directory.CreateDirectory(_sessionDir);
        
        // Ensure reports directory exists
        Directory.CreateDirectory(ReportsDir);

        // Perform log retention cleanup (async, non-blocking)
        Task.Run(() => PerformRetentionCleanup());

        // Initialize log files
        InitializeLogFiles();

        // Initialize session data
        _sessionData = new SessionData
        {
            SessionId = _sessionId,
            StartTime = _sessionStart.ToString("o"),
            RunType = runType,
            Status = "running",
            Environment = GatherEnvironmentInfo(),
            Summary = new SessionLogSummary
            {
                PackagesHandled = new List<string>()
            }
        };

        // Add metadata if provided
        if (metadata != null && _sessionData.Environment != null)
        {
            foreach (var kvp in metadata)
            {
                _sessionData.Environment[kvp.Key] = kvp.Value;
            }
        }

        // Write initial session.json
        WriteSessionFile();

        return _sessionId;
    }

    /// <summary>
    /// Initializes all log files for the session
    /// </summary>
    private void InitializeLogFiles()
    {
        try
        {
            // Main log file (install.log)
            var installLogPath = Path.Combine(_sessionDir, "install.log");
            _logFile = new StreamWriter(installLogPath, append: true) { AutoFlush = true };

            // Session run log (run.log in session directory)
            var sessionRunLogPath = Path.Combine(_sessionDir, "run.log");
            _runLogFile = new StreamWriter(sessionRunLogPath, append: true) { AutoFlush = true };

            // Report run log (reports/run.log - truncated each session)
            // This may fail if the file is locked by another process (e.g., Go version running)
            try
            {
                var reportRunLogPath = Path.Combine(ReportsDir, "run.log");
                // Delete existing file to start fresh (like Go does with O_TRUNC)
                if (File.Exists(reportRunLogPath))
                {
                    try { File.Delete(reportRunLogPath); } catch { /* ignore */ }
                }
                _reportRunLog = new StreamWriter(reportRunLogPath, append: false) { AutoFlush = true };
            }
            catch
            {
                // If we can't write to reports/run.log, just continue without it
                // This is non-fatal - the session logs are more important
                _reportRunLog = null;
            }

            // Events file (events.jsonl - JSON Lines format)
            var eventsPath = Path.Combine(_sessionDir, "events.jsonl");
            _eventsFile = new StreamWriter(eventsPath, append: true) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to initialize log files: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs a message to all log files
    /// </summary>
    public void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var formattedLine = $"[{timestamp}] {level,-5} {message}";

        lock (_logLock)
        {
            try
            {
                _logFile?.WriteLine(formattedLine);
                _runLogFile?.WriteLine(formattedLine);
                _reportRunLog?.WriteLine(formattedLine);
            }
            catch
            {
                // Silent failure - don't spam console with log file errors
            }
        }

        // Note: Console output is handled separately by ConsoleLogger
        // SessionLogger only writes to log files
    }

    /// <summary>
    /// Logs a structured event for external monitoring tools
    /// </summary>
    public void LogEvent(LogEvent evt)
    {
        // Ensure event has proper metadata
        if (string.IsNullOrEmpty(evt.SessionId))
            evt.SessionId = _sessionId;
        
        if (evt.Timestamp == default)
            evt.Timestamp = DateTime.Now;
        
        if (string.IsNullOrEmpty(evt.EventId))
            evt.EventId = $"{_sessionId}-{DateTime.Now.Ticks}";

        _events.Enqueue(evt);

        // Write to events.jsonl
        try
        {
            var json = JsonSerializer.Serialize(evt, JsonLinesOptions);
            lock (_logLock)
            {
                _eventsFile?.WriteLine(json);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to write event: {ex.Message}");
        }
    }

    /// <summary>
    /// Convenience method to log an installation event
    /// </summary>
    public void LogInstall(string packageName, string version, string action, string status, string message, string? error = null)
    {
        LogEvent(new LogEvent
        {
            EventType = "install",
            PackageName = packageName,
            PackageVersion = version,
            Action = action,
            Status = status,
            Message = message,
            Error = error,
            Level = status == "failed" ? "ERROR" : (status == "completed" ? "INFO" : "DEBUG")
        });
    }

    /// <summary>
    /// Convenience method to log an installation event with full status reason tracking
    /// </summary>
    /// <param name="packageName">Name of the package</param>
    /// <param name="version">Target version</param>
    /// <param name="action">Action: install, update, uninstall</param>
    /// <param name="status">Status: pending, completed, failed</param>
    /// <param name="message">Human-readable message</param>
    /// <param name="statusReason">Status reason from detection</param>
    /// <param name="statusReasonCode">Machine-readable reason code</param>
    /// <param name="detectionMethod">Detection method used</param>
    /// <param name="installedVersion">Installed version if detected</param>
    /// <param name="error">Error message if failed</param>
    public void LogInstallWithReason(
        string packageName,
        string version,
        string action,
        string status,
        string message,
        string? statusReason = null,
        string? statusReasonCode = null,
        string? detectionMethod = null,
        string? installedVersion = null,
        string? error = null)
    {
        LogEvent(new LogEvent
        {
            EventType = "install",
            PackageName = packageName,
            PackageVersion = version,
            TargetVersion = version,
            Action = action,
            Status = status,
            Message = message,
            Error = error,
            Level = status == "failed" ? "ERROR" : (status == "completed" ? "INFO" : "DEBUG"),
            StatusReason = statusReason,
            StatusReasonCode = statusReasonCode,
            DetectionMethod = detectionMethod,
            InstalledVersion = installedVersion
        });
    }

    /// <summary>
    /// Logs a status check event with full reason tracking
    /// </summary>
    public void LogStatusCheck(
        string packageName,
        string version,
        string status,
        string statusReason,
        string statusReasonCode,
        string detectionMethod,
        string? installedVersion = null,
        bool needsAction = false)
    {
        LogEvent(new LogEvent
        {
            EventType = "status_check",
            PackageName = packageName,
            PackageVersion = version,
            TargetVersion = version,
            Status = status,
            Message = statusReason,
            Level = "DEBUG",
            StatusReason = statusReason,
            StatusReasonCode = statusReasonCode,
            DetectionMethod = detectionMethod,
            InstalledVersion = installedVersion,
            Context = new Dictionary<string, object>
            {
                ["needs_action"] = needsAction
            }
        });
    }

    /// <summary>
    /// Ends the current session and writes final summary
    /// </summary>
    public void EndSession(string status, SessionLogSummary summary)
    {
        var endTime = DateTime.Now;
        var duration = endTime - _sessionStart;

        // Update session data
        _sessionData.EndTime = endTime.ToString("o");
        _sessionData.Status = status;
        _sessionData.DurationSeconds = (long)duration.TotalSeconds;
        _sessionData.Summary = summary;
        summary.Duration = duration;

        // Write final session.json
        WriteSessionFile();

        // Generate reports
        GenerateReports();

        // Cleanup
        CloseLogFiles();
    }

    /// <summary>
    /// Writes the session.json file
    /// </summary>
    private void WriteSessionFile()
    {
        try
        {
            var sessionPath = Path.Combine(_sessionDir, "session.json");
            var json = JsonSerializer.Serialize(_sessionData, JsonOptions);
            File.WriteAllText(sessionPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to write session.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Performs 7-day rolling retention cleanup.
    /// Removes day directories older than retention window and cleans up any legacy flat-format sessions.
    /// </summary>
    private void PerformRetentionCleanup()
    {
        try
        {
            if (!Directory.Exists(BaseLogsDir))
                return;

            var cutoff = DateTime.Now.AddDays(-DefaultMaxAgeDays);

            foreach (var entry in Directory.GetDirectories(BaseLogsDir))
            {
                var dirName = Path.GetFileName(entry);

                // New format: day directories (YYYY-MM-DD, 10 chars)
                if (IsDayDirectory(dirName))
                {
                    if (DateTime.TryParseExact(dirName, "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out var dayDate)
                        && dayDate < cutoff.Date)
                    {
                        TryDeleteSessionDirectory(entry);
                    }
                    continue;
                }

                // Legacy flat format (YYYY-MM-DD-HHMMss, 17 chars) — clean up old sessions
                if (IsLegacySessionDirectory(dirName))
                {
                    if (DateTime.TryParseExact(dirName, "yyyy-MM-dd-HHmmss", null,
                            System.Globalization.DateTimeStyles.None, out var legacyDate)
                        && legacyDate < cutoff)
                    {
                        TryDeleteSessionDirectory(entry);
                    }
                }
            }
        }
        catch
        {
            // Silent failure - retention cleanup is non-critical
        }
    }

    /// <summary>
    /// Checks if a directory name is a day directory (YYYY-MM-DD)
    /// </summary>
    private static bool IsDayDirectory(string name)
    {
        return name.Length == 10 && name[4] == '-' && name[7] == '-'
            && DateTime.TryParseExact(name, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out _);
    }

    /// <summary>
    /// Checks if a directory name is a time-of-day session (HHMM or HHMM_N for collisions)
    /// </summary>
    private static bool IsTimeSessionDirectory(string name)
    {
        // Primary: 4-digit HHMM (e.g. "1430")
        if (name.Length == 4 && int.TryParse(name, out var hhmm))
            return hhmm >= 0 && hhmm <= 2359;

        // Collision suffix: HHMM_N (e.g. "1430_2")
        if (name.Length == 6 && name[4] == '_' && char.IsDigit(name[5]))
            return int.TryParse(name[..4], out var hhmm2) && hhmm2 >= 0 && hhmm2 <= 2359;

        return false;
    }

    /// <summary>
    /// Checks if a directory name is a legacy flat-format session (YYYY-MM-DD-HHMMss)
    /// </summary>
    private static bool IsLegacySessionDirectory(string name)
    {
        return name.Length == 17 && name[4] == '-' && name[7] == '-' && name[10] == '-'
            && DateTime.TryParseExact(name, "yyyy-MM-dd-HHmmss", null,
                System.Globalization.DateTimeStyles.None, out _);
    }

    /// <summary>
    /// Safely attempts to delete a session directory
    /// </summary>
    private static void TryDeleteSessionDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore - directory may be in use or protected
        }
    }

    // Current session items for items.json generation (set by UpdateEngine)
    private List<SessionPackageInfo> _currentSessionItems = new();

    /// <summary>
    /// Sets the current session's managed items data for items.json generation.
    /// Called by UpdateEngine after IdentifyActions builds status tables.
    /// Go parity: DataExporter.SetCurrentSessionPackagesInfo()
    /// </summary>
    public void SetCurrentSessionItems(List<SessionPackageInfo> items)
    {
        _currentSessionItems = items ?? new List<SessionPackageInfo>();
    }

    /// <summary>
    /// Generates report files for external tools
    /// </summary>
    private void GenerateReports()
    {
        try
        {
            // Generate sessions.json - list of recent sessions
            GenerateSessionsReport();

            // Generate events.json - recent events
            GenerateEventsReport();

            // Generate items.json - current managed items snapshot
            GenerateItemsReport();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to generate reports: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates all session directories (both new nested and legacy flat format),
    /// returning full paths ordered newest-first.
    /// </summary>
    private static IEnumerable<string> EnumerateAllSessionDirs()
    {
        if (!Directory.Exists(BaseLogsDir))
            yield break;

        // New format: day dirs containing time subdirs
        var dayDirs = Directory.GetDirectories(BaseLogsDir)
            .Where(d => IsDayDirectory(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d));

        foreach (var dayDir in dayDirs)
        {
            var timeDirs = Directory.GetDirectories(dayDir)
                .Where(d => IsTimeSessionDirectory(Path.GetFileName(d)))
                .OrderByDescending(d => Path.GetFileName(d));

            foreach (var timeDir in timeDirs)
                yield return timeDir;
        }

        // Legacy flat format for backward compatibility
        var legacyDirs = Directory.GetDirectories(BaseLogsDir)
            .Where(d => IsLegacySessionDirectory(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d));

        foreach (var dir in legacyDirs)
            yield return dir;
    }

    /// <summary>
    /// Generates the sessions.json report file
    /// </summary>
    private void GenerateSessionsReport()
    {
        var sessions = new List<SessionData>();

        foreach (var dir in EnumerateAllSessionDirs().Take(100))
        {
            var sessionPath = Path.Combine(dir, "session.json");
            if (File.Exists(sessionPath))
            {
                try
                {
                    var json = File.ReadAllText(sessionPath);
                    var session = JsonSerializer.Deserialize<SessionData>(json, JsonOptions);
                    if (session != null)
                        sessions.Add(session);
                }
                catch { /* Skip invalid session files */ }
            }
        }

        var sessionsPath = Path.Combine(ReportsDir, "sessions.json");
        File.WriteAllText(sessionsPath, JsonSerializer.Serialize(sessions, JsonOptions));
    }

    /// <summary>
    /// Generates the events.json report file from recent sessions
    /// </summary>
    private void GenerateEventsReport()
    {
        var allEvents = new List<LogEvent>();
        var cutoff = DateTime.Now.AddHours(-48);

        foreach (var dir in EnumerateAllSessionDirs().Take(10))
        {
            var eventsPath = Path.Combine(dir, "events.jsonl");
            if (File.Exists(eventsPath))
            {
                try
                {
                    foreach (var line in File.ReadLines(eventsPath))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var evt = JsonSerializer.Deserialize<LogEvent>(line, JsonLinesOptions);
                            if (evt != null && evt.Timestamp >= cutoff)
                                allEvents.Add(evt);
                        }
                    }
                }
                catch { /* Skip invalid event files */ }
            }
        }

        var eventsReportPath = Path.Combine(ReportsDir, "events.json");
        File.WriteAllText(eventsReportPath, JsonSerializer.Serialize(allEvents, JsonOptions));
    }

    /// <summary>
    /// Generates the items.json report file - current snapshot of all managed items.
    /// Delegates to DataExporter.GenerateCurrentItemsFromPackagesInfo() for historical
    /// enrichment including install loop detection and attempt counting.
    /// Excludes MDM profiles/apps (managed externally by Device Management Service).
    /// </summary>
    private void GenerateItemsReport()
    {
        if (_currentSessionItems.Count == 0)
            return;

        // Filter out MDM-managed items before passing to DataExporter
        var cimianItems = _currentSessionItems
            .Where(pkg => !string.Equals(pkg.ItemType, "managedprofile", StringComparison.OrdinalIgnoreCase) &&
                          !string.Equals(pkg.ItemType, "managedapp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (cimianItems.Count == 0)
            return;

        try
        {
            // Use DataExporter for historical enrichment + loop detection
            var exporter = new DataExporter();
            var items = exporter.GenerateCurrentItemsFromPackagesInfo(cimianItems);

            var itemsPath = Path.Combine(ReportsDir, "items.json");
            File.WriteAllText(itemsPath, JsonSerializer.Serialize(items, JsonOptions));
        }
        catch (Exception ex)
        {
            // Fallback to simple generation if DataExporter fails
            Console.Error.WriteLine($"[WARN] DataExporter enrichment failed, using simple items report: {ex.Message}");
            GenerateItemsReportSimple(cimianItems);
        }
    }

    /// <summary>
    /// Simple items.json generation without historical enrichment (fallback).
    /// </summary>
    private void GenerateItemsReportSimple(List<SessionPackageInfo> items)
    {
        var records = new List<Cimian.Core.Models.ItemRecord>();
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        foreach (var pkg in items)
        {
            var displayName = !string.IsNullOrEmpty(pkg.DisplayName) ? pkg.DisplayName : pkg.Name;
            var normalizedStatus = NormalizeItemStatus(pkg.Status);

            records.Add(new Cimian.Core.Models.ItemRecord
            {
                Id = pkg.Name.ToLowerInvariant().Replace(" ", ""),
                ItemName = pkg.Name,
                DisplayName = displayName,
                ItemType = pkg.ItemType,
                CurrentStatus = normalizedStatus,
                LatestVersion = pkg.Version,
                InstalledVersion = pkg.InstalledVersion,
                LastSeenInSession = now,
                LastAttemptTime = now,
                LastAttemptStatus = normalizedStatus,
                LastUpdate = now,
                LastError = pkg.ErrorMessage ?? "",
                LastWarning = pkg.WarningMessage
            });
        }

        var itemsPath = Path.Combine(ReportsDir, "items.json");
        File.WriteAllText(itemsPath, JsonSerializer.Serialize(records, JsonOptions));
    }

    /// <summary>
    /// Normalizes session/action statuses to standard item statuses.
    /// Go parity: NormalizeItemStatus() in reporting.go
    /// </summary>
    private static string NormalizeItemStatus(string status)
    {
        return (status ?? "").ToLowerInvariant() switch
        {
            "completed" or "success" or "installed" or "ok" => "Installed",
            "failed" or "error" or "fail" => "Error",
            "warning" or "warn" => "Warning",
            "pending" or "pending install" or "pending update" or "skipped" or "not installed" => "Pending",
            "removed" or "uninstalled" => "Removed",
            "not available" => "Not Available",
            _ => status switch
            {
                "Installed" or "Error" or "Warning" or "Pending" or "Removed" or "Not Available" => status,
                _ => "Pending"
            }
        };
    }

    /// <summary>
    /// Returns the latest session directory (new nested or legacy flat format).
    /// Used by external consumers to find the most recent log session.
    /// </summary>
    public static string? GetLatestSessionDir()
    {
        return EnumerateAllSessionDirs().FirstOrDefault();
    }

    /// <summary>
    /// Gathers environment information for the session
    /// </summary>
    private Dictionary<string, object> GatherEnvironmentInfo()
    {
        return new Dictionary<string, object>
        {
            ["hostname"] = Environment.MachineName,
            ["user"] = Environment.UserName,
            ["os_version"] = Environment.OSVersion.ToString(),
            ["architecture"] = Environment.Is64BitOperatingSystem ? "x64" : "x86",
            ["process_id"] = Environment.ProcessId,
            ["log_version"] = "2.0"
        };
    }

    /// <summary>
    /// Closes all log files
    /// </summary>
    private void CloseLogFiles()
    {
        lock (_logLock)
        {
            _logFile?.Dispose();
            _logFile = null;

            _runLogFile?.Dispose();
            _runLogFile = null;

            _reportRunLog?.Dispose();
            _reportRunLog = null;

            _eventsFile?.Dispose();
            _eventsFile = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CloseLogFiles();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents a structured log event
/// </summary>
public class LogEvent
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "INFO";

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "";

    [JsonPropertyName("package_id")]
    public string? PackageId { get; set; }

    [JsonPropertyName("package_name")]
    public string? PackageName { get; set; }

    [JsonPropertyName("package_version")]
    public string? PackageVersion { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("duration")]
    public TimeSpan? Duration { get; set; }

    [JsonPropertyName("progress")]
    public int? Progress { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("context")]
    public Dictionary<string, object>? Context { get; set; }

    [JsonPropertyName("installer_type")]
    public string? InstallerType { get; set; }

    #region Status Reason Tracking

    /// <summary>
    /// Human-readable explanation of how status was determined.
    /// Example: "File at C:\Program Files\App\app.exe verified at version 1.2.3"
    /// </summary>
    [JsonPropertyName("status_reason")]
    public string? StatusReason { get; set; }

    /// <summary>
    /// Machine-readable status reason code.
    /// Example: "file_match", "registry_missing", "update_available"
    /// See Cimian.Core.Models.StatusReasonCode for all values.
    /// </summary>
    [JsonPropertyName("status_reason_code")]
    public string? StatusReasonCode { get; set; }

    /// <summary>
    /// Detection method used to determine status.
    /// Example: "file", "registry", "script", "msi"
    /// See Cimian.Core.Models.DetectionMethod for all values.
    /// </summary>
    [JsonPropertyName("detection_method")]
    public string? DetectionMethod { get; set; }

    /// <summary>
    /// Currently installed version at time of check, if detected.
    /// </summary>
    [JsonPropertyName("installed_version")]
    public string? InstalledVersion { get; set; }

    /// <summary>
    /// Target version from catalog that was checked against.
    /// </summary>
    [JsonPropertyName("target_version")]
    public string? TargetVersion { get; set; }

    #endregion
}

/// <summary>
/// Session data for session.json file
/// </summary>
public class SessionData
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = "";

    [JsonPropertyName("end_time")]
    public string? EndTime { get; set; }

    [JsonPropertyName("run_type")]
    public string RunType { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("duration_seconds")]
    public long? DurationSeconds { get; set; }

    [JsonPropertyName("summary")]
    public SessionLogSummary? Summary { get; set; }

    [JsonPropertyName("environment")]
    public Dictionary<string, object>? Environment { get; set; }
}

/// <summary>
/// Session summary statistics for session logging
/// </summary>
public class SessionLogSummary
{
    [JsonPropertyName("total_actions")]
    public int TotalActions { get; set; }

    [JsonPropertyName("installs")]
    public int Installs { get; set; }

    [JsonPropertyName("updates")]
    public int Updates { get; set; }

    [JsonPropertyName("removals")]
    public int Removals { get; set; }

    [JsonPropertyName("successes")]
    public int Successes { get; set; }

    [JsonPropertyName("failures")]
    public int Failures { get; set; }

    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TimeSpan Duration { get; set; }

    [JsonPropertyName("packages_handled")]
    public List<string> PackagesHandled { get; set; } = new();
}
