using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cimian.CLI.Cimiwatcher.Services;

/// <summary>
/// Lifetime service that handles Windows Service control commands (pause, continue, etc.)
/// </summary>
public class CimianWatcherServiceLifetime : WindowsServiceLifetime
{
    private readonly FileWatcherService? _fileWatcher;

    public CimianWatcherServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        IServiceProvider serviceProvider)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        // Try to get the FileWatcherService from the service provider
        _fileWatcher = serviceProvider.GetService<FileWatcherService>();
    }

    protected override void OnPause()
    {
        _fileWatcher?.Pause();
        base.OnPause();
    }

    protected override void OnContinue()
    {
        _fileWatcher?.Resume();
        base.OnContinue();
    }
}
