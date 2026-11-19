using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Cimian.Core.Services;
using Microsoft.Win32.SafeHandles;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Starts an installer or script process so that no window it opens reaches the
/// interactive user's desktop.
///
/// A run started as SYSTEM in session 0 (the scheduled run, or one requested from
/// Managed Software Center) never puts a window on the user's desktop. A run started
/// from a terminal in the user's session did: CreateNoWindow only suppresses a console
/// window, and STARTUPINFO's SW_HIDE (which .NET passes for WindowStyle.Hidden) only
/// affects the first window of the process it is given -- an Inno Setup installer, for
/// one, shows its wizard and message boxes from a child process it starts itself.
///
/// So outside session 0 the process is created on a desktop of its own in the current
/// window station. Nothing ever switches to that desktop, so its windows, and the
/// windows of every process it starts (they inherit the desktop), are never shown.
/// An installer that waits for input there waits until the installer timeout, exactly
/// as it does in session 0. In session 0 the process is started as before.
///
/// Output capture, exit codes and Kill(entireProcessTree) behave as with Process.Start:
/// the process is created suspended, wrapped with Process.GetProcessById while it cannot
/// yet have exited, and then resumed.
/// </summary>
internal sealed class InstallerProcess : IDisposable
{
    private readonly Task? _stdoutDrained;
    private readonly Task? _stderrDrained;
    private readonly SafeProcessHandle? _processHandle;
    private readonly IntPtr _desktop;
    private readonly Stream? _stdout;
    private readonly Stream? _stderr;

    /// <summary>The started process. Valid for Id, WaitForExitAsync, ExitCode and Kill.</summary>
    public Process Process { get; }

    /// <summary>The name of the private desktop the process runs on, or null when it was started normally.</summary>
    public string? DesktopName { get; }

    private static readonly object CreateProcessLock = new();

    private InstallerProcess(Process process)
    {
        Process = process;
    }

    private InstallerProcess(Process process, string desktopName, IntPtr desktop, SafeProcessHandle handle,
        Stream? stdout, Stream? stderr, Task? stdoutDrained, Task? stderrDrained)
    {
        Process = process;
        DesktopName = desktopName;
        _desktop = desktop;
        _processHandle = handle;
        _stdout = stdout;
        _stderr = stderr;
        _stdoutDrained = stdoutDrained;
        _stderrDrained = stderrDrained;
    }

    /// <summary>
    /// Starts the process, isolating it on a private desktop when this run is in an
    /// interactive session. The output callbacks receive each line, then null at end
    /// of stream, matching DataReceivedEventArgs.Data.
    /// </summary>
    public static InstallerProcess Start(ProcessStartInfo startInfo, Action<string?>? onStdout, Action<string?>? onStderr)
        => Start(startInfo, onStdout, onStderr, ShouldIsolate(startInfo, Process.GetCurrentProcess().SessionId));

    internal static InstallerProcess Start(ProcessStartInfo startInfo, Action<string?>? onStdout, Action<string?>? onStderr, bool isolate)
    {
        if (isolate)
        {
            var desktopName = $"Cimian-{Environment.ProcessId}-{Guid.NewGuid():N}";
            var desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
            if (desktop != IntPtr.Zero)
            {
                try
                {
                    return StartOnDesktop(startInfo, onStdout, onStderr, desktopName, desktop);
                }
                catch
                {
                    CloseDesktop(desktop);
                    throw;
                }
            }

            // Not being able to create a desktop is not a reason to skip the install:
            // fall back to starting the process the way it was always started.
            ConsoleLogger.Debug($"Could not create a private desktop for the installer (error {Marshal.GetLastWin32Error()}); starting it on the current desktop");
        }

        return StartNormally(startInfo, onStdout, onStderr);
    }

    /// <summary>
    /// Isolate only outside session 0, and only for the plain redirected launches the
    /// installer paths use. Session 0 has no user desktop to protect, so it keeps the
    /// launch it always had.
    /// </summary>
    internal static bool ShouldIsolate(ProcessStartInfo startInfo, int sessionId)
        => sessionId != 0 && !startInfo.UseShellExecute && !startInfo.RedirectStandardInput;

    /// <summary>
    /// Waits for the process to exit and for its output to be read to the end, as
    /// Process.WaitForExitAsync does for a process with redirected output.
    /// </summary>
    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await Process.WaitForExitAsync(cancellationToken);
        if (_stdoutDrained != null) await _stdoutDrained.WaitAsync(cancellationToken);
        if (_stderrDrained != null) await _stderrDrained.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        Process.Dispose();
        _processHandle?.Dispose();
        _stdout?.Dispose();
        _stderr?.Dispose();
        if (_desktop != IntPtr.Zero) CloseDesktop(_desktop);
    }

    private static InstallerProcess StartNormally(ProcessStartInfo startInfo, Action<string?>? onStdout, Action<string?>? onStderr)
    {
        var process = new Process { StartInfo = startInfo };
        if (onStdout != null) process.OutputDataReceived += (_, e) => onStdout(e.Data);
        if (onStderr != null) process.ErrorDataReceived += (_, e) => onStderr(e.Data);
        try
        {
            process.Start();
            if (startInfo.RedirectStandardOutput) process.BeginOutputReadLine();
            if (startInfo.RedirectStandardError) process.BeginErrorReadLine();
        }
        catch
        {
            process.Dispose();
            throw;
        }
        return new InstallerProcess(process);
    }

    private static InstallerProcess StartOnDesktop(ProcessStartInfo startInfo, Action<string?>? onStdout, Action<string?>? onStderr,
        string desktopName, IntPtr desktop)
    {
        AnonymousPipeServerStream? stdoutPipe = null;
        AnonymousPipeServerStream? stderrPipe = null;
        var environment = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var inheritedHandles = IntPtr.Zero;
        var pi = default(PROCESS_INFORMATION);
        try
        {
            var si = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    lpDesktop = desktopName,
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = GetStdHandle(STD_INPUT_HANDLE),
                    hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE),
                    hStdError = GetStdHandle(STD_ERROR_HANDLE),
                },
            };

            // The pipes are created not inheritable; their client ends are made
            // inheritable only for the CreateProcess call below, so a process started
            // elsewhere in the meantime does not pick them up and hold the pipe open.
            if (startInfo.RedirectStandardOutput)
            {
                stdoutPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
                si.StartupInfo.hStdOutput = stdoutPipe.ClientSafePipeHandle.DangerousGetHandle();
            }
            if (startInfo.RedirectStandardError)
            {
                stderrPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
                si.StartupInfo.hStdError = stderrPipe.ClientSafePipeHandle.DangerousGetHandle();
            }
            if (startInfo.WindowStyle != ProcessWindowStyle.Normal)
            {
                si.StartupInfo.dwFlags |= STARTF_USESHOWWINDOW;
                si.StartupInfo.wShowWindow = startInfo.WindowStyle switch
                {
                    ProcessWindowStyle.Hidden => 0,
                    ProcessWindowStyle.Minimized => 2,
                    ProcessWindowStyle.Maximized => 3,
                    _ => 1,
                };
            }

            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(startInfo.Environment));
            var flags = CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT
                | (startInfo.CreateNoWindow ? CREATE_NO_WINDOW : 0);
            var workingDirectory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory;

            bool created;
            int error;
            lock (CreateProcessLock)
            {
                if (stdoutPipe != null) SetHandleInformation(si.StartupInfo.hStdOutput, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
                if (stderrPipe != null) SetHandleInformation(si.StartupInfo.hStdError, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);

                // The child inherits its standard handles and nothing else, so it never
                // holds open a pipe that belongs to another process's output.
                var handles = new[] { si.StartupInfo.hStdInput, si.StartupInfo.hStdOutput, si.StartupInfo.hStdError }
                    .Where(IsInheritable)
                    .Distinct()
                    .ToArray();
                if (handles.Length > 0)
                {
                    inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                    Marshal.Copy(handles, 0, inheritedHandles, handles.Length);
                    attributeList = CreateHandleListAttribute(inheritedHandles, handles.Length);
                    si.lpAttributeList = attributeList;
                }

                created = CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, handles.Length > 0, flags,
                    environment, workingDirectory, ref si, out pi);
                error = Marshal.GetLastWin32Error();
            }

            stdoutPipe?.DisposeLocalCopyOfClientHandle();
            stderrPipe?.DisposeLocalCopyOfClientHandle();

            if (!created)
            {
                // Same wording as Process.Start, so callers' failure output does not change.
                var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
                throw new Win32Exception(error,
                    $"An error occurred trying to start process '{startInfo.FileName}' with working directory '{cwd}'. {new Win32Exception(error).Message}");
            }

            var handle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            Process process;
            try
            {
                process = Process.GetProcessById(pi.dwProcessId);
            }
            catch
            {
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hThread);
                handle.Dispose();
                throw;
            }

            var stdoutDrained = stdoutPipe != null ? DrainAsync(stdoutPipe, startInfo.StandardOutputEncoding, onStdout) : null;
            var stderrDrained = stderrPipe != null ? DrainAsync(stderrPipe, startInfo.StandardErrorEncoding, onStderr) : null;

            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);

            return new InstallerProcess(process, desktopName, desktop, handle, stdoutPipe, stderrPipe, stdoutDrained, stderrDrained);
        }
        catch
        {
            stdoutPipe?.Dispose();
            stderrPipe?.Dispose();
            throw;
        }
        finally
        {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
        }
    }

    private static bool IsInheritable(IntPtr handle)
        => handle != IntPtr.Zero && handle != new IntPtr(-1)
           && GetHandleInformation(handle, out var handleFlags) && (handleFlags & HANDLE_FLAG_INHERIT) != 0;

    private static IntPtr CreateHandleListAttribute(IntPtr handles, int count)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(error);
        }
        if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles,
                new IntPtr(IntPtr.Size * count), IntPtr.Zero, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(error);
        }
        return list;
    }

    private static Task DrainAsync(Stream pipe, Encoding? encoding, Action<string?>? onLine) => Task.Run(async () =>
    {
        try
        {
            using var reader = new StreamReader(pipe, encoding ?? Console.OutputEncoding, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                onLine?.Invoke(line);
            }
        }
        catch (IOException)
        {
            // A broken pipe is the end of the output.
        }
        catch (ObjectDisposedException)
        {
        }
        onLine?.Invoke(null);
    });

    /// <summary>
    /// Builds the command line the way Process.Start does on Windows: the file name in
    /// quotes, then either Arguments verbatim or each ArgumentList entry quoted for
    /// CommandLineToArgvW.
    /// </summary>
    internal static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var sb = new StringBuilder();
        var fileName = startInfo.FileName.Trim();
        var quoted = fileName.Length >= 2 && fileName.StartsWith('"') && fileName.EndsWith('"');
        if (!quoted) sb.Append('"');
        sb.Append(fileName);
        if (!quoted) sb.Append('"');

        if (startInfo.ArgumentList.Count > 0)
        {
            foreach (var argument in startInfo.ArgumentList)
            {
                AppendArgument(sb, argument);
            }
        }
        else if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            sb.Append(' ');
            sb.Append(startInfo.Arguments);
        }
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (sb.Length != 0) sb.Append(' ');

        if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        var i = 0;
        while (i < argument.Length)
        {
            var c = argument[i++];
            if (c == '\\')
            {
                var backslashes = 1;
                while (i < argument.Length && argument[i] == '\\')
                {
                    i++;
                    backslashes++;
                }

                if (i == argument.Length)
                {
                    sb.Append('\\', backslashes * 2);
                }
                else if (argument[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    i++;
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\');
                sb.Append('"');
                continue;
            }

            sb.Append(c);
        }
        sb.Append('"');
    }

    /// <summary>
    /// A CREATE_UNICODE_ENVIRONMENT block: NAME=value entries sorted without regard to
    /// case, each NUL-terminated, with a final NUL.
    /// </summary>
    internal static string BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var sb = new StringBuilder();
        foreach (var name in environment.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(name).Append('=').Append(environment[name]).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }

    private const uint GENERIC_ALL = 0x10000000;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = new(0x00020002);
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

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
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);
}
