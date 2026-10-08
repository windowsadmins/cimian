using System.Diagnostics;
using Xunit;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Preflight and postflight must be bounded.
/// </summary>
/// <remarks>
/// The regression these exist for is not a slow script, it is a machine that stops
/// updating permanently. ScriptService always had the timeout machinery - it kills the
/// process tree and returns TimeoutExitCode - but no caller ever supplied a deadline,
/// so it could not fire. postflight runs the reports collector, which shells out to
/// schtasks.exe; on a host whose Task Scheduler has partly wedged that never returns.
/// The session then never ends, the single-instance mutex is never released, and every
/// scheduled run afterwards exits immediately with "Another instance is running".
///
/// Measured on two lab workstations: 101 and 88 minutes hung, no packages installed for
/// hours, and nothing reported as failed - from the outside the machine looked idle.
/// </remarks>
public class FlightScriptTimeoutTests : IDisposable
{
    private readonly string _dir;
    private readonly ScriptService _service = new();

    public FlightScriptTimeoutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "FlightTimeout", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void BothFlightScriptsHaveADeadline()
    {
        // A deadline that is zero or absent is the bug: the machinery below it is
        // correct and simply never runs.
        Assert.True(ScriptService.PreflightTimeout > TimeSpan.Zero);
        Assert.True(ScriptService.PostflightTimeout > TimeSpan.Zero);
    }

    [Fact]
    public void DeadlinesAreGenerousEnoughNotToCutOffRealWork()
    {
        // These are safeguards against a script that will never finish, not limits on
        // one that is merely slow. postflight does the reporting work, so it gets the
        // longer budget of the two.
        Assert.True(ScriptService.PreflightTimeout >= TimeSpan.FromMinutes(5));
        Assert.True(ScriptService.PostflightTimeout >= ScriptService.PreflightTimeout);

        // But still bounded well under the hours that were actually observed.
        Assert.True(ScriptService.PostflightTimeout <= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task AScriptThatNeverFinishesIsAbandonedAndItsTreeKilled()
    {
        // Stands in for the real case: a child that never returns. The point is that
        // the call returns at all, and that nothing is left running behind it.
        var script = Path.Combine(_dir, "hang.ps1");
        await File.WriteAllTextAsync(script, "Start-Sleep -Seconds 600");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var sw = Stopwatch.StartNew();

        var (success, output) = await _service.ExecuteScriptFileAsync(script, cts.Token);

        sw.Stop();

        Assert.False(success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
            $"the run should be abandoned at its deadline, took {sw.Elapsed}");
        Assert.Contains("timed out", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AScriptThatFinishesNormallyIsUnaffected()
    {
        // The guard must not change the ordinary path.
        var script = Path.Combine(_dir, "ok.ps1");
        await File.WriteAllTextAsync(script, "Write-Output 'done'; exit 0");

        var (success, output) = await _service.ExecuteScriptFileAsync(script, CancellationToken.None);

        Assert.True(success, output);
        Assert.Contains("done", output);
    }
}
