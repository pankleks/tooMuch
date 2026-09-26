using System.Diagnostics;
using System.Runtime.InteropServices;
using TooMuch.Win32;

namespace TooMuch.Client;

// SYSTEM-side watchdog: a standard user can kill their own tray process,
// so the --service loop (session 0) respawns --tray in every active
// interactive session. Never throws: must not break the service loop.
internal static class SessionGuard
{
    public static void EnsureTray(string exePath)
    {
        try
        {
            foreach (var sid in ActiveSessionIds())
            {
                if (sid == 0) continue;
                if (TrayInSession(sid)) continue;
                LaunchInSession(exePath, sid);
            }
        }
        catch { }
    }

    private static List<uint> ActiveSessionIds()
    {
        var out_ = new List<uint>();
        IntPtr buf = IntPtr.Zero;
        int count = 0;
        try
        {
            if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, ref buf, ref count))
                return out_;
            var size = Marshal.SizeOf<Native.WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var si = Marshal.PtrToStructure<Native.WTS_SESSION_INFO>(IntPtr.Add(buf, i * size));
                if (si.State == 0 /* WTSActive */ && si.SessionId > 0)
                    out_.Add((uint)si.SessionId);
            }
        }
        catch { }
        finally
        {
            if (buf != IntPtr.Zero) Native.WTSFreeMemory(buf);
        }
        return out_;
    }

    private static bool TrayInSession(uint sid)
    {
        try
        {
            var me = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("TooMuch"))
            {
                try
                {
                    if (p.Id != me && p.SessionId == (int)sid) return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    private static void LaunchInSession(string exePath, uint sid)
    {
        IntPtr userToken = IntPtr.Zero;
        try
        {
            if (!Native.WTSQueryUserToken(sid, out userToken) || userToken == IntPtr.Zero)
                return;
            var si = new Native.STARTUPINFO();
            si.cb = Marshal.SizeOf<Native.STARTUPINFO>();
            si.lpDesktop = @"winsta0\default";
            var cmd = $"\"{exePath}\" --tray";
            var pi = new Native.PROCESS_INFORMATION();
            if (!Native.CreateProcessAsUser(
                    userToken, null, cmd,
                    IntPtr.Zero, IntPtr.Zero, false,
                    0x00000400 /* CREATE_UNICODE_ENVIRONMENT */,
                    IntPtr.Zero, null, ref si, out pi))
                return;
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
        }
        catch { }
        finally
        {
            if (userToken != IntPtr.Zero) Native.CloseHandle(userToken);
        }
    }
}
