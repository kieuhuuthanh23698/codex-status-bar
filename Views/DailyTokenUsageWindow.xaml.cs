using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CodexStatusBar.Infrastructure;
using CodexStatusBar.Models;
using CodexStatusBar.Services;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Rectangle = System.Windows.Shapes.Rectangle;
using Cursors = System.Windows.Input.Cursors;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CodexStatusBar.Views;

public partial class DailyTokenUsageWindow : Window
{
    private const string InputColor = "#FF9B8AFB";
    private const string OutputColor = "#FF37CDB0";
    private const string TotalColor = "#FFFFD58A";
    private static readonly Color[] ModelPalette =
    [
        Color.FromRgb(129, 140, 248), Color.FromRgb(45, 212, 191), Color.FromRgb(251, 146, 60),
        Color.FromRgb(244, 114, 182), Color.FromRgb(163, 230, 53), Color.FromRgb(56, 189, 248),
        Color.FromRgb(192, 132, 252), Color.FromRgb(250, 204, 21), Color.FromRgb(248, 113, 113)
    ];
    private static readonly IReadOnlyDictionary<string, Color> KnownModelColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.6-luna"] = Color.FromRgb(99, 102, 241),
        ["gpt-6-luna"] = Color.FromRgb(20, 184, 166),
        ["gpt-5.6-sol"] = Color.FromRgb(245, 158, 11),
        ["gpt-6-sol"] = Color.FromRgb(236, 72, 153),
        ["gpt-6.1-sol"] = Color.FromRgb(56, 189, 248),
        ["gpt-6-astra"] = Color.FromRgb(168, 85, 247),
        ["Unknown model"] = Color.FromRgb(125, 139, 158)
    };

    private readonly StatusBarController _bars;
    private readonly UsageHistoryStore _history;
    private DailyTokenUsageSummary _summary = new(Array.Empty<DailyTokenUsagePoint>(), 0, null);
    private int _rangeDays = 7;
    private bool _isLoading;
    private bool _reloadPending;
    private bool _updatingPickers;
    private string _selectedMetric = "output";
    private DateOnly? _selectedDayDate;

    public DailyTokenUsageWindow(StatusBarController bars, UsageHistoryStore history)
    {
        InitializeComponent();
        _bars = bars;
        _history = history;
        UpdatePeriodButtons();
        UpdateMetricButtons();
        _bars.SnapshotChanged += Bars_SnapshotChanged;
        Closed += (_, _) => _bars.SnapshotChanged -= Bars_SnapshotChanged;
        Loaded += async (_, _) => await LoadAsync();
        SizeChanged += (_, _) => DrawChart();
    }

    private async void Bars_SnapshotChanged(object? sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_isLoading)
        {
            _reloadPending = true;
            return;
        }

        _isLoading = true;
        try
        {
            do
            {
                _reloadPending = false;
                var rangeDays = _rangeDays;
                StatusText.Text = "Reading local token history…";
                var summary = await Task.Run(() => _history.GetDailyTokenUsageAsync(rangeDays));
                if (!IsLoaded) return;
                _summary = summary;
                UpdateModelPicker();
                UpdateSummary();
                DrawChart();
                if (rangeDays != _rangeDays) _reloadPending = true;
            } while (_reloadPending);
        }
        catch (Exception ex)
        {
            AppLogger.Error("DailyTokenUsageWindow", "LoadDailyTokenUsage", ex,
                $"rangeDays={_rangeDays}; source=local-sqlite; database={System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStatusBar", "usage.db")}");
            StatusText.Text = "Token history could not be loaded. See the local error log for details.";
            EmptyText.Text = "Usage history is temporarily unavailable";
            EmptyText.Visibility = Visibility.Visible;
        }
        finally { _isLoading = false; }
    }

    private void UpdateModelPicker()
    {
        var selectedModel = SelectedModel;
        _updatingPickers = true;
        ModelPicker.Items.Clear();
        ModelPicker.Items.Add(new ComboBoxItem { Content = "All models", Tag = string.Empty });
        foreach (var model in _summary.ModelTotals)
            ModelPicker.Items.Add(new ComboBoxItem { Content = model.Model, Tag = model.Model });

        var selected = ModelPicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedModel, StringComparison.OrdinalIgnoreCase));
        ModelPicker.SelectedItem = selected ?? ModelPicker.Items[0];
        _updatingPickers = false;
    }

    private string? SelectedModel => (ModelPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { Length: > 0 } model
        ? model : null;

    private string SelectedMetric => _selectedMetric;

    private void UpdateSummary()
    {
        var selectedModel = SelectedModel;
        var totals = selectedModel is null
            ? _summary.ModelTotals
            : _summary.ModelTotals.Where(item => string.Equals(item.Model, selectedModel, StringComparison.OrdinalIgnoreCase)).ToArray();
        var input = totals.Sum(item => item.InputTokens);
        var output = totals.Sum(item => item.OutputTokens);
        var chats = selectedModel is null ? _summary.TrackedChatCount : totals.Sum(item => item.ChatsWithData);
        var measuredDays = _summary.ModelDays.Where(day => selectedModel is null ||
                string.Equals(day.Model, selectedModel, StringComparison.OrdinalIgnoreCase))
            .Select(day => day.Date).Distinct().Count();
        var hasData = totals.Count > 0;

        InputTotalText.Text = hasData ? FormatTokens(input) : "—";
        OutputTotalText.Text = hasData ? FormatTokens(output) : "—";
        TotalTokensText.Text = hasData ? FormatTokens(input + output) : "—";
        TrackedChatsText.Text = hasData ? chats.ToString("N0", CultureInfo.CurrentCulture) : "—";
        CoverageText.Text = hasData ? $"{measuredDays} measured days" : "No measured usage";
        InputCoverageText.Text = selectedModel is null ? "Across all measured models" : $"Model · {selectedModel}";
        OutputCoverageText.Text = InputCoverageText.Text;
        TrackedHistoryText.Text = selectedModel is null
            ? $"of {_summary.CatalogChatCount:N0} catalog chats"
            : $"of {_summary.ChatsWithHistoryCount:N0} chats with history";

        var usageTime = _summary.LastSampleAt is DateTimeOffset last
            ? $"Updated {last.ToLocalTime():MMM d, h:mm tt}"
            : "No local token records yet";
        var catalogTime = _summary.CatalogSyncedAt is DateTimeOffset synced
            ? $"Chat catalog {synced.ToLocalTime():MMM d, h:mm tt}"
            : "Chat catalog not synced";
        UpdatedText.Text = $"{usageTime}  ·  {catalogTime}";

        var metricLabel = SelectedMetric switch { "input" => "input tokens", "output" => "output tokens", _ => "tokens" };
        ChartTitleText.Text = selectedModel is null ? $"Daily {metricLabel} by model" : $"Daily usage · {selectedModel}";
        ChartSubtitleText.Text = selectedModel is null
            ? "Model colors show daily mix · total line shows overall volume"
            : "Input and output are compared separately · local time";
        MetricControls.Visibility = selectedModel is null ? Visibility.Visible : Visibility.Collapsed;
        SelectedModelLegend.Visibility = selectedModel is null ? Visibility.Collapsed : Visibility.Visible;
        TotalLegend.Visibility = selectedModel is null ? Visibility.Visible : Visibility.Collapsed;
        TotalLegendText.Text = SelectedMetric switch { "input" => "Input total", "output" => "Output total", _ => "Total" };
        UpdateMetricButtons();
        ModelBreakdownSubtitleText.Text = _summary.ModelTotals.Count == 0
            ? "No model usage measured yet"
            : $"{_summary.ModelTotals.Count} models · {_summary.TrackedChatCount:N0} contributing chats";
        ModelDataNoteText.Text = _summary.ModelTotals.Any(item => string.Equals(item.Model, "Unknown model", StringComparison.OrdinalIgnoreCase))
            ? "Some records lack model metadata and are grouped as Unknown model."
            : "Colors stay consistent across the chart and model list. Data is read locally.";
        ChartFootnoteText.Text = _summary.CatalogChatCount > 0
            ? $"Local history only · {_summary.ChatsWithHistoryCount:N0} of {_summary.CatalogChatCount:N0} catalog chats have token records · days without measurements are left blank."
            : "Local history only · days without measured token records are left blank, not shown as zero.";
        StatusText.Text = hasData
            ? $"Showing {FormatTokens(input + output)} tokens across {chats:N0} chats · {measuredDays} measured days · local data only."
            : "No token deltas are available for this selection yet. The chart fills as local usage records are synchronized.";

        PopulateModelBreakdown();
        UpdateSelectedDayDetails();
    }

    private void PopulateModelBreakdown()
    {
        ModelBreakdownPanel.Children.Clear();
        if (_summary.ModelTotals.Count == 0)
        {
            ModelBreakdownPanel.Children.Add(new TextBlock
            {
                Text = "Model totals will appear as Codex writes local token records.",
                Foreground = Brush("#FFA3B1C5"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });
            return;
        }

        var totalTokens = _summary.ModelTotals.Sum(item => item.InputTokens + item.OutputTokens);
        foreach (var model in _summary.ModelTotals)
        {
            var modelTotal = model.InputTokens + model.OutputTokens;
            var share = totalTokens == 0 ? 0 : modelTotal / (double)totalTokens;
            var color = ModelColor(model.Model);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14), Cursor = Cursors.Hand, ToolTip = "Click to filter the chart to this model" };
            row.MouseLeftButtonUp += (_, _) => SelectModel(model.Model);
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var marker = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(marker, 0);
            var name = new TextBlock
            {
                Text = model.Model, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = model.Model, Margin = new Thickness(2, 0, 5, 0)
            };
            Grid.SetColumn(name, 1);
            var amount = new TextBlock { Text = FormatTokens(modelTotal), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(amount, 2);
            heading.Children.Add(marker);
            heading.Children.Add(name);
            heading.Children.Add(amount);
            row.Children.Add(heading);

            row.Children.Add(new TextBlock
            {
                Text = $"In {FormatTokens(model.InputTokens)}   ·   Out {FormatTokens(model.OutputTokens)}",
                FontSize = 10, Foreground = Brush("#FFA3B1C5"), Margin = new Thickness(12, 4, 0, 6)
            });

            var track = new Grid { Height = 4, Margin = new Thickness(12, 0, 0, 0), ClipToBounds = true };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.005, share), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.005, 1 - share), GridUnitType.Star) });
            track.Children.Add(new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(2) });
            var rest = new Border { Background = Brush("#FF29374B"), CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(rest, 1);
            track.Children.Add(rest);
            row.Children.Add(track);
            row.Children.Add(new TextBlock
            {
                Text = $"{share:P1} share  ·  {model.ChatsWithData:N0} chats",
                FontSize = 10, Foreground = Brush("#FF8090A6"), Margin = new Thickness(12, 4, 0, 0)
            });
            ModelBreakdownPanel.Children.Add(row);
        }
    }

    private void DrawChart()
    {
        if (ChartCanvas is null) return;
        ChartCanvas.Children.Clear();
        var days = _summary.Days;
        if (days.Count == 0 || ChartCanvas.ActualWidth < 180 || ChartCanvas.ActualHeight < 120) return;

        var selectedModel = SelectedModel;
        var selectedMetric = SelectedMetric;
        var allModels = selectedModel is null;
        var visibleModels = allModels ? _summary.ModelTotals.Select(item => item.Model).ToArray() : [selectedModel!];
        var modelDays = _summary.ModelDays.GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.ToDictionary(item => item.Model, StringComparer.OrdinalIgnoreCase));
        double Value(DailyModelTokenUsage item) => selectedMetric switch
        {
            "input" => item.InputTokens,
            "output" => item.OutputTokens,
            _ => item.InputTokens + item.OutputTokens
        };

        var dailyMaximum = days.Select(day => modelDays.TryGetValue(day.Date, out var byModel)
                ? allModels
                    ? byModel.Values.Sum(Value)
                    : byModel.TryGetValue(selectedModel!, out var selected)
                        ? Math.Max(selected.InputTokens, selected.OutputTokens)
                        : 0
                : 0)
            .DefaultIfEmpty(0).Max();
        var hasData = dailyMaximum > 0 && visibleModels.Length > 0;
        var measuredDays = _summary.ModelDays.Where(item => selectedModel is null ||
                string.Equals(item.Model, selectedModel, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Date).Distinct().Count();
        var showDataLabels = allModels ? measuredDays <= 12 : measuredDays <= 8;
        ChartSubtitleText.Text = allModels
            ? showDataLabels
                ? "Model colors show the daily mix · hover for exact counts and model details"
                : "Hover a point for exact counts and model details · details stay below"
            : showDataLabels
                ? "Input and output are labeled · hover either bar for full details"
                : "Hover either bar for exact input/output · details stay below";
        EmptyText.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        if (!hasData)
        {
            EmptyText.Text = selectedModel is null
                ? "No local token usage is available for this period yet."
                : $"No token usage for {selectedModel} in this period.";
            return;
        }

        const double left = 56;
        const double right = 12;
        const double top = 8;
        const double bottom = 30;
        var width = ChartCanvas.ActualWidth - left - right;
        var height = ChartCanvas.ActualHeight - top - bottom;
        if (width <= 0 || height <= 0) return;

        var axisMaximum = NiceAxisMaximum(dailyMaximum);
        var labelBrush = Brush("#FF91A1B6");
        for (var tick = 0; tick <= 4; tick++)
        {
            var value = axisMaximum * (4 - tick) / 4;
            var y = top + height * tick / 4;
            ChartCanvas.Children.Add(new Line
            {
                X1 = left, X2 = left + width, Y1 = y, Y2 = y,
                Stroke = Brush(tick == 4 ? "#FF3A4A60" : "#FF29374B"), StrokeThickness = 1
            });
            AddCanvasText(FormatAxis(value), 0, y - 8, labelBrush, 10);
        }

        var slotWidth = width / days.Count;
        var barWidth = Math.Clamp(slotWidth * (allModels ? 0.62 : 0.30), allModels ? 1.8 : 1.4, allModels ? 24 : 13);
        var pairGap = Math.Max(1, slotWidth * 0.08);
        var baseline = top + height;
        var totals = new double?[days.Count];
        var dayTooltipValues = new IReadOnlyList<DailyModelTokenUsage>?[days.Count];
        foreach (var day in days)
        {
            var index = day.Date.DayNumber - days[0].Date.DayNumber;
            var slotStart = left + index * slotWidth;
            var x = slotStart + (slotWidth - barWidth) / 2;
            var emptyMarkWidth = allModels ? barWidth : barWidth * 2 + pairGap;
            var emptyMarkX = slotStart + (slotWidth - emptyMarkWidth) / 2;
            if (!modelDays.TryGetValue(day.Date, out var byModel))
            {
                AddNoDataMark(emptyMarkX, emptyMarkWidth, baseline, day.Date);
                continue;
            }

            if (!allModels)
            {
                if (!byModel.TryGetValue(selectedModel!, out var selected) ||
                    selected.InputTokens == 0 && selected.OutputTokens == 0)
                {
                    AddNoDataMark(emptyMarkX, emptyMarkWidth, baseline, day.Date);
                    continue;
                }

                var pairWidth = barWidth * 2 + pairGap;
                var pairX = slotStart + (slotWidth - pairWidth) / 2;
                var inputX = pairX + barWidth / 2;
                var outputX = pairX + barWidth + pairGap + barWidth / 2;
                var inputTop = baseline - height * selected.InputTokens / axisMaximum;
                var outputTop = baseline - height * selected.OutputTokens / axisMaximum;
                AddBar(pairX, baseline, height * selected.InputTokens / axisMaximum, barWidth,
                    Brush(InputColor), day.Date, [selected]);
                AddBar(pairX + barWidth + pairGap, baseline, height * selected.OutputTokens / axisMaximum, barWidth,
                    Brush(OutputColor), day.Date, [selected]);
                if (showDataLabels)
                {
                    AddDataLabel(FormatTokens(selected.InputTokens), inputX, inputTop, 16, Brush(InputColor),
                        day.Date, [selected]);
                    AddDataLabel(FormatTokens(selected.OutputTokens), outputX, outputTop, 29, Brush(OutputColor),
                        day.Date, [selected]);
                }
                continue;
            }

            var allDayEntries = visibleModels.Where(byModel.ContainsKey).Select(name => byModel[name]).ToArray();
            var dayEntries = allDayEntries.Where(item => Value(item) > 0).ToArray();
            if (dayEntries.Length == 0)
            {
                AddNoDataMark(emptyMarkX, emptyMarkWidth, baseline, day.Date);
                continue;
            }

            var dailyTotal = dayEntries.Sum(Value);
            totals[index] = dailyTotal;
            dayTooltipValues[index] = allDayEntries;
            var accumulated = 0d;
            foreach (var entry in dayEntries)
            {
                var segmentHeight = height * Value(entry) / axisMaximum;
                AddBar(x, baseline - accumulated, segmentHeight, barWidth,
                    new SolidColorBrush(ModelColor(entry.Model)), day.Date, dayEntries);
                accumulated += segmentHeight;
            }
        }

        if (allModels) DrawTotalLine(totals, dayTooltipValues, days, left, height, baseline, slotWidth, axisMaximum, showDataLabels);

        var labelStep = Math.Max(1, (int)Math.Ceiling(days.Count / 7d));
        for (var index = 0; index < days.Count; index += labelStep)
        {
            var dateLabel = new TextBlock
            {
                Text = days[index].Date.ToString("MMM d", CultureInfo.CurrentCulture),
                Foreground = labelBrush, FontSize = 10
            };
            Canvas.SetLeft(dateLabel, left + index * slotWidth);
            Canvas.SetTop(dateLabel, baseline + 7);
            ChartCanvas.Children.Add(dateLabel);
        }
    }

    private void AddBar(double x, double baseline, double valueHeight, double barWidth, Brush fill,
        DateOnly date, IReadOnlyList<DailyModelTokenUsage> tooltipValues)
    {
        if (valueHeight <= 0) return;
        var rect = new Rectangle
        {
            Width = barWidth, Height = Math.Max(1, valueHeight),
            Fill = fill, ToolTip = CreateDayTooltip(date, tooltipValues), RadiusX = 2, RadiusY = 2,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, baseline - rect.Height);
        AttachDayHover(rect, date);
        ChartCanvas.Children.Add(rect);
    }

    private void DrawTotalLine(double?[] values, IReadOnlyList<DailyModelTokenUsage>?[] tooltipValues,
        IReadOnlyList<DailyTokenUsagePoint> days, double left, double height, double baseline,
        double slotWidth, double axisMaximum, bool showDataLabels)
    {
        var lineBrush = Brush(TotalColor);
        var markerBrush = Brush(TotalColor);
        (int Index, double X, double Y)? previous = null;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is not double value || value <= 0)
            {
                previous = null;
                continue;
            }

            var point = (Index: index, X: left + index * slotWidth + slotWidth / 2,
                Y: baseline - height * value / axisMaximum);
            if (previous is { } prior && prior.Index == index - 1)
            {
                var line = new Line
                {
                    X1 = prior.X, Y1 = prior.Y, X2 = point.X, Y2 = point.Y,
                    Stroke = lineBrush, StrokeThickness = 2,
                    ToolTip = CreateDayTooltip(days[index].Date, tooltipValues[index] ?? Array.Empty<DailyModelTokenUsage>())
                };
                AttachDayHover(line, days[index].Date);
                ChartCanvas.Children.Add(line);
            }

            var marker = new Ellipse
            {
                Width = 6, Height = 6, Fill = markerBrush,
                Stroke = Brush("#FF151F30"), StrokeThickness = 1,
                ToolTip = CreateDayTooltip(days[index].Date, tooltipValues[index] ?? Array.Empty<DailyModelTokenUsage>())
            };
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            AttachDayHover(marker, days[index].Date);
            ChartCanvas.Children.Add(marker);
            if (showDataLabels)
                AddDataLabel(FormatTokens((long)value), point.X, point.Y, 19, markerBrush,
                    days[index].Date, tooltipValues[index] ?? Array.Empty<DailyModelTokenUsage>());
            previous = point;
        }
    }

    private void AddDataLabel(string text, double centerX, double anchorY, double offsetY, Brush foreground,
        DateOnly date, IReadOnlyList<DailyModelTokenUsage> tooltipValues)
    {
        var labelText = new TextBlock
        {
            Text = text, Foreground = foreground, FontSize = 12, FontWeight = FontWeights.Bold
        };
        var badge = new Border
        {
            Background = Brush("#F0121B29"), BorderBrush = Brush("#663A4B63"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 3, 6, 3),
            Child = labelText, ToolTip = CreateDayTooltip(date, tooltipValues)
        };
        badge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelWidth = badge.DesiredSize.Width;
        var labelHeight = badge.DesiredSize.Height;
        Canvas.SetLeft(badge, Math.Clamp(centerX - labelWidth / 2, 0, Math.Max(0, ChartCanvas.ActualWidth - labelWidth)));
        var y = anchorY - offsetY;
        if (y < 2) y = anchorY + 7;
        Canvas.SetTop(badge, Math.Min(y, Math.Max(0, ChartCanvas.ActualHeight - labelHeight)));
        AttachDayHover(badge, date);
        ChartCanvas.Children.Add(badge);
    }

    private ToolTip CreateDayTooltip(DateOnly date, IEnumerable<DailyModelTokenUsage> values)
    {
        var entries = values.ToArray();
        var tooltip = new ToolTip
        {
            Placement = PlacementMode.Mouse,
            Background = Brush("#FF101927"), Foreground = Brush("#FFF3F6FC"),
            BorderBrush = Brush("#FF455A75"), BorderThickness = new Thickness(1),
            Padding = new Thickness(12), HasDropShadow = true
        };

        // Build the detailed visual tree only when the user hovers a chart point.
        // ToolTipService manages the popup lifetime; setting StaysOpen=false here
        // crashes WPF when a ToolTip control is assigned to an element's ToolTip property.
        tooltip.Opened += (_, _) =>
        {
            if (tooltip.Content is null)
                tooltip.Content = BuildDayTooltipContent(date, entries);
        };
        return tooltip;
    }

    private StackPanel BuildDayTooltipContent(DateOnly date, IEnumerable<DailyModelTokenUsage> values)
    {
        var entries = values.OrderByDescending(item => item.InputTokens + item.OutputTokens).ToArray();
        var total = entries.Sum(item => item.InputTokens + item.OutputTokens);
        var chats = SelectedModel is null
            ? _summary.Days.FirstOrDefault(item => item.Date == date)?.ChatsWithData ?? 0
            : entries.Sum(item => item.ChatsWithData);
        var content = new StackPanel { MinWidth = 280, MaxWidth = 370 };
        content.Children.Add(new TextBlock
        {
            Text = date.ToDateTime(TimeOnly.MinValue).ToString("dddd, MMMM d", CultureInfo.CurrentCulture),
            FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brush("#FFF3F6FC")
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{total:N0} total tokens  ·  {chats:N0} chats contributing",
            FontSize = 11, Foreground = Brush("#FFA3B1C5"), Margin = new Thickness(0, 3, 0, 9)
        });

        foreach (var item in entries)
        {
            var row = new Border
            {
                Background = Brush("#FF172235"), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 3, 0, 0)
            };
            var detail = new StackPanel();
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var modelName = new TextBlock { Text = item.Model, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("#FFF3F6FC") };
            var modelTotal = new TextBlock { Text = $"{item.InputTokens + item.OutputTokens:N0}", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(ModelColor(item.Model)) };
            Grid.SetColumn(modelTotal, 1);
            header.Children.Add(modelName);
            header.Children.Add(modelTotal);
            detail.Children.Add(header);
            detail.Children.Add(new TextBlock
            {
                Text = $"Input {item.InputTokens:N0}   ·   Output {item.OutputTokens:N0}   ·   {item.ChatsWithData:N0} chats",
                FontSize = 10, Foreground = Brush("#FFB7C2D1"), Margin = new Thickness(0, 3, 0, 0)
            });
            row.Child = detail;
            content.Children.Add(row);
        }

        return content;
    }

    private void AttachDayHover(UIElement element, DateOnly date)
    {
        ToolTipService.SetInitialShowDelay(element, 100);
        ToolTipService.SetShowDuration(element, 30000);
        element.MouseEnter += (_, _) => ShowSelectedDay(date);
    }

    private void ShowSelectedDay(DateOnly date)
    {
        _selectedDayDate = date;
        UpdateSelectedDayDetails();
    }

    private void UpdateSelectedDayDetails()
    {
        var selectedModel = SelectedModel;
        var candidates = _summary.ModelDays.Where(item => selectedModel is null ||
            string.Equals(item.Model, selectedModel, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0)
        {
            SelectedDayDateText.Text = "No measured day";
            SelectedDayTotalText.Text = "—";
            SelectedDayInputText.Text = "—";
            SelectedDayOutputText.Text = "—";
            SelectedDayChatsText.Text = "—";
            SelectedDayModelSummaryText.Text = "There is no measured token usage for this selection and period.";
            return;
        }

        if (_selectedDayDate is not DateOnly selectedDate ||
            !candidates.Any(item => item.Date == selectedDate))
            _selectedDayDate = candidates.Max(item => item.Date);
        var date = _selectedDayDate!.Value;
        var entries = candidates.Where(item => item.Date == date).ToArray();
        var input = entries.Sum(item => item.InputTokens);
        var output = entries.Sum(item => item.OutputTokens);
        var total = input + output;
        var dailyPoint = _summary.Days.FirstOrDefault(item => item.Date == date);
        var chats = selectedModel is null
            ? dailyPoint?.ChatsWithData ?? 0
            : entries.Sum(item => item.ChatsWithData);

        SelectedDayDateText.Text = date.ToDateTime(TimeOnly.MinValue).ToString("ddd, MMM d", CultureInfo.CurrentCulture);
        SelectedDayTotalText.Text = total.ToString("N0", CultureInfo.CurrentCulture);
        SelectedDayInputText.Text = input.ToString("N0", CultureInfo.CurrentCulture);
        SelectedDayOutputText.Text = output.ToString("N0", CultureInfo.CurrentCulture);
        SelectedDayChatsText.Text = chats.ToString("N0", CultureInfo.CurrentCulture);
        SelectedDayModelSummaryText.Text = selectedModel is not null
            ? $"Model: {selectedModel} · exact values are also available by hovering the chart bars."
            : "Model mix: " + string.Join("   ·   ", entries.OrderByDescending(item => item.InputTokens + item.OutputTokens)
                .Select(item => $"{item.Model}  {FormatTokens(item.InputTokens + item.OutputTokens)}"));
    }

    private void AddNoDataMark(double x, double barWidth, double baseline, DateOnly date)
    {
        var mark = new Rectangle
        {
            Width = Math.Max(2, barWidth * 0.55), Height = 2,
            Fill = Brush("#FF3A4A60"), ToolTip = $"{date.ToDateTime(TimeOnly.MinValue):dddd, MMMM d}\nNo measured token usage"
        };
        Canvas.SetLeft(mark, x + (barWidth - mark.Width) / 2);
        Canvas.SetTop(mark, baseline - 1);
        ChartCanvas.Children.Add(mark);
    }

    private void AddCanvasText(string value, double x, double y, Brush foreground, double fontSize)
    {
        var text = new TextBlock { Text = value, Foreground = foreground, FontSize = fontSize };
        Canvas.SetLeft(text, x);
        Canvas.SetTop(text, y);
        ChartCanvas.Children.Add(text);
    }

    private static Color ModelColor(string model)
    {
        if (KnownModelColors.TryGetValue(model, out var knownColor)) return knownColor;
        var normalized = model.Trim().ToLowerInvariant();
        var hash = 17;
        foreach (var character in normalized) hash = unchecked(hash * 31 + character);
        return ModelPalette[(hash & int.MaxValue) % ModelPalette.Length];
    }

    private static SolidColorBrush Brush(string color) => new((Color)System.Windows.Media.ColorConverter.ConvertFromString(color));

    private static double NiceAxisMaximum(double maximum)
    {
        if (maximum <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        var normalized = maximum / magnitude;
        var step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return step * magnitude;
    }

    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}k",
        _ => value.ToString("N0", CultureInfo.CurrentCulture)
    };

    private static string FormatAxis(double value) => value >= 1_000_000_000
        ? $"{value / 1_000_000_000:0.#}B"
        : value >= 1_000_000 ? $"{value / 1_000_000:0.#}M"
        : value >= 1_000 ? $"{value / 1_000:0.#}k" : value.ToString("0", CultureInfo.CurrentCulture);

    private void Period_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            !int.TryParse(button.Tag?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var days)) return;
        _rangeDays = days;
        UpdatePeriodButtons();
        _ = LoadAsync();
    }

    private void Metric_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string metric) return;
        _selectedMetric = metric;
        UpdateMetricButtons();
        UpdateSummary();
        DrawChart();
    }

    private void UpdatePeriodButtons()
    {
        SetSegmentButton(Range7Button, _rangeDays == 7);
        SetSegmentButton(Range30Button, _rangeDays == 30);
        SetSegmentButton(Range90Button, _rangeDays == 90);
    }

    private void UpdateMetricButtons()
    {
        SetSegmentButton(MetricTotalButton, _selectedMetric == "total");
        SetSegmentButton(MetricInputButton, _selectedMetric == "input");
        SetSegmentButton(MetricOutputButton, _selectedMetric == "output");
    }

    private static void SetSegmentButton(Button button, bool selected)
    {
        button.Background = Brush(selected ? "#FF344760" : "#00000000");
        button.Foreground = Brush(selected ? "#FFF3F6FC" : "#FFA3B1C5");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPickers || !IsLoaded) return;
        UpdateSummary();
        DrawChart();
    }

    private void SelectModel(string model)
    {
        var target = ModelPicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), model, StringComparison.OrdinalIgnoreCase));
        if (target is not null) ModelPicker.SelectedItem = target;
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
