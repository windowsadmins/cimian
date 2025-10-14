using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
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

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AllocConsole();
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);
        const int SM_REMOTESESSION = 0x1000;

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                // Check if running at login screen
                bool isLoginScreenMode = args.Contains("--login-screen");
                bool isSystemContext = WindowsIdentity.GetCurrent().IsSystem;
                bool hasBootstrapFile = File.Exists(@"C:\ProgramData\ManagedInstalls\.cimian.bootstrap");
                
                // Special handling for login screen mode
                if (isLoginScreenMode || (isSystemContext && hasBootstrapFile))
                {
                    RunAtLoginScreen();
                    return;
                }
                
                // CimianStatus is a GUI-only application
                // Check for single instance in normal mode
                _mutex = new Mutex(true, "CimianStatusSingleInstance", out bool isNewInstance);
                
                if (!isNewInstance)
                {
                    // Another instance is already running, try to bring it to front
                    BringExistingInstanceToFront();
                    return;
                }

                // Run with modern WPF UI
                RunWithUI(args);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\ProgramData\ManagedInstalls\logs\cimistatus_error.log", 
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Fatal error: {ex}\n");
            }
            finally
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
        }

        private static void RunAtLoginScreen()
        {
            // Set DPI awareness for login screen
            SetProcessDPIAware();
            
            // Check if we're in a remote session
            bool isRemoteSession = GetSystemMetrics(SM_REMOTESESSION) != 0;
            if (isRemoteSession)
            {
                // Cannot show UI in remote session at login screen
                return;
            }
            
            // Create simplified application for login screen
            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose
            };
            
            // Create a simplified window that works at login screen
            var window = new LoginScreenWindow();
            
            // Show window and run
            window.Show();
            app.MainWindow = window;
            app.Run();
        }

        private static void RunWithUI(string[] args)
        {
            // Create host builder for dependency injection
            var hostBuilder = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // Register services
                    services.AddSingleton<IStatusServer, StatusServer>();
                    services.AddSingleton<IEventStreamService, EventStreamService>();
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

            // Create WPF application and ensure resources are initialized
            var app = new App();
            
            // IMPORTANT: Set Startup event handler BEFORE calling Run()
            // This ensures resources are fully loaded when MainWindow is created
            app.Startup += (sender, e) =>
            {
                try
                {
                    var mainWindow = host.Services.GetRequiredService<MainWindow>();
                    mainWindow.Show();
                    app.MainWindow = mainWindow;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to start CimianStatus: {ex.Message}\n\nSee Event Log for details.", 
                        "CimianStatus Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    app.Shutdown(1);
                }
            };
            
            app.Run();
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
