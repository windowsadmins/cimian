using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Cimian.Status.Models;

namespace Cimian.Status.Services
{
    /// <summary>
    /// Service that monitors the events.jsonl file for real-time progress updates
    /// from managedsoftwareupdate.exe without needing to hook into process output.
    /// </summary>
    public class EventStreamService : IEventStreamService
    {
        private readonly ILogger<EventStreamService> _logger;
        private readonly string _managedInstallsPath;
        private readonly string _reportsPath;
        
        private FileSystemWatcher? _directoryWatcher;
        private FileSystemWatcher? _fileWatcher;
        private string? _currentEventsFile;
        private long _lastFilePosition = 0;

        public event EventHandler<InstallProgressEvent>? ProgressEventReceived;
        public event EventHandler<InstallStatusEvent>? StatusEventReceived;
        public event EventHandler<SessionStartEvent>? SessionStarted;
        public event EventHandler<SessionEndEvent>? SessionEnded;

        public EventStreamService(ILogger<EventStreamService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // Default to standard Cimian location
            _managedInstallsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ManagedInstalls"
            );
            // IMPORTANT: managedsoftwareupdate writes to "logs" directory, not "reports"
            _reportsPath = Path.Combine(_managedInstallsPath, "logs");
        }

        /// <summary>
        /// Starts monitoring for new sessions and events
        /// </summary>
        public void StartMonitoring()
        {
            try
            {
                // Ensure reports directory exists
                if (!Directory.Exists(_reportsPath))
                {
                    Directory.CreateDirectory(_reportsPath);
                }

                // Watch for new session directories being created
                _directoryWatcher = new FileSystemWatcher(_reportsPath)
                {
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                _directoryWatcher.Created += OnSessionDirectoryCreated;
                _directoryWatcher.Renamed += OnSessionDirectoryRenamed;

                _logger.LogInformation("Started monitoring for new sessions in {ReportsPath}", _reportsPath);

                // Check if there's already an active session
                CheckForActiveSession();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start monitoring events directory");
            }
        }

        /// <summary>
        /// Stops monitoring
        /// </summary>
        public void StopMonitoring()
        {
            _directoryWatcher?.Dispose();
            _directoryWatcher = null;
            
            StopTailingCurrentSession();
            
            _logger.LogInformation("Stopped monitoring events directory");
        }

        /// <summary>
        /// Checks for an active session directory and starts tailing it
        /// </summary>
        private void CheckForActiveSession()
        {
            try
            {
                if (!Directory.Exists(_reportsPath))
                    return;

                // Get all session directories sorted by name (which is timestamp-based)
                var sessionDirs = Directory.GetDirectories(_reportsPath);
                if (sessionDirs.Length == 0)
                    return;

                // Sort to get the most recent
                Array.Sort(sessionDirs);
                var latestSession = sessionDirs[^1]; // Last element

                // Check if session.json exists and is not marked as completed
                var sessionJsonPath = Path.Combine(latestSession, "session.json");
                if (File.Exists(sessionJsonPath))
                {
                    var sessionJson = File.ReadAllText(sessionJsonPath);
                    var sessionDoc = JsonDocument.Parse(sessionJson);
                    
                    if (sessionDoc.RootElement.TryGetProperty("status", out var statusProp))
                    {
                        var status = statusProp.GetString();
                        if (status == "running")
                        {
                            _logger.LogInformation("Found active session: {SessionDir}", Path.GetFileName(latestSession));
                            StartTailingSession(latestSession);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking for active session");
            }
        }

        /// <summary>
        /// Called when a new session directory is created
        /// </summary>
        private void OnSessionDirectoryCreated(object sender, FileSystemEventArgs e)
        {
            _logger.LogInformation("New session directory detected: {Path}", e.FullPath);
            
            // Small delay to allow session.json to be created
            Task.Delay(100).ContinueWith(_ => StartTailingSession(e.FullPath));
        }

        private void OnSessionDirectoryRenamed(object sender, RenamedEventArgs e)
        {
            _logger.LogInformation("Session directory renamed: {Path}", e.FullPath);
            Task.Delay(100).ContinueWith(_ => StartTailingSession(e.FullPath));
        }

        /// <summary>
        /// Starts tailing the events.jsonl file in a session directory
        /// </summary>
        private void StartTailingSession(string sessionDir)
        {
            try
            {
                // Stop any existing tail operation
                StopTailingCurrentSession();

                var eventsFile = Path.Combine(sessionDir, "events.jsonl");
                
                // Wait for events file to be created (up to 5 seconds)
                var waitCount = 0;
                while (!File.Exists(eventsFile) && waitCount < 50)
                {
                    Thread.Sleep(100);
                    waitCount++;
                }

                if (!File.Exists(eventsFile))
                {
                    _logger.LogWarning("events.jsonl not found in session directory: {SessionDir}", sessionDir);
                    return;
                }

                _currentEventsFile = eventsFile;
                _lastFilePosition = 0;

                // Notify that a session has started
                var sessionId = Path.GetFileName(sessionDir);
                SessionStarted?.Invoke(this, new SessionStartEvent 
                { 
                    SessionId = sessionId,
                    SessionPath = sessionDir 
                });

                // Start watching the events file for changes
                _fileWatcher = new FileSystemWatcher(sessionDir, "events.jsonl")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += OnEventsFileChanged;

                // Read any existing content
                ProcessNewEvents();

                _logger.LogInformation("Started tailing events file: {EventsFile}", eventsFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting to tail session directory: {SessionDir}", sessionDir);
            }
        }

        /// <summary>
        /// Stops tailing the current session
        /// </summary>
        private void StopTailingCurrentSession()
        {
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _currentEventsFile = null;
            _lastFilePosition = 0;
        }

        /// <summary>
        /// Called when the events.jsonl file changes
        /// </summary>
        private void OnEventsFileChanged(object sender, FileSystemEventArgs e)
        {
            ProcessNewEvents();
        }

        /// <summary>
        /// Reads new lines from the events file and processes them
        /// </summary>
        private void ProcessNewEvents()
        {
            if (string.IsNullOrEmpty(_currentEventsFile))
                return;

            try
            {
                using var fileStream = new FileStream(
                    _currentEventsFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );

                // Seek to last known position
                fileStream.Seek(_lastFilePosition, SeekOrigin.Begin);

                using var reader = new StreamReader(fileStream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    ProcessEventLine(line);
                }

                // Update position for next read
                _lastFilePosition = fileStream.Position;
            }
            catch (IOException ex)
            {
                // File might be locked, retry on next change
                _logger.LogDebug(ex, "Temporary IO error reading events file");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing events file");
            }
        }

        /// <summary>
        /// Processes a single JSON line from events.jsonl
        /// </summary>
        private void ProcessEventLine(string jsonLine)
        {
            if (string.IsNullOrWhiteSpace(jsonLine))
                return;

            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;

                // Extract common fields
                var eventType = root.GetPropertyOrDefault("event_type", "");
                var status = root.GetPropertyOrDefault("status", "");
                var message = root.GetPropertyOrDefault("message", "");
                
                // Package name can be in multiple places - try them all
                var packageName = root.GetPropertyOrDefault("package_name", "");
                if (string.IsNullOrEmpty(packageName))
                {
                    packageName = root.GetPropertyOrDefault("package", "");
                }
                if (string.IsNullOrEmpty(packageName) && root.TryGetProperty("context", out var contextProp))
                {
                    // Check context object for "item" field (managedsoftwareupdate uses this)
                    packageName = contextProp.GetPropertyOrDefault("item", "");
                }
                
                var packageId = root.GetPropertyOrDefault("package_id", "");
                if (string.IsNullOrEmpty(packageId) && packageName != "")
                {
                    packageId = packageName; // Use package name as ID if no explicit ID
                }
                
                // Handle progress field
                int? progress = null;
                if (root.TryGetProperty("progress", out var progressProp) && progressProp.ValueKind == JsonValueKind.Number)
                {
                    progress = progressProp.GetInt32();
                }

                // Determine if this is a progress event or status event
                if (progress.HasValue && !string.IsNullOrEmpty(packageName))
                {
                    // This is a progress event
                    ProgressEventReceived?.Invoke(this, new InstallProgressEvent
                    {
                        PackageName = packageName,
                        PackageId = packageId,
                        Progress = progress.Value,
                        Message = message,
                        EventType = eventType,
                        Status = status
                    });
                }
                
                // Always send status events for significant changes
                if (!string.IsNullOrEmpty(status) && (status == "completed" || status == "failed" || status == "error" || status == "started"))
                {
                    var isError = status == "failed" || status == "error" || root.TryGetProperty("error", out _);
                    var errorMessage = root.GetPropertyOrDefault("error", "");
                    
                    // Also check context.error and context.error_details
                    if (string.IsNullOrEmpty(errorMessage) && root.TryGetProperty("context", out var ctxProp))
                    {
                        errorMessage = ctxProp.GetPropertyOrDefault("error", "");
                        if (string.IsNullOrEmpty(errorMessage))
                        {
                            errorMessage = ctxProp.GetPropertyOrDefault("error_details", "");
                        }
                    }

                    StatusEventReceived?.Invoke(this, new InstallStatusEvent
                    {
                        PackageName = packageName,
                        PackageId = packageId,
                        Message = message,
                        EventType = eventType,
                        Status = status,
                        IsError = isError,
                        ErrorMessage = errorMessage
                    });

                    // Check if this is a session-ending event
                    if (status == "completed" && eventType == "session")
                    {
                        SessionEnded?.Invoke(this, new SessionEndEvent
                        {
                            Success = true,
                            Message = message
                        });
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse event JSON: {Line}", jsonLine);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing event line");
            }
        }

        public void Dispose()
        {
            StopMonitoring();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Extension methods for JSON parsing
    /// </summary>
    public static class JsonExtensions
    {
        public static string GetPropertyOrDefault(this JsonElement element, string propertyName, string defaultValue = "")
        {
            return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() ?? defaultValue
                : defaultValue;
        }
    }
}
