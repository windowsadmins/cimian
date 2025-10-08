using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Cimian.Status.Services
{
    public class LogService : ILogService
    {
        private readonly ILogger<LogService> _logger;
        private readonly IUpdateService _updateService;
        private readonly string _lastRunTimeFile;
        private readonly string _logsBaseDirectory;
        
        // Log tailing fields
        private FileSystemWatcher? _fileWatcher;
        private StreamReader? _logReader;
        private Timer? _pollTimer;
        private string? _currentLogFile;
        private long _lastPosition;
        private readonly object _lockObject = new();
        private CancellationTokenSource? _cancellationTokenSource;

        // Process output tailing fields
        private Process? _liveProcess;
        private bool _isProcessTailing;
        private readonly StringBuilder _processOutput = new();

        public event EventHandler<string>? LogLineReceived;
        public event EventHandler<int>? ProgressDetected;
        public bool IsLogTailing { get; private set; }

        public LogService(ILogger<LogService> logger, IUpdateService updateService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
            
            var programDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var managedInstallsPath = Path.Combine(programDataPath, "ManagedInstalls");
            
            _lastRunTimeFile = Path.Combine(managedInstallsPath, "LastRunTime.txt");
            _logsBaseDirectory = Path.Combine(managedInstallsPath, "logs");
        }

        public string GetLastRunTime()
        {
            try
            {
                if (File.Exists(_lastRunTimeFile))
                {
                    var content = File.ReadAllText(_lastRunTimeFile).Trim();
                    if (!string.IsNullOrEmpty(content))
                    {
                        return content;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read last run time from {File}", _lastRunTimeFile);
            }

            return "Never";
        }

        public void SaveLastRunTime()
        {
            try
            {
                var directory = Path.GetDirectoryName(_lastRunTimeFile);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }

                var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(_lastRunTimeFile, currentTime);
                
                _logger.LogInformation("Saved last run time: {Time}", currentTime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save last run time to {File}", _lastRunTimeFile);
            }
        }

        public void OpenLogsDirectory()
        {
            try
            {
                if (!Directory.Exists(_logsBaseDirectory))
                {
                    _logger.LogWarning("Logs directory does not exist: {Directory}", _logsBaseDirectory);
                    return;
                }

                // Find the most recent timestamped session directory
                var latestSessionDir = GetLatestLogDirectory();
                
                if (!string.IsNullOrEmpty(latestSessionDir))
                {
                    Process.Start("explorer.exe", latestSessionDir);
                    _logger.LogInformation("Opened latest log session: {Directory}", latestSessionDir);
                }
                else
                {
                    Process.Start("explorer.exe", _logsBaseDirectory);
                    _logger.LogInformation("Opened logs base directory: {Directory}", _logsBaseDirectory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error opening logs directory");
            }
        }

        public string GetLatestLogDirectory()
        {
            try
            {
                if (!Directory.Exists(_logsBaseDirectory))
                {
                    return string.Empty;
                }

                // Find directories matching the timestamped format: YYYY-MM-DD-HHMMss
                var sessionDirectories = Directory.GetDirectories(_logsBaseDirectory)
                    .Where(d => 
                    {
                        var dirName = Path.GetFileName(d);
                        return dirName.Length == 17 && 
                               dirName[4] == '-' && 
                               dirName[7] == '-' && 
                               dirName[10] == '-';
                    })
                    .OrderByDescending(d => Path.GetFileName(d))
                    .ToArray();

                return sessionDirectories.FirstOrDefault() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error finding latest log directory");
                return string.Empty;
            }
        }

        public string GetCurrentLogFilePath()
        {
            var latestLogDir = GetLatestLogDirectory();
            if (string.IsNullOrEmpty(latestLogDir))
                return string.Empty;

            var installLogPath = Path.Combine(latestLogDir, "install.log");
            return File.Exists(installLogPath) ? installLogPath : string.Empty;
        }

        public Task StartLogTailingAsync()
        {
            if (IsLogTailing)
                return Task.CompletedTask;

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                IsLogTailing = true;

                // First priority: Try to tail a running managedsoftwareupdate.exe process
                if (TryAttachToRunningProcess())
                {
                    _logger.LogInformation("Started tailing live managedsoftwareupdate.exe process output");
                    return Task.CompletedTask;
                }

                // If we're in live process mode, don't fall back to file monitoring
                if (_isProcessTailing)
                {
                    _logger.LogInformation("Live process monitoring active");
                    return Task.CompletedTask;
                }

                // Fallback: Tail the log file as before
                _currentLogFile = GetCurrentLogFilePath();
                if (string.IsNullOrEmpty(_currentLogFile))
                {
                    _logger.LogWarning("No current log file found for tailing");
                    LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] No current log file found - waiting for process...");
                    IsLogTailing = false;
                    return Task.CompletedTask;
                }

                // Read existing content if file exists
                if (File.Exists(_currentLogFile))
                {
                    _lastPosition = 0; // Start from beginning to show existing content
                    // Read existing content immediately
                    ReadNewLogContent();
                }

                // Set up file system watcher for the logs directory
                var logDirectory = Path.GetDirectoryName(_currentLogFile);
                if (!string.IsNullOrEmpty(logDirectory))
                {
                    _fileWatcher = new FileSystemWatcher(logDirectory)
                    {
                        Filter = "install.log",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    _fileWatcher.Changed += OnLogFileChanged;
                    _fileWatcher.EnableRaisingEvents = true;
                }

                // Poll more frequently (250ms) for faster, more responsive log updates
                _pollTimer = new Timer(PollLogFile, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

                _logger.LogInformation("Started log tailing for: {LogFile}", _currentLogFile);
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start log tailing");
                IsLogTailing = false;
                return Task.CompletedTask;
            }
        }

        public Task StopLogTailingAsync()
        {
            if (!IsLogTailing)
                return Task.CompletedTask;

            try
            {
                IsLogTailing = false;

                _cancellationTokenSource?.Cancel();
                
                _fileWatcher?.Dispose();
                _fileWatcher = null;

                _pollTimer?.Dispose();
                _pollTimer = null;

                lock (_lockObject)
                {
                    _logReader?.Dispose();
                    _logReader = null;
                }

                // Stop process tailing if active
                if (_isProcessTailing && _liveProcess != null)
                {
                    try
                    {
                        _liveProcess.OutputDataReceived -= OnProcessOutputReceived;
                        _liveProcess.ErrorDataReceived -= OnProcessErrorReceived;
                        if (!_liveProcess.HasExited)
                        {
                            _liveProcess.CancelOutputRead();
                            _liveProcess.CancelErrorRead();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error stopping process output monitoring");
                    }
                    _liveProcess = null;
                    _isProcessTailing = false;
                }

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _logger.LogInformation("Stopped log tailing");
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping log tailing");
                return Task.CompletedTask;
            }
        }

        private void OnLogFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed)
            {
                ReadNewLogContent();
            }
        }

        private void PollLogFile(object? state)
        {
            ReadNewLogContent();
        }

        private void ReadNewLogContent()
        {
            if (!IsLogTailing || string.IsNullOrEmpty(_currentLogFile))
                return;

            try
            {
                lock (_lockObject)
                {
                    if (!File.Exists(_currentLogFile))
                    {
                        // Check if there's a new log file
                        var newLogFile = GetCurrentLogFilePath();
                        if (!string.IsNullOrEmpty(newLogFile) && newLogFile != _currentLogFile)
                        {
                            _currentLogFile = newLogFile;
                            _lastPosition = 0;
                            _logger.LogInformation("Switched to new log file: {LogFile}", _currentLogFile);
                        }
                        else
                        {
                            return;
                        }
                    }

                    var fileInfo = new FileInfo(_currentLogFile);
                    if (fileInfo.Length <= _lastPosition)
                        return;

                    using var fileStream = new FileStream(_currentLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fileStream.Seek(_lastPosition, SeekOrigin.Begin);

                    using var reader = new StreamReader(fileStream);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        LogLineReceived?.Invoke(this, line);
                        
                        // Try to detect progress from log content
                        TryDetectProgressFromLog(line);
                    }

                    _lastPosition = fileStream.Position;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading log file content");
            }
        }

        /// <summary>
        /// Attempts to attach to a running managedsoftwareupdate.exe process to capture live output
        /// </summary>
        private bool TryAttachToRunningProcess()
        {
            try
            {
                var processes = Process.GetProcessesByName("managedsoftwareupdate");
                if (processes.Length == 0)
                {
                    // No running process - monitor for new processes starting
                    LogLineReceived?.Invoke(this, "No running managedsoftwareupdate.exe process found");
                    LogLineReceived?.Invoke(this, "Monitoring for process start...");
                    
                    // Start monitoring for new processes
                    StartProcessMonitoring();
                    return false; // Return false to also start file monitoring as backup
                }

                // Use the first (most likely only) process
                _liveProcess = processes[0];
                
                LogLineReceived?.Invoke(this, $"Found running managedsoftwareupdate.exe process (PID: {_liveProcess.Id})");
                LogLineReceived?.Invoke(this, "NOTE: Progress bar updates require process started with --show-status flag");
                LogLineReceived?.Invoke(this, "Monitoring via log file for detailed progress...");
                
                // Monitor the existing process without trying to start another one
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _liveProcess.WaitForExitAsync();
                        LogLineReceived?.Invoke(this, $"Process completed with exit code: {_liveProcess.ExitCode}");
                        _isProcessTailing = false;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error monitoring process exit");
                    }
                });
                
                // Return false to continue with file monitoring setup
                // We want to tail the log file even when attaching to existing process
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to attach to running process");
                return false;
            }
        }

        /// <summary>
        /// Attempts to start a new managedsoftwareupdate.exe process with live output capture
        /// </summary>
        private bool TryStartProcessWithCapture()
        {
            try
            {
                if (!_updateService.IsExecutableFound())
                {
                    LogLineReceived?.Invoke(this, "ERROR: managedsoftwareupdate.exe not found");
                    return false;
                }

                LogLineReceived?.Invoke(this, "Starting managedsoftwareupdate.exe with live output capture (-vv)...");

                _liveProcess = _updateService.LaunchWithOutputCapture(
                    output => {
                        var timestamp = DateTime.Now.ToString("HH:mm:ss");
                        LogLineReceived?.Invoke(this, $"[{timestamp}] {output}");
                    },
                    error => {
                        var timestamp = DateTime.Now.ToString("HH:mm:ss");
                        LogLineReceived?.Invoke(this, $"[{timestamp}] ERROR: {error}");
                    }
                );

                if (_liveProcess != null)
                {
                    _isProcessTailing = true;
                    LogLineReceived?.Invoke(this, $"Successfully started process with live output capture (PID: {_liveProcess.Id})");
                    
                    // Monitor for process exit
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _liveProcess.WaitForExitAsync();
                            LogLineReceived?.Invoke(this, $"Process completed with exit code: {_liveProcess.ExitCode}");
                            _isProcessTailing = false;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error monitoring process exit");
                        }
                    });
                    
                    return true;
                }
                
                LogLineReceived?.Invoke(this, "Failed to start process with output capture");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start process with output capture");
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Manually start a process with live monitoring for testing
        /// </summary>
        public Task<bool> StartProcessWithLiveMonitoringAsync()
        {
            if (_isProcessTailing)
            {
                LogLineReceived?.Invoke(this, "Process monitoring already active");
                return Task.FromResult(true);
            }

            LogLineReceived?.Invoke(this, "Manually starting process with live monitoring...");
            
            var result = TryStartProcessWithCapture();
            if (result)
            {
                LogLineReceived?.Invoke(this, "Live process monitoring started successfully");
            }
            else
            {
                LogLineReceived?.Invoke(this, "Failed to start live process monitoring");
            }
            
            return Task.FromResult(result);
        }

        /// <summary>
        /// Starts monitoring for new managedsoftwareupdate.exe processes to capture their output
        /// </summary>
        private void StartProcessMonitoring()
        {
            // Start a background task to monitor for process launches
            _ = Task.Run(async () =>
            {
                try
                {
                    while (IsLogTailing && !_cancellationTokenSource!.Token.IsCancellationRequested)
                    {
                        var processes = Process.GetProcessesByName("managedsoftwareupdate");
                        foreach (var process in processes)
                        {
                            if (!_isProcessTailing)
                            {
                                // Try to capture output if this is a newly started process
                                if (TryCaptureFutureProcessOutput(process))
                                {
                                    break; // Successfully attached to one process
                                }
                            }
                        }
                        
                        await Task.Delay(500, _cancellationTokenSource.Token); // Check every 500ms for faster detection
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error in process monitoring");
                }
            });
        }

        private int _lastMonitoredProcessId = -1;

        /// <summary>
        /// Attempts to capture output from newly started managedsoftwareupdate.exe processes
        /// </summary>
        private bool TryCaptureFutureProcessOutput(Process process)
        {
            try
            {
                // We can only capture output if we start the process ourselves
                // For existing processes, we'll provide helpful info and fall back to file tailing
                
                if (process.HasExited)
                    return false;

                // Only log once per process to avoid spam
                if (_lastMonitoredProcessId != process.Id)
                {
                    _lastMonitoredProcessId = process.Id;
                    LogLineReceived?.Invoke(this, $"Found running process PID {process.Id}");
                    LogLineReceived?.Invoke(this, "Monitoring via log file...");
                }
                
                return false; // Return false to fall back to file tailing
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not capture output from process {ProcessId}", process.Id);
                return false;
            }
        }

        /// <summary>
        /// Event handler for process stdout output
        /// </summary>
        private void OnProcessOutputReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogLineReceived?.Invoke(this, $"[{timestamp}] {e.Data}");
            }
        }

        /// <summary>
        /// Event handler for process stderr output
        /// </summary>
        private void OnProcessErrorReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogLineReceived?.Invoke(this, $"[{timestamp}] ERROR: {e.Data}");
            }
        }

        private int _totalItems = 0;
        private int _processedItems = 0;

        /// <summary>
        /// Attempts to detect progress percentage from log file content patterns
        /// </summary>
        private void TryDetectProgressFromLog(string logLine)
        {
            try
            {
                // Pattern: "INSTALLING/UPDATING (21 items)" or "INSTALLING (5 items)"
                if ((logLine.Contains("INSTALLING/UPDATING") || logLine.Contains("INSTALLING (")) && logLine.Contains("items)"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(logLine, @"\((\d+)\s+items?\)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int items))
                    {
                        _totalItems = items;
                        _processedItems = 0;
                        _logger.LogInformation("Detected {TotalItems} items to install/update", _totalItems);
                        ProgressDetected?.Invoke(this, 5); // Start at 5% when we know total
                    }
                }
                // Pattern: "Processing install of item: Chrome source: from managed_updates"
                else if (logLine.Contains("Processing install of item:") || 
                         logLine.Contains("Installing item:") ||
                         logLine.Contains("INFO  Installing item:"))
                {
                    _processedItems++;
                    if (_totalItems > 0)
                    {
                        // Calculate progress: 5% start + (90% * items_done / total_items)
                        int progress = 5 + (int)((90.0 * _processedItems) / _totalItems);
                        progress = Math.Min(progress, 95); // Cap at 95% until completion
                        _logger.LogDebug("Progress: {ProcessedItems}/{TotalItems} = {Progress}%", _processedItems, _totalItems, progress);
                        ProgressDetected?.Invoke(this, progress);
                    }
                    else
                    {
                        // Don't know total, just show indeterminate progress
                        _logger.LogDebug("Processing item {ProcessedItems} (total unknown)", _processedItems);
                    }
                }
                // Pattern: "populateFromCurrentManifests completed" or final completion markers
                else if (logLine.Contains("populateFromCurrentManifests completed") ||
                         logLine.Contains("All operations completed successfully") ||
                         logLine.Contains("INSTALLATION COMPLETE"))
                {
                    _logger.LogInformation("Installation completed");
                    ProgressDetected?.Invoke(this, 100); // Completion marker
                    _totalItems = 0;
                    _processedItems = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error detecting progress from log line");
            }
        }
    }
}
