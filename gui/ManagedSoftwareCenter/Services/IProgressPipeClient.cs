// IProgressPipeClient.cs - Interface for progress IPC

using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Client for receiving real-time progress updates from managedsoftwareupdate via named pipe
/// Windows equivalent of Munki's NSDistributedNotificationCenter
/// </summary>
public interface IProgressPipeClient
{
    /// <summary>
    /// Connect to the progress pipe
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from the progress pipe
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Send a command to managedsoftwareupdate
    /// </summary>
    Task SendCommandAsync(CommandMessage command);

    /// <summary>
    /// Whether currently connected
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Event raised when a progress message is received
    /// </summary>
    event EventHandler<ProgressMessage>? ProgressReceived;

    /// <summary>
    /// Event raised when connection state changes
    /// </summary>
    event EventHandler<bool>? ConnectionChanged;
}
