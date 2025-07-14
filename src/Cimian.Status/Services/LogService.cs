using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Cimian.Status.Services
{
    public class LogService : ILogService
    {
        private readonly ILogger<LogService> _logger;
        private readonly string _lastRunTimeFile;
        private readonly string _logsBaseDirectory;

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

                // Find directories matching the timestamped format: YYYY-MM-DD-HHMMSS
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
    }
}
