// ProgressServer.cs - TCP server for receiving progress from managedsoftwareupdate
// Listens on TCP port 19848 (the login-window CimianStatus uses 19847)

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// TCP server that receives real-time progress updates from managedsoftwareupdate.
/// The Go code's PipeReporter connects to this server on port 19847.
/// </summary>
public class ProgressServer : IProgressPipeClient, IDisposable
{
    // MSC listens on its OWN port, distinct from the login-window CimianStatus
    // listener (19847). Both can be alive at once — e.g. a locked machine shows
    // the login window (19847) while the user session's MSC is also running — so
    // sharing a port made them collide (whoever bound first won; the other
    // retried forever). MSC tells the engine to report here via --status-port.
    public const int Port = 19848;

    private readonly ILogger<ProgressServer>? _logger;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;
    private bool _isConnected;

    public bool IsConnected => _isConnected;

    public event EventHandler<ProgressMessage>? ProgressReceived;
    public event EventHandler<bool>? ConnectionChanged;

    public ProgressServer(ILogger<ProgressServer>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start listening in background
        _listenTask = Task.Run(async () => await ListenLoopAsync(_cts.Token), _cts.Token);

        await Task.CompletedTask;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cimian", "msc-progress.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        
        void Log(string msg)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
            System.Diagnostics.Debug.WriteLine($"[ProgressServer] {msg}");
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        }
        
        Log("ListenLoopAsync starting");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _logger?.LogInformation("Progress server listening on port {Port}", Port);
                Log($"Listening on port {Port}");

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Wait for a client to connect
                        _logger?.LogDebug("Waiting for managedsoftwareupdate to connect...");
                        Log("Waiting for connection...");
                        
                        // Use AcceptTcpClientAsync with cancellation
                        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        
                        _client = await _listener.AcceptTcpClientAsync(acceptCts.Token);
                        _stream = _client.GetStream();
                        _reader = new StreamReader(_stream, Encoding.UTF8);

                        _isConnected = true;
                        Log("CLIENT CONNECTED!");
                        _logger?.LogInformation("managedsoftwareupdate connected");
                        // Listen loop runs on the threadpool; subscribers touch
                        // XAML-bound state, so raise on the UI thread.
                        UiDispatcher.Post(() => ConnectionChanged?.Invoke(this, true));

                        // Read messages from this client
                        await ReadMessagesAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
                    {
                        // Listener was stopped
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Client connection error");
                    }
                    finally
                    {
                        var wasConnected = _isConnected;
                        await CleanupClientAsync();
                        _isConnected = false;
                        if (wasConnected)
                        {
                            Log("Client disconnected");
                            UiDispatcher.Post(() => ConnectionChanged?.Invoke(this, false));
                        }
                    }
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _logger?.LogWarning("Port {Port} already in use, retrying in 2 seconds...", Port);
                await Task.Delay(2000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Progress server error");
                await Task.Delay(2000, cancellationToken);
            }
            finally
            {
                _listener?.Stop();
                _listener = null;
            }
        }
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        System.Diagnostics.Debug.WriteLine("[ProgressServer] ReadMessagesAsync started");
        while (!cancellationToken.IsCancellationRequested && _reader != null && _client?.Connected == true)
        {
            try
            {
                var line = await _reader.ReadLineAsync(cancellationToken);

                if (string.IsNullOrEmpty(line))
                {
                    // Client disconnected
                    _logger?.LogDebug("Client disconnected (empty read)");
                    System.Diagnostics.Debug.WriteLine("[ProgressServer] Client disconnected (empty read)");
                    break;
                }

                _logger?.LogDebug("Received: {Line}", line);
                System.Diagnostics.Debug.WriteLine($"[ProgressServer] RECEIVED: {line}");

                // Parse the Go StatusMessage format
                var goMessage = JsonSerializer.Deserialize<GoStatusMessage>(line);
                if (goMessage != null)
                {
                    var progressMessage = ConvertGoMessage(goMessage);
                    if (progressMessage != null)
                    {
                        _logger?.LogDebug("Progress: Type={Type}, Message={Message}, Detail={Detail}, Percent={Percent}",
                            progressMessage.Type, progressMessage.Message, progressMessage.Detail, progressMessage.Percent);
                        System.Diagnostics.Debug.WriteLine($"[ProgressServer] Parsed: Type={progressMessage.Type}, Message={progressMessage.Message}");
                        
                        UiDispatcher.Post(() => ProgressReceived?.Invoke(this, progressMessage));
                    }

                    // Check for quit message
                    if (goMessage.Type == "quit")
                    {
                        _logger?.LogDebug("Received quit message");
                        System.Diagnostics.Debug.WriteLine("[ProgressServer] Received quit message");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Failed to parse progress message");
            }
            catch (IOException)
            {
                // Client disconnected
                _logger?.LogDebug("Client disconnected (IO error)");
                break;
            }
        }
    }

    private ProgressMessage? ConvertGoMessage(GoStatusMessage goMessage)
    {
        var progress = new ProgressMessage();

        switch (goMessage.Type)
        {
            case "statusMessage":
                progress.Type = ProgressMessageType.Status;
                progress.Message = goMessage.Data ?? string.Empty;
                progress.Error = goMessage.Error;
                if (goMessage.Error)
                {
                    progress.Type = ProgressMessageType.Error;
                }
                break;

            case "detailMessage":
                progress.Type = ProgressMessageType.Detail;
                progress.Detail = goMessage.Data;
                break;

            case "percentProgress":
                progress.Type = ProgressMessageType.Progress;
                progress.Percent = goMessage.Percent;
                break;

            case "displayLog":
                progress.Type = ProgressMessageType.ShowLog;
                progress.Detail = goMessage.Data;
                break;

            case "itemStatus":
                // Per-item lifecycle stage: pending, downloading, downloaded,
                // installing, installed, removing, removed, failed. For "failed",
                // Message carries the reason (e.g. "Exit code 1603").
                progress.Type = ProgressMessageType.ItemStatus;
                progress.ItemName = goMessage.Item;
                progress.Detail = goMessage.Data;
                progress.Message = goMessage.Message ?? string.Empty;
                break;

            case "quit":
                progress.Type = ProgressMessageType.Complete;
                progress.Message = "Complete";
                break;

            default:
                return null;
        }

        return progress;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_listenTask != null)
        {
            try
            {
                await _listenTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning("Listen task did not complete in time");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        await CleanupClientAsync();
        _listener?.Stop();
    }

    /// <inheritdoc />
    public async Task SendCommandAsync(CommandMessage command)
    {
        // The status connection is duplex: managedsoftwareupdate's reporter
        // runs a command read loop. Stop is the only supported command.
        var stream = _stream;
        if (stream == null || _client?.Connected != true)
        {
            _logger?.LogWarning("Cannot send {Type} command - no client connected", command.Type);
            return;
        }

        try
        {
            var json = command.Type == CommandType.Stop
                ? "{\"type\":\"stop\"}"
                : "{\"type\":\"requestStatus\"}";
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            _logger?.LogInformation("Sent {Type} command to managedsoftwareupdate", command.Type);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send {Type} command", command.Type);
        }
    }

    private async Task CleanupClientAsync()
    {
        if (_reader != null)
        {
            _reader.Dispose();
            _reader = null;
        }

        if (_stream != null)
        {
            await _stream.DisposeAsync();
            _stream = null;
        }

        if (_client != null)
        {
            _client.Dispose();
            _client = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _reader?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
        _listener?.Stop();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Message format used by Go's PipeReporter
/// </summary>
internal class GoStatusMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("item")]
    public string? Item { get; set; }

    [JsonPropertyName("percent")]
    public int Percent { get; set; }

    [JsonPropertyName("error")]
    public bool Error { get; set; }
}
