using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cimian.Status.Services;
using Cimian.Status.ViewModels;
using Cimian.Status.Views;

namespace Cimian.Status
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Check if we should run in background mode (SYSTEM context)
            bool isBackgroundMode = Environment.UserName == "SYSTEM" || 
                                  string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USERPROFILE"));

            if (isBackgroundMode)
            {
                // Run as a background service without UI
                RunBackgroundService(args);
            }
            else
            {
                // Run with modern WPF UI
                RunWithUI(args);
            }
        }

        private static void RunWithUI(string[] args)
        {
            // Create host builder for dependency injection
            var hostBuilder = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // Register services
                    services.AddSingleton<IStatusServer, StatusServer>();
                    services.AddSingleton<IUpdateService, UpdateService>();
                    services.AddSingleton<ILogService, LogService>();
                    
                    // Register ViewModels
                    services.AddTransient<MainViewModel>();
                    
                    // Register Views
                    services.AddTransient<MainWindow>();
                })
                .ConfigureLogging(logging =>
                {
                    logging.AddEventLog();
                    logging.SetMinimumLevel(LogLevel.Information);
                });

            var host = hostBuilder.Build();

            // Create and run WPF application
            var app = new App();
            app.InitializeComponent();
            
            // Set the main window from DI container
            app.MainWindow = host.Services.GetRequiredService<MainWindow>();
            app.MainWindow.Show();
            
            app.Run();
        }

        private static void RunBackgroundService(string[] args)
        {
            // Create a host for background operation
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<IStatusServer, StatusServer>();
                    services.AddHostedService<BackgroundStatusService>();
                })
                .UseWindowsService()
                .ConfigureLogging(logging =>
                {
                    logging.AddEventLog();
                    logging.SetMinimumLevel(LogLevel.Information);
                })
                .Build();

            host.Run();
        }
    }
}
