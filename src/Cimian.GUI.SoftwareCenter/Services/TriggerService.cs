// TriggerService.cs - Triggers managedsoftwareupdate via cimiwatcher
// Uses trigger file mechanism similar to Munki's launchd trigger files

using System.IO;
using Microsoft.Extensions.Logging;

namespace Cimian.GUI.SoftwareCenter.Services;

/// <summary>
/// Service for triggering managedsoftwareupdate operations via cimiwatcher
/// Creates trigger files that cimiwatcher monitors and responds to
/// </summary>
public class TriggerService : ITriggerService, IDisposable
{
    private const string TriggerDirectory = @"C:\ProgramData\ManagedInstalls";
    private const string CheckTriggerFile = ".trigger_updatecheck";
    private const string InstallTriggerFile = ".trigger_install";
    private const string StopTriggerFile = ".trigger_stop";
    private const string RunningLockFile = ".managedsoftwareupdate_running";

    private readonly ILogger<TriggerService>? _logger;
    private FileSystemWatcher? _watcher;
    private bool _isOperationRunning;

    public bool IsOperationRunning => _isOperationRunning;

    public event EventHandler<bool>? OperationStatusChanged;

    public TriggerService(ILogger<TriggerService>? logger = null)
    {
        _logger = logger;
        StartWatchingLockFile();
        CheckInitialLockState();
    }

    /// <inheritdoc />
    public async Task TriggerCheckAsync()
    {
        _logger?.LogInformation("Triggering update check");
        await CreateTriggerFileAsync(CheckTriggerFile);
    }

    /// <inheritdoc />
    public async Task TriggerInstallAsync()
    {
        _logger?.LogInformation("Triggering installation");
        await CreateTriggerFileAsync(InstallTriggerFile);
    }

    /// <inheritdoc />
    public async Task TriggerStopAsync()
    {
        _logger?.LogInformation("Triggering stop");
        await CreateTriggerFileAsync(StopTriggerFile);
    }

    private async Task CreateTriggerFileAsync(string triggerFileName)
    {
        try
        {
            // Ensure directory exists
            if (!Directory.Exists(TriggerDirectory))
            {
                Directory.CreateDirectory(TriggerDirectory);
            }

            var triggerPath = Path.Combine(TriggerDirectory, triggerFileName);
            
            // Write timestamp to trigger file
            var content = DateTime.UtcNow.ToString("O");
            await File.WriteAllTextAsync(triggerPath, content);

            _logger?.LogDebug("Created trigger file: {Path}", triggerPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create trigger file: {FileName}", triggerFileName);
            throw;
        }
    }

    private void StartWatchingLockFile()
    {
        try
        {
            if (!Directory.Exists(TriggerDirectory))
            {
                _logger?.LogDebug("Trigger directory does not exist yet, cannot watch lock file");
                return;
            }

            _watcher = new FileSystemWatcher(TriggerDirectory)
            {
                Filter = RunningLockFile,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnLockFileCreated;
            _watcher.Deleted += OnLockFileDeleted;

            _logger?.LogDebug("Started watching for lock file");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start watching lock file");
        }
    }

    private void CheckInitialLockState()
    {
        var lockPath = Path.Combine(TriggerDirectory, RunningLockFile);
        var isRunning = File.Exists(lockPath);

        if (_isOperationRunning != isRunning)
        {
            _isOperationRunning = isRunning;
            _logger?.LogDebug("Initial operation running state: {IsRunning}", isRunning);
            OperationStatusChanged?.Invoke(this, isRunning);
        }
    }

    private void OnLockFileCreated(object sender, FileSystemEventArgs e)
    {
        _logger?.LogDebug("Lock file created - operation started");
        _isOperationRunning = true;
        OperationStatusChanged?.Invoke(this, true);
    }

    private void OnLockFileDeleted(object sender, FileSystemEventArgs e)
    {
        _logger?.LogDebug("Lock file deleted - operation completed");
        _isOperationRunning = false;
        OperationStatusChanged?.Invoke(this, false);
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnLockFileCreated;
            _watcher.Deleted -= OnLockFileDeleted;
            _watcher.Dispose();
            _watcher = null;
        }

        GC.SuppressFinalize(this);
    }
}
