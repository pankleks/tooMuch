using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

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
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

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

    [DllImport("wtsapi32.dll", EntryPoint = "WTSSendMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNativeMessage(IntPtr server, int session, string title, int titleBytes,
        string message, int messageBytes, uint style, uint timeout, out uint response,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    // Called from background tasks, never from the service's enforcement loop.
    public static bool ShowMessage(int session, string title, string text, uint timeout)
    {
        if (TryShowLargeMessage(session, title, text, timeout, out var confirmed)) return confirmed;

        // Keep messages deliverable if the custom child-session UI cannot be launched.
        if (!SendNativeMessage(IntPtr.Zero, session, title, title.Length * 2, text, text.Length * 2,
            0x00010040, timeout, out var response, true))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return response == 1;
    }

    private static bool TryShowLargeMessage(int session, string title, string text, uint timeout, out bool confirmed)
    {
        confirmed = false;
        if (!WTSQueryUserToken((uint)session, out var token)) return false;
        try
        {
            if (!CreateEnvironmentBlock(out var environment, token, false)) return false;
            try
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable)) return false;
                var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new MessageDialogPayload(title, text)));
                var commandLine = new StringBuilder($"\"{executable}\" --message {payload} {timeout}");
                var startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfo>(),
                    Desktop = @"winsta0\default",
                };
                if (!CreateProcessAsUser(token, executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    0x00000400, environment, null, ref startup, out var process))
                    return false;

                try
                {
                    var milliseconds = timeout > uint.MaxValue / 1000 ? uint.MaxValue - 1 : timeout * 1000;
                    var wait = WaitForSingleObject(process.Process, milliseconds);
                    if (wait == 0x00000102) // WAIT_TIMEOUT
                    {
                        if (!TerminateProcess(process.Process, 0) && WaitForSingleObject(process.Process, 1000) != 0)
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        WaitForSingleObject(process.Process, 1000);
                        return true;
                    }
                    if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!GetExitCodeProcess(process.Process, out var exitCode))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    confirmed = exitCode == 1;
                    return true;
                }
                finally
                {
                    CloseHandle(process.Thread);
                    CloseHandle(process.Process);
                }
            }
            finally { DestroyEnvironmentBlock(environment); }
        }
        finally { CloseHandle(token); }
    }
}
