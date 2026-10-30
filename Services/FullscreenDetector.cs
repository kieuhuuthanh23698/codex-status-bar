using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexStatusBar.Services;

public static class FullscreenDetector
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    public static bool IsFullscreen(MonitorInfo monitor)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return false;
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == (uint)Environment.ProcessId || !GetWindowRect(hwnd, out var rect)) return false;
        var bounds = monitor.Bounds;
        return rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
    }
}
