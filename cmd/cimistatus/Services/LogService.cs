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

                // Start with existing content if file exists
                if (File.Exists(_currentLogFile))
                {
                    _lastPosition = new FileInfo(_currentLogFile).Length;
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

                // Also poll periodically as backup
                _pollTimer = new Timer(PollLogFile, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

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
                    // No running process - try to start one with output capture
                    LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] No running managedsoftwareupdate.exe process found");
                    LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Attempting to start process with live output capture...");
                    
                    return TryStartProcessWithCapture();
                }

                // Use the first (most likely only) process
                _liveProcess = processes[0];
                
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Found running managedsoftwareupdate.exe process (PID: {_liveProcess.Id})");
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Note: Cannot capture output from already running process");
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Use 'Run Now' button for live output capture, or monitor log file...");
                
                // Fall back to file monitoring since we can't capture output from existing process
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
                    LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] ERROR: managedsoftwareupdate.exe not found");
                    return false;
                }

                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Starting managedsoftwareupdate.exe with live output capture (-vv)...");

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
                    LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Successfully started process with live output capture (PID: {_liveProcess.Id})");
                    
                    // Monitor for process exit
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _liveProcess.WaitForExitAsync();
                            var timestamp = DateTime.Now.ToString("HH:mm:ss");
                            LogLineReceived?.Invoke(this, $"[{timestamp}] Process completed with exit code: {_liveProcess.ExitCode}");
                            _isProcessTailing = false;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error monitoring process exit");
                        }
                    });
                    
                    return true;
                }
                
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Failed to start process with output capture");
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
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Process monitoring already active");
                return Task.FromResult(true);
            }

            LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Manually starting process with live monitoring...");
            
            var result = TryStartProcessWithCapture();
            if (result)
            {
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Live process monitoring started successfully");
            }
            else
            {
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Failed to start live process monitoring");
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
                        
                        await Task.Delay(2000, _cancellationTokenSource.Token); // Check every 2 seconds
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

                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Monitoring process PID {process.Id}");
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Note: Live console output capture requires launching the process with cimistatus");
                LogLineReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] Falling back to log file monitoring...");
                
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
    }
}
