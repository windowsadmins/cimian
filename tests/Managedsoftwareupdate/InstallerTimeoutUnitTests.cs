using System.Diagnostics;
using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// installer_timeout is a number of seconds. A pkgsinfo that sets it to 2 has its
/// installer killed after about two seconds, not two minutes (#159).
/// </summary>
public class InstallerTimeoutUnitTests
{
    [Fact]
    public async Task ItemInstallerTimeout_IsSeconds()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 30 127.0.0.1 >nul",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var service = new InstallerService(new CimianConfig { InstallerTimeout = 900 });

        var clock = Stopwatch.StartNew();
        var (success, output) = await service.RunProcessWithTimeoutAsync(
            startInfo, "SlowApp", CancellationToken.None, itemTimeoutSeconds: 2);
        clock.Stop();

        Assert.False(success);
        Assert.Contains("timed out", output);
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 20);
    }
}
