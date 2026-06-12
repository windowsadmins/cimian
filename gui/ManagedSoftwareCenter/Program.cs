using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace Cimian.GUI.ManagedSoftwareCenter;

public static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls", "msc_crash.log");

    [STAThread]
    static void Main(string[] args)
    {
        // App.UnhandledException only sees XAML-thread exceptions. Exceptions
        // escaping async void handlers on threadpool threads (e.g. service
        // event callbacks) kill the process with no trace in msc_crash.log —
        // log them here so post-mortems don't depend on Windows Error Reporting.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"FATAL (background): {e.ExceptionObject}");

        try
        {
            Log("Starting COM wrappers initialization...");

            ComWrappersSupport.InitializeComWrappers();

            Log("COM wrappers initialized. Starting application...");

            Application.Start(p =>
            {
                try
                {
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    Log("Creating App instance...");
                    _ = new App();
                    Log("App instance created successfully.");
                }
                catch (Exception ex)
                {
                    Log($"CRASH in App creation: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
                    throw;
                }
            });

            Log("Application exited normally.");
        }
        catch (Exception ex)
        {
            Log($"CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            Environment.Exit(1);
        }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n"); }
        catch { }
    }
}
