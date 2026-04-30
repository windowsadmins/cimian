using System.Runtime.InteropServices;

namespace Cimian.CLI.Cimiwatcher.Services;

/// <summary>
/// Detects whether any interactive user is currently signed onto this machine.
/// Used by FileWatcherService to decide whether bootstrap-style triggers should
/// run now or defer until the LoginWindow (Munki-parity behaviour).
///
/// Enumerates every session via WTSEnumerateSessions rather than only the
/// physical console. WTSGetActiveConsoleSessionId only sees the local console
/// and returns false on RDP, fast-user-switched, or disconnected sessions —
/// which would let bootstrap fire while a user is clearly working.
/// </summary>
internal static class SessionProbe
{
    /// <summary>
    /// True when at least one session has a real user signed in (Active or
    /// Disconnected with a non-empty username). False only when no user is
    /// signed in anywhere — the LogonUI/secure-desktop state.
    /// </summary>
    public static bool IsInteractiveUserLoggedOn()
    {
        IntPtr pSessionInfo = IntPtr.Zero;
        int count = 0;

        if (!WTSEnumerateSessions(WTS_CURRENT_SERVER_HANDLE, 0, 1, out pSessionInfo, out count))
            return false;

        try
        {
            int stride = Marshal.SizeOf<WTS_SESSION_INFO>();
            IntPtr current = pSessionInfo;
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(current);
                current = IntPtr.Add(current, stride);

                // Only sessions that are signed-in count: Active = at desktop,
                // Disconnected = signed in but RDP/console disconnected. Both
                // mean a user has live processes and a profile loaded.
                if (info.State != WTS_CONNECTSTATE_CLASS.WTSActive &&
                    info.State != WTS_CONNECTSTATE_CLASS.WTSDisconnected)
                    continue;

                // Session 0 is the services session — never an interactive user.
                if (info.SessionId == 0) continue;

                if (TryGetSessionUserName(info.SessionId, out string user) &&
                    !string.IsNullOrWhiteSpace(user))
                    return true;
            }
            return false;
        }
        finally
        {
            if (pSessionInfo != IntPtr.Zero) WTSFreeMemory(pSessionInfo);
        }
    }

    private static bool TryGetSessionUserName(uint sessionId, out string userName)
    {
        userName = string.Empty;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(WTS_CURRENT_SERVER_HANDLE, sessionId,
                    WTS_INFO_CLASS.WTSUserName, out buffer, out _))
                return false;

            var s = Marshal.PtrToStringUni(buffer);
            userName = s ?? string.Empty;
            return true;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    private enum WTS_INFO_CLASS
    {
        WTSInitialProgram,
        WTSApplicationName,
        WTSWorkingDirectory,
        WTSOEMId,
        WTSSessionId,
        WTSUserName,
        WTSWinStationName,
        WTSDomainName,
        WTSConnectState,
        WTSClientBuildNumber,
        WTSClientName,
        WTSClientDirectory,
        WTSClientProductId,
        WTSClientHardwareId,
        WTSClientAddress,
        WTSClientDisplay,
        WTSClientProtocolType,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [DllImport("Wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        int reserved,
        int version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("Wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        uint sessionId,
        WTS_INFO_CLASS infoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
