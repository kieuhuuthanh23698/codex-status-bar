namespace CodexStatusBar.Models;

public sealed record RolloutTokenEvent(
    string ThreadId,
    string SourcePath,
    long ByteOffset,
    DateTimeOffset CapturedAt,
    long InputTokens,
    long OutputTokens,
    string? Model = null);

public sealed record RolloutFileCursor(
    string ThreadId,
    long ByteOffset,
    long FileLength,
    long LastWriteUtcTicks,
    long CreationUtcTicks,
    string? CurrentModel = null);

public sealed record CatalogStatus(int ChatCount, DateTimeOffset? SyncedAt);
