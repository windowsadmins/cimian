using Cimian.Core.Services;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;
using Moq;
using Xunit;

namespace Cimian.GUI.ManagedSoftwareCenter.Tests;

public class BulkInstallTests
{
    private readonly Mock<IInstallInfoService> catalog = new();
    private readonly Mock<ISelfServiceManifestService> manifest = new();
    private readonly Mock<ITriggerService> trigger = new();
    private readonly Mock<IAlertService> alerts = new();
    private readonly List<string> events = [];

    private async Task<SoftwareViewModel> Create(params InstallableItem[] items)
    {
        catalog.Setup(x => x.GetBrowseableItemsAsync()).ReturnsAsync(items);
        catalog.Setup(x => x.GetCategoriesAsync()).ReturnsAsync(Array.Empty<string>());
        catalog.Setup(x => x.LoadAsync()).ReturnsAsync(new InstallInfo());
        manifest.Setup(x => x.GetAllRequestsAsync()).ReturnsAsync(new SelfServiceManifest());
        manifest.Setup(x => x.AddInstallRequestAsync(It.IsAny<string>()))
            .Callback<string>(name => events.Add("save:" + name)).Returns(Task.CompletedTask);
        trigger.Setup(x => x.TriggerInstallItemsAsync(It.IsAny<IEnumerable<string>>(), null))
            .Callback<IEnumerable<string>, IEnumerable<string>?>((names, _) => events.Add("trigger:" + string.Join(",", names)))
            .Returns(Task.CompletedTask);
        var vm = new SoftwareViewModel(catalog.Object, manifest.Object, trigger.Object,
            Mock.Of<IIconService>(), alerts.Object);
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task SavesEverySelectedRequestBeforeOneBatchAndPreservesTenantNames()
    {
        var first = new InstallableItem { Name = "tenant-one-chatgpt" };
        var second = new InstallableItem { Name = "tenant-two-chatgpt" };
        var vm = await Create(first, second);
        vm.SetInstallSelection([first, second, first]);
        Assert.Equal("Install selected (2)", vm.InstallSelectedLabel);
        await vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "save:tenant-one-chatgpt", "save:tenant-two-chatgpt", "trigger:tenant-one-chatgpt,tenant-two-chatgpt" }, events);
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        Assert.Equal(ItemStatus.InstallRequested, first.Status);
        Assert.True(second.UserRequested);
        trigger.Verify(x => x.TriggerInstallItemAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task DeclinedAlertSkipsOnlyThatItem()
    {
        var first = new InstallableItem { Name = "declined", PreinstallAlert = new AlertInfo() };
        var second = new InstallableItem { Name = "approved" };
        var vm = await Create(first, second);
        vm.SetInstallSelection([first, second]);
        await vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "save:approved", "trigger:approved" }, events);
    }

    [Fact]
    public async Task DeclinedBlockingApplicationWarningDoesNotQueueTheItem()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var item = new InstallableItem { Name = "blocked", BlockingApplications = [process.ProcessName + ".exe"] };
        var vm = await Create(item);
        vm.SetInstallSelection([item]);
        await vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.Empty(events);
        alerts.Verify(x => x.ShowWarningAsync("Blocking Applications Running", It.IsAny<string>(),
            "Install Anyway", "Cancel"), Times.Once);
    }

    [Fact]
    public async Task FilteringClearsSelectionInsteadOfInstallingHiddenApps()
    {
        var first = new InstallableItem { Name = "first" };
        var vm = await Create(first);
        vm.SetInstallSelection([first]);
        vm.SearchText = "no-match";
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        await vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.Empty(events);
    }

    [Fact]
    public async Task CurrentInstalledAndPendingItemsAreNotSelected()
    {
        var current = new InstallableItem { Name = "current", Installed = true, Version = "1", InstalledVersion = "1" };
        var pending = new InstallableItem { Name = "pending" };
        var vm = await Create(current, pending);
        pending.Status = ItemStatus.InstallRequested;
        vm.SetInstallSelection([current, pending]);
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        await vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.Empty(events);
    }

    [Fact]
    public async Task FailedManifestWriteDoesNotDiscardOtherRequests()
    {
        var first = new InstallableItem { Name = "failed" };
        var second = new InstallableItem { Name = "saved" };
        var vm = await Create(first, second);
        manifest.Setup(x => x.AddInstallRequestAsync("failed")).ThrowsAsync(new IOException("test failure"));
        vm.SetInstallSelection([first, second]);
        await vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "save:saved", "trigger:saved" }, events);
        alerts.Verify(x => x.ShowInfoAsync("Some requests could not be saved", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task IndividualRequestRefreshesTileBeforeWaitingForWatcher()
    {
        var item = new InstallableItem { Name = "one" };
        var vm = await Create(item);
        var oldItems = vm.Items;
        var consumed = new TaskCompletionSource();
        trigger.Setup(x => x.TriggerInstallItemAsync("one", false)).Returns(consumed.Task);
        var pending = vm.InstallItemCommand.ExecuteAsync(item);
        Assert.False(pending.IsCompleted);
        Assert.NotSame(oldItems, vm.Items);
        Assert.Equal(ItemStatus.InstallRequested, vm.Items.Single().Status);
        Assert.False(vm.Items.Single().ShowInstallAction);
        consumed.SetResult();
        await pending;
    }

    [Fact]
    public async Task WithdrawnOfferDuringAlertIsNotRequested()
    {
        var item = new InstallableItem { Name = "withdrawn", PreinstallAlert = new AlertInfo() };
        var vm = await Create(item);
        var answer = new TaskCompletionSource<bool>();
        alerts.Setup(x => x.ShowAlertAsync(item.PreinstallAlert, "Install Alert")).Returns(answer.Task);
        vm.SetInstallSelection([item]);
        var pending = vm.InstallSelectedCommand.ExecuteAsync(null);
        catalog.Setup(x => x.GetBrowseableItemsAsync()).ReturnsAsync(Array.Empty<InstallableItem>());
        await vm.LoadAsync();
        answer.SetResult(true);
        await pending;
        Assert.Empty(events);
    }

    [Fact]
    public async Task TriggerFailureLeavesSavedRequestsPendingAndExplainsRetry()
    {
        var item = new InstallableItem { Name = "saved" };
        var vm = await Create(item);
        trigger.Setup(x => x.TriggerInstallItemsAsync(It.IsAny<IEnumerable<string>>(), null))
            .ThrowsAsync(new IOException("test failure"));
        vm.SetInstallSelection([item]);
        await vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(ItemStatus.InstallRequested, item.Status);
        alerts.Verify(x => x.ShowInfoAsync("Could not start installation", It.IsAny<string>()), Times.Once);
    }
}
