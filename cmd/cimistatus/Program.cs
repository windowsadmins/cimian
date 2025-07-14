using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CimianStatus
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
                // Run with WPF UI
                RunWithUI(args);
            }
        }

        private static void RunWithUI(string[] args)
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        private static void RunBackgroundService(string[] args)
        {
            // Create a host for background operation
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<StatusServer>();
                    services.AddHostedService<BackgroundStatusService>();
                })
                .UseWindowsService()
                .Build();

            host.Run();
        }
    }
}
