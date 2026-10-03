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

    public static List<int> UnlockedSessions(string childSid, Action<Exception>? log = null) =>
        ScanSessions(childSid, log ?? (_ => { })).Unlocked;

    internal sealed record SessionScan(List<int> ActiveChild, List<int> Unlocked);

    public static SessionScan ScanSessions(string childSid, Action<Exception> log)
    {
        if (!Enumerate(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var active = new List<int>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<Session>(buffer + i * Marshal.SizeOf<Session>());
                if (session.Id == 0 || session.State != 0) continue;
                active.Add(session.Id);
            }
        }
        finally { WTSFreeMemory(buffer); }
        return ScanSessions(active, id => IsChild(id, childSid), IsUnlocked, log);
    }

    internal static SessionScan ScanSessions(IEnumerable<int> active, Func<int, bool> isChild,
        Func<int, bool> isUnlocked, Action<Exception> log)
    {
        var activeChild = new List<int>();
        var unlocked = new List<int>();
        foreach (var id in active)
        {
            try
            {
                if (!isChild(id)) continue;
                // Retain verified child sessions even when their lock state is unknown.
                activeChild.Add(id);
                if (isUnlocked(id)) unlocked.Add(id);
            }
            catch (Exception ex) { log(ex); }
        }
        return new SessionScan(activeChild, unlocked);
    }

    private static bool IsChild(int id, string childSid)
    {
        if (!WTSQueryUserToken((uint)id, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value == childSid;
        }
        finally { CloseHandle(token); }
    }

    private static bool IsUnlocked(int id)
    {
        return ReadSessionInfo(id).Flags == 1;
    }

    internal static bool IsCountingSession(int id, bool countOnlyActive, int idleThresholdSec,
        Action<Exception>? log = null) =>
        IsCountingSession(id, countOnlyActive, idleThresholdSec, ReadProtocol, ReadInputTimes, log ?? (_ => { }));

    // Caller must first verify that this is an unlocked session belonging to the child.
    internal static bool IsCountingSession(int id, bool countOnlyActive, int idleThresholdSec,
        Func<int, ushort> readProtocol, Func<int, (long Current, long LastInput)> readInputTimes,
        Action<Exception> log)
    {
        if (!countOnlyActive) return true;
        try
        {
            // WTS last-input timestamps are RDP data. On the physical console
            // they can be zero or stale even while playing a game. Count unlocked
            // local screen time, including videos, instead of treating it as idle.
            if (readProtocol(id) == 0) return true;
            var input = readInputTimes(id);
            // Unknown activity must not silently provide unlimited screen time.
            if (input.Current <= 0 || input.LastInput <= 0 || input.LastInput > input.Current) return true;
            return IsWithinIdleThreshold(input.Current, input.LastInput, idleThresholdSec);
        }
        catch (Exception ex)
        {
            log(ex);
            return true; // Identity and unlocked state were already verified by ScanSessions.
        }
    }

    private static (long Current, long LastInput) ReadInputTimes(int id)
    {
        var info = ReadSessionInfo(id);
        return (info.Current, info.LastInput);
    }

    private static ushort ReadProtocol(int id)
    {
        // WTSClientProtocolType: 0=physical console, 2=RDP.
        if (!Query(IntPtr.Zero, id, 16, out var buffer, out var size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (size < sizeof(ushort)) throw new InvalidOperationException("Incomplete WTS protocol information.");
            return unchecked((ushort)Marshal.ReadInt16(buffer));
        }
        finally { WTSFreeMemory(buffer); }
    }

    internal static bool IsWithinIdleThreshold(long current, long lastInput, int thresholdSec) =>
        lastInput > 0 && current >= lastInput && thresholdSec > 0 &&
        (current - lastInput) / (double)TimeSpan.TicksPerSecond < thresholdSec;

    private static InfoLevel1 ReadSessionInfo(int id)
    {
        // WTSSessionInfoEx: Windows 10/11 flags 0=locked, 1=unlocked.
        if (!Query(IntPtr.Zero, id, 25, out var info, out var size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (size < Marshal.SizeOf<Info>()) throw new InvalidOperationException("Incomplete WTS session information.");
            var value = Marshal.PtrToStructure<Info>(info);
            if (value.Level != 1) throw new InvalidOperationException("Unsupported WTS information level.");
            return value.Data;
        }
        finally { WTSFreeMemory(info); }
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
        // MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND.
        if (!SendNativeMessage(IntPtr.Zero, session, title, title.Length * 2, text, text.Length * 2,
            0x00010040, timeout, out var response, true))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return response == 1; // IDOK; timeout is not confirmation.
    }
}
