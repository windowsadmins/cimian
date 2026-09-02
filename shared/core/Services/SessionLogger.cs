using System.Collections.Concurrent;
using System.Linq;
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
/// - Creates session.json, events.jsonl and install.log files
/// - 30-day rolling retention with automatic cleanup, covering the whole logs tree
/// - Writes reports to C:\ProgramData\ManagedInstalls\reports
/// - Structured data formats for external tool integration
/// </summary>
public class SessionLogger : IDisposable
{
    private static readonly string BaseLogsDir = CimianPaths.LogsDir;
    private static readonly string ReportsDir = CimianPaths.ReportsDir;

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

        // A previous run that was killed mid-session left its session.json at
        // "running" forever. Close those out now that we are the live process,
        // so a truncated run is reportable instead of invisible.
        ReapOrphanedSessions();

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

            // There is deliberately no second copy in the session directory. install.log
            // and the old sibling run.log received the identical formatted line from
            // Log(), so the session tree carried every byte twice. reports/run.log below
            // still exists: it is the fixed path external tooling tails, and it is
            // truncated per session rather than accumulating.

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
    /// Closes out session.json files left at "running" by a previous run that never
    /// reached EndSession.
    /// </summary>
    /// <remarks>
    /// A session that is killed mid-run -- the process dies, the machine reboots, a
    /// scheduled task is torn down -- leaves session.json saying "running" with an
    /// empty summary, and because GenerateReports() only runs from EndSession, the
    /// reports directory keeps advertising the last session that DID finish. The run
    /// is then invisible: the fleet view shows an older, healthy, completed session
    /// while every install in the truncated one silently never happened. Detection
    /// still reports the package as needed on the next run, so what surfaces is a
    /// loop warning blaming the pkginfo criteria -- which sends anyone reading it to
    /// the wrong place entirely.
    ///
    /// Marking the corpse "aborted" and naming the last thing it was doing is the
    /// difference between "this host is fine" and "this host has not completed a run
    /// in days".
    ///
    /// Only sessions that cannot still be alive are touched: a session whose recorded
    /// process id belongs to a live process started no later than the session itself
    /// is left alone, so a genuinely concurrent run is never clobbered.
    /// </remarks>
    private void ReapOrphanedSessions()
    {
        foreach (var recovered in ReapAbandonedSessions(EnumerateAllSessionDirs().Take(50), _sessionDir, _sessionId))
        {
            Log("WARN", "Recovered abandoned session " + recovered);
        }
    }

    /// <summary>
    /// Rewrites every abandoned "running" session.json under <paramref name="sessionDirs"/>
    /// as "aborted" and returns a description of each one recovered.
    /// </summary>
    internal static List<string> ReapAbandonedSessions(IEnumerable<string> sessionDirs, string currentSessionDir, string currentSessionId)
    {
        var recovered = new List<string>();
        try
        {
            foreach (var dir in sessionDirs)
            {
                if (!string.IsNullOrEmpty(currentSessionDir) &&
                    string.Equals(Path.GetFullPath(dir), Path.GetFullPath(currentSessionDir), StringComparison.OrdinalIgnoreCase))
                    continue;

                var sessionPath = Path.Combine(dir, "session.json");
                if (!File.Exists(sessionPath))
                    continue;

                SessionData? session;
                try
                {
                    session = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(sessionPath), JsonOptions);
                }
                catch
                {
                    continue;
                }

                if (session == null || !string.Equals(session.Status, "running", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!DateTime.TryParse(session.StartTime, out var startedAt))
                    startedAt = Directory.GetCreationTime(dir);

                if (IsSessionProcessStillAlive(session, startedAt))
                    continue;

                // The last event the session managed to write is the truest end time
                // available, and the item it names is where the run actually stopped.
                var lastEvent = ReadLastEvent(Path.Combine(dir, "events.jsonl"));
                var endedAt = lastEvent.Timestamp ?? File.GetLastWriteTime(sessionPath);
                if (endedAt < startedAt)
                    endedAt = startedAt;

                var reason = string.IsNullOrEmpty(lastEvent.Item)
                    ? "session ended without reaching EndSession"
                    : "session ended without reaching EndSession while processing " + lastEvent.Item;

                session.Status = "aborted";
                session.EndTime = endedAt.ToString("o");
                session.DurationSeconds = (long)(endedAt - startedAt).TotalSeconds;
                session.Environment ??= new Dictionary<string, object>();
                session.Environment["aborted_reason"] = reason;
                session.Environment["aborted_detected_by"] = currentSessionId;

                try
                {
                    File.WriteAllText(sessionPath, JsonSerializer.Serialize(session, JsonOptions));
                    recovered.Add(session.SessionId + " - marked aborted (" + reason + ")");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[ERROR] Failed to mark session " + session.SessionId + " aborted: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            // Housekeeping must never stop a run.
            Console.Error.WriteLine("[ERROR] Failed to reap abandoned sessions: " + ex.Message);
        }

        return recovered;
    }

    /// <summary>
    /// True when the session's recorded process id still belongs to a process that
    /// could plausibly be that session, so it must not be reaped.
    /// </summary>
    private static bool IsSessionProcessStillAlive(SessionData session, DateTime sessionStart)
    {
        if (session.Environment == null || !session.Environment.TryGetValue("process_id", out var raw) || raw == null)
            return false;

        if (!int.TryParse(raw.ToString(), out var pid) || pid <= 0)
            return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            // Process ids are recycled. A process that started AFTER the session did
            // is a different one wearing the same number.
            return process.StartTime <= sessionStart.AddSeconds(1);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the timestamp and package name of the last event a session wrote.
    /// </summary>
    private static (DateTime? Timestamp, string? Item) ReadLastEvent(string eventsPath)
    {
        if (!File.Exists(eventsPath))
            return (null, null);

        try
        {
            string? lastLine = null;
            foreach (var line in File.ReadLines(eventsPath))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lastLine = line;
            }

            if (lastLine == null)
                return (null, null);

            var evt = JsonSerializer.Deserialize<LogEvent>(lastLine, JsonLinesOptions);
            if (evt == null)
                return (null, null);

            return (evt.Timestamp == default ? (DateTime?)null : evt.Timestamp, evt.PackageName);
        }
        catch
        {
            return (null, null);
        }
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
    /// Performs the 30-day rolling retention cleanup over the whole logs tree.
    /// </summary>
    /// <remarks>
    /// This used to enumerate directories only. Everything the client itself writes
    /// lives in a directory, so that looked sufficient — but the logs root is shared:
    /// package scripts, msiexec verbose logs and the self-update installer all drop
    /// loose files there, and none of them were ever considered for deletion. Files at
    /// the root are swept by age here, and named subdirectories are swept by the age of
    /// their newest file, so a package that stops being installed eventually goes away
    /// with its logs.
    /// </remarks>
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

            // Relocate before sweeping: a legacy artifact that is still being rewritten
            // never looks expired, so age alone would never clear it.
            RelocateLegacyArtifacts();

            SweepExpiredFiles(BaseLogsDir, cutoff);
            SweepExpiredFiles(CimianPaths.SelfUpdateLogsDir, cutoff);
            SweepExpiredFiles(CimianPaths.InstallLogsDir, cutoff);
            SweepExpiredSubdirectories(CimianPaths.PackageLogsDir, cutoff);
        }
        catch
        {
            // Silent failure - retention cleanup is non-critical
        }
    }

    /// <summary>
    /// Moves everything this client no longer writes to the logs root out of it, and
    /// pulls installer logs out of the download cache, so the root converges to holding
    /// only the dated session tree.
    /// </summary>
    /// <remarks>
    /// Age alone cannot do this job. The artifacts here are written by MSIs that are
    /// already installed on the machine and by earlier versions of this client: an
    /// already-installed package rewrites its sidecar log on every session, so the file
    /// is permanently younger than any retention window and an age-based sweep never
    /// reaches it. Waiting for every package on every endpoint to be rebuilt is not a
    /// plan; relocating on sight is. Once packages are rebuilt this is a no-op.
    ///
    /// Relocation, not deletion, because the content is still wanted — an installcheck
    /// may tail the last attempt's output, and it now looks for it at the new path.
    /// </remarks>
    internal static void RelocateLegacyArtifacts()
        => RelocateLegacyArtifacts(BaseLogsDir, CimianPaths.CacheDir);

    /// <summary>
    /// Roots are parameters so this can be exercised against a temporary tree; the
    /// production call site passes the real ones.
    /// </summary>
    internal static void RelocateLegacyArtifacts(string logsRoot, string cacheRoot)
    {
        // Pre-move sidecar logs: cimipkg-<ProductName>-Cimian<Action>.log, flat at the
        // root. The product name can itself contain hyphens, so key off the suffix.
        foreach (var action in new[] { "Preinstall", "Postinstall", "Uninstall" })
        {
            var suffix = $"-Cimian{action}.log";
            foreach (var file in SafeGetFiles(logsRoot, $"cimipkg-*{suffix}"))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(suffix, StringComparison.Ordinal))
                    continue;

                var product = name["cimipkg-".Length..^suffix.Length];
                if (product.Length == 0)
                    continue;

                TryMove(file, Path.Combine(logsRoot, "packages", product,
                    $"{action.ToLowerInvariant()}.log"));
            }
        }

        // Pre-move self-update installer logs.
        foreach (var file in SafeGetFiles(logsRoot, "selfupdate-*.log"))
        {
            TryMove(file, Path.Combine(logsRoot, "selfupdate", Path.GetFileName(file)));
        }

        // Pre-move msiexec / MSIX verbose logs, which used to be written into the
        // download cache alongside the payloads.
        foreach (var file in SafeGetFiles(cacheRoot, "*.log", recurse: true))
        {
            var name = Path.GetFileName(file);
            if (!name.Contains("_install", StringComparison.OrdinalIgnoreCase))
                continue;

            TryMove(file, Path.Combine(logsRoot, "installs", name));
        }

        // Files left loose at the root by third-party package scripts are deliberately
        // not touched here. This client does not own them and cannot tell a log from a
        // state marker by looking - VerifyHarmony's sentinel was named ".log" and lived
        // right here. They expire on age like anything else, which is the correct signal
        // for "nothing is writing this any more".
    }

    /// <summary>
    /// One package script's output, recovered from the sidecar file its MSI custom
    /// action wrote.
    /// </summary>
    public sealed record PackageScriptOutput(
        string Package, string Phase, IReadOnlyList<string> Lines, bool Truncated);

    /// <summary>
    /// A script that prints more than this has a problem of its own; the session log
    /// should not become unreadable because of it.
    /// </summary>
    public const int MaxPackageScriptLogLines = 500;

    private static readonly string[] ScriptPhases = { "preinstall", "postinstall", "uninstall" };

    /// <summary>
    /// Shared files that package scripts used to append to instead of printing to
    /// stdout. Matched by exact name, never by pattern: a file at the logs root can be
    /// a state marker owned by something else.
    /// </summary>
    /// <remarks>
    /// Every script that wrote these has since been changed to print to stdout, but the
    /// deployed payloads still carry the old version, so the file is recreated on every
    /// install until each package happens to be rebuilt. Draining it retires the file on
    /// every machine now, and puts the contents somewhere ReportMate actually reads.
    ///
    /// These are aggregates -- many packages appended to one file -- so the lines cannot
    /// be attributed to a single package the way a sidecar can.
    /// </remarks>
    private static readonly string[] LegacyAggregateLogs = { "installers.log" };

    /// <summary>
    /// Takes the output a package's install scripts produced and hands it back so the
    /// caller can put it in this session's log. The sidecar files are removed as they
    /// are read.
    /// </summary>
    /// <remarks>
    /// A cimipkg pre/postinstall script runs as an MSI custom action inside msiexec,
    /// not as a child of this process, so its stdout cannot be captured directly. The
    /// custom action redirects it to a file, and this drains that file: it is a handoff
    /// between two processes, not a log, and nothing should be left behind once its
    /// contents are in the session log where ReportMate will pick them up.
    ///
    /// Draining here rather than changing what the custom action writes means every
    /// package already deployed is covered without waiting for a rebuild — the same
    /// reason <see cref="RelocateLegacyArtifacts(string, string)"/> exists.
    /// </remarks>
    public static IReadOnlyList<PackageScriptOutput> CollectPackageScriptLogs()
        => CollectPackageScriptLogs(BaseLogsDir);

    /// <summary>
    /// Root is a parameter so this can be exercised against a temporary tree; the
    /// production call site passes the real one.
    /// </summary>
    internal static IReadOnlyList<PackageScriptOutput> CollectPackageScriptLogs(string logsRoot)
    {
        var collected = new List<PackageScriptOutput>();

        // Current layout: logs\packages\<Package>\<phase>.log
        var packagesRoot = Path.Combine(logsRoot, "packages");
        foreach (var dir in SafeGetDirectories(packagesRoot))
        {
            var package = Path.GetFileName(dir);
            foreach (var file in SafeGetFiles(dir, "*.log"))
            {
                if (TryDrainScriptLog(file, package, Path.GetFileNameWithoutExtension(file), out var drained))
                    collected.Add(drained);
            }

            TryRemoveEmptyDirectory(dir);
        }

        // Hand-rolled sidecars: <Package>-<phase>.log, flat at the logs root, written by
        // package scripts that opened a file themselves instead of printing to stdout.
        // Matched by suffix, never by "any .log here" — a file at this root can just as
        // easily be a state marker owned by something else, and deleting one of those
        // makes its package reinstall forever.
        foreach (var phase in ScriptPhases)
        {
            var suffix = $"-{phase}.log";
            foreach (var file in SafeGetFiles(logsRoot, $"*{suffix}"))
            {
                var name = Path.GetFileName(file);
                if (name.Length <= suffix.Length)
                    continue;

                if (TryDrainScriptLog(file, name[..^suffix.Length], phase, out var drained))
                    collected.Add(drained);
            }
        }

        // Shared aggregate files, matched by exact name.
        foreach (var name in LegacyAggregateLogs)
        {
            var file = Path.Combine(logsRoot, name);
            if (!File.Exists(file))
                continue;

            if (TryDrainScriptLog(file, name, "legacy", out var drained))
                collected.Add(drained);
        }

        return collected;
    }

    /// <summary>
    /// Reads a sidecar and removes it. Returns false when there was nothing worth
    /// logging, or when the file could not be read — in which case it is left for the
    /// next session rather than silently dropped.
    /// </summary>
    private static bool TryDrainScriptLog(
        string file, string package, string phase, out PackageScriptOutput output)
    {
        output = null!;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch
        {
            // Still held open by its writer. Leave it; this runs every session.
            return false;
        }

        var truncated = lines.Length > MaxPackageScriptLogLines;
        var kept = truncated ? lines[..MaxPackageScriptLogLines] : lines;

        // Delete only after a successful read. Losing the file without having recorded
        // what was in it is worse than draining the same content twice.
        try
        {
            File.Delete(file);
        }
        catch
        {
            return false;
        }

        // An empty sidecar means the script ran and printed nothing. The file is gone
        // either way; there is just nothing to say about it.
        var meaningful = kept.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (meaningful.Length == 0)
            return false;

        output = new PackageScriptOutput(package, phase, meaningful, truncated);
        return true;
    }

    private static IEnumerable<string> SafeGetDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetDirectories(directory)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void TryRemoveEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) &&
                Directory.GetFileSystemEntries(directory).Length == 0)
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Best-effort tidying; an empty directory harms nothing.
        }
    }

    private static IEnumerable<string> SafeGetFiles(string directory, string pattern, bool recurse = false)
    {
        try
        {
            if (!Directory.Exists(directory))
                return Array.Empty<string>();

            return Directory.GetFiles(directory, pattern,
                recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Moves a file, creating the destination directory and overwriting any previous
    /// copy. Best-effort: a file still held open by its writer is left for next session.
    /// </summary>
    private static void TryMove(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: true);
        }
        catch
        {
            // Ignore - relocation is best-effort and retried every session.
        }
    }

    /// <summary>
    /// Deletes files directly inside <paramref name="directory"/> that were last written
    /// before <paramref name="cutoff"/>. Not recursive: subdirectories are handled by
    /// their own rules.
    /// </summary>
    internal static void SweepExpiredFiles(string directory, DateTime cutoff)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.GetFiles(directory))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                    info.Delete();
            }
            catch
            {
                // A log still held open by another process throws here. Ignore it and
                // try again next session rather than failing the whole sweep.
            }
        }
    }

    /// <summary>
    /// Deletes immediate subdirectories of <paramref name="directory"/> whose most
    /// recently written file predates <paramref name="cutoff"/>. Used for trees keyed by
    /// something other than a date — per-package log directories, where the meaningful
    /// question is "has anything touched this package lately".
    /// </summary>
    internal static void SweepExpiredSubdirectories(string directory, DateTime cutoff)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var entry in Directory.GetDirectories(directory))
        {
            try
            {
                // No files at all means an empty leftover, which is also expired.
                var newest = NewestWriteTime(entry);
                if (newest is null || newest < cutoff)
                    Directory.Delete(entry, recursive: true);
            }
            catch
            {
                // Ignore - retention is best-effort.
            }
        }
    }

    /// <summary>
    /// Most recent LastWriteTime among the files under <paramref name="directory"/>, or
    /// null when it holds no files at all.
    /// </summary>
    private static DateTime? NewestWriteTime(string directory)
    {
        DateTime? newest = null;

        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            var written = new FileInfo(file).LastWriteTime;
            if (newest is null || written > newest)
                newest = written;
        }

        return newest;
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
    /// <summary>
    /// Writes the report files (items.json, sessions.json, events.json) without ending
    /// the session.
    /// </summary>
    /// <remarks>
    /// EndSession generates the reports and then closes the log files. Anything that
    /// needs the reports on disk AND needs the session log still open - postflight,
    /// which hands the reports to the reporting client and whose success or failure is
    /// worth recording - cannot use EndSession for that. Callers generate here, do the
    /// work, then end the session.
    /// </remarks>
    public void GenerateReportsNow()
    {
        try
        {
            GenerateReports();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to generate reports: {ex.Message}");
        }
    }

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

            // Generate loop_suppressed.json - LoopGuard suppressions surfaced for
            // dashboards. Skipped silently if no suppressions were registered.
            GenerateLoopSuppressedReport();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to generate reports: {ex.Message}");
        }
    }

    private List<LoopSuppressedReportItem> _currentLoopSuppressed = new();

    /// <summary>
    /// Sets the current run's loop-suppressed package list. Pass an empty list (or
    /// don't call) when LoopGuard isn't active. UpdateEngine populates this from
    /// <see cref="LoopGuard.GetSuppressedReport"/> before EndSession.
    /// </summary>
    public void SetCurrentLoopSuppressed(List<LoopSuppressedReportItem> items)
    {
        _currentLoopSuppressed = items ?? new List<LoopSuppressedReportItem>();
    }

    private void GenerateLoopSuppressedReport()
    {
        var path = Path.Combine(ReportsDir, "loop_suppressed.json");
        File.WriteAllText(path, JsonSerializer.Serialize(_currentLoopSuppressed, JsonOptions));
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
            var items = exporter.GenerateCurrentItemsFromPackagesInfo(cimianItems, _sessionId);

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
            var actedOnThisRun = !string.IsNullOrEmpty(pkg.ActionPerformed);

            records.Add(new Cimian.Core.Models.ItemRecord
            {
                Id = pkg.Name.ToLowerInvariant().Replace(" ", ""),
                ItemName = pkg.Name,
                DisplayName = displayName,
                ItemType = pkg.ItemType,
                CurrentStatus = normalizedStatus,
                LatestVersion = pkg.Version,
                InstalledVersion = pkg.InstalledVersion,
                // Stamp session id (yyyy-MM-dd-HHmm) only when this run touched the
                // item; status-checked items get an empty string so consumers can
                // filter to "what the last run actually did."
                LastSeenInSession = actedOnThisRun ? _sessionId : "",
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
