using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using CodexStatusBar.Infrastructure;
using CodexStatusBar.Models;
using CodexStatusBar.Services;

namespace CodexStatusBar.Views;

public partial class StatusBarWindow : Window
{
    private readonly Action<MonitorInfo> _openDashboard;
    private readonly Action _toggle, _openSettings, _exit;
    private readonly Func<Task> _refresh;
    private readonly Action<StatusBarWindow, MonitorInfo, double, double> _savePosition;
    private readonly Func<System.Drawing.Point, MonitorInfo> _getMonitorAtPoint;
    private readonly Func<Task> _saveSettings;
    private System.Windows.Point _mouseDown;
    private bool _dragged;
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint EventSystemForeground = 0x0003, WineventOutOfContext = 0x0000, WineventSkipOwnProcess = 0x0002;
    private static readonly nint HwndTopmost = new(-1);
    private MonitorSettings? _monitorSettings;
    private AppSettings? _settings;
    private readonly WinEventDelegate _foregroundCallback;
    private nint _foregroundHook;
    public MonitorInfo Monitor { get; private set; }

    public StatusBarWindow(MonitorInfo monitor, Action<MonitorInfo> openDashboard, Action toggle, Func<Task> refresh,
        Action<StatusBarWindow, MonitorInfo, double, double> savePosition, Func<System.Drawing.Point, MonitorInfo> getMonitorAtPoint,
        Action settings, Action exit, Func<Task> saveSettings)
    {
        InitializeComponent();
        _foregroundCallback = OnForegroundChanged;
        Monitor = monitor;
        _openDashboard = openDashboard;
        _toggle = toggle;
        _refresh = refresh;
        _savePosition = savePosition;
        _getMonitorAtPoint = getMonitorAtPoint;
        _saveSettings = saveSettings;
        _openSettings = settings;
        _exit = exit;
        ToolTip = "Codex Status";
        ContextMenu = CreateContextMenu();
        Closed += (_, _) =>
        {
            if (_foregroundHook == nint.Zero) return;
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = nint.Zero;
        };
    }

    public void SetMonitor(MonitorInfo monitor)
    {
        var changed = monitor.Id != Monitor.Id || monitor.Bounds != Monitor.Bounds || monitor.WorkingArea != Monitor.WorkingArea;
        Monitor = monitor;
        if (changed && IsLoaded) SchedulePlacement();
    }

    public void ApplySettings(AppSettings settings, MonitorSettings? monitorSettings)
    {
        _settings = settings;
        _monitorSettings = monitorSettings ?? new MonitorSettings { Position = settings.DefaultPosition, Locked = settings.LockPosition };
        Topmost = settings.AlwaysOnTop;
        Opacity = settings.Opacity;
        Place(_monitorSettings.Position, _monitorSettings);
        if (IsLoaded) ContextMenu = CreateContextMenu();
    }

    public void EnsureOnAssignedMonitor()
    {
        if (!IsLoaded || !IsVisible) return;
        var bounds = GetPhysicalWindowBounds();
        var center = new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        if (!string.Equals(_getMonitorAtPoint(center).Id, Monitor.Id, StringComparison.OrdinalIgnoreCase))
            Place(_monitorSettings?.Position ?? BarPosition.BottomRight, _monitorSettings);
    }

    public void UpdateData(ThreadUsage? thread, UsageSnapshot snapshot)
    {
        var pieces = new List<string>();
        var settings = _settings;
        var fiveHour = snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 300);
        var weekly = snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 10080);
        var showFiveHour = settings?.ShowFiveHourQuota != false;
        var showWeekly = settings?.ShowWeeklyQuota != false;
        var quotaMode = settings?.QuotaValueMode ?? QuotaValueMode.Used;
        if (settings?.DisplayMode == BarDisplayMode.Minimal)
        {
            if (showFiveHour && fiveHour is not null) pieces.Add($"5H {QuotaPercentFormatter.Format(fiveHour.UsedPercent, quotaMode)}");
            else if (showWeekly && weekly is not null) pieces.Add($"W {QuotaPercentFormatter.Format(weekly.UsedPercent, quotaMode)}");
        }
        else
        {
            if (showFiveHour && fiveHour is not null)
                pieces.Add($"5H {QuotaPercentFormatter.Format(fiveHour.UsedPercent, quotaMode)}{FormatReset(fiveHour)}");
            if (showWeekly && weekly is not null)
                pieces.Add($"{(settings?.DisplayMode == BarDisplayMode.Full ? "Week" : "W")} {QuotaPercentFormatter.Format(weekly.UsedPercent, quotaMode)}{FormatReset(weekly)}");
        }
        MetricsText.Text = pieces.Count > 0 ? string.Join("   ", pieces) : "Usage unavailable";
        var severity = GetSeverity(thread, snapshot, settings);
        var severityColor = severity >= 3 ? System.Windows.Media.Color.FromRgb(248, 113, 113)
            : severity >= 2 ? System.Windows.Media.Color.FromRgb(251, 191, 36)
            : severity >= 1 ? System.Windows.Media.Color.FromRgb(250, 204, 21)
            : System.Windows.Media.Color.FromArgb(102, 95, 107, 130);
        Root.BorderBrush = new System.Windows.Media.SolidColorBrush(severityColor);
        var metricsColor = snapshot.ErrorMessage is null && severity > 0
            ? severityColor : System.Windows.Media.Color.FromRgb(248, 250, 252);
        MetricsText.Foreground = new System.Windows.Media.SolidColorBrush(metricsColor);
        StateText.Text = snapshot.ErrorMessage is not null ? "STALE" : snapshot.WarningMessage is not null ? "CHAT?"
            : severity >= 3 ? "CRITICAL" : severity >= 2 ? "HIGH" : severity >= 1 ? "WARN" : "";
        var lines = new List<string> { "Codex Status", "Open dashboard for the selected chat's usage and model details." };
        foreach (var quota in snapshot.Quotas)
        {
            var reset = quota.ResetsAt is null ? "" : $" · reset in {FormatRemaining(quota.ResetsAt.Value)}";
            lines.Add($"{quota.Label}: {QuotaPercentFormatter.Format(quota.UsedPercent, quotaMode, includeMeaning: true)}{reset}");
        }
        lines.Add(snapshot.ErrorMessage ?? snapshot.WarningMessage ?? $"Updated: {snapshot.UpdatedAt.ToLocalTime():T}");
        if (snapshot.LastSuccessfulUpdate is not null && snapshot.ErrorMessage is not null)
            lines.Add($"Last successful update: {snapshot.LastSuccessfulUpdate.Value.ToLocalTime():g}");
        ToolTip = string.Join(Environment.NewLine, lines);
    }

    private System.Windows.Controls.ContextMenu CreateContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Open dashboard", Command = new RelayCommand(_ => _openDashboard(Monitor)) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Refresh now", Command = new RelayCommand(async _ => await _refresh()) });
        var lockItem = new System.Windows.Controls.MenuItem { Header = "Lock position", IsCheckable = true, IsChecked = _monitorSettings?.Locked ?? false };
        lockItem.Click += (_, _) =>
        {
            if (_settings is not null && !_settings.Monitors.ContainsKey(Monitor.Id))
                _settings.Monitors[Monitor.Id] = _monitorSettings ??= new MonitorSettings();
            if (_monitorSettings is not null) _monitorSettings.Locked = lockItem.IsChecked;
            _ = _saveSettings();
        };
        menu.Items.Add(lockItem);
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Hide status bars", Command = new RelayCommand(_ => _toggle()) });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Settings", Command = new RelayCommand(_ => _openSettings()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Exit", Command = new RelayCommand(_ => _exit()) });
        return menu;
    }

    private void Place(BarPosition position, MonitorSettings? settings)
    {
        if (!IsLoaded) return;
        var area = Monitor.WorkingArea;
        var bounds = GetPhysicalWindowBounds();
        var width = Math.Max(bounds.Width, (int)Math.Ceiling(ActualWidth * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX));
        var height = Math.Max(bounds.Height, (int)Math.Ceiling(ActualHeight * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY));
        var effectivePosition = position;
        var left = 0;
        var top = 0;
        if (position == BarPosition.Custom && settings?.Left is double customLeft && settings.Top is double customTop)
        {
            left = (int)Math.Round(customLeft);
            top = (int)Math.Round(customTop);
            var monitorBounds = Monitor.Bounds;
            var intersectsMonitor = left < monitorBounds.Right && left + width > monitorBounds.Left
                && top < monitorBounds.Bottom && top + height > monitorBounds.Top;
            if (intersectsMonitor)
            {
                (left, top) = ClampToMonitorBounds(left, top, Monitor, width, height);
                SetPhysicalPosition(left, top);
                return;
            }
            effectivePosition = _settings?.DefaultPosition ?? BarPosition.BottomRight;
        }
        const int margin = 12;
        left = effectivePosition switch
        {
            BarPosition.TopLeft or BarPosition.BottomLeft => area.Left + margin,
            BarPosition.TopCenter or BarPosition.BottomCenter => area.Left + (area.Width - width) / 2,
            _ => area.Right - width - margin
        };
        top = effectivePosition switch
        {
            BarPosition.TopLeft or BarPosition.TopCenter or BarPosition.TopRight => area.Top + margin,
            _ => area.Bottom - height - margin
        };
        (left, top) = ClampToMonitorBounds(left, top, Monitor, width, height);
        SetPhysicalPosition(left, top);
    }

    private static (int Left, int Top) ClampToMonitorBounds(int left, int top, MonitorInfo monitor, int width, int height)
    {
        var area = monitor.Bounds;
        var maxLeft = Math.Max(area.Left, area.Right - width);
        var maxTop = Math.Max(area.Top, area.Bottom - height);
        return (Math.Clamp(left, area.Left, maxLeft), Math.Clamp(top, area.Top, maxTop));
    }

    private System.Drawing.Rectangle GetPhysicalWindowBounds()
    {
        var previousContext = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != nint.Zero && GetWindowRect(hwnd, out var rect))
                return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            return new System.Drawing.Rectangle(0, 0,
                (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        }
        finally
        {
            if (previousContext != nint.Zero) SetThreadDpiAwarenessContext(previousContext);
        }
    }

    private void SetPhysicalPosition(int left, int top)
    {
        var previousContext = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != nint.Zero)
                SetWindowPos(hwnd, nint.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            if (previousContext != nint.Zero) SetThreadDpiAwarenessContext(previousContext);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_foregroundHook == nint.Zero)
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, nint.Zero,
                _foregroundCallback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
        SchedulePlacement();
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint foregroundWindow, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (_settings?.AlwaysOnTop != true || !IsLoaded || !IsVisible || Dispatcher.HasShutdownStarted) return;

        // Explorer can raise its taskbar above topmost tool windows when it receives a taskbar click.
        // Reassert our topmost z-order after the newly foregrounded app settles, without activating this window.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(EnsureTopmost));
    }

    private void EnsureTopmost()
    {
        if (_settings?.AlwaysOnTop != true || !IsLoaded || !IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero)
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void SchedulePlacement()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            Place(_monitorSettings?.Position ?? BarPosition.BottomRight, _monitorSettings);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                Place(_monitorSettings?.Position ?? BarPosition.BottomRight, _monitorSettings)));
        }));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    private delegate void WinEventDelegate(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventDelegate callback,
        uint processId, uint threadId, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(nint hook);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDown = e.GetPosition(this);
        _dragged = false;
        CaptureMouse();
    }
    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _monitorSettings?.Locked == true) return;
        var point = e.GetPosition(this);
        if (!_dragged && (Math.Abs(point.X - _mouseDown.X) > 4 || Math.Abs(point.Y - _mouseDown.Y) > 4))
        {
            _dragged = true;
            ReleaseMouseCapture();
            try { DragMove(); } catch { }
            var bounds = GetPhysicalWindowBounds();
            var center = new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            var destination = _getMonitorAtPoint(center);
            Monitor = destination;
            (var left, var top) = ClampToMonitorBounds(bounds.Left, bounds.Top, destination, bounds.Width, bounds.Height);
            SetPhysicalPosition(left, top);
            _monitorSettings = _settings?.Monitors.GetValueOrDefault(destination.Id)
                ?? new MonitorSettings { Position = BarPosition.Custom, Locked = _settings?.LockPosition ?? false };
            _savePosition(this, destination, left, top);
        }
    }
    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (!_dragged) _openDashboard(Monitor);
        _dragged = false;
    }

    private static string FormatReset(QuotaWindow quota) => quota.ResetsAt is DateTimeOffset reset ? $" · {FormatRemaining(reset)}" : "";
    private static string FormatRemaining(DateTimeOffset reset)
    {
        var remaining = reset - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "soon";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        return $"{remaining:hh\\:mm}";
    }
    private static int GetSeverity(ThreadUsage? thread, UsageSnapshot snapshot, AppSettings? settings)
    {
        if (settings is null) return 0;
        var severity = thread?.ContextUsedPercent is double context
            ? context >= settings.CriticalWarningPercent ? 3
            : context >= settings.ContextWarningPercent ? 1 : 0
            : 0;
        foreach (var used in snapshot.Quotas.Where(q => q.UsedPercent.HasValue).Select(q => q.UsedPercent!.Value))
            severity = Math.Max(severity, used >= settings.CriticalWarningPercent ? 3
                : used >= settings.HighWarningPercent ? 2
                : used >= settings.QuotaWarningPercent ? 1 : 0);
        return severity;
    }
    private sealed class RelayCommand(Action<object?> execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }
}
