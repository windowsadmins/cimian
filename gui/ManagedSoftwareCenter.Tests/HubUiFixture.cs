// Compiled only by the explicit HubUiTest build. No real installs or IPC.
using Cimian.Core.Services;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Cimian.GUI.ManagedSoftwareCenter;

internal static class HubUiFixture
{
    public static void Configure(IServiceCollection services)
    {
        var items = new List<InstallableItem>
        {
            new() { Name = "fixture-alpha", DisplayName = "Test Alpha", Version = "1", Uninstallable = true },
            new() { Name = "fixture-beta", DisplayName = "Test Beta", Version = "1", Uninstallable = true },
            new() { Name = "fixture-gamma", DisplayName = "Test Gamma", Version = "1", Uninstallable = true },
            new() { Name = "fixture-current", DisplayName = "Already Installed", Version = "1", InstalledVersion = "1", Installed = true }
        };
        var info = new InstallInfo { OptionalInstalls = items };
        var requests = new SelfServiceManifest();
        var catalog = new Mock<IInstallInfoService>();
        catalog.Setup(x => x.LoadAsync()).ReturnsAsync(info);
        catalog.Setup(x => x.GetBrowseableItemsAsync()).ReturnsAsync(items);
        catalog.Setup(x => x.GetOptionalInstallsAsync()).ReturnsAsync(items);
        catalog.Setup(x => x.GetAllItemsAsync()).ReturnsAsync(items);
        catalog.Setup(x => x.GetCategoriesAsync()).ReturnsAsync(Array.Empty<string>());
        catalog.Setup(x => x.GetManagedInstallsAsync()).ReturnsAsync(() => info.ManagedInstalls);
        catalog.Setup(x => x.GetRemovalsAsync()).ReturnsAsync(Array.Empty<InstallableItem>());
        catalog.Setup(x => x.GetManagedUpdatesAsync()).ReturnsAsync(Array.Empty<string>());
        catalog.Setup(x => x.GetProblemItemsAsync()).ReturnsAsync(Array.Empty<ProblemItem>());
        catalog.Setup(x => x.GetItemByNameAsync(It.IsAny<string>()))
            .ReturnsAsync((string name) => items.FirstOrDefault(item => item.Name == name));
        var manifest = new Mock<ISelfServiceManifestService>();
        manifest.Setup(x => x.GetAllRequestsAsync()).ReturnsAsync(requests);
        manifest.Setup(x => x.AddInstallRequestAsync(It.IsAny<string>()))
            .Callback<string>(name => { requests.ManagedInstalls.Add(name); Record("saved", [name]); })
            .Returns(Task.CompletedTask);
        var trigger = new Mock<ITriggerService>();
        trigger.SetupGet(x => x.IsItemScopedOperation).Returns(true);
        async Task Install(IEnumerable<string> names)
        {
            var batch = names.ToArray();
            Record("targeted-batch", batch);
            trigger.Raise(x => x.OperationStatusChanged += null, trigger.Object, true);
            await Task.Delay(1500);
            foreach (var item in items.Where(item => batch.Contains(item.Name)))
            {
                item.Installed = true;
                item.InstalledVersion = item.Version;
                item.Status = ItemStatus.Installed;
            }
            catalog.Raise(x => x.InstallInfoChanged += null, catalog.Object, info);
            trigger.Raise(x => x.OperationStatusChanged += null, trigger.Object, false);
            Record("simulated-complete", batch);
        }
        trigger.Setup(x => x.TriggerInstallItemsAsync(It.IsAny<IEnumerable<string>>(), null))
            .Returns((IEnumerable<string> names, IEnumerable<string>? _) => Install(names));
        trigger.Setup(x => x.TriggerInstallItemAsync(It.IsAny<string>(), false))
            .Returns((string name, bool _) => Install([name]));
        var branding = new Mock<IBrandingService>();
        branding.SetupGet(x => x.AppTitle).Returns("Hub UI TEST — simulated installs only");
        services.AddSingleton(catalog.Object);
        services.AddSingleton(manifest.Object);
        services.AddSingleton(trigger.Object);
        services.AddSingleton(branding.Object);
        services.AddSingleton(Mock.Of<IProgressPipeClient>());
        services.AddSingleton(Mock.Of<INotificationService>());
        services.AddSingleton(Mock.Of<ICatalogCacheService>());
        services.AddSingleton(Mock.Of<IPreferencesService>());
        services.AddSingleton(Mock.Of<IIconService>());
        services.AddSingleton(Mock.Of<IUpdateTrackingService>());
    }

    private static void Record(string kind, string[] names) => File.AppendAllText(
        Path.Combine(AppContext.BaseDirectory, "ui-fixture-events.jsonl"),
        System.Text.Json.JsonSerializer.Serialize(new { kind, names }) + Environment.NewLine);
}
