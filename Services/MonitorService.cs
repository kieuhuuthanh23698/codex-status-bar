using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexStatusBar.Services;

public sealed record MonitorInfo(string Id, string Name, bool IsPrimary, Rectangle Bounds, Rectangle WorkingArea)
{
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
}

public sealed class MonitorService : IDisposable
{
    public event EventHandler? Changed;
    public MonitorService() => SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

    public IReadOnlyList<MonitorInfo> GetMonitors() => Screen.AllScreens
        .Select(s => new MonitorInfo(s.DeviceName, s.DeviceName.TrimStart('\\', '.'), s.Primary, s.Bounds, s.WorkingArea))
        .OrderByDescending(s => s.IsPrimary).ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();

    public MonitorInfo GetMonitorAt(System.Drawing.Point point)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) throw new InvalidOperationException("No display monitor is available.");
        return monitors.FirstOrDefault(m => m.Bounds.Contains(point))
            ?? monitors.OrderBy(m => DistanceToMonitor(point, m.Bounds)).First();
    }

    private static long DistanceToMonitor(System.Drawing.Point point, Rectangle bounds)
    {
        var x = Math.Clamp(point.X, bounds.Left, bounds.Right - 1);
        var y = Math.Clamp(point.Y, bounds.Top, bounds.Bottom - 1);
        var dx = (long)point.X - x;
        var dy = (long)point.Y - y;
        return dx * dx + dy * dy;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
}
