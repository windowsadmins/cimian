using System.Diagnostics;
using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Which installer exit codes count as a successful install. Runs a real process
/// that exits with the code under test (#138).
/// </summary>
public class InstallerExitCodeTests
{
    private static Task<(bool Success, string Output)> RunExitingWith(int code, IEnumerable<int>? successCodes = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c exit {code}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var service = new InstallerService(new CimianConfig { InstallerTimeout = 30 });
        return service.RunProcessWithTimeoutAsync(startInfo, "ExitCodeApp", CancellationToken.None, null, successCodes);
    }

    [Fact]
    public async Task RebootInitiated1641_IsSuccess()
    {
        var (success, output) = await RunExitingWith(1641);

        Assert.True(success, output);
        Assert.Contains("started a reboot", output);
    }

    [Fact]
    public async Task RebootRequired3010_IsSuccess()
    {
        var (success, output) = await RunExitingWith(3010);

        Assert.True(success, output);
        Assert.Contains("reboot is required", output);
    }

    [Fact]
    public async Task Zero_IsSuccess()
    {
        var (success, _) = await RunExitingWith(0);

        Assert.True(success);
    }

    [Fact]
    public async Task FatalError1603_IsFailure()
    {
        var (success, output) = await RunExitingWith(1603);

        Assert.False(success);
        Assert.Contains("Exit code: 1603", output);
    }

    [Fact]
    public async Task DeclaredSuccessCode_IsSuccess()
    {
        var (success, _) = await RunExitingWith(2, successCodes: [2]);

        Assert.True(success);
    }
}
