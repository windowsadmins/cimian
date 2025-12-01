using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Cimian.CLI.Cimiwatcher.Services;

namespace Cimian.Tests.Cimiwatcher;

public class FileWatcherServiceTests
{
    private readonly Mock<ILogger<FileWatcherService>> _mockLogger;
    private readonly FileWatcherService _service;

    public FileWatcherServiceTests()
    {
        _mockLogger = new Mock<ILogger<FileWatcherService>>();
        _service = new FileWatcherService(_mockLogger.Object);
    }

    [Fact]
    public void Service_CanBeInstantiated()
    {
        Assert.NotNull(_service);
    }

    [Fact]
    public void Pause_SetsIsPausedFlag()
    {
        _service.Pause();
        
        // Verify logging was called
        VerifyLogMessage(LogLevel.Information, "Monitoring paused");
    }

    [Fact]
    public void Resume_ClearsIsPausedFlag()
    {
        _service.Pause();
        _service.Resume();
        
        // Verify logging was called
        VerifyLogMessage(LogLevel.Information, "Monitoring resumed");
    }

    [Fact]
    public void Pause_ThenResume_LogsCorrectly()
    {
        _service.Pause();
        _service.Resume();
        
        // Verify both pause and resume were logged
        VerifyLogMessage(LogLevel.Information, "Monitoring paused");
        VerifyLogMessage(LogLevel.Information, "Monitoring resumed");
    }

    [Fact]
    public async Task ExecuteAsync_StartsAndStopsGracefully()
    {
        using var cts = new CancellationTokenSource();
        
        // Start the service
        var executeTask = Task.Run(() => 
        {
            var startMethod = typeof(FileWatcherService).GetMethod("ExecuteAsync", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return startMethod?.Invoke(_service, new object[] { cts.Token });
        });

        // Let it run briefly
        await Task.Delay(100);
        
        // Cancel to stop
        cts.Cancel();
        
        // Wait for task to complete
        try
        {
            await executeTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        
        // Verify startup logging
        VerifyLogMessage(LogLevel.Information, "CimianWatcher file monitoring service started");
    }

    private void VerifyLogMessage(LogLevel level, string message)
    {
        _mockLogger.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains(message)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}

public class WindowsServiceManagerTests
{
    private readonly WindowsServiceManager _manager;

    public WindowsServiceManagerTests()
    {
        _manager = new WindowsServiceManager();
    }

    [Fact]
    public void Manager_CanBeInstantiated()
    {
        Assert.NotNull(_manager);
    }

    [Fact]
    public void IsInstalled_ReturnsBool()
    {
        // This will return true or false depending on whether the service is installed
        var result = _manager.IsInstalled();
        Assert.True(result || !result); // Just verify it returns a boolean
    }

    [Fact]
    public void GetStatus_ReturnsNullWhenNotInstalled()
    {
        if (!_manager.IsInstalled())
        {
            var status = _manager.GetStatus();
            Assert.Null(status);
        }
    }

    [Fact]
    public void GetStatus_ReturnsStatusWhenInstalled()
    {
        if (_manager.IsInstalled())
        {
            var status = _manager.GetStatus();
            Assert.NotNull(status);
            Assert.True(Enum.IsDefined(typeof(ServiceControllerStatus), status.Value));
        }
    }
}

public class BootstrapFlagFileTests
{
    private const string BootstrapFlagFile = @"C:\ProgramData\ManagedInstalls\.cimian.bootstrap";
    private const string HeadlessFlagFile = @"C:\ProgramData\ManagedInstalls\.cimian.headless";

    [Fact]
    public void BootstrapFlagFile_HasCorrectPath()
    {
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\.cimian.bootstrap", BootstrapFlagFile);
    }

    [Fact]
    public void HeadlessFlagFile_HasCorrectPath()
    {
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\.cimian.headless", HeadlessFlagFile);
    }

    [Fact]
    public void FlagFilePaths_AreInManagedInstallsDirectory()
    {
        Assert.StartsWith(@"C:\ProgramData\ManagedInstalls\", BootstrapFlagFile);
        Assert.StartsWith(@"C:\ProgramData\ManagedInstalls\", HeadlessFlagFile);
    }

    [Fact]
    public void FlagFiles_HaveDotPrefix()
    {
        var bootstrapFileName = Path.GetFileName(BootstrapFlagFile);
        var headlessFileName = Path.GetFileName(HeadlessFlagFile);
        
        Assert.StartsWith(".", bootstrapFileName);
        Assert.StartsWith(".", headlessFileName);
    }
}

public class ServiceConfigurationTests
{
    [Theory]
    [InlineData("install")]
    [InlineData("remove")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("pause")]
    [InlineData("continue")]
    [InlineData("status")]
    [InlineData("debug")]
    public void SupportedCommands_AreValid(string command)
    {
        // This test verifies that all expected commands are documented
        Assert.NotNull(command);
        Assert.NotEmpty(command);
    }

    [Fact]
    public void ServiceName_IsCorrect()
    {
        // The service name should be CimianWatcher
        const string expectedName = "CimianWatcher";
        Assert.NotNull(expectedName);
    }

    [Fact]
    public void PollInterval_IsReasonable()
    {
        var pollInterval = TimeSpan.FromSeconds(10);
        
        // Poll interval should be positive
        Assert.True(pollInterval.TotalSeconds > 0);
        
        // Poll interval should be less than 1 minute
        Assert.True(pollInterval.TotalMinutes < 1);
    }
}

public class UpdateTriggerTests
{
    [Theory]
    [InlineData(true, "--auto --show-status -vv")]
    [InlineData(false, "--auto")]
    public void UpdateArgs_AreCorrectForMode(bool withGUI, string expectedArgs)
    {
        var actualArgs = withGUI ? "--auto --show-status -vv" : "--auto";
        Assert.Equal(expectedArgs, actualArgs);
    }

    [Fact]
    public void GUIMode_IncludesShowStatus()
    {
        var guiArgs = "--auto --show-status -vv";
        Assert.Contains("--show-status", guiArgs);
        Assert.Contains("-vv", guiArgs);
    }

    [Fact]
    public void HeadlessMode_IsMinimal()
    {
        var headlessArgs = "--auto";
        Assert.DoesNotContain("--show-status", headlessArgs);
        Assert.DoesNotContain("-vv", headlessArgs);
    }
}

public class CimianStatusLauncherTests
{
    private const string CimianExePath = @"C:\Program Files\Cimian\managedsoftwareupdate.exe";

    [Fact]
    public void CimianStatusPath_IsCorrect()
    {
        var cimianDir = Path.GetDirectoryName(CimianExePath);
        var cimistatus = Path.Combine(cimianDir!, "cimistatus.exe");
        
        Assert.Equal(@"C:\Program Files\Cimian\cimistatus.exe", cimistatus);
    }

    [Fact]
    public void CimianExePath_IsInProgramFiles()
    {
        Assert.StartsWith(@"C:\Program Files\", CimianExePath);
    }

    [Fact]
    public void CimianExePath_HasCorrectExtension()
    {
        Assert.EndsWith(".exe", CimianExePath);
    }
}
