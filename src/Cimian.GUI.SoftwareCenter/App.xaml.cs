// Cimian Software Center - WPF Application Entry Point
// Self-service software installation for end users

using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Cimian.GUI.SoftwareCenter.Services;
using Cimian.GUI.SoftwareCenter.ViewModels;

namespace Cimian.GUI.SoftwareCenter;

/// <summary>
/// Main application class for Cimian Software Center
/// </summary>
public partial class App : Application
{
    private readonly ServiceProvider _services;

    /// <summary>
    /// Gets the current application instance
    /// </summary>
    public static new App Current => (App)Application.Current;

    /// <summary>
    /// Gets the service provider for dependency injection
    /// </summary>
    public IServiceProvider Services => _services;

    public App()
    {
        // Configure dependency injection
        var services = new ServiceCollection();
        
        // Services
        services.AddSingleton<ISelfServiceManifestService, SelfServiceManifestService>();
        services.AddSingleton<IInstallInfoService, InstallInfoService>();
        services.AddSingleton<IProgressPipeClient, ProgressPipeClient>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<ITriggerService, TriggerService>();
        services.AddSingleton<ICatalogCacheService, CatalogCacheService>();

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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Initialize services
        var notificationService = Services.GetRequiredService<INotificationService>();
        notificationService.Initialize();

        var progressClient = Services.GetRequiredService<IProgressPipeClient>();
        _ = progressClient.ConnectAsync();
    }
}
