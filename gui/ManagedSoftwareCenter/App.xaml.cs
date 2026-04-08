// Cimian Software Center - WinUI 3 Application Entry Point
// Self-service software installation for end users

using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppLifecycle;
using Cimian.GUI.ManagedSoftwareCenter.Services;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;

namespace Cimian.GUI.ManagedSoftwareCenter;

/// <summary>
/// Main application class for Cimian Software Center
/// </summary>
public partial class App : Application
{
    private readonly ServiceProvider _services;
    private static Window? s_mainWindow;

    /// <summary>
    /// Gets the current application instance
    /// </summary>
    public static new App Current => (App)Application.Current;

    /// <summary>
    /// Gets the service provider for dependency injection
    /// </summary>
    public IServiceProvider Services => _services;

    /// <summary>
    /// Gets the main application window
    /// </summary>
    public static Window MainWindow => s_mainWindow!;

    public App()
    {
        this.InitializeComponent();
        
        this.UnhandledException += App_UnhandledException;

        // Configure dependency injection
        var services = new ServiceCollection();
        
        // Services
        services.AddSingleton<ISelfServiceManifestService, SelfServiceManifestService>();
        services.AddSingleton<IInstallInfoService, InstallInfoService>();
        services.AddSingleton<IProgressPipeClient, ProgressServer>();  // TCP server for Go reporter
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<ITriggerService, TriggerService>();
        services.AddSingleton<ICatalogCacheService, CatalogCacheService>();
        services.AddSingleton<IIconService, IconService>();
        services.AddSingleton<IAlertService, AlertService>();
        services.AddSingleton<IUpdateTrackingService, UpdateTrackingService>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IBrandingService, BrandingService>();

        // ViewModels
        services.AddTransient<ShellViewModel>();
        services.AddTransient<SoftwareViewModel>();
        services.AddTransient<CategoriesViewModel>();
        services.AddTransient<MyItemsViewModel>();
        services.AddTransient<UpdatesViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<ItemDetailViewModel>();
        services.AddTransient<ProgressViewModel>();
        
        _services = services.BuildServiceProvider();
    }

    /// <summary>
    /// Gets a service from the DI container
    /// </summary>
    public static T GetService<T>() where T : class
    {
        return Current.Services.GetRequiredService<T>();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Check for protocol activation (cimian:// deep link)
            Uri? pendingUri = null;
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activationArgs.Kind == ExtendedActivationKind.Protocol &&
                activationArgs.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
            {
                pendingUri = protocolArgs.Uri;
                LogStartup($"Protocol activation: {pendingUri}");
            }

            LogStartup("OnLaunched starting - creating MainWindow...");
            s_mainWindow = new MainWindow();
            LogStartup("MainWindow created - activating...");
            s_mainWindow.Activate();
            LogStartup("MainWindow activated - initializing services...");

            // Initialize services
            var notificationService = Services.GetRequiredService<INotificationService>();
            notificationService.Initialize();

            var progressClient = Services.GetRequiredService<IProgressPipeClient>();
            await progressClient.ConnectAsync();

            // Handle pending deep link after window is ready
            if (pendingUri != null)
            {
                HandleProtocolActivation(pendingUri);
            }

            LogStartup("OnLaunched complete.");
        }
        catch (Exception ex)
        {
            LogStartup($"CRASH in OnLaunched: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
        }
    }

    /// <summary>
    /// Handles cimian:// protocol URIs.
    /// Supported: cimian://showitem?name=AppName, cimian://updates, cimian://history, cimian://categories
    /// </summary>
    public static void HandleProtocolActivation(Uri uri)
    {
        if (s_mainWindow is not MainWindow mainWindow) return;

        var host = uri.Host.ToLowerInvariant();
        switch (host)
        {
            case "showitem":
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var itemName = query["name"];
                if (!string.IsNullOrEmpty(itemName))
                    mainWindow.NavigateToItemDetail(itemName);
                break;
            case "updates":
                mainWindow.NavigateToPage("updates");
                break;
            case "history":
                mainWindow.NavigateToPage("history");
                break;
            case "categories":
                mainWindow.NavigateToPage("categories");
                break;
            default:
                mainWindow.NavigateToPage("software");
                break;
        }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogStartup($"UNHANDLED: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n");
        e.Handled = true;
    }

    private static void LogStartup(string message)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ManagedInstalls", "msc_crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
