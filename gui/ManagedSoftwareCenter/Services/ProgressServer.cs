// ProgressServer.cs - TCP server for receiving progress from managedsoftwareupdate
// Listens on TCP port 19847 (matching Go's PipeReporter)

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
    private const int Port = 19847;

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
                        ConnectionChanged?.Invoke(this, true);

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
                        await CleanupClientAsync();
                        _isConnected = false;
                        ConnectionChanged?.Invoke(this, false);
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
                        
                        ProgressReceived?.Invoke(this, progressMessage);
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
    public Task SendCommandAsync(CommandMessage command)
    {
        // Commands not supported in TCP mode - Go reporter doesn't listen for responses
        _logger?.LogDebug("SendCommand not supported in TCP server mode");
        return Task.CompletedTask;
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

    [JsonPropertyName("percent")]
    public int Percent { get; set; }

    [JsonPropertyName("error")]
    public bool Error { get; set; }
}
