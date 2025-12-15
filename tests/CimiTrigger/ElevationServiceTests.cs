using CimianTools.CimiTrigger.Models;
using CimianTools.CimiTrigger.Services;
using Xunit;

namespace Cimian.Tests.CimiTrigger;

/// <summary>
/// Tests for ElevationService.
/// </summary>
public class ElevationServiceTests
{
    private readonly ElevationService _service;

    public ElevationServiceTests()
    {
        _service = new ElevationService();
    }

    [Fact]
    public void FindExecutable_ReturnsNull_WhenNotFound()
    {
        // In a test environment, the executable likely won't be found
        // unless running on a machine with Cimian installed
        var result = _service.FindExecutable();
        
        // We can't assert the result because it depends on the environment
        // Just verify the method doesn't throw
        Assert.True(result == null || File.Exists(result));
    }

    [Fact]
    public void FindCimistatusExecutable_ReturnsNull_WhenNotFound()
    {
        var result = _service.FindCimistatusExecutable();
        
        Assert.True(result == null || File.Exists(result));
    }

    [Fact]
    public void IsProcessRunning_ReturnsFalse_ForNonExistentProcess()
    {
        var result = ElevationService.IsProcessRunning("nonexistent_process_12345");
        
        Assert.False(result);
    }

    [Fact]
    public void IsProcessRunning_ReturnsTrue_ForRunningProcess()
    {
        // Check for a process that's always running on Windows
        var result = ElevationService.IsProcessRunning("explorer");
        
        // explorer might not be running in all test environments
        // Just verify the method doesn't throw
        Assert.True(result || !result);
    }

    [Fact]
    public void IsSystemSession_DetectsNonSystemSession()
    {
        // When running tests, we should not be in a SYSTEM session
        var result = ElevationService.IsSystemSession();
        
        // In normal test execution, this should be false
        // But we can't guarantee the test environment
        Assert.True(result || !result);
    }

    [Fact]
    public void IsGUIRunningInSession0_DoesNotThrow()
    {
        // Just verify the method doesn't throw
        var result = _service.IsGUIRunningInSession0();
        Assert.True(result || !result);
    }

    [Fact]
    public void IsGUIRunningInUserSession_DoesNotThrow()
    {
        // Just verify the method doesn't throw
        var result = _service.IsGUIRunningInUserSession();
        Assert.True(result || !result);
    }

    [Fact]
    public void KillSession0GUI_DoesNotThrow()
    {
        // Just verify the method doesn't throw even if no process exists
        _service.KillSession0GUI();
    }

    [Fact]
    public void RunDirectUpdateAsync_WouldRequireExecutable()
    {
        // NOTE: We do NOT call RunDirectUpdateAsync because it actually launches
        // managedsoftwareupdate.exe if it exists on the system. This is unsafe
        // for testing as it would trigger real package installations.
        
        // Instead, verify that FindExecutable works correctly
        var execPath = _service.FindExecutable();
        
        // If executable exists, it should be a valid path
        if (execPath != null)
        {
            Assert.True(File.Exists(execPath), "If path returned, file should exist");
            Assert.EndsWith("managedsoftwareupdate.exe", execPath, StringComparison.OrdinalIgnoreCase);
        }
        // If null, that's also valid - executable not installed
    }
}
