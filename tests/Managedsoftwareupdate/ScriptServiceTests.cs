using Xunit;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for ScriptService - PowerShell script execution for preflight/postflight.
/// </summary>
public class ScriptServiceTests : IDisposable
{
    private readonly string _testScriptDir;
    private readonly ScriptService _service;

    public ScriptServiceTests()
    {
        _testScriptDir = Path.Combine(Path.GetTempPath(), "CimianTests", "Scripts", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testScriptDir);
        _service = new ScriptService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testScriptDir))
            {
                Directory.Delete(_testScriptDir, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    #region ExecuteScriptAsync Tests

    [Fact]
    public async Task ExecuteScriptAsync_EmptyScript_ReturnsSuccess()
    {
        var (success, output) = await _service.ExecuteScriptAsync("");

        Assert.True(success);
        Assert.Contains("No script content", output);
    }

    [Fact]
    public async Task ExecuteScriptAsync_WhitespaceScript_ReturnsSuccess()
    {
        var (success, output) = await _service.ExecuteScriptAsync("   \n\t  ");

        Assert.True(success);
        Assert.Contains("No script content", output);
    }

    [Fact]
    public async Task ExecuteScriptAsync_SimpleOutput_CapturesOutput()
    {
        var script = "Write-Output 'Hello from PowerShell'";

        var (success, output) = await _service.ExecuteScriptAsync(script);

        Assert.True(success);
        Assert.Contains("Hello from PowerShell", output);
    }

    [Fact]
    public async Task ExecuteScriptAsync_MultipleOutputs_CapturesAll()
    {
        var script = @"
Write-Output 'Line 1'
Write-Output 'Line 2'
Write-Output 'Line 3'
";

        var (success, output) = await _service.ExecuteScriptAsync(script);

        Assert.True(success);
        Assert.Contains("Line 1", output);
        Assert.Contains("Line 2", output);
        Assert.Contains("Line 3", output);
    }

    [Fact]
    public async Task ExecuteScriptAsync_WithError_ReturnsFailure()
    {
        var script = "Write-Error 'Something went wrong'";

        var (success, output) = await _service.ExecuteScriptAsync(script);

        Assert.False(success);
        // PowerShell error output format varies - just check that something was captured
        Assert.Contains("Something went wrong", output);
    }

    [Fact]
    public async Task ExecuteScriptAsync_ThrowsException_ReturnsFailure()
    {
        var script = "throw 'Intentional exception'";

        var (success, output) = await _service.ExecuteScriptAsync(script);

        Assert.False(success);
        Assert.Contains("Intentional", output);
    }

    [Fact]
    public async Task ExecuteScriptAsync_VariableAssignment_Works()
    {
        var script = @"
$x = 42
$y = 'hello'
Write-Output ""x=$x, y=$y""
";

        var (success, output) = await _service.ExecuteScriptAsync(script);

        Assert.True(success);
        Assert.Contains("x=42", output);
        Assert.Contains("y=hello", output);
    }

    [Fact]
    public async Task ExecuteScriptAsync_ConditionalLogic_Works()
    {
        var script = @"
$value = 10
if ($value -gt 5) {
    Write-Output 'Greater than 5'
} else {
    Write-Output 'Not greater than 5'
}
";

        var (success, output) = await _service.ExecuteScriptAsync(script);

        Assert.True(success);
        Assert.Contains("Greater than 5", output);
    }

    #endregion

    #region ExecuteScriptFileAsync Tests

    [Fact]
    public async Task ExecuteScriptFileAsync_FileNotFound_ReturnsFailure()
    {
        var nonExistentPath = Path.Combine(_testScriptDir, "nonexistent.ps1");

        var (success, output) = await _service.ExecuteScriptFileAsync(nonExistentPath);

        Assert.False(success);
        Assert.Contains("Script file not found", output);
    }

    [Fact]
    public async Task ExecuteScriptFileAsync_ValidScript_ExecutesSuccessfully()
    {
        var scriptPath = Path.Combine(_testScriptDir, "valid.ps1");
        File.WriteAllText(scriptPath, "Write-Output 'Script file executed'");

        var (success, output) = await _service.ExecuteScriptFileAsync(scriptPath);

        Assert.True(success);
        Assert.Contains("Script file executed", output);
    }

    [Fact]
    public async Task ExecuteScriptFileAsync_EmptyScriptFile_ReturnsSuccess()
    {
        var scriptPath = Path.Combine(_testScriptDir, "empty.ps1");
        File.WriteAllText(scriptPath, "");

        var (success, output) = await _service.ExecuteScriptFileAsync(scriptPath);

        Assert.True(success);
    }

    #endregion

    #region RunPreflightAsync Tests

    [Fact]
    public async Task RunPreflightAsync_ReturnsResult()
    {
        // Preflight may or may not exist depending on machine state
        var (success, output) = await _service.RunPreflightAsync();

        // Should either find and run script, or report not found - both are valid
        Assert.NotNull(output);
        // Success depends on whether script exists and runs successfully
    }

    #endregion

    #region RunPostflightAsync Tests

    [Fact]
    public async Task RunPostflightAsync_ReturnsResult()
    {
        // Postflight may or may not exist depending on machine state
        var (success, output) = await _service.RunPostflightAsync();

        // Should either find and run script, or report not found - both are valid
        Assert.NotNull(output);
        // Success depends on whether script exists and runs successfully
    }

    #endregion

    #region Cancellation Token Tests

    [Fact]
    public async Task ExecuteScriptAsync_WithCancellationToken_RespectsToken()
    {
        using var cts = new CancellationTokenSource();
        var script = "Write-Output 'Quick script'";

        var (success, output) = await _service.ExecuteScriptAsync(script, cts.Token);

        Assert.True(success);
        Assert.Contains("Quick script", output);
    }

    #endregion

    #region ExecuteScriptWithDetailsAsync - CIMIAN-WARNING marker tests

    [Fact]
    public async Task ExecuteScriptWithDetailsAsync_EmptyScript_ReturnsSuccessWithNullWarning()
    {
        var result = await _service.ExecuteScriptWithDetailsAsync("");

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.WarningMessage);
    }

    [Fact]
    public async Task ExecuteScriptWithDetailsAsync_NoMarker_ExitZero_HasNullWarning()
    {
        var script = "Write-Output 'just a normal message'";

        var result = await _service.ExecuteScriptWithDetailsAsync(script);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.WarningMessage);
    }

    [Fact]
    public async Task ExecuteScriptWithDetailsAsync_StdoutMarker_CapturesMessage()
    {
        var script = "Write-Output 'CIMIAN-WARNING: needs-followup'";

        var result = await _service.ExecuteScriptWithDetailsAsync(script);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("needs-followup", result.WarningMessage);
    }

    [Fact]
    public async Task ExecuteScriptWithDetailsAsync_StderrMarker_CapturesMessage()
    {
        // Write-Error routes through stderr; the marker must still be extracted.
        var script = "Write-Error 'CIMIAN-WARNING: stderr-reason'";

        var result = await _service.ExecuteScriptWithDetailsAsync(script);

        // Write-Error sets non-zero exit code, so Success=false, but marker is still extracted.
        Assert.Equal("stderr-reason", result.WarningMessage);
    }

    [Fact]
    public async Task ExecuteScriptWithDetailsAsync_MarkerWithLeadingWhitespace_StillExtracted()
    {
        var script = "Write-Output '   CIMIAN-WARNING:   spaced-reason   '";

        var result = await _service.ExecuteScriptWithDetailsAsync(script);

        Assert.True(result.Success);
        // Regex captures the message and trims surrounding whitespace.
        Assert.Equal("spaced-reason", result.WarningMessage);
    }

    [Fact]
    public async Task ExecuteScriptWithDetailsAsync_MarkerInMultilineOutput_StillExtracted()
    {
        var script = @"
Write-Output 'preamble line 1'
Write-Output 'preamble line 2'
Write-Output 'CIMIAN-WARNING: buried-in-output'
Write-Output 'trailing line'
";

        var result = await _service.ExecuteScriptWithDetailsAsync(script);

        Assert.True(result.Success);
        Assert.Equal("buried-in-output", result.WarningMessage);
    }

    [Fact]
    public async Task ExecuteScriptWithDetailsAsync_FirstMarkerWins_WhenMultiplePresent()
    {
        var script = @"
Write-Output 'CIMIAN-WARNING: first-reason'
Write-Output 'CIMIAN-WARNING: second-reason'
";

        var result = await _service.ExecuteScriptWithDetailsAsync(script);

        Assert.True(result.Success);
        Assert.Equal("first-reason", result.WarningMessage);
    }

    [Fact]
    public async Task ExecuteScriptWithDetailsAsync_HardFailure_NoMarker_HasNullWarning()
    {
        var script = "exit 1";

        var result = await _service.ExecuteScriptWithDetailsAsync(script);

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.WarningMessage);
    }

    [Fact]
    public async Task ExecuteScriptWithDetailsAsync_PreservesExitCode()
    {
        var script = "exit 42";

        var result = await _service.ExecuteScriptWithDetailsAsync(script);

        Assert.Equal(42, result.ExitCode);
        Assert.False(result.Success);
    }

    #endregion
}
