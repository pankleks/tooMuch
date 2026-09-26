using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TooMuch.Client;

// Called only by LocalSystem. No user-owned process participates in enforcement.
internal static class SessionGuard
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Session { public int Id; public IntPtr Name; public int State; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InfoLevel1
    {
        public int SessionId, State, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string Station;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string User;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string Domain;
        public long Logon, Connect, Disconnect, LastInput, Current;
        public uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames,
            IncomingCompressed, OutgoingCompressed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Info { public uint Level; public InfoLevel1 Data; }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Enumerate(IntPtr server, int reserved, int version, out IntPtr buffer, out int count);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Query(IntPtr server, int id, int infoClass, out IntPtr buffer, out int size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint id, out IntPtr token);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSDisconnectSession(IntPtr server, int id, [MarshalAs(UnmanagedType.Bool)] bool wait);

    public static List<int> UnlockedSessions(string childSid)
    {
        if (!Enumerate(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new List<int>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<Session>(buffer + i * Marshal.SizeOf<Session>());
                if (session.Id == 0 || session.State != 0) continue;
                if (!WTSQueryUserToken((uint)session.Id, out var token))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    using var identity = new WindowsIdentity(token);
                    if (identity.User?.Value != childSid) continue;
                }
                finally { CloseHandle(token); }
                // WTSSessionInfoEx: Windows 10/11 flags 0=locked, 1=unlocked.
                if (!Query(IntPtr.Zero, session.Id, 25, out var info, out var size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (size < Marshal.SizeOf<Info>()) throw new InvalidOperationException("Incomplete WTS session information.");
                    var value = Marshal.PtrToStructure<Info>(info);
                    if (value.Level != 1) throw new InvalidOperationException("Unsupported WTS information level.");
                    if (value.Data.Flags == 1) result.Add(session.Id);
                }
                finally { WTSFreeMemory(info); }
            }
        }
        finally { WTSFreeMemory(buffer); }
        return result;
    }

    public static void Disconnect(int id)
    {
        if (!WTSDisconnectSession(IntPtr.Zero, id, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
