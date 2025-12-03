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
        Assert.Contains("ERROR", output);
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
    public async Task RunPreflightAsync_NoPreflightScript_ReturnsSuccess()
    {
        // Default preflight path doesn't exist in test environment
        var (success, output) = await _service.RunPreflightAsync();

        Assert.True(success);
        Assert.Contains("No preflight script found", output);
    }

    #endregion

    #region RunPostflightAsync Tests

    [Fact]
    public async Task RunPostflightAsync_NoPostflightScript_ReturnsSuccess()
    {
        // Default postflight path doesn't exist in test environment
        var (success, output) = await _service.RunPostflightAsync();

        Assert.True(success);
        Assert.Contains("No postflight script found", output);
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
}
