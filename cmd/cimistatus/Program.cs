using System;
using System.Threading;
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
        private static Mutex? _mutex;

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
                // Check for single instance (only for UI mode)
                _mutex = new Mutex(true, "CimianStatusSingleInstance", out bool isNewInstance);
                
                if (!isNewInstance)
                {
                    // Another instance is already running, try to bring it to front
                    BringExistingInstanceToFront();
                    return;
                }

                try
                {
                    // Run with modern WPF UI
                    RunWithUI(args);
                }
                finally
                {
                    _mutex?.ReleaseMutex();
                    _mutex?.Dispose();
                }
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
                    services.AddSingleton<IServiceStatusService, ServiceStatusService>();
                    
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
                    services.AddSingleton<IServiceStatusService, ServiceStatusService>();
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

        private static void BringExistingInstanceToFront()
        {
            // Try to find and activate the existing CimianStatus window
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("cimistatus");
                foreach (var process in processes)
                {
                    if (process.Id != System.Diagnostics.Process.GetCurrentProcess().Id && process.MainWindowHandle != IntPtr.Zero)
                    {
                        // Import Windows API functions for window manipulation
                        ShowWindow(process.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(process.MainWindowHandle);
                        break;
                    }
                }
            }
            catch
            {
                // Ignore errors when trying to bring window to front
            }
        }

        // Windows API imports for window manipulation
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        
        private const int SW_RESTORE = 9;
    }
}
