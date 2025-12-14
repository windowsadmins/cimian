// ProgressMessage.cs - IPC messages for real-time progress updates
// Mirrors Munki's NSDistributedNotificationCenter messages

namespace Cimian.GUI.SoftwareCenter.Models;

/// <summary>
/// Progress message sent from managedsoftwareupdate to Software Center
/// via named pipe (Windows equivalent of NSDistributedNotificationCenter)
/// </summary>
public class ProgressMessage
{
    /// <summary>
    /// Main status message (e.g., "Installing...", "Downloading...")
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Detail text (e.g., item name being processed)
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Progress percentage (0-100), or -1 for indeterminate
    /// </summary>
    public int Percent { get; set; } = -1;

    /// <summary>
    /// Whether the stop button should be visible
    /// </summary>
    public bool StopButtonVisible { get; set; }

    /// <summary>
    /// Whether the stop button should be enabled
    /// </summary>
    public bool StopButtonEnabled { get; set; }

    /// <summary>
    /// Current command being executed
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Type of progress message
    /// </summary>
    public ProgressMessageType Type { get; set; } = ProgressMessageType.Status;

    /// <summary>
    /// Current item index (1-based)
    /// </summary>
    public int CurrentItemIndex { get; set; }

    /// <summary>
    /// Total number of items
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Name of current item being processed
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Bytes downloaded so far (for download progress)
    /// </summary>
    public long BytesReceived { get; set; }

    /// <summary>
    /// Total bytes to download
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Human-readable download speed (e.g., "2.5 MB/s")
    /// </summary>
    public string? DownloadSpeed { get; set; }

    /// <summary>
    /// Human-readable time remaining (e.g., "3 min")
    /// </summary>
    public string? TimeRemaining { get; set; }
}

/// <summary>
/// Types of progress messages
/// </summary>
public enum ProgressMessageType
{
    /// <summary>
    /// General status update
    /// </summary>
    Status,

    /// <summary>
    /// Download progress
    /// </summary>
    Downloading,

    /// <summary>
    /// Installation progress
    /// </summary>
    Installing,

    /// <summary>
    /// Uninstallation progress
    /// </summary>
    Uninstalling,

    /// <summary>
    /// Check/scan progress
    /// </summary>
    Checking,

    /// <summary>
    /// Operation completed successfully
    /// </summary>
    Complete,

    /// <summary>
    /// Operation failed
    /// </summary>
    Error,

    /// <summary>
    /// Restart required notification
    /// </summary>
    RestartRequired,

    /// <summary>
    /// Logout required notification
    /// </summary>
    LogoutRequired
}

/// <summary>
/// Command message sent from Software Center to managedsoftwareupdate
/// </summary>
public class CommandMessage
{
    /// <summary>
    /// Type of command
    /// </summary>
    public CommandType Type { get; set; }

    /// <summary>
    /// Optional item name for item-specific commands
    /// </summary>
    public string? ItemName { get; set; }
}

/// <summary>
/// Types of commands that can be sent to managedsoftwareupdate
/// </summary>
public enum CommandType
{
    /// <summary>
    /// Stop current operation (only during download phase)
    /// </summary>
    Stop,

    /// <summary>
    /// Request status update
    /// </summary>
    RequestStatus
}
