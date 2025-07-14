using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CimianStatus
{
    public class BackgroundStatusService : BackgroundService
    {
        private readonly StatusServer _statusServer;
        private readonly ILogger<BackgroundStatusService> _logger;

        public BackgroundStatusService(StatusServer statusServer, ILogger<BackgroundStatusService> logger)
        {
            _statusServer = statusServer;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("CimianStatus background service starting");
            
            await _statusServer.StartAsync();
            
            // Keep the service running
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
            
            _logger.LogInformation("CimianStatus background service stopping");
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _statusServer.Stop();
            await base.StopAsync(cancellationToken);
        }
    }
}
