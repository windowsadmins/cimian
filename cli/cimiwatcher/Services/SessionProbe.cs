using System.Runtime.InteropServices;

namespace Cimian.CLI.Cimiwatcher.Services;

/// <summary>
/// Detects whether an interactive user is currently logged onto the active
/// console session. Used by FileWatcherService to decide whether to launch
/// the WPF cimistatus.exe (logged-in) or rely on the PLAP credential provider
/// loaded by LogonUI.exe (pre-logon).
/// </summary>
internal static class SessionProbe
{
    /// <summary>
    /// True when a real interactive user token is available on the active
    /// console session. False when the active session is the Winlogon screen
    /// (no user logged on yet) or no session is active.
    /// </summary>
    public static bool IsInteractiveUserLoggedOn()
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFFu) return false; // no session attached

        if (!WTSQueryUserToken(sessionId, out IntPtr token))
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_NO_TOKEN (1008) = pre-logon: there is no user token to
            // hand back. Anything else is an unexpected failure but should
            // be treated as "no user" so we err on the side of letting the
            // PLAP own the screen.
            _ = err;
            return false;
        }

        try
        {
            return token != IntPtr.Zero;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
