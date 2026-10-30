using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodexStatusBar.Models;

public enum BarDisplayMode { Compact, Full, Minimal }
public enum BarPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight, Custom }
public enum QuotaValueMode { Used, Remaining }

public sealed class AppSettings : INotifyPropertyChanged
{
    private int _refreshIntervalSeconds = 60;
    private bool _showTrayIcon = true;
    private bool _showFloatingBar = true;
    private bool _alwaysOnTop = true;
    private bool _hideWhenFullscreen = true;
    private bool _lockPosition;
    private bool _startWithWindows;
    private bool _showSession = true;
    private bool _showContext = true;
    private bool _showFiveHourQuota = true;
    private bool _showWeeklyQuota = true;
    private bool _showOnAllMonitors;
    private double _opacity = 0.96;
    private string? _pinnedThreadId;
    private BarDisplayMode _displayMode = BarDisplayMode.Compact;
    private QuotaValueMode _quotaValueMode = QuotaValueMode.Used;
    private BarPosition _defaultPosition = BarPosition.BottomRight;
    private double _contextWarningPercent = 80;
    private double _quotaWarningPercent = 70;
    private double _highWarningPercent = 85;
    private double _criticalWarningPercent = 95;

    public event PropertyChangedEventHandler? PropertyChanged;
    public int RefreshIntervalSeconds { get => _refreshIntervalSeconds; set => Set(ref _refreshIntervalSeconds, value); }
    public bool ShowTrayIcon { get => _showTrayIcon; set => Set(ref _showTrayIcon, value); }
    public bool ShowFloatingBar { get => _showFloatingBar; set => Set(ref _showFloatingBar, value); }
    public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }
    public bool HideWhenFullscreen { get => _hideWhenFullscreen; set => Set(ref _hideWhenFullscreen, value); }
    public bool LockPosition { get => _lockPosition; set => Set(ref _lockPosition, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool ShowSession { get => _showSession; set => Set(ref _showSession, value); }
    public bool ShowContext { get => _showContext; set => Set(ref _showContext, value); }
    public bool ShowFiveHourQuota { get => _showFiveHourQuota; set => Set(ref _showFiveHourQuota, value); }
    public bool ShowWeeklyQuota { get => _showWeeklyQuota; set => Set(ref _showWeeklyQuota, value); }
    public bool ShowOnAllMonitors { get => _showOnAllMonitors; set => Set(ref _showOnAllMonitors, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, .3, 1)); }
    public string? PinnedThreadId { get => _pinnedThreadId; set => Set(ref _pinnedThreadId, value); }
    public BarDisplayMode DisplayMode { get => _displayMode; set => Set(ref _displayMode, value); }
    public QuotaValueMode QuotaValueMode { get => _quotaValueMode; set => Set(ref _quotaValueMode, value); }
    public BarPosition DefaultPosition { get => _defaultPosition; set => Set(ref _defaultPosition, value); }
    public double ContextWarningPercent { get => _contextWarningPercent; set => Set(ref _contextWarningPercent, Math.Clamp(value, 1, 100)); }
    public double QuotaWarningPercent { get => _quotaWarningPercent; set => Set(ref _quotaWarningPercent, Math.Clamp(value, 1, 100)); }
    public double HighWarningPercent { get => _highWarningPercent; set => Set(ref _highWarningPercent, Math.Clamp(value, 1, 100)); }
    public double CriticalWarningPercent { get => _criticalWarningPercent; set => Set(ref _criticalWarningPercent, Math.Clamp(value, 1, 100)); }

    public Dictionary<string, MonitorSettings> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SelectedMonitorIds { get; set; } = new();
    public void NotifyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class MonitorSettings
{
    public BarPosition Position { get; set; } = BarPosition.BottomRight;
    public bool Locked { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
}
