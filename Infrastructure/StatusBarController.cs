using System.Windows;
using System.Windows.Threading;
using CodexStatusBar.Models;
using CodexStatusBar.Services;
using CodexStatusBar.Views;

namespace CodexStatusBar.Infrastructure;

public sealed class StatusBarController : IDisposable
{
    private readonly MonitorService _monitorService = new();
    private readonly Dictionary<string, StatusBarWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action _showDashboard, _showSettings, _exit;
    private readonly Func<Task> _refresh;
    private readonly Func<string, Task> _refreshSelectedThread;
    private readonly Func<Task> _saveSettings;
    private readonly DispatcherTimer _fullscreenTimer;
    private UsageSnapshot _snapshot = new();
    private bool _visible = true;

    public AppSettings Settings { get; }
    public bool IsBarVisible => Settings.ShowFloatingBar && _visible;
    public MonitorInfo? DashboardMonitor { get; private set; }
    public ThreadUsage? SelectedThread => _snapshot.Threads.FirstOrDefault(t => t.Id == Settings.PinnedThreadId)
        ?? _snapshot.Threads.FirstOrDefault(t => !t.IsArchived) ?? _snapshot.Threads.FirstOrDefault();
    public UsageSnapshot Snapshot => _snapshot;
    public event EventHandler? SnapshotChanged;
    public event Action<bool>? VisibilityChanged;

    public StatusBarController(AppSettings settings, Action showDashboard, Action showSettings, Func<Task> refresh,
        Func<string, Task> refreshSelectedThread, Action exit, Func<Task> saveSettings)
    {
        Settings = settings;
        _showDashboard = showDashboard;
        _showSettings = showSettings;
        _refresh = refresh;
        _refreshSelectedThread = refreshSelectedThread;
        _exit = exit;
        _saveSettings = saveSettings;
        _monitorService.Changed += (_, _) => System.Windows.Application.Current.Dispatcher.InvokeAsync(ApplySettings);
        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreenVisibility();
        _fullscreenTimer.Start();
    }

    public void ApplySettings()
    {
        foreach (var monitorSettings in Settings.Monitors.Values.Where(m => m.Position != BarPosition.Custom))
            monitorSettings.Position = Settings.DefaultPosition;
        var monitors = _monitorService.GetMonitors();
        var selected = ResolveSelectedMonitors(monitors);
        foreach (var stale in _windows.Keys.Except(selected.Select(m => m.Id), StringComparer.OrdinalIgnoreCase).ToList())
        {
            _windows[stale].Close();
            _windows.Remove(stale);
        }
        foreach (var monitor in selected)
        {
            if (!_windows.TryGetValue(monitor.Id, out var window))
            {
                window = new StatusBarWindow(monitor, OpenDashboard, ToggleVisibility, _refresh, SavePosition,
                    _monitorService.GetMonitorAt, _showSettings, _exit, _saveSettings);
                _windows[monitor.Id] = window;
                window.Show();
            }
            window.SetMonitor(monitor);
            window.ApplySettings(Settings, Settings.Monitors.GetValueOrDefault(monitor.Id));
            window.UpdateData(SelectedThread, _snapshot);
            if (Settings.ShowFloatingBar && _visible) window.Show(); else window.Hide();
        }
        UpdateFullscreenVisibility();
    }

    private IReadOnlyList<MonitorInfo> ResolveSelectedMonitors(IReadOnlyList<MonitorInfo> all)
    {
        if (all.Count == 0) return Array.Empty<MonitorInfo>();
        if (Settings.ShowOnAllMonitors) return all;
        var chosen = all.Where(m => Settings.SelectedMonitorIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        return chosen.Count > 0 ? chosen : new[] { all.FirstOrDefault(m => m.IsPrimary) ?? all[0] };
    }

    private void UpdateFullscreenVisibility()
    {
        if (!Settings.ShowFloatingBar) return;
        var monitors = _monitorService.GetMonitors().ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _windows)
        {
            var hide = Settings.HideWhenFullscreen && monitors.TryGetValue(pair.Key, out var monitor) && FullscreenDetector.IsFullscreen(monitor);
            if (hide || !_visible) pair.Value.Hide();
            else
            {
                if (!pair.Value.IsVisible) pair.Value.Show();
                pair.Value.EnsureOnAssignedMonitor();
            }
        }
    }

    public void Update(UsageSnapshot snapshot)
    {
        if (snapshot.Threads.Count > 0 && !snapshot.Threads.Any(t => t.Id == Settings.PinnedThreadId))
            Settings.PinnedThreadId = snapshot.Threads.FirstOrDefault(t => !t.IsArchived)?.Id;
        _snapshot = snapshot;
        foreach (var window in _windows.Values) window.UpdateData(SelectedThread, snapshot);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowError(string message)
    {
        _snapshot.ErrorMessage = message;
        foreach (var window in _windows.Values) window.UpdateData(SelectedThread, _snapshot);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifySnapshotChanged() => SnapshotChanged?.Invoke(this, EventArgs.Empty);

    public void SelectThread(string? threadId)
    {
        Settings.PinnedThreadId = threadId;
        foreach (var window in _windows.Values) window.UpdateData(SelectedThread, _snapshot);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        if (threadId is not null) _ = _refreshSelectedThread(threadId);
        else _ = _refresh();
    }

    public void ToggleVisibility()
    {
        _visible = !_visible;
        foreach (var window in _windows.Values)
            if (_visible && Settings.ShowFloatingBar) window.Show(); else window.Hide();
        VisibilityChanged?.Invoke(_visible && Settings.ShowFloatingBar);
    }

    private void OpenDashboard() => _showDashboard();

    private void OpenDashboard(MonitorInfo monitor)
    {
        DashboardMonitor = monitor;
        _showDashboard();
    }

    public void ShowDashboardFromTray()
    {
        DashboardMonitor ??= _monitorService.GetMonitors().FirstOrDefault(m => m.IsPrimary);
        _showDashboard();
    }

    private void SavePosition(StatusBarWindow window, MonitorInfo monitor, double left, double top)
    {
        var currentKey = _windows.FirstOrDefault(pair => ReferenceEquals(pair.Value, window)).Key;
        if (!string.Equals(currentKey, monitor.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (Settings.ShowOnAllMonitors && _windows.TryGetValue(monitor.Id, out var existing) && !ReferenceEquals(existing, window))
            {
                existing.Close();
                _windows.Remove(monitor.Id);
            }
            if (currentKey is not null) _windows.Remove(currentKey);
            _windows[monitor.Id] = window;
            if (!Settings.ShowOnAllMonitors) Settings.SelectedMonitorIds = new List<string> { monitor.Id };
        }
        if (!Settings.Monitors.TryGetValue(monitor.Id, out var monitorSettings))
            Settings.Monitors[monitor.Id] = monitorSettings = new MonitorSettings();
        monitorSettings.Position = BarPosition.Custom;
        monitorSettings.Left = left;
        monitorSettings.Top = top;
        window.ApplySettings(Settings, monitorSettings);
        _ = _saveSettings();
        if (Settings.ShowOnAllMonitors && currentKey is not null && !string.Equals(currentKey, monitor.Id, StringComparison.OrdinalIgnoreCase))
            ApplySettings();
    }

    public void Dispose()
    {
        _fullscreenTimer.Stop();
        _monitorService.Dispose();
        foreach (var window in _windows.Values.ToList()) window.Close();
        _windows.Clear();
    }
}
