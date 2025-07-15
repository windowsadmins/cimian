using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Cimian.Status.Models;

namespace Cimian.Status.Services
{
    public class StatusServer : IStatusServer, IDisposable
    {
        private readonly ILogger<StatusServer> _logger;
        private TcpListener? _tcpListener;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isRunning;

        public event EventHandler<StatusMessage>? MessageReceived;

        public bool IsRunning => _isRunning;

        public StatusServer(ILogger<StatusServer> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task StartAsync()
        {
            if (_isRunning) return Task.CompletedTask;

            try
            {
                _tcpListener = new TcpListener(IPAddress.Loopback, 19847);
                _cancellationTokenSource = new CancellationTokenSource();
                
                _tcpListener.Start();
                _isRunning = true;

                _logger.LogInformation("Status server started on port 19847");

                // Start accepting connections in background
                _ = Task.Run(async () => await AcceptConnectionsAsync(_cancellationTokenSource.Token));
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start status server");
                throw;
            }
        }

        public Task StopAsync()
        {
            if (!_isRunning) return Task.CompletedTask;

            try
            {
                _isRunning = false;
                _cancellationTokenSource?.Cancel();
                _tcpListener?.Stop();

                _logger.LogInformation("Status server stopped");
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping status server");
                return Task.CompletedTask;
            }
        }

        private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var tcpClient = await _tcpListener!.AcceptTcpClientAsync();
                    _ = Task.Run(async () => await HandleClientAsync(tcpClient, cancellationToken));
                }
                catch (ObjectDisposedException)
                {
                    // Expected when stopping
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error accepting TCP connection");
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
                    while ((line = await reader.ReadLineAsync()) != null && !cancellationToken.IsCancellationRequested)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        try
                        {
                            var message = JsonConvert.DeserializeObject<StatusMessage>(line);
                            if (message != null)
                            {
                                _logger.LogDebug("Received status message: {Type} - {Data}", message.Type, message.Data);
                                MessageReceived?.Invoke(this, message);
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Failed to deserialize status message: {Line}", line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error handling TCP client");
            }
        }

        public void Dispose()
        {
            StopAsync().Wait(5000);
            _cancellationTokenSource?.Dispose();
            _tcpListener = null;
        }
    }

    public class BackgroundStatusService : BackgroundService
    {
        private readonly IStatusServer _statusServer;
        private readonly ILogger<BackgroundStatusService> _logger;

        public BackgroundStatusService(IStatusServer statusServer, ILogger<BackgroundStatusService> logger)
        {
            _statusServer = statusServer ?? throw new ArgumentNullException(nameof(statusServer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await _statusServer.StartAsync();
                _logger.LogInformation("Background status service started");

                // Keep running until cancellation is requested
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in background status service");
            }
            finally
            {
                await _statusServer.StopAsync();
                _logger.LogInformation("Background status service stopped");
            }
        }
    }
}
