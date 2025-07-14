using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CimianStatus
{
    public class StatusServer : IDisposable
    {
        private readonly ILogger<StatusServer> _logger;
        private TcpListener? _tcpListener;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _serverTask;

        public event Action<StatusMessage>? MessageReceived;

        public StatusServer(ILogger<StatusServer> logger)
        {
            _logger = logger;
        }

        public async Task StartAsync()
        {
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                
                // Start TCP server on localhost
                _tcpListener = new TcpListener(IPAddress.Loopback, 19847);
                _tcpListener.Start();
                
                _logger.LogInformation("StatusServer started on port 19847");
                
                _serverTask = AcceptClientsAsync(_cancellationTokenSource.Token);
                
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start StatusServer");
                throw;
            }
        }

        private async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _tcpListener != null)
            {
                try
                {
                    var tcpClient = await _tcpListener.AcceptTcpClientAsync();
                    _logger.LogDebug("Client connected from {RemoteEndPoint}", tcpClient.Client.RemoteEndPoint);
                    
                    // Handle client in background
                    _ = Task.Run(() => HandleClientAsync(tcpClient, cancellationToken), cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    // Server was stopped
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting client connection");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string? line;
                    while (!cancellationToken.IsCancellationRequested && 
                           (line = await reader.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        try
                        {
                            var message = JsonConvert.DeserializeObject<StatusMessage>(line);
                            if (message != null)
                            {
                                _logger.LogDebug("Received message: Type={Type}, Data={Data}", 
                                    message.Type, message.Data);
                                
                                MessageReceived?.Invoke(message);
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse status message: {Message}", line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling client connection");
            }
        }

        public void Stop()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _tcpListener?.Stop();
                _serverTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping StatusServer");
            }
        }

        public void Dispose()
        {
            Stop();
            _cancellationTokenSource?.Dispose();
            _tcpListener = null;
        }
    }
}
