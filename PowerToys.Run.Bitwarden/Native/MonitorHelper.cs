using System.Runtime.InteropServices;
using System.Windows;

namespace PowerToys.Run.Bitwarden.Native;

internal static class MonitorHelper
{
    public static bool TryGetWorkAreaUnderCursor(out Rect workArea)
    {
        workArea = default;
        if (!GetCursorPos(out var p)) return false;
        var hMon = MonitorFromPoint(p, MonitorOptions.DefaultToNearest);
        if (hMon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return false;
        var r = mi.rcWork;
        workArea = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, MonitorOptions dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private enum MonitorOptions : uint
    {
        DefaultToNull = 0,
        DefaultToPrimary = 1,
        DefaultToNearest = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
