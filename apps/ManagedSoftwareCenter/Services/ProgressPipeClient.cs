// ProgressPipeClient.cs - Named pipe client for real-time progress from managedsoftwareupdate
// Windows equivalent of Munki's NSDistributedNotificationCenter

using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Named pipe client for receiving real-time progress updates from managedsoftwareupdate
/// </summary>
public class ProgressPipeClient : IProgressPipeClient, IDisposable
{
    private const string PipeName = "CimianProgress";
    private const int ReconnectDelayMs = 2000;

    private readonly ILogger<ProgressPipeClient>? _logger;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private bool _disposed;

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public event EventHandler<ProgressMessage>? ProgressReceived;
    public event EventHandler<bool>? ConnectionChanged;

    public ProgressPipeClient(ILogger<ProgressPipeClient>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start connection loop in background
        _readTask = Task.Run(async () => await ConnectionLoopAsync(_cts.Token), _cts.Token);

        await Task.CompletedTask;
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger?.LogDebug("Attempting to connect to progress pipe...");

                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                
                // Try to connect with timeout
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await _pipe.ConnectAsync(connectCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Connection timeout, will retry
                    _logger?.LogDebug("Progress pipe connection timeout, will retry...");
                    await CleanupPipeAsync();
                    await Task.Delay(ReconnectDelayMs, cancellationToken);
                    continue;
                }

                _reader = new StreamReader(_pipe);
                _writer = new StreamWriter(_pipe) { AutoFlush = true };

                _logger?.LogInformation("Connected to progress pipe");
                ConnectionChanged?.Invoke(this, true);

                // Read messages until disconnected
                await ReadMessagesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Progress pipe error, will retry...");
            }
            finally
            {
                await CleanupPipeAsync();
                ConnectionChanged?.Invoke(this, false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ReconnectDelayMs, cancellationToken);
            }
        }
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _reader != null && _pipe?.IsConnected == true)
        {
            try
            {
                var line = await _reader.ReadLineAsync(cancellationToken);
                
                if (string.IsNullOrEmpty(line))
                {
                    // Server disconnected
                    break;
                }

                var message = JsonSerializer.Deserialize<ProgressMessage>(line);
                if (message != null)
                {
                    _logger?.LogDebug("Received progress: {Message} - {Detail} ({Percent}%)", 
                        message.Message, message.Detail, message.Percent);
                    
                    ProgressReceived?.Invoke(this, message);
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
                // Pipe disconnected
                break;
            }
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_readTask != null)
        {
            try
            {
                await _readTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning("Read task did not complete in time");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        await CleanupPipeAsync();
    }

    /// <inheritdoc />
    public async Task SendCommandAsync(CommandMessage command)
    {
        if (_writer == null || _pipe?.IsConnected != true)
        {
            _logger?.LogWarning("Cannot send command - not connected to progress pipe");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(command);
            await _writer.WriteLineAsync(json);
            _logger?.LogDebug("Sent command: {Type}", command.Type);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send command to progress pipe");
        }
    }

    private async Task CleanupPipeAsync()
    {
        if (_reader != null)
        {
            _reader.Dispose();
            _reader = null;
        }

        if (_writer != null)
        {
            await _writer.DisposeAsync();
            _writer = null;
        }

        if (_pipe != null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();

        GC.SuppressFinalize(this);
    }
}
