// StatusReporter.cs - Reports progress to GUI via TCP socket
// Implements same protocol as Go's PipeReporter for GUI integration

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Reports status messages to GUI (ManagedSoftwareCenter) via TCP connection.
/// Matches Go's pkg/status/reporter.go PipeReporter behavior.
/// The GUI runs a TCP server on port 19847 that receives these messages.
/// </summary>
public class StatusReporter : IDisposable
{
    private const int DefaultPort = 19847;
    private const string DefaultHost = "127.0.0.1";
    private const int ConnectionTimeoutMs = 1000;
    private const int MaxRetries = 3;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamWriter? _writer;
    private readonly object _lock = new();
    private bool _disposed;
    private bool _connected;
    private readonly int _verbosity;

    /// <summary>
    /// Creates a new StatusReporter that will connect to the GUI on first message
    /// </summary>
    /// <param name="verbosity">Verbosity level for console output</param>
    public StatusReporter(int verbosity = 0)
    {
        _verbosity = verbosity;
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
                    var connectTask = _client.ConnectAsync(DefaultHost, DefaultPort);
                    if (!connectTask.Wait(ConnectionTimeoutMs))
                    {
                        _client.Dispose();
                        _client = null;
                        Thread.Sleep(100);
                        continue;
                    }

                    _stream = _client.GetStream();
                    _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
                    _connected = true;

                    if (_verbosity >= 2)
                    {
                        Console.WriteLine($"[DEBUG] Connected to GUI status server on port {DefaultPort}");
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    if (_verbosity >= 3)
                    {
                        Console.WriteLine($"[DEBUG] Failed to connect to GUI (attempt {attempt + 1}): {ex.Message}");
                    }
                    
                    _client?.Dispose();
                    _client = null;
                    Thread.Sleep(100);
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
                    Console.WriteLine($"[DEBUG] Sent to GUI: {json}");
                }
            }
            catch (Exception ex)
            {
                if (_verbosity >= 2)
                {
                    Console.WriteLine($"[DEBUG] Failed to send status message: {ex.Message}");
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
