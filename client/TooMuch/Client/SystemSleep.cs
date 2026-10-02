using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TooMuch.Client;

internal static class SystemSleep
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges privileges,
        uint size, out TokenPrivileges previous, out uint returnedSize);
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);

    public static void Sleep()
    {
        // Explicit suspend does not depend on the idle timer or app power requests.
        // LocalSystem has SeShutdownPrivilege, but it must be enabled for this API.
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token)) // ADJUST_PRIVILEGES | QUERY
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref privileges,
                (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error);
            try
            {
                // The force parameter is ignored by modern Windows. Disable wake
                // timers for this suspend; hardware/manual wakes are still possible.
                if (!SetSuspendState(false, false, true))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (!AdjustTokenPrivileges(token, false, ref previous,
                    (uint)Marshal.SizeOf<TokenPrivileges>(), out _, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally { CloseHandle(token); }
    }
}
