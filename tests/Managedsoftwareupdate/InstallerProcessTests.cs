using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Cimian.CLI.managedsoftwareupdate.Services;
using FluentAssertions;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// InstallerProcess starts installers on a desktop of their own when the run is in a
/// user's session, so a GUI installer's windows never reach that user's desktop. These
/// tests start real GUI processes and look for their windows on both desktops.
/// </summary>
public class InstallerProcessTests
{
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(1, false, false, true)]
    [InlineData(0, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(1, false, true, false)]
    public void ShouldIsolate_OnlyPlainLaunchesOutsideSessionZero(int sessionId, bool useShellExecute, bool redirectStdin, bool expected)
    {
        var startInfo = new ProcessStartInfo("x.exe")
        {
            UseShellExecute = useShellExecute,
            RedirectStandardInput = redirectStdin,
        };

        InstallerProcess.ShouldIsolate(startInfo, sessionId).Should().Be(expected);
    }

    [Fact]
    public async Task GuiInstaller_ShowsNoWindowOnCurrentDesktop()
    {
        var marker = NewMarker();
        using var started = InstallerProcess.Start(MessageBoxStartInfo(marker), null, null, isolate: true);
        try
        {
            started.DesktopName.Should().NotBeNull();
            (await WaitForWindowAsync(started.DesktopName!, marker)).Should().BeTrue("the installer's window should exist on its own desktop");
            Windows.VisibleOnCurrentDesktop(marker).Should().BeFalse();
        }
        finally
        {
            KillTree(started.Process);
        }
    }

    [Fact]
    public async Task ProcessStartedByInstaller_ShowsNoWindowOnCurrentDesktop()
    {
        // A bootstrapper that starts its UI in a second process, as Inno Setup and many
        // other wrappers do: STARTUPINFO's show flag would not reach it, the desktop does.
        var marker = NewMarker();
        var inner = Convert.ToBase64String(Encoding.Unicode.GetBytes(MessageBoxScript(marker)));
        var startInfo = PowerShellStartInfo($"Start-Process powershell.exe -ArgumentList '-NoProfile','-EncodedCommand','{inner}' -Wait");

        using var started = InstallerProcess.Start(startInfo, null, null, isolate: true);
        try
        {
            (await WaitForWindowAsync(started.DesktopName!, marker)).Should().BeTrue("the child's window should exist on the private desktop");
            Windows.VisibleOnCurrentDesktop(marker).Should().BeFalse();
        }
        finally
        {
            KillTree(started.Process);
        }
    }

    [Fact]
    public async Task InstallerWaitingForInput_EndsAtTimeoutAndIsKilled()
    {
        var marker = NewMarker();
        using var started = InstallerProcess.Start(MessageBoxStartInfo(marker), null, null, isolate: true);
        (await WaitForWindowAsync(started.DesktopName!, marker)).Should().BeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var wait = async () => await started.WaitForExitAsync(cts.Token);
        await wait.Should().ThrowAsync<OperationCanceledException>();

        started.Process.Kill(entireProcessTree: true);
        started.Process.WaitForExit(10000).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExitCodeAndOutput_AreCapturedAsBefore(bool isolate)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"echo first& echo second& echo problem 1>&2& exit /b 7\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var stdout = new List<string?>();
        var stderr = new List<string?>();

        using var started = InstallerProcess.Start(startInfo, d => { lock (stdout) stdout.Add(d); }, d => { lock (stderr) stderr.Add(d); }, isolate);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await started.WaitForExitAsync(cts.Token);

        started.Process.ExitCode.Should().Be(7);
        stdout.Where(l => l != null).Select(l => l!.Trim()).Should().Equal("first", "second");
        stdout.Last().Should().BeNull("end of stream is reported as null, as DataReceivedEventArgs does");
        stderr.Where(l => l != null).Select(l => l!.Trim()).Should().Equal("problem");
        (started.DesktopName != null).Should().Be(isolate);
    }

    [Fact]
    public async Task EnvironmentAndWorkingDirectory_ArePassedToTheProcess()
    {
        var directory = Path.GetTempPath().TrimEnd('\\');
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"echo %CIMIAN_TEST_VALUE%& cd\"",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["CIMIAN_TEST_VALUE"] = "from-start-info";
        var stdout = new List<string>();

        using var started = InstallerProcess.Start(startInfo, d => { if (d != null) lock (stdout) stdout.Add(d.Trim()); }, null, isolate: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await started.WaitForExitAsync(cts.Token);

        stdout.Should().Equal("from-start-info", directory);
    }

    [Fact]
    public void Start_MissingFile_ThrowsLikeProcessStart()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var start = () => InstallerProcess.Start(startInfo, null, null, isolate: true);

        start.Should().Throw<System.ComponentModel.Win32Exception>().Which.NativeErrorCode.Should().Be(2);
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\setup.exe", "/S /D=C:\\x", "\"C:\\Program Files\\App\\setup.exe\" /S /D=C:\\x")]
    [InlineData("\"C:\\a b\\setup.exe\"", "", "\"C:\\a b\\setup.exe\"")]
    [InlineData("msiexec.exe", "/i \"C:\\a b.msi\" /qn", "\"msiexec.exe\" /i \"C:\\a b.msi\" /qn")]
    public void BuildCommandLine_UsesArgumentsVerbatim(string fileName, string arguments, string expected)
    {
        InstallerProcess.BuildCommandLine(new ProcessStartInfo(fileName, arguments)).Should().Be(expected);
    }

    [Fact]
    public async Task BuildCommandLine_ArgumentListRoundTripsThroughTheChild()
    {
        var arguments = new[] { "plain", "with space", "quote\"inside", @"trailing\", @"C:\dir with space\", "" };
        var startInfo = PowerShellStartInfo(null);
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("$args | ForEach-Object { '[' + $_ + ']' }");
        foreach (var a in arguments) startInfo.ArgumentList.Add(a);

        var expected = await CaptureAsync(startInfo, isolate: false);
        var actual = await CaptureAsync(startInfo, isolate: true);

        actual.Should().Equal(expected, "the private-desktop launch must quote ArgumentList as Process.Start does");
    }

    private static async Task<List<string>> CaptureAsync(ProcessStartInfo startInfo, bool isolate)
    {
        var lines = new List<string>();
        using var started = InstallerProcess.Start(startInfo, d => { if (d != null) lock (lines) lines.Add(d); }, null, isolate);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await started.WaitForExitAsync(cts.Token);
        return lines;
    }

    private static string NewMarker() => $"cimian-test-{Guid.NewGuid():N}";

    private static string MessageBoxScript(string marker)
        => $"Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.MessageBox]::Show('Installer waiting for input', '{marker}')";

    private static ProcessStartInfo MessageBoxStartInfo(string marker) => PowerShellStartInfo(MessageBoxScript(marker));

    /// <summary>A GUI launch as the installer paths make it.</summary>
    private static ProcessStartInfo PowerShellStartInfo(string? command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (command != null)
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
        }
        return startInfo;
    }

    private static async Task<bool> WaitForWindowAsync(string desktopName, string title)
    {
        var deadline = DateTime.UtcNow + WindowWait;
        while (DateTime.UtcNow < deadline)
        {
            if (Windows.ExistsOnDesktop(desktopName, title)) return true;
            await Task.Delay(200);
        }
        return false;
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
        process.WaitForExit(10000);
    }

    private static class Windows
    {
        public static bool VisibleOnCurrentDesktop(string title)
        {
            var found = false;
            EnumWindows((hwnd, _) =>
            {
                if (IsWindowVisible(hwnd) && TitleOf(hwnd) == title) found = true;
                return !found;
            }, IntPtr.Zero);
            return found;
        }

        public static bool ExistsOnDesktop(string desktopName, string title)
        {
            var desktop = OpenDesktopW(desktopName, 0, false, DESKTOP_READOBJECTS | DESKTOP_ENUMERATE);
            if (desktop == IntPtr.Zero) return false;
            try
            {
                var found = false;
                EnumDesktopWindows(desktop, (hwnd, _) =>
                {
                    if (TitleOf(hwnd) == title) found = true;
                    return !found;
                }, IntPtr.Zero);
                return found;
            }
            finally
            {
                CloseDesktop(desktop);
            }
        }

        private static string TitleOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private const uint DESKTOP_READOBJECTS = 0x0001;
        private const uint DESKTOP_ENUMERATE = 0x0040;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll")]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);
    }
}
