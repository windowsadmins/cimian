// TriggerService.cs - Triggers managedsoftwareupdate directly with --show-status
// Always runs managedsoftwareupdate directly so progress is reported back to the GUI

using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for triggering managedsoftwareupdate operations.
/// Always runs managedsoftwareupdate directly with --show-status so progress 
/// is reported back to the GUI via TCP port 19847.
/// </summary>
public class TriggerService : ITriggerService, IDisposable
{
    /// <summary>
    /// Paths to search for managedsoftwareupdate.exe (in order of preference)
    /// </summary>
    private static readonly string[] ExecutablePaths =
    [
        // Installed location
        @"C:\Program Files\Cimian\managedsoftwareupdate.exe",
        // Development build locations (relative to ManagedSoftwareCenter bin output)
        // From: apps/ManagedSoftwareCenter/bin/Debug/net8.0-windows10.0.17763.0/win-x64/
        // To: cli/managedsoftwareupdate/bin/Debug/net10.0-windows/win-x64/
        // Need 6 levels up to reach CimianTools root
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "cli", "managedsoftwareupdate", "bin", "Debug", "net10.0-windows", "win-x64", "managedsoftwareupdate.exe"),
        // Release build locations  
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "release", "x64", "managedsoftwareupdate.exe"),
        // Output directory
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "output", "bin", "managedsoftwareupdate.exe"),
    ];

    private readonly ILogger<TriggerService>? _logger;
    private bool _isOperationRunning;
    private Process? _currentProcess;

    public bool IsOperationRunning => _isOperationRunning;

    public event EventHandler<bool>? OperationStatusChanged;

    public TriggerService(ILogger<TriggerService>? logger = null)
    {
        _logger = logger;
        _logger?.LogInformation("TriggerService initialized - will run managedsoftwareupdate directly with --show-status");
    }

    /// <inheritdoc />
    public async Task TriggerCheckAsync()
    {
        _logger?.LogInformation("Running managedsoftwareupdate --checkonly --show-status");
        await RunDirectAsync("--checkonly --show-status -vv");
    }

    /// <inheritdoc />
    public async Task TriggerInstallAsync()
    {
        _logger?.LogInformation("Running managedsoftwareupdate --auto --show-status");
        await RunDirectAsync("--auto --show-status -vv");
    }

    /// <inheritdoc />
    public async Task TriggerStopAsync()
    {
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            _logger?.LogInformation("Stopping managedsoftwareupdate process");
            try
            {
                _currentProcess.Kill();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to stop process");
            }
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Runs managedsoftwareupdate.exe directly with --show-status
    /// </summary>
    private async Task RunDirectAsync(string arguments)
    {
        var exePath = FindExecutable();
        if (exePath == null)
        {
            _logger?.LogError("Could not find managedsoftwareupdate.exe");
            return;
        }

        _logger?.LogInformation("Running: {Path} {Args}", exePath, arguments);

        _isOperationRunning = true;
        OperationStatusChanged?.Invoke(this, true);

        try
        {
            // ManagedSoftwareCenter runs as admin, so no UAC needed
            // Run hidden without a console window
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            };

            _currentProcess = Process.Start(startInfo);
            
            if (_currentProcess != null)
            {
                _logger?.LogInformation("Started managedsoftwareupdate (PID: {Pid})", _currentProcess.Id);
                
                // Wait for process to exit in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _currentProcess.WaitForExitAsync();
                        _logger?.LogInformation("managedsoftwareupdate exited with code: {Code}", _currentProcess.ExitCode);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error waiting for process");
                        System.Diagnostics.Debug.WriteLine($"[TriggerService] Error waiting for process: {ex.Message}");
                    }
                    finally
                    {
                        _isOperationRunning = false;
                        OperationStatusChanged?.Invoke(this, false);
                        _currentProcess = null;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start managedsoftwareupdate");
            System.Diagnostics.Debug.WriteLine($"[TriggerService] Failed to start: {ex.Message}");
            _isOperationRunning = false;
            OperationStatusChanged?.Invoke(this, false);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Finds the managedsoftwareupdate.exe executable
    /// </summary>
    private string? FindExecutable()
    {
        System.Diagnostics.Debug.WriteLine($"[TriggerService] FindExecutable - searching for managedsoftwareupdate.exe");
        foreach (var path in ExecutablePaths)
        {
            var fullPath = Path.GetFullPath(path);
            _logger?.LogDebug("Checking for executable at: {Path}", fullPath);
            System.Diagnostics.Debug.WriteLine($"[TriggerService] Checking: {fullPath} - Exists: {File.Exists(fullPath)}");
            
            if (File.Exists(fullPath))
            {
                System.Diagnostics.Debug.WriteLine($"[TriggerService] FOUND: {fullPath}");
                return fullPath;
            }
        }

        // Also check PATH environment variable
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(dir, "managedsoftwareupdate.exe");
                if (File.Exists(fullPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[TriggerService] FOUND in PATH: {fullPath}");
                    return fullPath;
                }
            }
        }

        System.Diagnostics.Debug.WriteLine("[TriggerService] NOT FOUND - managedsoftwareupdate.exe");
        return null;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
