using CodexStatusBar.Infrastructure;
using CodexStatusBar.Models;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CodexStatusBar.Services;

public static class NotificationService
{
    private static readonly HashSet<string> Sent = new(StringComparer.Ordinal);
    private static bool _registered;

    public static void Initialize()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            _registered = false;
            AppLogger.Error("NotificationService", "Initialize", ex);
        }
    }

    public static void Shutdown()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception ex) { AppLogger.Error("NotificationService", "Shutdown", ex); }
        _registered = false;
    }

    public static void Check(UsageSnapshot snapshot, AppSettings settings, TrayIconManager? tray)
    {
        if (tray is null) return;
        if (snapshot.ErrorMessage is not null)
        {
            tray.SetWarningLevel(0);
            return;
        }
        var selected = snapshot.Threads.FirstOrDefault(t => t.Id == settings.PinnedThreadId)
            ?? snapshot.Threads.FirstOrDefault(t => !t.IsArchived);
        var warningLevel = snapshot.WarningMessage is not null ||
            (selected?.ContextUsedPercent is double contextLevel && contextLevel >= settings.ContextWarningPercent) ? 1 : 0;
        foreach (var value in snapshot.Quotas.Where(q => q.UsedPercent.HasValue).Select(q => q.UsedPercent!.Value))
            warningLevel = Math.Max(warningLevel, value >= settings.CriticalWarningPercent ? 3
                : value >= settings.HighWarningPercent ? 2
                : value >= settings.QuotaWarningPercent ? 1 : 0);
        tray.SetWarningLevel(warningLevel);
        if (selected?.ContextUsedPercent is double context && context >= settings.ContextWarningPercent)
            SendOnce($"context:{selected.Id}:{settings.ContextWarningPercent}", "Codex context warning", $"Current context usage: {context:0}%");
        foreach (var quota in snapshot.Quotas.Where(q => q.UsedPercent.HasValue))
        {
            var used = quota.UsedPercent!.Value;
            if (used >= settings.CriticalWarningPercent)
                SendOnce($"critical:{quota.Id}:{quota.ResetsAt}", "Codex quota critical", $"{quota.Label}: {used:0}% used");
            else if (used >= settings.HighWarningPercent)
                SendOnce($"high:{quota.Id}:{quota.ResetsAt}", "Codex quota warning", $"{quota.Label}: {used:0}% used");
            else if (used >= settings.QuotaWarningPercent)
                SendOnce($"quota:{quota.Id}:{quota.ResetsAt}", "Codex quota warning", $"{quota.Label}: {used:0}% used");
        }
        if (Sent.Count > 500) Sent.Clear();

        void SendOnce(string key, string title, string message)
        {
            if (!Sent.Add(key)) return;
            if (_registered)
            {
                try
                {
                    var notification = new AppNotificationBuilder()
                        .AddText(title)
                        .AddText(message)
                        .BuildNotification();
                    AppNotificationManager.Default.Show(notification);
                    return;
                }
                catch (Exception ex) { AppLogger.Error("NotificationService", "ShowToast", ex); }
            }
            tray.NotifyAlert(title, message);
        }
    }
}
