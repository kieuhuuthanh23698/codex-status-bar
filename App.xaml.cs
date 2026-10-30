using System.Windows;
using System.IO;
using CodexStatusBar.Infrastructure;
using CodexStatusBar.Models;
using CodexStatusBar.Services;
using CodexStatusBar.Views;

namespace CodexStatusBar;

public partial class App : System.Windows.Application
{
    private SettingsService? _settingsService;
    private UsageHistoryStore? _history;
    private CodexUsageProvider? _usageProvider;
    private RolloutUsageSyncService? _rolloutSync;
    private TrayIconManager? _tray;
    private StatusBarController? _bars;
    private DashboardWindow? _dashboardWindow;
    private DailyTokenUsageWindow? _dailyTokenUsageWindow;
    private System.Threading.Timer? _refreshTimer;
    private System.Threading.Timer? _catalogTimer;
    private System.Threading.Timer? _catalogDebounceTimer;
    private System.Threading.Timer? _usageDebounceTimer;
    private System.Windows.Threading.DispatcherTimer? _settingsTimer;
    private int _refreshing;
    private int _catalogSyncing;
    private int _catalogSyncPending;
    private int _exitStarted;
    private DateTimeOffset _lastFullCatalogSync = DateTimeOffset.MinValue;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => AppLogger.Error("App", "DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                AppLogger.Error("App", "UnhandledException", exception, $"isTerminating={args.IsTerminating}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppLogger.Error("App", "UnobservedTaskException", args.Exception);
        try
        {
            NotificationService.Initialize();
            _settingsService = new SettingsService();
            var settings = await _settingsService.LoadAsync();
            _history = new UsageHistoryStore();
            try { await _history.InitializeAsync(); }
            catch (Exception ex)
            {
                AppLogger.Error("UsageHistoryStore", "InitializeAsync", ex,
                    $"database={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStatusBar", "usage.db")}");
                throw;
            }
            _usageProvider = new CodexUsageProvider();
            _usageProvider.CatalogChanged += ScheduleCatalogSync;
            _usageProvider.UsageUpdated += ScheduleUsageRefresh;
            _usageProvider.ThreadDeleted += OnThreadDeleted;
            _catalogDebounceTimer = new System.Threading.Timer(CatalogDebounceTick, null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _usageDebounceTimer = new System.Threading.Timer(UsageDebounceTick, null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _rolloutSync = new RolloutUsageSyncService(_history);
            _rolloutSync.SyncRequested += ScheduleCatalogSync;
            _rolloutSync.UsageHistoryChanged += NotifyUsageHistoryChanged;
            await _rolloutSync.StartAsync();
            _bars = new StatusBarController(settings, ShowDashboard, ShowSettings, RefreshNow,
                RefreshSelectedThread, ExitApp, () => _settingsService.SaveAsync(settings));
            _tray = new TrayIconManager(ShowDashboardFromTray, ShowSettings, ToggleBars, RefreshNow, ShowLogs, ExitApp);
            _bars.VisibilityChanged += visible => _tray?.SetBarVisible(visible);
            _settingsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _settingsTimer.Tick += async (_, _) =>
            {
                _settingsTimer.Stop();
                try
                {
                    await _settingsService.SaveAsync(settings);
                    if (_tray is not null)
                    {
                        _tray.Visible = settings.ShowTrayIcon;
                        _tray.SetBarVisible(_bars.IsBarVisible);
                    }
                    _bars.ApplySettings();
                    var currentSnapshot = _bars.Snapshot;
                    _tray?.SetStatus(currentSnapshot.ErrorMessage ?? currentSnapshot.WarningMessage ??
                        FormatTraySummary(currentSnapshot, _bars.SelectedThread, settings.QuotaValueMode),
                        isError: currentSnapshot.ErrorMessage is not null);
                    ConfigureTimer(settings.RefreshIntervalSeconds);
                }
                catch (Exception ex) { AppLogger.Error("App", "SaveSettingsTimer", ex); }
            };
            settings.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AppSettings.StartWithWindows))
                    StartupService.Apply(settings.StartWithWindows);
                _settingsTimer.Stop();
                _settingsTimer.Start();
            };
            await _settingsService.SaveAsync(settings);
            StartupService.Apply(settings.StartWithWindows);
            _tray.Visible = settings.ShowTrayIcon;
            _tray.SetBarVisible(settings.ShowFloatingBar);
            _bars.ApplySettings();
            ConfigureTimer(settings.RefreshIntervalSeconds);
            await RefreshNow();
            _catalogTimer = new System.Threading.Timer(CatalogTimerTick, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
        catch (Exception ex)
        {
            AppLogger.Error("App", "OnStartup", ex);
            if (_usageProvider is not null)
            {
                try { await _usageProvider.DisposeAsync(); }
                catch (Exception disposeException) { AppLogger.Error("CodexUsageProvider", "DisposeAfterStartupFailure", disposeException); }
            }
            if (_rolloutSync is not null)
            {
                try { await _rolloutSync.DisposeAsync(); }
                catch (Exception disposeException) { AppLogger.Error("RolloutUsageSync", "DisposeAfterStartupFailure", disposeException); }
            }
            System.Windows.MessageBox.Show($"Codex Status Bar could not start.\n\n{ex.Message}", "Codex Status Bar", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _refreshTimer?.Dispose();
        _catalogTimer?.Dispose();
        _catalogDebounceTimer?.Dispose();
        _usageDebounceTimer?.Dispose();
        _settingsTimer?.Stop();
        if (_rolloutSync is not null)
        {
            try
            {
                _ = _rolloutSync.DisposeAsync().AsTask().ContinueWith(task =>
                {
                    if (task.Exception is not null) AppLogger.Error("RolloutUsageSync", "DisposeOnApplicationExit", task.Exception);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex) { AppLogger.Error("RolloutUsageSync", "StartDisposeOnApplicationExit", ex); }
        }
        if (_usageProvider is not null)
        {
            try
            {
                _ = _usageProvider.DisposeAsync().AsTask().ContinueWith(task =>
                {
                    if (task.Exception is not null)
                        AppLogger.Error("CodexUsageProvider", "DisposeOnApplicationExit", task.Exception);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex) { AppLogger.Error("CodexUsageProvider", "StartDisposeOnApplicationExit", ex); }
        }
        _tray?.Dispose();
        _bars?.Dispose();
        NotificationService.Shutdown();
        base.OnExit(e);
    }

    private void ConfigureTimer(int seconds)
    {
        _refreshTimer?.Dispose();
        _refreshTimer = new System.Threading.Timer(async _ => await RefreshNow(), null,
            TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds));
    }

    private Task RefreshNow() => RefreshUsageAsync(selectedThreadId: null);

    private Task RefreshSelectedThread(string threadId) => RefreshUsageAsync(threadId);

    private async Task RefreshUsageAsync(string? selectedThreadId)
    {
        if (Volatile.Read(ref _exitStarted) != 0 || _usageProvider is null || _history is null || _bars is null ||
            Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            var snapshot = selectedThreadId is null
                ? await _usageProvider.ReadSnapshotAsync(_bars.Settings.PinnedThreadId)
                : await _usageProvider.ReadSelectedThreadSnapshotAsync(selectedThreadId);
            if (Volatile.Read(ref _exitStarted) != 0) return;
            if (selectedThreadId is null && snapshot.ErrorMessage is null)
                _lastFullCatalogSync = DateTimeOffset.UtcNow;
            try { snapshot = await _history.RecordAndEnrichAsync(snapshot); }
            catch (Exception ex)
            {
                AppLogger.Error("UsageHistoryStore", "RecordAndEnrichAsync", ex,
                    $"database={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStatusBar", "usage.db")}");
                throw;
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _exitStarted) != 0) return;
                _bars.Update(snapshot);
                _tray?.SetStatus(snapshot.ErrorMessage ?? snapshot.WarningMessage ??
                    FormatTraySummary(snapshot, _bars.SelectedThread, _bars.Settings.QuotaValueMode),
                    isError: snapshot.ErrorMessage is not null);
                NotificationService.Check(snapshot, _bars.Settings, _tray);
            });
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _exitStarted) != 0) { }
        catch (Exception ex)
        {
            if (ex is not Microsoft.Data.Sqlite.SqliteException)
                AppLogger.Error("App", "RefreshUsageAsync", ex);
            if (Volatile.Read(ref _exitStarted) == 0)
            {
                await Dispatcher.InvokeAsync(() => _bars.ShowError(ex.Message));
                await Dispatcher.InvokeAsync(() => _tray?.SetStatus(ex.Message, isError: true));
            }
        }
        finally { Volatile.Write(ref _refreshing, 0); }
    }

    private void CatalogTimerTick(object? state)
    {
        if (Volatile.Read(ref _exitStarted) != 0) return;
        var fullScanDue = DateTimeOffset.UtcNow - _lastFullCatalogSync >= TimeSpan.FromMinutes(5);
        _ = SyncCatalogAsync(useStateDbOnly: !fullScanDue);
    }

    private void ScheduleCatalogSync()
    {
        if (Volatile.Read(ref _exitStarted) != 0 || _catalogDebounceTimer is null) return;
        _catalogDebounceTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    private void ScheduleUsageRefresh()
    {
        if (Volatile.Read(ref _exitStarted) != 0 || _usageDebounceTimer is null) return;
        _usageDebounceTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    private void NotifyUsageHistoryChanged()
    {
        if (Volatile.Read(ref _exitStarted) != 0) return;
        _ = Dispatcher.InvokeAsync(() => _bars?.NotifySnapshotChanged());
    }

    private async void CatalogDebounceTick(object? state) => await SyncCatalogAsync(useStateDbOnly: true);

    private async void UsageDebounceTick(object? state) => await RefreshNow();

    private async Task SyncCatalogAsync(bool useStateDbOnly)
    {
        if (Volatile.Read(ref _exitStarted) != 0 || _usageProvider is null || _history is null || _bars is null) return;
        if (Interlocked.Exchange(ref _catalogSyncing, 1) != 0)
        {
            Interlocked.Exchange(ref _catalogSyncPending, 1);
            return;
        }
        try
        {
            var catalog = await _usageProvider.RefreshCatalogAsync(useStateDbOnly);
            if (Volatile.Read(ref _exitStarted) != 0) return;
            var syncedAt = DateTimeOffset.UtcNow;
            await _history.UpsertCatalogAsync(catalog, syncedAt);
            var stored = await _history.GetCatalogAsync();
            if (Volatile.Read(ref _exitStarted) != 0) return;
            if (!useStateDbOnly) _lastFullCatalogSync = syncedAt;
            await Dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _exitStarted) != 0 || _bars is null) return;
                var current = _bars.Snapshot;
                _bars.Update(new UsageSnapshot
                {
                    UpdatedAt = current.UpdatedAt,
                    LastSuccessfulUpdate = current.LastSuccessfulUpdate,
                    Quotas = current.Quotas,
                    Threads = stored.Threads,
                    ErrorMessage = current.ErrorMessage,
                    WarningMessage = current.WarningMessage,
                    CatalogSyncedAt = stored.Status.SyncedAt,
                    CatalogSyncMessage = null
                });
            });
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _exitStarted) != 0) { }
        catch (Exception ex)
        {
            AppLogger.Error("CodexUsageProvider", "CatalogReconcile", ex,
                $"useStateDbOnly={useStateDbOnly}; showingSavedCatalog=true");
            if (Volatile.Read(ref _exitStarted) == 0)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_bars is null) return;
                    var current = _bars.Snapshot;
                    current.CatalogSyncMessage = "Chat list sync failed; showing saved local catalog.";
                    _bars.Update(current);
                });
        }
        finally
        {
            Volatile.Write(ref _catalogSyncing, 0);
            if (Interlocked.Exchange(ref _catalogSyncPending, 0) != 0) ScheduleCatalogSync();
        }
    }

    private void OnThreadDeleted(string threadId)
    {
        if (_history is null) return;
        _ = DeleteThreadMetadataAsync(threadId);
    }

    private async Task DeleteThreadMetadataAsync(string threadId)
    {
        try
        {
            if (_history is null) return;
            await _history.DeleteCatalogEntryAsync(threadId);
            ScheduleCatalogSync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("UsageHistoryStore", "DeleteChatMetadata", ex,
                $"threadId={threadId}; usageHistoryRetained=true");
        }
    }

    private void ShowDashboard()
    {
        if (_bars is null || _history is null) return;
        if (_dashboardWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            if (!existing.IsVisible) existing.Show();
            existing.Activate();
            existing.Focus();
            return;
        }

        var window = new DashboardWindow(_bars, _history, RefreshNow, ShowDailyTokenUsage, _bars.DashboardMonitor);
        _dashboardWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dashboardWindow, window)) _dashboardWindow = null;
        };
        window.Show();
        window.Activate();
    }

    private void ShowDailyTokenUsage()
    {
        if (_bars is null || _history is null) return;
        if (_dailyTokenUsageWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            if (!existing.IsVisible) existing.Show();
            existing.Activate();
            existing.Focus();
            return;
        }

        var window = new DailyTokenUsageWindow(_bars, _history);
        _dailyTokenUsageWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dailyTokenUsageWindow, window)) _dailyTokenUsageWindow = null;
        };
        window.Show();
        window.Activate();
    }

    private void ShowDashboardFromTray() => _bars?.ShowDashboardFromTray();

    private void ShowSettings()
    {
        if (_bars is null) return;
        var window = new SettingsWindow(_bars.Settings);
        window.Show();
        window.Activate();
    }

    private void ShowLogs()
    {
        var window = new LogViewerWindow();
        window.Show();
        window.Activate();
    }

    private void ToggleBars() => _bars?.ToggleVisibility();

    private static string FormatTraySummary(UsageSnapshot snapshot, ThreadUsage? selected, QuotaValueMode quotaMode)
    {
        var fiveHour = snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 300);
        var weekly = snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 10080);
        var parts = new List<string>();
        parts.Add(selected?.TotalTokens is long tokens ? $"S {FormatTokens(tokens)}" : "S —");
        parts.Add(selected?.ContextUsedPercent is double context ? $"C {context:0}%" : "C —");
        if (fiveHour is not null) parts.Add($"5H {FormatTrayQuotaPercent(fiveHour.UsedPercent, quotaMode)}");
        if (weekly is not null) parts.Add($"W {FormatTrayQuotaPercent(weekly.UsedPercent, quotaMode)}");
        return string.Join("  ", parts);
    }

    private static string FormatTrayQuotaPercent(double? usedPercent, QuotaValueMode mode)
    {
        var value = QuotaPercentFormatter.Format(usedPercent, mode);
        return mode == QuotaValueMode.Remaining && value != "—" ? $"{value} left" : value;
    }

    private static string FormatTokens(long value) => value switch
    { >= 1_000_000 => $"{value / 1_000_000d:0.#}M", >= 1_000 => $"{value / 1_000d:0.#}k", _ => value.ToString("N0") };
    private static string FormatPercent(double? value) => value is double percent ? $"{percent:0}%" : "—";

    private async void ExitApp()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0) return;
        _refreshTimer?.Dispose();
        _catalogTimer?.Dispose();
        _catalogDebounceTimer?.Dispose();
        _usageDebounceTimer?.Dispose();
        _settingsTimer?.Stop();
        if (_rolloutSync is not null)
        {
            try { await _rolloutSync.DisposeAsync(); }
            catch (Exception ex) { AppLogger.Error("RolloutUsageSync", "DisposeDuringExit", ex); }
        }
        var providerDisposal = _usageProvider?.DisposeAsync().AsTask();
        if (_settingsService is not null && _bars is not null)
        {
            try { await _settingsService.SaveAsync(_bars.Settings); }
            catch (Exception ex) { AppLogger.Error("SettingsService", "SaveDuringExit", ex); }
        }
        if (providerDisposal is not null)
        {
            try { await providerDisposal; }
            catch (Exception ex) { AppLogger.Error("CodexUsageProvider", "DisposeDuringExit", ex); }
        }
        _tray?.Dispose();
        _bars?.Dispose();
        NotificationService.Shutdown();
        Shutdown();
    }
}
