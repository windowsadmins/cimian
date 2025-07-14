using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CimianStatus
{
    public partial class App : Application
    {
        private IHost? _host;
        private IServiceProvider? _serviceProvider;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Configure dependency injection
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<StatusServer>();
                    services.AddSingleton<StatusViewModel>();
                    services.AddTransient<MainWindow>();
                    services.AddLogging(builder =>
                    {
                        builder.AddDebug();
                        builder.AddConsole();
                        builder.SetMinimumLevel(LogLevel.Information);
                    });
                })
                .Build();

            _serviceProvider = _host.Services;

            // Start the status server
            var statusServer = _serviceProvider.GetRequiredService<StatusServer>();
            _ = statusServer.StartAsync(); // Fire and forget - we don't need to await this

            // Create and show main window
            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();
            base.OnExit(e);
        }
    }
}
