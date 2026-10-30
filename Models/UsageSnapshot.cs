namespace CodexStatusBar.Models;

public sealed class UsageSnapshot
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ThreadUsage> Threads { get; set; } = Array.Empty<ThreadUsage>();
    public IReadOnlyList<QuotaWindow> Quotas { get; set; } = Array.Empty<QuotaWindow>();
    public string? ErrorMessage { get; set; }
    public string? WarningMessage { get; set; }
    public DateTimeOffset? LastSuccessfulUpdate { get; set; }
    public DateTimeOffset? CatalogSyncedAt { get; set; }
    public string? CatalogSyncMessage { get; set; }
}

public sealed class ThreadUsage
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "Codex chat";
    public string Source { get; set; } = "unknown";
    public string? Workspace { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? UsageCapturedAt { get; set; }
    public long? TotalTokens { get; set; }
    public long? LastInputTokens { get; set; }
    public long? TotalInputTokens { get; set; }
    public long? TotalOutputTokens { get; set; }
    public long? ContextWindow { get; set; }
    public double? ContextUsedPercent { get; set; }
    public bool IsArchived { get; set; }

    public string ListSubtitle => IsArchived
        ? string.IsNullOrWhiteSpace(Workspace) ? "Archived chat" : $"Archived · {Workspace}"
        : Workspace ?? "Codex chat";
}

public sealed class QuotaWindow
{
    public string Id { get; set; } = "codex";
    public string Label { get; set; } = "Quota";
    public double? UsedPercent { get; set; }
    public long? WindowDurationMinutes { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
}

public sealed record HistoryPoint(DateTimeOffset CapturedAt, long? TotalTokens, double? ContextPercent,
    double? FiveHourPercent, double? WeeklyPercent);
