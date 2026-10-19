using Cimian.GUI.ManagedSoftwareCenter.Services;
using Cimian.Core.Services;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;
using Moq;
using Xunit;

namespace Cimian.GUI.ManagedSoftwareCenter.Tests;

public class ShellNavigationTests
{
    [Fact]
    public void NormalBuildDoesNotContainSimulatedInstallFixture()
    {
        Assert.Null(typeof(ShellViewModel).Assembly.GetType("Cimian.GUI.ManagedSoftwareCenter.HubUiFixture"));
    }

    [Theory]
    [InlineData("software")]
    [InlineData("categories")]
    [InlineData("detail")]
    [InlineData("updates")]
    public void TargetedInstallKeepsCurrentPage(string currentPage)
    {
        var trigger = new Mock<ITriggerService>();
        trigger.SetupGet(x => x.IsItemScopedOperation).Returns(true);
        trigger.SetupGet(x => x.CurrentOperationLabel).Returns("Installing First, Second...");
        var shell = CreateShell(trigger);
        shell.NavigateToPage = currentPage;
        var navigationEvents = 0;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(shell.NavigateToPage)) navigationEvents++;
        };

        trigger.Raise(x => x.OperationStatusChanged += null, trigger.Object, true);

        Assert.Equal(currentPage, shell.NavigateToPage);
        Assert.Equal(0, navigationEvents);
        Assert.True(shell.IsInstalling);
        Assert.False(shell.ShowGlobalBanner);
        Assert.Equal("Installing First, Second...", shell.ProgressMessage);
    }

    [Fact]
    public void BroadOperationStillOpensUpdates()
    {
        var trigger = new Mock<ITriggerService>();
        var shell = CreateShell(trigger);
        shell.NavigateToPage = "software";
        trigger.Raise(x => x.OperationStatusChanged += null, trigger.Object, true);
        Assert.Equal("updates", shell.NavigateToPage);
        Assert.True(shell.ShowGlobalBanner);
    }

    private static ShellViewModel CreateShell(Mock<ITriggerService> trigger) => new(
        Mock.Of<IInstallInfoService>(), Mock.Of<ISelfServiceManifestService>(),
        Mock.Of<IProgressPipeClient>(), trigger.Object, Mock.Of<INotificationService>(),
        Mock.Of<ICatalogCacheService>(), Mock.Of<IPreferencesService>(), Mock.Of<IAlertService>());
}
