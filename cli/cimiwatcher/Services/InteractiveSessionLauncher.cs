using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Cimian.CLI.Cimiwatcher.Services;

/// <summary>
/// Launches a process into the interactive console session as the logged-in user.
///
/// CimianWatcher runs as LocalSystem in Session 0, which has no visible desktop. A
/// child started with the service's own token (e.g. plain Process.Start) lands in
/// Session 0 too and is invisible to the console user. This helper resolves the
/// active console session, duplicates the session user's token to a primary token,
/// builds their environment block, and calls CreateProcessAsUser targeting
/// winsta0\default so the process appears on the user's desktop.
///
/// Every failure is logged and returns false — the caller (a background service)
/// must never crash because a UI could not be shown.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InteractiveSessionLauncher
{
    /// <summary>
    /// Attempts to launch <paramref name="exePath"/> in the active console session.
    /// Returns false (after logging) when there is no interactive session or any
    /// step fails. Does not fall back to a Session-0 launch.
    /// </summary>
    public static bool TryLaunch(string exePath, ILogger logger)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();

        // 0xFFFFFFFF = no session currently attached to the console.
        // 0 = Session 0 (the non-interactive services session) — nothing to show a UI on.
        if (sessionId == 0xFFFFFFFF || sessionId == 0)
        {
            logger.LogInformation(
                "No interactive console session (WTSGetActiveConsoleSessionId returned {SessionId}) - skipping UI launch",
                sessionId);
            return false;
        }

        logger.LogInformation("Resolved active console session id: {SessionId}", sessionId);

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        var pi = new PROCESS_INFORMATION();

        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                LogWin32(logger, "WTSQueryUserToken");
                return false;
            }

            var sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>();

            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ALL_ACCESS,
                    ref sa,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary,
                    out primaryToken))
            {
                LogWin32(logger, "DuplicateTokenEx");
                return false;
            }

            // Non-fatal: without the user's environment block the process still runs,
            // it just inherits a minimal environment. Proceed with IntPtr.Zero on failure.
            if (!CreateEnvironmentBlock(out envBlock, primaryToken, false))
            {
                LogWin32(logger, "CreateEnvironmentBlock");
                envBlock = IntPtr.Zero;
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf<STARTUPINFO>();
            si.lpDesktop = @"winsta0\default";

            uint creationFlags = CREATE_UNICODE_ENVIRONMENT | NORMAL_PRIORITY_CLASS;
            string? workingDir = Path.GetDirectoryName(exePath);

            // lpCommandLine is null: cimistatus takes no arguments, and passing the
            // application name via lpApplicationName avoids the mutable-buffer contract.
            bool ok = CreateProcessAsUser(
                primaryToken,
                exePath,
                null,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                creationFlags,
                envBlock,
                workingDir,
                ref si,
                out pi);

            if (!ok)
            {
                LogWin32(logger, "CreateProcessAsUser");
                return false;
            }

            logger.LogInformation(
                "Launched {Exe} in session {SessionId} (PID: {Pid})",
                Path.GetFileName(exePath), sessionId, pi.dwProcessId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to launch {Exe} in interactive session", exePath);
            return false;
        }
        finally
        {
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    private static void LogWin32(ILogger logger, string api)
    {
        int err = Marshal.GetLastWin32Error();
        logger.LogWarning(
            "{Api} failed (Win32 error 0x{Error:X8}: {Message})",
            api, err, new Win32Exception(err).Message);
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation
    }

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType,
        out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
