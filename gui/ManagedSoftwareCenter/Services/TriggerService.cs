// TriggerService.cs - Triggers managedsoftwareupdate via CimianWatcher service
// Uses flag-file IPC so the SYSTEM service launches the process — no UAC needed.

using System.IO;
using Microsoft.Extensions.Logging;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for triggering managedsoftwareupdate operations via CimianWatcher.
/// Writes a flag file with custom arguments; the CimianWatcher service (running
/// as SYSTEM) picks it up and launches managedsoftwareupdate elevated — no UAC
/// prompt required, even for standard users.
/// </summary>
public class TriggerService : ITriggerService, IDisposable
{
    private const string BootstrapFlagFile = @"C:\ProgramData\ManagedInstalls\.cimian.bootstrap";
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<TriggerService>? _logger;
    private bool _isOperationRunning;

    public bool IsOperationRunning => _isOperationRunning;

    public event EventHandler<bool>? OperationStatusChanged;

    public TriggerService(ILogger<TriggerService>? logger = null)
    {
        _logger = logger;
        _logger?.LogInformation("TriggerService initialized — flag-file IPC via CimianWatcher");
    }

    /// <inheritdoc />
    public async Task TriggerCheckAsync()
    {
        _logger?.LogInformation("Triggering check via CimianWatcher");
        await TriggerViaFlagFileAsync("--checkonly --show-status -vv");
    }

    /// <inheritdoc />
    public async Task TriggerInstallAsync()
    {
        _logger?.LogInformation("Triggering install via CimianWatcher");
        await TriggerViaFlagFileAsync("--installonly --show-status -vv");
    }

    /// <inheritdoc />
    public Task TriggerStopAsync()
    {
        // With the flag-file approach we don't hold a process handle.
        // The best we can do is delete the flag file if the service hasn't
        // picked it up yet, or rely on managedsoftwareupdate's own
        // graceful-shutdown logic.
        try
        {
            if (File.Exists(BootstrapFlagFile))
            {
                File.Delete(BootstrapFlagFile);
                _logger?.LogInformation("Deleted bootstrap flag file (stop requested)");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not delete flag file during stop");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the bootstrap flag file with custom arguments and waits for
    /// CimianWatcher to consume it (indicating it launched MSU).
    /// </summary>
    private async Task TriggerViaFlagFileAsync(string arguments)
    {
        _isOperationRunning = true;
        OperationStatusChanged?.Invoke(this, true);

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var content = $"Bootstrap triggered at: {timestamp}\nSource: ManagedSoftwareCenter\nArgs: {arguments}\n";

            // Ensure the directory exists (it should, but be safe)
            var dir = Path.GetDirectoryName(BootstrapFlagFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(BootstrapFlagFile, content);
            _logger?.LogInformation("Wrote bootstrap flag file with args: {Args}", arguments);

            // Wait for CimianWatcher to consume the flag file
            var elapsed = TimeSpan.Zero;
            while (File.Exists(BootstrapFlagFile) && elapsed < ServiceTimeout)
            {
                await Task.Delay(PollInterval);
                elapsed += PollInterval;
            }

            if (File.Exists(BootstrapFlagFile))
            {
                _logger?.LogError("CimianWatcher did not consume flag file within {Timeout}s — is the service running?",
                    ServiceTimeout.TotalSeconds);
                
                // Clean up the stale flag file
                try { File.Delete(BootstrapFlagFile); } catch { }

                _isOperationRunning = false;
                OperationStatusChanged?.Invoke(this, false);
                throw new InvalidOperationException(
                    "CimianWatcher service did not respond. Ensure the service is running: sc query CimianWatcher");
            }

            _logger?.LogInformation("CimianWatcher consumed flag file — managedsoftwareupdate is running");

            // The service has picked up the flag file and launched MSU.
            // From here, completion is signaled by the progress pipe
            // (ProgressMessageType.Complete) or the InstallInfo.yaml
            // FileSystemWatcher — same as before.
        }
        catch (InvalidOperationException)
        {
            throw; // re-throw the service-not-running error
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to trigger via flag file");
            _isOperationRunning = false;
            OperationStatusChanged?.Invoke(this, false);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
