using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Data;
using System.Runtime.InteropServices;
using CodexStatusBar.Infrastructure;
using CodexStatusBar.Models;
using CodexStatusBar.Services;

namespace CodexStatusBar.Views;

public partial class DashboardWindow : Window
{
    private readonly StatusBarController _bars;
    private readonly UsageHistoryStore _history;
    private readonly Func<Task> _refresh;
    private readonly Action _openDailyTokens;
    private bool _updating;
    private IReadOnlyList<HistoryPoint> _points = Array.Empty<HistoryPoint>();
    private IReadOnlyList<ThreadUsage> _allChats = Array.Empty<ThreadUsage>();
    private HashSet<string> _selectedChatIds = new(StringComparer.Ordinal);

    public DashboardWindow(StatusBarController bars, UsageHistoryStore history, Func<Task> refresh,
        Action openDailyTokens, MonitorInfo? monitor = null)
    {
        InitializeComponent();
        _bars = bars; _history = history; _refresh = refresh; _openDailyTokens = openDailyTokens;
        _bars.SnapshotChanged += Bars_SnapshotChanged;
        _bars.Settings.PropertyChanged += Settings_PropertyChanged;
        Closed += (_, _) =>
        {
            _bars.SnapshotChanged -= Bars_SnapshotChanged;
            _bars.Settings.PropertyChanged -= Settings_PropertyChanged;
        };
        Loaded += async (_, _) =>
        {
            if (monitor is not null) PositionOnMonitor(monitor);
            await UpdateViewAsync();
        };
        SizeChanged += (_, _) => DrawChart();
    }

    private async void Bars_SnapshotChanged(object? sender, EventArgs e) => await UpdateViewAsync();

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.QuotaValueMode) || !IsLoaded) return;
        var snapshot = _bars.Snapshot;
        UpdateQuotaValues(
            snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 300),
            snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 10080));
    }

    private void UpdateQuotaValues(QuotaWindow? fiveHour, QuotaWindow? weekly)
    {
        var mode = _bars.Settings.QuotaValueMode;
        FiveHourText.Text = QuotaPercentFormatter.Format(fiveHour?.UsedPercent, mode, includeMeaning: true);
        WeeklyText.Text = QuotaPercentFormatter.Format(weekly?.UsedPercent, mode, includeMeaning: true);
    }

    private async Task UpdateViewAsync()
    {
        _updating = true;
        try
        {
            var snapshot = _bars.Snapshot;
            var selected = _bars.SelectedThread;
            _allChats = snapshot.Threads;
            var chatsView = CollectionViewSource.GetDefaultView(_allChats);
            chatsView.Filter = MatchesChatSearch;
            ChatsList.ItemsSource = chatsView;
            ChatsList.SelectedValuePath = nameof(ThreadUsage.Id);
            var desiredIds = _selectedChatIds.Count > 1
                ? _selectedChatIds.Where(id => _allChats.Any(chat => chat.Id == id)).ToHashSet(StringComparer.Ordinal)
                : selected is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>([selected.Id], StringComparer.Ordinal);
            _selectedChatIds = desiredIds;
            ChatsList.SelectedItems.Clear();
            foreach (var chat in _allChats.Where(chat => desiredIds.Contains(chat.Id) && ChatsList.Items.Contains(chat)))
                ChatsList.SelectedItems.Add(chat);
            UpdateChatCount();
            var selectedChats = _allChats.Where(chat => _selectedChatIds.Contains(chat.Id)).ToList();
            var aggregate = selectedChats.Count > 1;
            var five = snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 300);
            var week = snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 10080);
            UpdateQuotaValues(five, week);
            FiveHourReset.Text = ResetText(five);
            WeeklyReset.Text = ResetText(week);
            InputTokensLabel.Text = aggregate ? "SELECTED INPUT" : "CHAT INPUT";
            OutputTokensLabel.Text = aggregate ? "SELECTED OUTPUT" : "CHAT OUTPUT";
            ContextLabel.Text = aggregate ? "PEAK CONTEXT" : "CONTEXT USED";
            var inputTotal = SumAvailable(selectedChats.Select(chat => chat.TotalInputTokens));
            var outputTotal = SumAvailable(selectedChats.Select(chat => chat.TotalOutputTokens));
            InputTokensText.Text = TokenValue(inputTotal);
            OutputTokensText.Text = TokenValue(outputTotal);
            InputTokensText.ToolTip = aggregate
                ? AggregateTokenTooltip(inputTotal, selectedChats.Count(chat => chat.TotalInputTokens.HasValue), selectedChats.Count)
                : ExactTokens(inputTotal);
            OutputTokensText.ToolTip = aggregate
                ? AggregateTokenTooltip(outputTotal, selectedChats.Count(chat => chat.TotalOutputTokens.HasValue), selectedChats.Count)
                : ExactTokens(outputTotal);
            var models = selectedChats.Select(chat => string.IsNullOrWhiteSpace(chat.Model) ? "Unavailable" : chat.Model!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(model => model, StringComparer.CurrentCultureIgnoreCase).ToArray();
            SelectedChatText.Text = selectedChats.Count switch
            {
                0 => "No chat selected",
                1 => $"Selected chat: {selectedChats[0].Label} · Model: {models.FirstOrDefault() ?? "Unavailable"}",
                _ => $"Selected chats: {selectedChats.Count} · Models: {string.Join(", ", models)}"
            };
            SelectedChatText.ToolTip = selectedChats.Count > 1
                ? string.Join(Environment.NewLine, selectedChats.Select(chat => $"{chat.Label} · {chat.Model ?? "Model unavailable"}"))
                : SelectedChatText.Text;
            var peakContext = selectedChats.Where(chat => chat.ContextUsedPercent.HasValue).MaxBy(chat => chat.ContextUsedPercent)?.ContextUsedPercent;
            ContextText.Text = Percent(peakContext);
            ContextText.ToolTip = aggregate && peakContext.HasValue
                ? $"Highest current context among selected chats ({selectedChats.Count} selected)."
                : null;
            FreshnessText.Text = snapshot.CatalogSyncedAt is DateTimeOffset catalogAt
                ? $"Catalog · {_allChats.Count:N0} chats · synced {catalogAt.ToLocalTime():T}"
                : snapshot.ErrorMessage ?? snapshot.WarningMessage ?? "Chat catalog is not synced yet";
            UpdatedText.Text = snapshot.ErrorMessage ?? $"Last updated {snapshot.UpdatedAt.ToLocalTime():g}";
            StatusMessage.Text = snapshot.ErrorMessage ?? snapshot.CatalogSyncMessage ?? snapshot.WarningMessage ??
                "Chat list and token usage are read locally through Codex app-server and rollout history.";
            HistoryCaption.Text = aggregate
                ? "Peak context across selected chats · daily samples over the last 30 days"
                : "Context usage over the last 30 days";
            try
            {
                if (selectedChats.Count == 0) _points = Array.Empty<HistoryPoint>();
                else if (selectedChats.Count == 1) _points = await _history.GetHistoryAsync(selectedChats[0].Id);
                else
                {
                    var histories = await _history.GetHistoriesAsync(selectedChats.Select(chat => chat.Id));
                    _points = AggregateDailyHistory(histories);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("UsageHistoryStore", "GetHistoryAsync", ex,
                    $"database={System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStatusBar", "usage.db")}");
                _points = Array.Empty<HistoryPoint>();
                StatusMessage.Text = "Usage history could not be loaded. See local logs for details.";
            }
            var dayDelta = GetTokenDeltaSince(DateTimeOffset.UtcNow.AddDays(-1));
            var weekDelta = GetTokenDeltaSince(DateTimeOffset.UtcNow.AddDays(-7));
            HistorySummary.Text = _points.Count > 0
                ? $"24h {SignedTokens(dayDelta)} · 7d {SignedTokens(weekDelta)} · {_points.Count} { (aggregate ? "days" : "samples") }"
                : "No history yet";
            DrawChart();
        }
        finally { _updating = false; }
    }

    private void DrawChart()
    {
        if (ChartCanvas is null) return;
        ChartCanvas.Children.Clear();
        var width = Math.Max(0, ChartCanvas.ActualWidth - 20);
        var height = Math.Max(0, ChartCanvas.ActualHeight - 20);
        if (width < 10 || height < 10) return;
        for (var i = 0; i <= 4; i++)
        {
            var y = 10 + height * i / 4;
            var grid = new Line { X1 = 10, X2 = width + 10, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255)), StrokeThickness = 1 };
            ChartCanvas.Children.Add(grid);
            var label = new TextBlock { Text = $"{100 - i * 25}%", Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175)), FontSize = 10 };
            Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 8); ChartCanvas.Children.Add(label);
        }
        var points = _points.Where(p => p.ContextPercent.HasValue).ToList();
        if (points.Count < 2) return;
        var minTime = points[0].CapturedAt.ToUnixTimeSeconds();
        var maxTime = Math.Max(minTime + 1, points[^1].CapturedAt.ToUnixTimeSeconds());
        var path = new Polyline { Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(167, 139, 250)), StrokeThickness = 2 };
        foreach (var point in points)
        {
            var x = 10 + width * (point.CapturedAt.ToUnixTimeSeconds() - minTime) / (maxTime - minTime);
            var y = 10 + height * (1 - Math.Clamp(point.ContextPercent!.Value / 100, 0, 1));
            path.Points.Add(new System.Windows.Point(x, y));
        }
        ChartCanvas.Children.Add(path);
    }

    private long? GetTokenDeltaSince(DateTimeOffset cutoff)
    {
        var comparable = _points.Where(p => p.TotalTokens.HasValue).ToList();
        if (comparable.Count == 0 || comparable[^1].CapturedAt < cutoff) return null;
        var start = comparable.LastOrDefault(p => p.CapturedAt <= cutoff) ?? comparable[0];
        return Math.Max(0, comparable[^1].TotalTokens!.Value - start.TotalTokens!.Value);
    }

    private static string SignedTokens(long? delta) => delta is long value ? $"+{Tokens(value)}" : "—";

    private void PositionOnMonitor(MonitorInfo monitor)
    {
        var area = monitor.WorkingArea;
        SetWindowPos(new WindowInteropHelper(this).Handle, nint.Zero, area.Left + 8, area.Top + 8, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
        var dpi = VisualTreeHelper.GetDpi(this);
        var areaLeft = area.Left / dpi.DpiScaleX;
        var areaTop = area.Top / dpi.DpiScaleY;
        Left = areaLeft + (area.Width / dpi.DpiScaleX - ActualWidth) / 2;
        Top = areaTop + (area.Height / dpi.DpiScaleY - ActualHeight) / 2;
    }

    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    private void ChatsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var selectedChats = ChatsList.SelectedItems.OfType<ThreadUsage>().ToList();
        if (selectedChats.Count == 0) return;
        _selectedChatIds = selectedChats.Select(chat => chat.Id).ToHashSet(StringComparer.Ordinal);
        var latestSelection = e.AddedItems.OfType<ThreadUsage>().LastOrDefault() ?? selectedChats[^1];
        _bars.SelectThread(latestSelection.Id);
    }

    private bool MatchesChatSearch(object item)
    {
        if (item is not ThreadUsage chat) return false;
        var query = ChatSearchBox.Text.Trim();
        return query.Length == 0 || chat.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (chat.Workspace?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
            chat.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ChatSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ChatsList?.ItemsSource is ICollectionView chatsView) chatsView.Refresh();
        UpdateChatCount();
    }

    private void UpdateChatCount()
    {
        if (ChatCountText is null) return;
        var total = _allChats.Count;
        var query = ChatSearchBox?.Text.Trim() ?? "";
        if (query.Length == 0)
        {
            ChatCountText.Text = SelectionCountText(total);
            return;
        }
        var matches = _allChats.Count(chat => MatchesChatSearch(chat));
        ChatCountText.Text = $"{matches} / {total}" + (_selectedChatIds.Count > 1 ? $" · {_selectedChatIds.Count} selected" : "");
    }
    private string SelectionCountText(int total) => $"{total} {(total == 1 ? "chat" : "chats")}" +
        (_selectedChatIds.Count > 1 ? $" · {_selectedChatIds.Count} selected" : "");
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _refresh();
    private void DailyTokens_Click(object sender, RoutedEventArgs e) => _openDailyTokens();
    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(_bars.Settings).Show();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var selectedIds = _selectedChatIds.ToArray();
        if (selectedIds.Length == 0 && _bars.SelectedThread is { } pinned) selectedIds = [pinned.Id];
        if (selectedIds.Length == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = selectedIds.Length > 1
                ? $"codex-usage-{selectedIds.Length}-chats-{DateTime.Now:yyyyMMdd}.csv"
                : $"codex-usage-{DateTime.Now:yyyyMMdd}.csv",
            Filter = "CSV files|*.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (selectedIds.Length > 1) await _history.ExportCsvAsync(selectedIds, dialog.FileName);
            else await _history.ExportCsvAsync(selectedIds[0], dialog.FileName);
            StatusMessage.Text = selectedIds.Length > 1 ? $"CSV exported for {selectedIds.Length} chats." : "CSV exported.";
        }
        catch (Exception ex)
        {
            AppLogger.Error("UsageHistoryStore", "ExportCsvAsync", ex,
                $"database={System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStatusBar", "usage.db")}; exportContent=usage-metrics-only");
            StatusMessage.Text = $"Export failed: {ex.Message}";
        }
    }

    private static string Percent(double? value) => value is double number ? $"{number:0}% used" : "Unavailable";
    private static string TokenValue(long? value) => value is long count ? Tokens(count) : "Unavailable";
    private static string ExactTokens(long? value) => value is long count ? count.ToString("N0") : "Unavailable";
    private static string Tokens(long? value) => value is long count ? count switch
    { >= 1_000_000 => $"{count / 1_000_000d:0.#}M", >= 1_000 => $"{count / 1_000d:0.#}k", _ => count.ToString("N0") } : "—";
    private static string ResetText(QuotaWindow? quota) => quota?.ResetsAt is DateTimeOffset reset
        ? $"Resets in {Remaining(reset)}" : "Reset time unavailable";
    private static string Remaining(DateTimeOffset reset)
    {
        var remaining = reset - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "soon";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        return $"{remaining:hh\\:mm\\:ss}";
    }

    private static long? SumAvailable(IEnumerable<long?> values)
    {
        var available = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Sum();
    }

    private static string AggregateTokenTooltip(long? total, int availableChats, int selectedChats) => total.HasValue
        ? $"{total.Value:N0} tokens total. Data available for {availableChats} of {selectedChats} selected chats; the sum may be incomplete."
        : $"Token usage is unavailable for all {selectedChats} selected chats.";

    private static IReadOnlyList<HistoryPoint> AggregateDailyHistory(
        IReadOnlyDictionary<string, IReadOnlyList<HistoryPoint>> histories)
    {
        var dailyPerChat = histories.Values.SelectMany(points => points
            .GroupBy(point => point.CapturedAt.UtcDateTime.Date)
            .Select(day => day.OrderBy(point => point.CapturedAt).Last()));
        return dailyPerChat
            .GroupBy(point => point.CapturedAt.UtcDateTime.Date)
            .OrderBy(day => day.Key)
            .Select(day => new HistoryPoint(
                day.Max(point => point.CapturedAt),
                day.Any(point => point.TotalTokens.HasValue)
                    ? day.Where(point => point.TotalTokens.HasValue).Sum(point => point.TotalTokens!.Value)
                    : null,
                day.Where(point => point.ContextPercent.HasValue).Select(point => point.ContextPercent).DefaultIfEmpty().Max(),
                null,
                null))
            .ToArray();
    }
}
