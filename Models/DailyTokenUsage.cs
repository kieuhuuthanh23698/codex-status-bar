namespace CodexStatusBar.Models;

public sealed record DailyTokenUsagePoint(
    DateOnly Date,
    long? InputTokens,
    long? OutputTokens,
    int ChatsWithData);

public sealed record DailyTokenUsageSummary(
    IReadOnlyList<DailyTokenUsagePoint> Days,
    int TrackedChatCount,
    DateTimeOffset? LastSampleAt,
    int CatalogChatCount = 0,
    int ChatsWithHistoryCount = 0,
    DateTimeOffset? CatalogSyncedAt = null)
{
    public IReadOnlyList<DailyModelTokenUsage> ModelDays { get; init; } = Array.Empty<DailyModelTokenUsage>();
    public IReadOnlyList<DailyModelTokenTotal> ModelTotals { get; init; } = Array.Empty<DailyModelTokenTotal>();
}

public sealed record DailyModelTokenUsage(
    DateOnly Date,
    string Model,
    long InputTokens,
    long OutputTokens,
    int ChatsWithData);

public sealed record DailyModelTokenTotal(
    string Model,
    long InputTokens,
    long OutputTokens,
    int ChatsWithData);
