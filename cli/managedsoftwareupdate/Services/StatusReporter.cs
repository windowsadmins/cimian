// StatusReporter.cs - Reports progress to GUI via TCP socket
// Implements same protocol as Go's PipeReporter for GUI integration

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Reports status messages to GUI (ManagedSoftwareCenter) via TCP connection.
/// Matches Go's pkg/status/reporter.go PipeReporter behavior.
/// The GUI runs a TCP server on port 19847 that receives these messages.
/// </summary>
public class StatusReporter : IDisposable
{
    public const int DefaultPort = 19847;
    private const string DefaultHost = "127.0.0.1";
    private const int ConnectionTimeoutMs = 2000;
    private const int MaxRetries = 10;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamWriter? _writer;
    private readonly object _lock = new();
    private bool _disposed;
    private bool _connected;
    private readonly int _verbosity;
    private readonly int _port;
    private Task? _commandReadTask;
    private CancellationTokenSource? _commandReadCts;

    /// <summary>
    /// Raised when the GUI sends a stop command over the status connection.
    /// The engine links this to its cancellation token so the user's Cancel
    /// button gracefully aborts between items.
    /// </summary>
    public event Action? StopRequested;

    /// <summary>
    /// Creates a new StatusReporter that will connect to the GUI on first message
    /// </summary>
    /// <param name="verbosity">Verbosity level for console output</param>
    /// <param name="port">
    /// TCP port of the GUI status listener. Defaults to 19847 (the login-window
    /// CimianStatus listener). Managed Software Center runs its own listener on a
    /// separate port and passes it via --status-port so the two never collide —
    /// e.g. when a locked machine has the login window up while a user session's
    /// MSC is also running.
    /// </param>
    public StatusReporter(int verbosity = 0, int port = DefaultPort)
    {
        _verbosity = verbosity;
        _port = port;
    }

    /// <summary>
    /// Gets whether the reporter is connected to the GUI
    /// </summary>
    public bool IsConnected => _connected;

    /// <summary>
    /// Try to connect to the GUI TCP server
    /// </summary>
    /// <returns>True if connected successfully</returns>
    public bool TryConnect()
    {
        if (_connected) return true;

        lock (_lock)
        {
            if (_connected) return true;

            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    _client = new TcpClient();
                    
                    // Try to connect with timeout
                    var connectTask = _client.ConnectAsync(DefaultHost, _port);
                    if (!connectTask.Wait(ConnectionTimeoutMs))
                    {
                        _client.Dispose();
                        _client = null;
                        Thread.Sleep(Math.Min(500 * (attempt + 1), 3000));
                        continue;
                    }

                    _stream = _client.GetStream();
                    _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
                    _connected = true;

                    // The TCP stream is duplex: listen for commands (stop) from
                    // the GUI without blocking the write path.
                    StartCommandReader();

                    if (_verbosity >= 2)
                    {
                        ConsoleLogger.Debug($"Connected to GUI status server on port {_port}");
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    if (_verbosity >= 3)
                    {
                        ConsoleLogger.Debug($"Failed to connect to GUI (attempt {attempt + 1}): {ex.Message}");
                    }
                    
                    _client?.Dispose();
                    _client = null;
                    Thread.Sleep(Math.Min(500 * (attempt + 1), 3000));
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Send a status message (main headline text)
    /// </summary>
    public void Message(string text)
    {
        SendMessage(new StatusMessage
        {
            Type = "statusMessage",
            Data = text
        });
    }

    /// <summary>
    /// Send a detail message (secondary descriptive text)
    /// </summary>
    public void Detail(string text)
    {
        SendMessage(new StatusMessage
        {
            Type = "detailMessage",
            Data = text
        });
    }

    /// <summary>
    /// Send a percentage update
    /// </summary>
    public void Percent(int percent)
    {
        SendMessage(new StatusMessage
        {
            Type = "percentProgress",
            Percent = percent
        });
    }

    /// <summary>
    /// Send an error message
    /// </summary>
    public void Error(string text)
    {
        SendMessage(new StatusMessage
        {
            Type = "statusMessage",
            Data = text,
            Error = true
        });
    }

    /// <summary>
    /// Report a per-item lifecycle stage so the GUI can render live status on
    /// each row: pending, downloading, downloaded, installing, installed,
    /// removing, removed, failed. For the "failed" stage, <paramref name="detail"/>
    /// carries the failure reason (e.g. "Exit code 1603") so the GUI can surface
    /// the exact code to the user.
    /// </summary>
    public void ItemStatus(string itemName, string stage, string? detail = null)
    {
        SendMessage(new StatusMessage
        {
            Type = "itemStatus",
            Item = itemName,
            Data = stage,
            Message = detail
        });
    }

    private void StartCommandReader()
    {
        if (_commandReadTask is { IsCompleted: false }) return;

        _commandReadCts = new CancellationTokenSource();
        var token = _commandReadCts.Token;
        var stream = _stream;
        if (stream == null) return;

        _commandReadTask = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line == null) break; // GUI disconnected

                    if (line.Contains("\"stop\"", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_verbosity >= 1)
                        {
                            ConsoleLogger.Info("Stop requested from GUI");
                        }
                        StopRequested?.Invoke();
                    }
                }
            }
            catch
            {
                // Reader dies with the connection; writes detect that separately.
            }
        }, token);
    }

    /// <summary>
    /// Request to display the log file
    /// </summary>
    public void DisplayLog(string logPath)
    {
        SendMessage(new StatusMessage
        {
            Type = "displayLog",
            Data = logPath
        });
    }

    /// <summary>
    /// Send quit message to signal completion
    /// </summary>
    public void Quit()
    {
        SendMessage(new StatusMessage
        {
            Type = "quit"
        });
    }

    private void SendMessage(StatusMessage message)
    {
        // Try to connect if not already connected
        if (!_connected)
        {
            TryConnect();
        }

        if (!_connected || _writer == null) return;

        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(message, StatusMessageContext.Default.StatusMessage);
                _writer.WriteLine(json);
                
                if (_verbosity >= 3)
                {
                    ConsoleLogger.Debug($"Sent to GUI: {json}");
                }
            }
            catch (Exception ex)
            {
                if (_verbosity >= 2)
                {
                    ConsoleLogger.Debug($"Failed to send status message: {ex.Message}");
                }
                
                // Connection lost - mark as disconnected
                _connected = false;
                CleanupConnection();
            }
        }
    }

    private void CleanupConnection()
    {
        _writer?.Dispose();
        _writer = null;
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Send quit message before disconnecting
        if (_connected)
        {
            try { Quit(); } catch { }
        }

        _commandReadCts?.Cancel();
        _commandReadCts?.Dispose();

        lock (_lock)
        {
            CleanupConnection();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Wire format for status messages - matches Go's StatusMessage struct
/// </summary>
internal class StatusMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("item")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Item { get; set; }

    [JsonPropertyName("percent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Percent { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Error { get; set; }
}

/// <summary>
/// JSON source generator for AOT compatibility
/// </summary>
[JsonSerializable(typeof(StatusMessage))]
internal partial class StatusMessageContext : JsonSerializerContext
{
}
