using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cimian.Status.Services
{
    public class LogService : ILogService
    {
        private readonly ILogger<LogService> _logger;
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

        public event EventHandler<string>? LogLineReceived;
        public bool IsLogTailing { get; private set; }

        public LogService(ILogger<LogService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
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

        public async Task StartLogTailingAsync()
        {
            if (IsLogTailing)
                return;

            try
            {
                _currentLogFile = GetCurrentLogFilePath();
                if (string.IsNullOrEmpty(_currentLogFile))
                {
                    _logger.LogWarning("No current log file found for tailing");
                    return;
                }

                _cancellationTokenSource = new CancellationTokenSource();
                IsLogTailing = true;

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start log tailing");
                IsLogTailing = false;
            }
        }

        public async Task StopLogTailingAsync()
        {
            if (!IsLogTailing)
                return;

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

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _logger.LogInformation("Stopped log tailing");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping log tailing");
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
    }
}
