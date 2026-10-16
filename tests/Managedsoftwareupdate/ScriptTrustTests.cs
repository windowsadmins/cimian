using Xunit;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// SYSTEM must not run a preflight, postflight or condition script that a standard user
/// could have written. The test process is an ordinary account, so every file it creates
/// has exactly the ownership a planted script would.
/// </summary>
public class ScriptTrustTests : IDisposable
{
    private readonly string _dir;

    public ScriptTrustTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CimianTests", "ScriptTrust", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FlightScriptWritableByNonAdminsIsRefusedAndNeverRuns()
    {
        var ran = Path.Combine(_dir, "ran.txt");
        var script = Path.Combine(_dir, "preflight.ps1");
        await File.WriteAllTextAsync(script, $"Set-Content -Path '{ran}' -Value yes");

        var (success, output) = await new ScriptService().RunFlightScriptAsync(
            "preflight", new[] { script }, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.False(success);
        Assert.Contains("Refusing", output);
        Assert.False(File.Exists(ran), "the untrusted script must not have run");
    }

    [Fact]
    public async Task MissingFlightScriptIsStillSuccess()
    {
        var (success, _) = await new ScriptService().RunFlightScriptAsync(
            "postflight", new[] { Path.Combine(_dir, "absent.ps1") }, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.True(success);
    }

    [Fact]
    public void ConditionScriptsWritableByNonAdminsAreSkipped()
    {
        File.WriteAllText(Path.Combine(_dir, "planted.ps1"), "Write-Output 'role=admin'");

        Assert.Empty(ManifestService.ConditionScripts(_dir));
    }
}
