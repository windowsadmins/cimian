using System.Diagnostics;

namespace Cimian.Core.Services;

/// <summary>
/// Manages Cimian self-update operations safely.
/// </summary>
public static class SelfUpdateService
{
    // Self-update flag file - indicates a self-update is pending
    public const string SelfUpdateFlagFile = @"C:\ProgramData\ManagedInstalls\.cimian.selfupdate";
    
    // Backup directory for current installation during self-update
    public const string SelfUpdateBackupDir = @"C:\ProgramData\ManagedInstalls\SelfUpdateBackup";
    
    // Installation directory for Cimian
    public const string CimianInstallDir = @"C:\Program Files\Cimian";

    /// <summary>
    /// Metadata parsed from the self-update flag file
    /// </summary>
    public class SelfUpdateMetadata
    {
        public string Item { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string InstallerType { get; set; } = string.Empty;
        public string LocalFile { get; set; } = string.Empty;
        public string ScheduledAt { get; set; } = string.Empty;
    }

    /// <summary>
    /// Checks if a self-update is pending
    /// </summary>
    public static bool IsSelfUpdatePending()
    {
        return File.Exists(SelfUpdateFlagFile);
    }

    /// <summary>
    /// Gets the self-update status and metadata
    /// </summary>
    public static (bool pending, SelfUpdateMetadata? metadata, string? error) GetSelfUpdateStatus()
    {
        if (!IsSelfUpdatePending())
        {
            return (false, null, null);
        }

        try
        {
            var flagData = File.ReadAllText(SelfUpdateFlagFile);
            var metadata = ParseMetadata(flagData);
            return (true, metadata, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"Failed to read self-update flag file: {ex.Message}");
        }
    }

    /// <summary>
    /// Schedules a self-update to be performed on next service restart
    /// </summary>
    public static bool ScheduleSelfUpdate(string itemName, string version, string installerType, string localFile)
    {
        try
        {
            var flagData = $"""
                # Cimian Self-Update Scheduled
                Item: {itemName}
                Version: {version}
                InstallerType: {installerType}
                LocalFile: {localFile}
                ScheduledAt: {DateTime.Now:O}
                """;

            File.WriteAllText(SelfUpdateFlagFile, flagData);
            
            ConsoleLogger.Success("Self-update scheduled successfully. Cimian will update on next service restart.");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Failed to create self-update flag file: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears the self-update flag file
    /// </summary>
    public static bool ClearSelfUpdateFlag()
    {
        try
        {
            if (File.Exists(SelfUpdateFlagFile))
            {
                File.Delete(SelfUpdateFlagFile);
            }
            return true;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Failed to clear self-update flag: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Launches the installer as a fully detached process so CimianWatcher can exit
    /// cleanly before the installer replaces its binary.  The caller should call
    /// Environment.Exit(0) immediately after this returns true.
    ///
    /// Flow: backup → clear flag → spawn detached installer → return true → caller exits.
    /// Windows SCM restarts CimianWatcher after the installer completes.
    /// </summary>
    public static bool LaunchDetachedSelfUpdate(Action<string> log)
    {
        if (!IsSelfUpdatePending())
        {
            log("No self-update pending. Nothing to do.");
            return false;
        }

        var (pending, metadata, error) = GetSelfUpdateStatus();
        if (error != null || metadata == null)
        {
            log($"Failed to read self-update metadata: {error}");
            return false;
        }

        log($"Launching detached self-update: {metadata.Item} v{metadata.Version}");

        if (!File.Exists(metadata.LocalFile))
        {
            log($"Installer file not found: {metadata.LocalFile}");
            ClearSelfUpdateFlag();
            return false;
        }

        if (!CreateBackup())
        {
            log("Failed to create backup before self-update");
            return false;
        }

        // Clear the flag BEFORE launching the installer to prevent an infinite loop.
        ClearSelfUpdateFlag();
        log("Cleared self-update flag (pre-install)");

        // Build the installer command based on type.
        string fileName;
        string arguments;

        switch (metadata.InstallerType.ToLowerInvariant())
        {
            case "msi":
            {
                var logFile = Path.Combine(
                    @"C:\ProgramData\ManagedInstalls\logs",
                    $"selfupdate-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                fileName = "msiexec.exe";
                arguments = $"/i \"{metadata.LocalFile}\" /quiet /norestart /l*v \"{logFile}\" REINSTALLMODE=vamus REINSTALL=ALL";
                break;
            }
            case "pkg":
            case "nupkg":
            {
                const string sbinInstaller = @"C:\Program Files\sbin\installer.exe";
                if (!File.Exists(sbinInstaller))
                {
                    log($"sbin-installer not found at: {sbinInstaller}");
                    return false;
                }
                fileName = sbinInstaller;
                arguments = $"--pkg \"{metadata.LocalFile}\" --target / --verbose";
                break;
            }
            default:
                log($"Unsupported installer type for self-update: {metadata.InstallerType}");
                return false;
        }

        try
        {
            // UseShellExecute=true creates a fully detached process — it survives the parent exit.
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };

            process.Start();
            log($"Detached installer process started (PID {process.Id}). CimianWatcher will now exit.");
            return true;
        }
        catch (Exception ex)
        {
            log($"Failed to launch detached installer: {ex.Message}");
            // Re-schedule so we retry on next SCM restart.
            ScheduleSelfUpdate(metadata.Item, metadata.Version, metadata.InstallerType, metadata.LocalFile);
            return false;
        }
    }

    /// <summary>
    /// Performs the actual self-update (called during service restart)
    /// </summary>
    public static bool PerformSelfUpdate()
    {
        if (!IsSelfUpdatePending())
        {
            ConsoleLogger.Info("No self-update pending. Nothing to do.");
            return true;
        }

        var (pending, metadata, error) = GetSelfUpdateStatus();
        if (error != null || metadata == null)
        {
            ConsoleLogger.Error($"Failed to read self-update metadata: {error}");
            return false;
        }

        ConsoleLogger.Info($"Performing scheduled Cimian self-update: {metadata.Item} v{metadata.Version}");

        // Verify the installer file exists
        if (!File.Exists(metadata.LocalFile))
        {
            ConsoleLogger.Error($"Installer file not found: {metadata.LocalFile}");
            ClearSelfUpdateFlag();
            return false;
        }

        // Create backup of current installation
        if (!CreateBackup())
        {
            ConsoleLogger.Error("Failed to create backup before self-update");
            return false;
        }

        // Clear the flag file BEFORE running the installer.
        // The MSI's custom action will taskkill cimiwatcher.exe during install,
        // so we must clear the flag now to prevent an infinite self-update loop
        // on service restart.
        ClearSelfUpdateFlag();
        ConsoleLogger.Info("Cleared self-update flag (pre-install)");

        // Execute the update based on installer type
        bool success = metadata.InstallerType.ToLowerInvariant() switch
        {
            "msi" => PerformMsiUpdate(metadata.LocalFile, metadata.Item),
            "pkg" => PerformPkgUpdate(metadata.LocalFile, metadata.Item),
            "nupkg" => PerformNupkgUpdate(metadata.LocalFile, metadata.Item),
            _ => HandleUnsupportedInstaller(metadata.InstallerType)
        };

        if (success)
        {
            CleanupAfterSuccess();
            ConsoleLogger.Success("Cimian self-update completed successfully");
        }
        else
        {
            ConsoleLogger.Warn("Self-update failed, attempting rollback...");
            if (PerformRollback())
            {
                ConsoleLogger.Info("Rollback completed successfully");
            }
            else
            {
                ConsoleLogger.Error("Rollback failed!");
            }
            // Re-schedule the self-update for retry on next service restart
            ScheduleSelfUpdate(metadata.Item, metadata.Version,
                metadata.InstallerType, metadata.LocalFile);
        }

        return success;
    }

    private static SelfUpdateMetadata ParseMetadata(string flagData)
    {
        var metadata = new SelfUpdateMetadata();
        var lines = flagData.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith('#'))
                continue;

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
                continue;

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();

            switch (key)
            {
                case "Item": metadata.Item = value; break;
                case "Version": metadata.Version = value; break;
                case "InstallerType": metadata.InstallerType = value; break;
                case "LocalFile": metadata.LocalFile = value; break;
                case "ScheduledAt": metadata.ScheduledAt = value; break;
            }
        }

        return metadata;
    }

    private static bool CreateBackup()
    {
        try
        {
            ConsoleLogger.Info("Creating backup of current Cimian installation...");

            // Remove any existing backup
            if (Directory.Exists(SelfUpdateBackupDir))
            {
                Directory.Delete(SelfUpdateBackupDir, recursive: true);
            }

            // Create backup directory
            Directory.CreateDirectory(SelfUpdateBackupDir);

            // Copy all files from install dir to backup
            if (Directory.Exists(CimianInstallDir))
            {
                foreach (var file in Directory.GetFiles(CimianInstallDir))
                {
                    var destFile = Path.Combine(SelfUpdateBackupDir, Path.GetFileName(file));
                    File.Copy(file, destFile, overwrite: true);
                }
                ConsoleLogger.Info($"Backup created at {SelfUpdateBackupDir}");
            }

            return true;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Failed to create backup: {ex.Message}");
            return false;
        }
    }

    private static bool PerformMsiUpdate(string msiPath, string itemName)
    {
        try
        {
            ConsoleLogger.Info($"Executing MSI update: {msiPath}");

            var logFile = Path.Combine(
                @"C:\ProgramData\ManagedInstalls\logs",
                $"selfupdate-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            // Ensure log directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{msiPath}\" /quiet /norestart /l*v \"{logFile}\" REINSTALLMODE=vamus REINSTALL=ALL",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            process.WaitForExit(TimeSpan.FromMinutes(10));

            if (process.ExitCode == 0)
            {
                ConsoleLogger.Success($"MSI update completed successfully (exit code: {process.ExitCode})");
                return true;
            }
            else
            {
                ConsoleLogger.Error($"MSI update failed with exit code: {process.ExitCode}");
                ConsoleLogger.Info($"See log file: {logFile}");
                return false;
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"MSI update failed: {ex.Message}");
            return false;
        }
    }

    private static bool PerformPkgUpdate(string pkgPath, string itemName)
    {
        // .pkg files are installed using sbin-installer (C:\Program Files\sbin\installer.exe)
        // This is the primary installation method for Cimian packages
        return PerformSbinInstallerUpdate(pkgPath, itemName);
    }

    private static bool PerformNupkgUpdate(string nupkgPath, string itemName)
    {
        // .nupkg files are also installed using sbin-installer
        // sbin-installer handles both .pkg and .nupkg formats
        return PerformSbinInstallerUpdate(nupkgPath, itemName);
    }

    /// <summary>
    /// Performs installation using sbin-installer (C:\Program Files\sbin\installer.exe)
    /// This is the primary package installer for Cimian, supporting .pkg and .nupkg formats.
    /// Command: installer.exe --pkg &lt;path&gt; --target / --verbose
    /// </summary>
    private static bool PerformSbinInstallerUpdate(string packagePath, string itemName)
    {
        const string SbinInstallerPath = @"C:\Program Files\sbin\installer.exe";

        try
        {
            // Verify sbin-installer exists
            if (!File.Exists(SbinInstallerPath))
            {
                ConsoleLogger.Error($"sbin-installer not found at: {SbinInstallerPath}");
                ConsoleLogger.Info("sbin-installer is required for .pkg/.nupkg installation.");
                ConsoleLogger.Info("Install the SbinInstaller package first.");
                return false;
            }

            ConsoleLogger.Info($"Installing package with sbin-installer: {packagePath}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = SbinInstallerPath,
                    Arguments = $"--pkg \"{packagePath}\" --target / --verbose",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromMinutes(15));

            if (process.ExitCode == 0)
            {
                ConsoleLogger.Success($"sbin-installer completed successfully for {itemName}");
                if (!string.IsNullOrEmpty(output))
                {
                    // Log first 500 chars of output for context
                    var truncatedOutput = output.Length > 500 ? output[..500] + "..." : output;
                    ConsoleLogger.Detail($"Installer output: {truncatedOutput}");
                }
                return true;
            }
            else
            {
                ConsoleLogger.Error($"sbin-installer failed with exit code: {process.ExitCode}");
                if (!string.IsNullOrEmpty(error))
                    ConsoleLogger.Error($"Error output: {error}");
                if (!string.IsNullOrEmpty(output))
                    ConsoleLogger.Error($"Standard output: {output}");
                return false;
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"sbin-installer execution failed: {ex.Message}");
            return false;
        }
    }

    private static bool HandleUnsupportedInstaller(string installerType)
    {
        ConsoleLogger.Error($"Unsupported installer type for self-update: {installerType}");
        return false;
    }

    private static bool PerformRollback()
    {
        try
        {
            if (!Directory.Exists(SelfUpdateBackupDir))
            {
                ConsoleLogger.Warn("No backup directory found for rollback");
                return false;
            }

            ConsoleLogger.Info("Rolling back to previous version...");

            // Copy backup files back to install directory
            foreach (var file in Directory.GetFiles(SelfUpdateBackupDir))
            {
                var destFile = Path.Combine(CimianInstallDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            ConsoleLogger.Info("Rollback completed");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Rollback failed: {ex.Message}");
            return false;
        }
    }

    private static void CleanupAfterSuccess()
    {
        try
        {
            // Remove the self-update flag file
            ClearSelfUpdateFlag();

            // Remove the backup directory
            if (Directory.Exists(SelfUpdateBackupDir))
            {
                Directory.Delete(SelfUpdateBackupDir, recursive: true);
            }

            ConsoleLogger.Info("Self-update cleanup completed");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Cleanup after self-update had issues: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a stale SelfUpdateBackup directory left behind after a successful
    /// detached self-update.  In the detached path the installer runs after
    /// CimianWatcher exits, so CleanupAfterSuccess never executes.  Call this on
    /// service startup when no self-update is pending — its presence means the
    /// new version is running and the backup is no longer needed.
    /// </summary>
    public static void CleanupStaleBackup()
    {
        if (IsSelfUpdatePending())
            return;

        if (!Directory.Exists(SelfUpdateBackupDir))
            return;

        try
        {
            Directory.Delete(SelfUpdateBackupDir, recursive: true);
            ConsoleLogger.Info($"Removed stale self-update backup: {SelfUpdateBackupDir}");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to remove stale self-update backup: {ex.Message}");
        }
    }
}
