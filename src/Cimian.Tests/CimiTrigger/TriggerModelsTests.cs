using CimianTools.CimiTrigger.Models;
using CimianTools.CimiTrigger.Services;
using Xunit;

namespace Cimian.Tests.CimiTrigger;

/// <summary>
/// Tests for TriggerModels.
/// </summary>
public class TriggerModelsTests
{
    [Fact]
    public void DiagnosticResult_DefaultValues_AreCorrect()
    {
        var result = new DiagnosticResult();

        Assert.False(result.IsAdmin);
        Assert.False(result.ServiceRunning);
        Assert.False(result.DirectoryOK);
        Assert.False(result.ExecutablesOK);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void DiagnosticResult_CanAddIssues()
    {
        var result = new DiagnosticResult
        {
            IsAdmin = true,
            ServiceRunning = false,
            DirectoryOK = true,
            ExecutablesOK = true
        };
        result.Issues.Add("Service not running");
        result.Issues.Add("Another issue");

        Assert.True(result.IsAdmin);
        Assert.False(result.ServiceRunning);
        Assert.Equal(2, result.Issues.Count);
        Assert.Contains("Service not running", result.Issues);
    }

    [Fact]
    public void ElevationResult_Success_Properties()
    {
        var result = new ElevationResult
        {
            Success = true,
            Method = "PowerShell RunAs"
        };

        Assert.True(result.Success);
        Assert.Equal("PowerShell RunAs", result.Method);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ElevationResult_Failure_Properties()
    {
        var result = new ElevationResult
        {
            Success = false,
            Error = "Access denied",
            Method = "Scheduled Task"
        };

        Assert.False(result.Success);
        Assert.Equal("Access denied", result.Error);
        Assert.Equal("Scheduled Task", result.Method);
    }

    [Theory]
    [InlineData(TriggerMode.Gui, 0)]
    [InlineData(TriggerMode.Headless, 1)]
    public void TriggerMode_EnumValues_AreCorrect(TriggerMode mode, int expectedValue)
    {
        Assert.Equal(expectedValue, (int)mode);
    }
}
