using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CodexStatusBar.Models;
using CodexStatusBar.Services;

namespace CodexStatusBar.Views;

public sealed class MonitorOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsSelected { get; set; }
}

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<MonitorOption> _monitors = new();

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        StartWindows.IsChecked = settings.StartWithWindows;
        ShowTray.IsChecked = settings.ShowTrayIcon;
        ShowBar.IsChecked = settings.ShowFloatingBar;
        AlwaysTop.IsChecked = settings.AlwaysOnTop;
        HideFullscreen.IsChecked = settings.HideWhenFullscreen;
        LockDefault.IsChecked = settings.LockPosition;
        AllMonitors.IsChecked = settings.ShowOnAllMonitors;
        Show5H.IsChecked = settings.ShowFiveHourQuota;
        ShowWeek.IsChecked = settings.ShowWeeklyQuota;
        OpacitySlider.Value = settings.Opacity * 100;
        ContextThreshold.Text = settings.ContextWarningPercent.ToString("0", CultureInfo.InvariantCulture);
        QuotaThreshold.Text = settings.QuotaWarningPercent.ToString("0", CultureInfo.InvariantCulture);
        HighThreshold.Text = settings.HighWarningPercent.ToString("0", CultureInfo.InvariantCulture);
        CriticalThreshold.Text = settings.CriticalWarningPercent.ToString("0", CultureInfo.InvariantCulture);
        SelectCombo(RefreshInterval, settings.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture));
        SelectCombo(QuotaValueModePicker, settings.QuotaValueMode.ToString());
        DisplayMode.SelectedIndex = (int)settings.DisplayMode;
        Position.SelectedIndex = Math.Clamp((int)settings.DefaultPosition, 0, 5);
        using var monitorService = new MonitorService();
        foreach (var screen in monitorService.GetMonitors())
            _monitors.Add(new MonitorOption
            {
                Id = screen.Id,
                Label = $"{screen.Name} — {screen.Bounds.Width}x{screen.Bounds.Height}{(screen.IsPrimary ? " — Primary" : "")}",
                IsSelected = settings.SelectedMonitorIds.Contains(screen.Id, StringComparer.OrdinalIgnoreCase)
            });
        MonitorsList.ItemsSource = _monitors;
    }

    private static void SelectCombo(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (item.Tag?.ToString() == tag) { combo.SelectedItem = item; return; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = StartWindows.IsChecked == true;
        _settings.ShowTrayIcon = ShowTray.IsChecked == true;
        _settings.ShowFloatingBar = ShowBar.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysTop.IsChecked == true;
        _settings.HideWhenFullscreen = HideFullscreen.IsChecked == true;
        _settings.LockPosition = LockDefault.IsChecked == true;
        _settings.ShowOnAllMonitors = AllMonitors.IsChecked == true;
        _settings.ShowFiveHourQuota = Show5H.IsChecked == true;
        _settings.ShowWeeklyQuota = ShowWeek.IsChecked == true;
        if (QuotaValueModePicker.SelectedItem is ComboBoxItem quotaMode &&
            Enum.TryParse<QuotaValueMode>(quotaMode.Tag?.ToString(), out var selectedQuotaMode))
            _settings.QuotaValueMode = selectedQuotaMode;
        _settings.Opacity = OpacitySlider.Value / 100;
        if (RefreshInterval.SelectedItem is ComboBoxItem interval && int.TryParse(interval.Tag?.ToString(), out var seconds))
            _settings.RefreshIntervalSeconds = seconds;
        _settings.DisplayMode = (BarDisplayMode)Math.Max(0, DisplayMode.SelectedIndex);
        _settings.DefaultPosition = (BarPosition)Math.Clamp(Position.SelectedIndex, 0, 5);
        _settings.SelectedMonitorIds = _monitors.Where(m => m.IsSelected).Select(m => m.Id).ToList();
        _settings.ContextWarningPercent = ParseThreshold(ContextThreshold.Text, 80);
        _settings.QuotaWarningPercent = ParseThreshold(QuotaThreshold.Text, 70);
        _settings.HighWarningPercent = ParseThreshold(HighThreshold.Text, 85);
        _settings.CriticalWarningPercent = ParseThreshold(CriticalThreshold.Text, 95);
        _settings.NotifyChanged(nameof(AppSettings.SelectedMonitorIds));
        Close();
    }

    private static double ParseThreshold(string value, double fallback) =>
        double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 1, 100) : fallback;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
