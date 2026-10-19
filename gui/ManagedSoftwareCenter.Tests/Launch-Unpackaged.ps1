param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
# A terminal hosted by a packaged desktop application can pass its package
# identity to children. An unpackaged WinUI test app then resolves ms-appx
# resources in the host package, not its own PRI. Opt this process tree out;
# no machine settings, installed files, credentials or privileges are changed.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class UnpackagedTestLauncher {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO {
        public int cb; public string reserved, desktop, title;
        public int x, y, width, height, charsX, charsY, fill, flags;
        public short show, reservedSize; public IntPtr reservedBytes, stdin, stdout, stderr;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEX { public STARTUPINFO info; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr process, thread; public int pid, tid; }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttrs, IntPtr threadAttrs,
        bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION result);
    public static int Launch(string exe) {
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var policy = Marshal.AllocHGlobal(sizeof(int));
        bool initialized = false;
        try {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
            initialized = true;
            Marshal.WriteInt32(policy, 1); // PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE
            if (!UpdateProcThreadAttribute(list, 0, new IntPtr(0x20012), policy, new IntPtr(sizeof(int)), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var startup = new STARTUPINFOEX();
            startup.info.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startup.attributes = list;
            PROCESS_INFORMATION result;
            if (!CreateProcess(exe, new StringBuilder("\"" + exe + "\""), IntPtr.Zero, IntPtr.Zero, false,
                0x00080000, IntPtr.Zero, System.IO.Path.GetDirectoryName(exe), ref startup, out result))
                throw new Win32Exception();
            CloseHandle(result.thread); CloseHandle(result.process);
            return result.pid;
        } finally {
            if (initialized) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list); Marshal.FreeHGlobal(policy);
        }
    }
}
'@
[UnpackagedTestLauncher]::Launch($exe)
