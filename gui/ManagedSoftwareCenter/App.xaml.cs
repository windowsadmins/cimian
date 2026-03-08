// Cimian Software Center - WinUI 3 Application Entry Point
// Self-service software installation for end users

using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
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

        // ViewModels
        services.AddTransient<ShellViewModel>();
        services.AddTransient<SoftwareViewModel>();
        services.AddTransient<CategoriesViewModel>();
        services.AddTransient<MyItemsViewModel>();
        services.AddTransient<UpdatesViewModel>();
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
        s_mainWindow = new MainWindow();
        s_mainWindow.Activate();

        // Initialize services
        var notificationService = Services.GetRequiredService<INotificationService>();
        notificationService.Initialize();

        var progressClient = Services.GetRequiredService<IProgressPipeClient>();
        await progressClient.ConnectAsync();
        
        // Write to log file to verify server started
        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
            "Cimian", "msc-startup.log");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
        await System.IO.File.AppendAllTextAsync(logPath, 
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ProgressServer started on port 19847{Environment.NewLine}");
    }
}
