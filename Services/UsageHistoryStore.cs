using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CodexStatusBar.Models;

namespace CodexStatusBar.Services;

public sealed class UsageHistoryStore
{
    private readonly string _directory;
    private string DbPath => Path.Combine(_directory, "usage.db");
    private string BackupDirectory => Path.Combine(_directory, "backups");
    private const int RetainedBackups = 28;
    private static readonly TimeSpan BackupInterval = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _backupGate = new(1, 1);

    public UsageHistoryStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStatusBar")) { }

    internal UsageHistoryStore(string dataDirectory) => _directory = Path.GetFullPath(dataDirectory);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(BackupDirectory);
        await EnsureDatabaseIsValidAsync();
        if (File.Exists(DbPath))
            await TryCreateBackupAsync(force: true, "before-schema-initialization");

        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS thread_samples (
              thread_id TEXT NOT NULL, captured_at INTEGER NOT NULL, thread_updated_at INTEGER NOT NULL,
              total_tokens INTEGER NULL, input_tokens INTEGER NULL, total_input_tokens INTEGER NULL,
              total_output_tokens INTEGER NULL, model TEXT NULL, context_percent REAL NULL,
              five_hour_percent REAL NULL, weekly_percent REAL NULL,
              PRIMARY KEY(thread_id, captured_at));
            CREATE INDEX IF NOT EXISTS idx_thread_samples_time ON thread_samples(captured_at);
            CREATE TABLE IF NOT EXISTS quota_samples (
              bucket_id TEXT NOT NULL, label TEXT NOT NULL, captured_at INTEGER NOT NULL,
              used_percent REAL NULL, window_minutes INTEGER NULL, resets_at INTEGER NULL,
              PRIMARY KEY(bucket_id, captured_at));
            CREATE INDEX IF NOT EXISTS idx_quota_samples_time ON quota_samples(captured_at);
            CREATE TABLE IF NOT EXISTS thread_catalog (
              thread_id TEXT PRIMARY KEY, label TEXT NOT NULL, source TEXT NOT NULL, workspace TEXT NULL,
              model TEXT NULL, updated_at INTEGER NOT NULL, is_archived INTEGER NOT NULL,
              last_seen_at INTEGER NOT NULL, deleted_at INTEGER NULL);
            CREATE INDEX IF NOT EXISTS idx_thread_catalog_updated ON thread_catalog(updated_at DESC);
            CREATE TABLE IF NOT EXISTS catalog_sync_state (
              singleton INTEGER PRIMARY KEY CHECK(singleton = 1), synced_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS rollout_file_cursors (
              source_path TEXT PRIMARY KEY, thread_id TEXT NOT NULL, byte_offset INTEGER NOT NULL,
              file_length INTEGER NOT NULL, last_write_ticks INTEGER NOT NULL, creation_ticks INTEGER NOT NULL,
              current_model TEXT NULL);
            CREATE TABLE IF NOT EXISTS rollout_usage_events (
              source_path TEXT NOT NULL, byte_offset INTEGER NOT NULL, thread_id TEXT NOT NULL,
              captured_at INTEGER NOT NULL, input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
              model TEXT NULL,
              PRIMARY KEY(source_path, byte_offset));
            CREATE INDEX IF NOT EXISTS idx_rollout_usage_thread_time
              ON rollout_usage_events(thread_id, captured_at);
            """;
        await command.ExecuteNonQueryAsync();
        var rolloutModelWasMissing = !await HasColumnAsync(connection, "rollout_usage_events", "model");
        await EnsureColumnAsync(connection, "thread_samples", "total_input_tokens", "INTEGER NULL");
        await EnsureColumnAsync(connection, "thread_samples", "total_output_tokens", "INTEGER NULL");
        await EnsureColumnAsync(connection, "thread_samples", "model", "TEXT NULL");
        await EnsureColumnAsync(connection, "rollout_usage_events", "model", "TEXT NULL");
        await EnsureColumnAsync(connection, "rollout_file_cursors", "current_model", "TEXT NULL");
        if (rolloutModelWasMissing)
        {
            // Re-read existing local rollout records once so model metadata can be attached to
            // historical token_count rows without changing their token counters or timestamps.
            var resetRolloutCursors = connection.CreateCommand();
            resetRolloutCursors.CommandText = "UPDATE rollout_file_cursors SET byte_offset=0, current_model=NULL;";
            await resetRolloutCursors.ExecuteNonQueryAsync();
        }
        var seedCatalog = connection.CreateCommand();
        seedCatalog.CommandText = """
            INSERT OR IGNORE INTO thread_catalog
              (thread_id, label, source, workspace, model, updated_at, is_archived, last_seen_at, deleted_at)
            SELECT sample.thread_id, 'Codex chat · ' || substr(sample.thread_id, 1, 8), 'cached', NULL,
                   sample.model, sample.thread_updated_at, 1, sample.captured_at, NULL
            FROM thread_samples sample
            JOIN (SELECT thread_id, MAX(captured_at) captured_at FROM thread_samples GROUP BY thread_id) latest
              ON latest.thread_id = sample.thread_id AND latest.captured_at = sample.captured_at;
            """;
        await seedCatalog.ExecuteNonQueryAsync();
        var cleanupRolloutHistory = connection.CreateCommand();
        cleanupRolloutHistory.CommandText = "DELETE FROM rollout_usage_events WHERE captured_at < $cutoff;";
        cleanupRolloutHistory.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds());
        await cleanupRolloutHistory.ExecuteNonQueryAsync();
    }

    public async Task UpsertCatalogAsync(IEnumerable<ThreadUsage> threads, DateTimeOffset syncedAt)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var now = syncedAt.ToUnixTimeMilliseconds();
        foreach (var thread in threads)
        {
            if (string.IsNullOrWhiteSpace(thread.Id)) continue;
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO thread_catalog
                  (thread_id, label, source, workspace, model, updated_at, is_archived, last_seen_at, deleted_at)
                VALUES ($id, $label, $source, $workspace, $model, $updated, $archived, $seen, NULL)
                ON CONFLICT(thread_id) DO UPDATE SET
                  label=excluded.label, source=excluded.source, workspace=excluded.workspace,
                  model=COALESCE(excluded.model, thread_catalog.model), updated_at=excluded.updated_at,
                  is_archived=excluded.is_archived, last_seen_at=excluded.last_seen_at
                WHERE thread_catalog.deleted_at IS NULL;
                """;
            command.Parameters.AddWithValue("$id", thread.Id);
            command.Parameters.AddWithValue("$label", string.IsNullOrWhiteSpace(thread.Label) ? $"Codex chat · {thread.Id[..Math.Min(8, thread.Id.Length)]}" : thread.Label);
            command.Parameters.AddWithValue("$source", thread.Source ?? "unknown");
            command.Parameters.AddWithValue("$workspace", (object?)thread.Workspace ?? DBNull.Value);
            command.Parameters.AddWithValue("$model", (object?)thread.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", thread.UpdatedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$archived", thread.IsArchived ? 1 : 0);
            command.Parameters.AddWithValue("$seen", now);
            await command.ExecuteNonQueryAsync();
        }
        var state = connection.CreateCommand();
        state.Transaction = (SqliteTransaction)transaction;
        state.CommandText = "INSERT INTO catalog_sync_state(singleton, synced_at) VALUES (1, $at) ON CONFLICT(singleton) DO UPDATE SET synced_at=excluded.synced_at;";
        state.Parameters.AddWithValue("$at", now);
        await state.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task DeleteCatalogEntryAsync(string threadId)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        var deletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.CommandText = """
            INSERT INTO thread_catalog
              (thread_id, label, source, workspace, model, updated_at, is_archived, last_seen_at, deleted_at)
            VALUES ($id, '', 'deleted', NULL, NULL, $at, 1, $at, $at)
            ON CONFLICT(thread_id) DO UPDATE SET label='', source='deleted', workspace=NULL, model=NULL,
              updated_at=excluded.updated_at, is_archived=1, last_seen_at=excluded.last_seen_at, deleted_at=excluded.deleted_at;
            """;
        command.Parameters.AddWithValue("$id", threadId);
        command.Parameters.AddWithValue("$at", deletedAt);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<(IReadOnlyList<ThreadUsage> Threads, CatalogStatus Status)> GetCatalogAsync()
    {
        await using var connection = await OpenAsync();
        var threads = await GetCatalogAsync(connection);
        var statusCommand = connection.CreateCommand();
        statusCommand.CommandText = "SELECT (SELECT COUNT(*) FROM thread_catalog WHERE deleted_at IS NULL), synced_at FROM catalog_sync_state WHERE singleton=1;";
        await using var reader = await statusCommand.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (threads, new CatalogStatus(threads.Count, null));
        var count = reader.GetInt32(0);
        DateTimeOffset? syncedAt = reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
        return (threads, new CatalogStatus(count, syncedAt));
    }

    public async Task<RolloutFileCursor?> GetRolloutFileCursorAsync(string path)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT thread_id, byte_offset, file_length, last_write_ticks, creation_ticks, current_model FROM rollout_file_cursors WHERE source_path=$path;";
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new RolloutFileCursor(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5))
            : null;
    }

    public async Task<IReadOnlyDictionary<string, RolloutFileCursor>> GetRolloutFileCursorsAsync()
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT source_path, thread_id, byte_offset, file_length, last_write_ticks, creation_ticks, current_model FROM rollout_file_cursors;";
        var result = new Dictionary<string, RolloutFileCursor>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = new RolloutFileCursor(reader.GetString(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6));
        return result;
    }

    public async Task SaveRolloutFileProgressAsync(string path, RolloutFileCursor cursor,
        IReadOnlyList<RolloutTokenEvent> events, bool resetFile)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (resetFile)
        {
            var removeEvents = connection.CreateCommand();
            removeEvents.Transaction = (SqliteTransaction)transaction;
            removeEvents.CommandText = "DELETE FROM rollout_usage_events WHERE source_path=$path;";
            removeEvents.Parameters.AddWithValue("$path", path);
            await removeEvents.ExecuteNonQueryAsync();
        }
        foreach (var item in events)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO rollout_usage_events
                  (source_path, byte_offset, thread_id, captured_at, input_tokens, output_tokens, model)
                VALUES ($path, $offset, $id, $at, $input, $output, $model)
                ON CONFLICT(source_path, byte_offset) DO UPDATE SET
                  model=COALESCE(excluded.model, rollout_usage_events.model);
                """;
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$offset", item.ByteOffset);
            insert.Parameters.AddWithValue("$id", item.ThreadId);
            insert.Parameters.AddWithValue("$at", item.CapturedAt.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$input", item.InputTokens);
            insert.Parameters.AddWithValue("$output", item.OutputTokens);
            insert.Parameters.AddWithValue("$model", (object?)item.Model ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
        var save = connection.CreateCommand();
        save.Transaction = (SqliteTransaction)transaction;
        save.CommandText = """
            INSERT INTO rollout_file_cursors(source_path, thread_id, byte_offset, file_length, last_write_ticks, creation_ticks, current_model)
            VALUES ($path, $id, $offset, $length, $write, $creation, $model)
            ON CONFLICT(source_path) DO UPDATE SET thread_id=excluded.thread_id, byte_offset=excluded.byte_offset,
              file_length=excluded.file_length, last_write_ticks=excluded.last_write_ticks, creation_ticks=excluded.creation_ticks,
              current_model=excluded.current_model;
            """;
        save.Parameters.AddWithValue("$path", path);
        save.Parameters.AddWithValue("$id", cursor.ThreadId);
        save.Parameters.AddWithValue("$offset", cursor.ByteOffset);
        save.Parameters.AddWithValue("$length", cursor.FileLength);
        save.Parameters.AddWithValue("$write", cursor.LastWriteUtcTicks);
        save.Parameters.AddWithValue("$creation", cursor.CreationUtcTicks);
        save.Parameters.AddWithValue("$model", (object?)cursor.CurrentModel ?? DBNull.Value);
        await save.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<RolloutTokenEvent>> GetRolloutUsageEventsAsync(DateTimeOffset from, DateTimeOffset to)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT thread_id, source_path, byte_offset, captured_at, input_tokens, output_tokens, model
            FROM rollout_usage_events WHERE captured_at >= $from AND captured_at <= $to
            ORDER BY thread_id, captured_at, source_path, byte_offset;
            """;
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        var result = new List<RolloutTokenEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new RolloutTokenEvent(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetInt64(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }

    public async Task<UsageSnapshot> RecordAndEnrichAsync(UsageSnapshot snapshot)
    {
        try
        {
            return await RecordAndEnrichCoreAsync(snapshot);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            AppLogger.Error("UsageHistoryStore", "RuntimeCorruptionDetected", ex,
                $"database={DbPath}; sqliteError={ex.SqliteErrorCode}; automaticRecovery=started");
            await InitializeAsync();
            return await RecordAndEnrichCoreAsync(snapshot);
        }
    }

    private async Task<UsageSnapshot> RecordAndEnrichCoreAsync(UsageSnapshot snapshot)
    {
        if (snapshot.ErrorMessage is null)
            await UpsertCatalogAsync(snapshot.Threads, snapshot.UpdatedAt);

        await using var connection = await OpenAsync();
        if (snapshot.ErrorMessage is null)
        {
            var captured = snapshot.UpdatedAt.ToUnixTimeMilliseconds();
            var fiveHour = snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 300)?.UsedPercent;
            var weekly = snapshot.Quotas.FirstOrDefault(q => q.WindowDurationMinutes == 10080)?.UsedPercent;
            await using var transaction = connection.BeginTransaction();
            foreach (var thread in snapshot.Threads.Where(t => t.TotalTokens.HasValue || t.ContextUsedPercent.HasValue ||
                t.TotalInputTokens.HasValue || t.TotalOutputTokens.HasValue))
            {
                thread.UsageCapturedAt ??= snapshot.UpdatedAt;
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO thread_samples
                    (thread_id, captured_at, thread_updated_at, total_tokens, input_tokens, total_input_tokens, total_output_tokens, model, context_percent, five_hour_percent, weekly_percent)
                    VALUES ($id, $captured, $updated, $total, $input, $totalInput, $totalOutput, $model, $context, $five, $weekly);
                    """;
                command.Parameters.AddWithValue("$id", thread.Id);
                command.Parameters.AddWithValue("$captured", captured);
                command.Parameters.AddWithValue("$updated", thread.UpdatedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$total", (object?)thread.TotalTokens ?? DBNull.Value);
                command.Parameters.AddWithValue("$input", (object?)thread.LastInputTokens ?? DBNull.Value);
                command.Parameters.AddWithValue("$totalInput", (object?)thread.TotalInputTokens ?? DBNull.Value);
                command.Parameters.AddWithValue("$totalOutput", (object?)thread.TotalOutputTokens ?? DBNull.Value);
                command.Parameters.AddWithValue("$model", (object?)thread.Model ?? DBNull.Value);
                command.Parameters.AddWithValue("$context", (object?)thread.ContextUsedPercent ?? DBNull.Value);
                command.Parameters.AddWithValue("$five", (object?)fiveHour ?? DBNull.Value);
                command.Parameters.AddWithValue("$weekly", (object?)weekly ?? DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }
            foreach (var quota in snapshot.Quotas)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO quota_samples
                    (bucket_id, label, captured_at, used_percent, window_minutes, resets_at)
                    VALUES ($id, $label, $captured, $used, $duration, $reset);
                    """;
                command.Parameters.AddWithValue("$id", quota.Id);
                command.Parameters.AddWithValue("$label", quota.Label);
                command.Parameters.AddWithValue("$captured", captured);
                command.Parameters.AddWithValue("$used", (object?)quota.UsedPercent ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration", (object?)quota.WindowDurationMinutes ?? DBNull.Value);
                command.Parameters.AddWithValue("$reset", (object?)(quota.ResetsAt?.ToUnixTimeSeconds()) ?? DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }
            var cutoff = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
            var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM thread_samples WHERE captured_at < $cutoff; DELETE FROM quota_samples WHERE captured_at < $cutoff;";
            cleanup.Parameters.AddWithValue("$cutoff", cutoff);
            await cleanup.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            await TryCreateBackupAsync(force: false, "periodic-after-usage-write");
        }

        var threads = new List<ThreadUsage>();
        if (snapshot.ErrorMessage is not null && snapshot.Threads.Count == 0)
            threads.AddRange(await GetCatalogAsync(connection));
        foreach (var thread in snapshot.Threads)
        {
            if (thread.TotalTokens is not null && thread.ContextUsedPercent is not null)
            {
                threads.Add(thread);
                continue;
            }
            var cached = await GetLatestThreadAsync(connection, thread.Id);
            if (cached is not null)
            {
                cached.Label = thread.Label;
                cached.Source = thread.Source;
                cached.Workspace = thread.Workspace;
                cached.Model = thread.Model ?? cached.Model;
                cached.UpdatedAt = thread.UpdatedAt;
                cached.IsArchived = thread.IsArchived;
                if (thread.UsageCapturedAt.HasValue) cached.UsageCapturedAt = thread.UsageCapturedAt;
                if (thread.TotalTokens.HasValue) cached.TotalTokens = thread.TotalTokens;
                if (thread.LastInputTokens.HasValue) cached.LastInputTokens = thread.LastInputTokens;
                if (thread.TotalInputTokens.HasValue) cached.TotalInputTokens = thread.TotalInputTokens;
                if (thread.TotalOutputTokens.HasValue) cached.TotalOutputTokens = thread.TotalOutputTokens;
                if (thread.ContextUsedPercent.HasValue) cached.ContextUsedPercent = thread.ContextUsedPercent;
                if (thread.ContextWindow.HasValue) cached.ContextWindow = thread.ContextWindow;
                threads.Add(cached);
            }
            else threads.Add(thread);
        }
        var visibleCatalogIds = new HashSet<string>(StringComparer.Ordinal);
        var visibleCatalogCommand = connection.CreateCommand();
        visibleCatalogCommand.CommandText = "SELECT thread_id FROM thread_catalog WHERE deleted_at IS NULL;";
        await using (var catalogReader = await visibleCatalogCommand.ExecuteReaderAsync())
        {
            while (await catalogReader.ReadAsync()) visibleCatalogIds.Add(catalogReader.GetString(0));
        }
        threads.RemoveAll(thread => !visibleCatalogIds.Contains(thread.Id));
        if (snapshot.ErrorMessage is not null && snapshot.Quotas.Count == 0)
            snapshot.Quotas = await GetLatestQuotasAsync(connection);
        if (snapshot.LastSuccessfulUpdate is null)
            snapshot.LastSuccessfulUpdate = await GetLastCaptureAsync(connection);
        var catalogStatusCommand = connection.CreateCommand();
        catalogStatusCommand.CommandText = "SELECT synced_at FROM catalog_sync_state WHERE singleton=1;";
        var syncedAt = await catalogStatusCommand.ExecuteScalarAsync();
        snapshot.CatalogSyncedAt = syncedAt is long syncedMillis
            ? DateTimeOffset.FromUnixTimeMilliseconds(syncedMillis) : null;
        snapshot.Threads = threads;
        return snapshot;
    }

    private static async Task<IReadOnlyList<ThreadUsage>> GetCatalogAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            WITH latest AS (
              SELECT *, ROW_NUMBER() OVER (PARTITION BY thread_id ORDER BY captured_at DESC) AS row_number
              FROM thread_samples
            )
            SELECT catalog.thread_id, catalog.label, catalog.source, catalog.workspace, catalog.model,
                   catalog.updated_at, catalog.is_archived, sample.captured_at, sample.total_tokens,
                   sample.input_tokens, sample.total_input_tokens, sample.total_output_tokens,
                   sample.context_percent
            FROM thread_catalog catalog
            LEFT JOIN latest sample ON sample.thread_id=catalog.thread_id AND sample.row_number=1
            WHERE catalog.deleted_at IS NULL
            ORDER BY catalog.updated_at DESC;
            """;
        var threads = new List<ThreadUsage>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            threads.Add(new ThreadUsage
            {
                Id = reader.GetString(0), Label = reader.GetString(1), Source = reader.GetString(2),
                Workspace = reader.IsDBNull(3) ? null : reader.GetString(3),
                Model = reader.IsDBNull(4) ? null : reader.GetString(4),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                IsArchived = reader.GetInt32(6) != 0,
                UsageCapturedAt = reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                TotalTokens = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                LastInputTokens = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                TotalInputTokens = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                TotalOutputTokens = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                ContextUsedPercent = reader.IsDBNull(12) ? null : reader.GetDouble(12)
            });
        }
        return threads;
    }

    public async Task<IReadOnlyList<HistoryPoint>> GetHistoryAsync(string threadId, int days = 30)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at, total_tokens, context_percent, five_hour_percent, weekly_percent
            FROM thread_samples WHERE thread_id = $id AND captured_at >= $cutoff ORDER BY captured_at;
            """;
        command.Parameters.AddWithValue("$id", threadId);
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds());
        var points = new List<HistoryPoint>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            points.Add(new HistoryPoint(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }
        return points;
    }

    public async Task<DailyTokenUsageSummary> GetDailyTokenUsageAsync(int days = 30)
    {
        if (days is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(days), "History range must be between 1 and 90 days.");

        var today = DateOnly.FromDateTime(DateTime.Now);
        var firstDay = today.AddDays(1 - days);
        var rangeStartUtc = GetLocalDayStartUtc(firstDay);
        var scanStartUtc = rangeStartUtc.AddDays(-1);
        var now = DateTimeOffset.UtcNow;
        var retentionStart = now.AddDays(-90);
        await using var connection = await OpenAsync();

        var catalogCommand = connection.CreateCommand();
        catalogCommand.CommandText = "SELECT (SELECT COUNT(*) FROM thread_catalog WHERE deleted_at IS NULL), (SELECT synced_at FROM catalog_sync_state WHERE singleton=1);";
        await using var catalogReader = await catalogCommand.ExecuteReaderAsync();
        await catalogReader.ReadAsync();
        var catalogChatCount = catalogReader.IsDBNull(0) ? 0 : catalogReader.GetInt32(0);
        DateTimeOffset? catalogSyncedAt = catalogReader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(catalogReader.GetInt64(1));
        await catalogReader.CloseAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            WITH daily_capture AS (
                SELECT thread_id,
                       date(captured_at / 1000.0, 'unixepoch', 'localtime') AS local_day,
                       MAX(captured_at) AS captured_at
                FROM thread_samples
                WHERE captured_at >= $scanStart AND captured_at <= $now
                GROUP BY thread_id, local_day
            ), daily_counters AS (
                SELECT daily.thread_id, daily.local_day, daily.captured_at,
                       sample.total_input_tokens AS input_tokens,
                       sample.total_output_tokens AS output_tokens,
                       sample.model AS model,
                       LAG(daily.captured_at) OVER (PARTITION BY daily.thread_id ORDER BY daily.local_day) AS previous_captured_at,
                       LAG(sample.total_input_tokens) OVER (PARTITION BY daily.thread_id ORDER BY daily.local_day) AS previous_input_tokens,
                       LAG(sample.total_output_tokens) OVER (PARTITION BY daily.thread_id ORDER BY daily.local_day) AS previous_output_tokens
                FROM daily_capture daily
                JOIN thread_samples sample
                  ON sample.thread_id = daily.thread_id AND sample.captured_at = daily.captured_at
            ), valid_deltas AS (
                SELECT local_day, thread_id, model,
                       input_tokens - previous_input_tokens AS input_delta,
                       output_tokens - previous_output_tokens AS output_delta
                FROM daily_counters
                WHERE local_day >= $firstDay
                  AND previous_captured_at IS NOT NULL
                  AND captured_at - previous_captured_at <= $maxGap
                  AND input_tokens IS NOT NULL AND previous_input_tokens IS NOT NULL
                  AND output_tokens IS NOT NULL AND previous_output_tokens IS NOT NULL
                  AND input_tokens >= previous_input_tokens
                  AND output_tokens >= previous_output_tokens
            )
            SELECT local_day, thread_id, model, input_delta, output_delta
            FROM valid_deltas ORDER BY local_day, thread_id;
            """;
        command.Parameters.AddWithValue("$scanStart", scanStartUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$firstDay", firstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$maxGap", TimeSpan.FromHours(24).TotalMilliseconds);

        var snapshotDeltas = new Dictionary<(string ThreadId, DateOnly Date, string Model), (long Input, long Output)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (!DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)) continue;
                var id = reader.GetString(1);
                var model = reader.IsDBNull(2) ? "Unknown model" : NormalizeModel(reader.GetString(2));
                snapshotDeltas[(id, date, model)] = (reader.GetInt64(3), reader.GetInt64(4));
            }
        }

        var eventsCommand = connection.CreateCommand();
        eventsCommand.CommandText = """
            SELECT thread_id, captured_at, input_tokens, output_tokens, model
            FROM rollout_usage_events WHERE captured_at >= $cutoff AND captured_at <= $now
            ORDER BY thread_id, captured_at, source_path, byte_offset;
            """;
        eventsCommand.Parameters.AddWithValue("$cutoff", retentionStart.ToUnixTimeMilliseconds());
        eventsCommand.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        var rolloutDeltas = new Dictionary<(string ThreadId, DateOnly Date, string Model), (long Input, long Output)>();
        var rolloutBackedThreads = new HashSet<string>(StringComparer.Ordinal);
        var latestRolloutAt = (DateTimeOffset?)null;
        string? currentThreadId = null;
        long? previousInput = null, previousOutput = null;
        await using (var reader = await eventsCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var threadId = reader.GetString(0);
                var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
                var input = reader.GetInt64(2);
                var output = reader.GetInt64(3);
                var model = reader.IsDBNull(4) ? "Unknown model" : NormalizeModel(reader.GetString(4));
                latestRolloutAt = latestRolloutAt is null || capturedAt > latestRolloutAt ? capturedAt : latestRolloutAt;
                if (!string.Equals(currentThreadId, threadId, StringComparison.Ordinal))
                {
                    currentThreadId = threadId;
                    previousInput = null;
                    previousOutput = null;
                }
                if (previousInput is long oldInput && previousOutput is long oldOutput)
                {
                    if (input >= oldInput && output >= oldOutput)
                    {
                        var localDate = DateOnly.FromDateTime(capturedAt.ToLocalTime().DateTime);
                        if (localDate >= firstDay && localDate <= today)
                        {
                            var key = (threadId, localDate, model);
                            var old = rolloutDeltas.GetValueOrDefault(key);
                            rolloutDeltas[key] = (old.Input + input - oldInput, old.Output + output - oldOutput);
                            rolloutBackedThreads.Add(threadId);
                        }
                    }
                }
                previousInput = input;
                previousOutput = output;
            }
        }

        var combined = new Dictionary<DateOnly, (long Input, long Output, HashSet<string> Chats)>();
        var modelCombined = new Dictionary<(DateOnly Date, string Model),
            (long Input, long Output, HashSet<string> Chats)>();
        void AddDelta(string threadId, DateOnly date, string model, long input, long output)
        {
            if (!combined.TryGetValue(date, out var entry))
                entry = (0, 0, new HashSet<string>(StringComparer.Ordinal));
            entry.Input += input;
            entry.Output += output;
            entry.Chats.Add(threadId);
            combined[date] = entry;

            var modelKey = (date, model);
            if (!modelCombined.TryGetValue(modelKey, out var modelEntry))
                modelEntry = (0, 0, new HashSet<string>(StringComparer.Ordinal));
            modelEntry.Input += input;
            modelEntry.Output += output;
            modelEntry.Chats.Add(threadId);
            modelCombined[modelKey] = modelEntry;
        }
        foreach (var (key, value) in rolloutDeltas) AddDelta(key.ThreadId, key.Date, key.Model, value.Input, value.Output);
        foreach (var (key, value) in snapshotDeltas)
        {
            if (rolloutBackedThreads.Contains(key.ThreadId)) continue;
            AddDelta(key.ThreadId, key.Date, key.Model, value.Input, value.Output);
        }

        var historyCountCommand = connection.CreateCommand();
        historyCountCommand.CommandText = """
            SELECT COUNT(DISTINCT thread_id), MAX(captured_at) FROM (
              SELECT thread_id, captured_at FROM thread_samples WHERE captured_at >= $cutoff AND captured_at <= $now
              UNION ALL
              SELECT thread_id, captured_at FROM rollout_usage_events WHERE captured_at >= $cutoff AND captured_at <= $now
            );
            """;
        historyCountCommand.Parameters.AddWithValue("$cutoff", retentionStart.ToUnixTimeMilliseconds());
        historyCountCommand.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        int chatsWithHistory;
        DateTimeOffset? lastSampleAt;
        await using (var reader = await historyCountCommand.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            chatsWithHistory = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var sampleMillis = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            lastSampleAt = sampleMillis.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(sampleMillis.Value) : latestRolloutAt;
            if (latestRolloutAt is not null && (lastSampleAt is null || latestRolloutAt > lastSampleAt)) lastSampleAt = latestRolloutAt;
        }

        var result = Enumerable.Range(0, days)
            .Select(offset => firstDay.AddDays(offset))
            .Select(date => combined.TryGetValue(date, out var measured)
                ? new DailyTokenUsagePoint(date, measured.Input, measured.Output, measured.Chats.Count)
                : new DailyTokenUsagePoint(date, null, null, 0))
            .ToArray();
        var trackedChatCount = combined.Values.SelectMany(value => value.Chats).Distinct(StringComparer.Ordinal).Count();
        var modelDays = modelCombined.Select(item => new DailyModelTokenUsage(item.Key.Date, item.Key.Model,
                item.Value.Input, item.Value.Output, item.Value.Chats.Count))
            .OrderBy(item => item.Date).ThenBy(item => item.Model, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var modelTotals = modelCombined.GroupBy(item => item.Key.Model, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new DailyModelTokenTotal(group.Key, group.Sum(item => item.Value.Input),
                group.Sum(item => item.Value.Output), group.SelectMany(item => item.Value.Chats).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(item => item.InputTokens + item.OutputTokens)
            .ThenBy(item => item.Model, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new DailyTokenUsageSummary(result, trackedChatCount, lastSampleAt, catalogChatCount,
            chatsWithHistory, catalogSyncedAt) { ModelDays = modelDays, ModelTotals = modelTotals };
    }

    private static string NormalizeModel(string model) => string.IsNullOrWhiteSpace(model) ? "Unknown model" : model.Trim();

    private static DateTimeOffset GetLocalDayStartUtc(DateOnly date)
    {
        var localMidnight = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localMidnight, TimeZoneInfo.Local));
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<HistoryPoint>>> GetHistoriesAsync(
        IEnumerable<string> threadIds, int days = 30)
    {
        var ids = threadIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var histories = ids.ToDictionary(id => id, _ => (IReadOnlyList<HistoryPoint>)Array.Empty<HistoryPoint>(), StringComparer.Ordinal);
        if (ids.Length == 0) return histories;

        await using var connection = await OpenAsync();
        const int batchSize = 900;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            var command = connection.CreateCommand();
            var parameters = new List<string>(batch.Length);
            command.Parameters.AddWithValue("$cutoff", cutoff);
            for (var index = 0; index < batch.Length; index++)
            {
                var parameter = $"$id{index}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, batch[index]);
            }
            command.CommandText = $"""
                SELECT thread_id, captured_at, total_tokens, context_percent, five_hour_percent, weekly_percent
                FROM thread_samples WHERE thread_id IN ({string.Join(",", parameters)})
                  AND captured_at >= $cutoff ORDER BY thread_id, captured_at;
                """;
            var batchHistories = batch.ToDictionary(id => id, _ => new List<HistoryPoint>(), StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                batchHistories[id].Add(new HistoryPoint(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5)));
            }
            foreach (var (id, points) in batchHistories)
                histories[id] = points;
        }
        return histories;
    }

    public async Task ExportCsvAsync(string threadId, string path, int days = 90)
    {
        var points = await GetHistoryAsync(threadId, days);
        var lines = new List<string> { "CapturedAt,TotalTokens,ContextPercent,FiveHourPercent,WeeklyPercent" };
        lines.AddRange(points.Select(p => string.Join(',',
            p.CapturedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
            p.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
            p.ContextPercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
            p.FiveHourPercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
            p.WeeklyPercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "")));
        await File.WriteAllLinesAsync(path, lines, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public async Task ExportCsvAsync(IReadOnlyList<string> threadIds, string path, int days = 90)
    {
        var histories = await GetHistoriesAsync(threadIds, days);
        var lines = new List<string> { "ChatId,CapturedAt,TotalTokens,ContextPercent,FiveHourPercent,WeeklyPercent" };
        foreach (var (id, points) in histories.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            lines.AddRange(points.Select(point => string.Join(',',
                Csv(id),
                point.CapturedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
                point.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
                point.ContextPercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                point.FiveHourPercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                point.WeeklyPercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "")));
        }
        await File.WriteAllLinesAsync(path, lines, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public async Task ClearHistoryAsync()
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM thread_samples; DELETE FROM quota_samples;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ThreadUsage?> GetLatestThreadAsync(SqliteConnection connection, string id)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT captured_at, thread_updated_at, total_tokens, input_tokens, context_percent, total_input_tokens, total_output_tokens, model FROM thread_samples WHERE thread_id=$id ORDER BY captured_at DESC LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ThreadUsage
        {
            Id = id,
            UsageCapturedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            TotalTokens = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            LastInputTokens = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            ContextUsedPercent = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            TotalInputTokens = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            TotalOutputTokens = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            Model = reader.IsDBNull(7) ? null : reader.GetString(7)
        };
    }

    private async Task<IReadOnlyList<ThreadUsage>> GetLatestThreadsAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sample.thread_id, sample.captured_at, sample.thread_updated_at, sample.total_tokens, sample.input_tokens,
                   sample.context_percent, sample.total_input_tokens, sample.total_output_tokens, sample.model
            FROM thread_samples sample
            JOIN (SELECT thread_id, MAX(captured_at) captured_at FROM thread_samples GROUP BY thread_id) latest
              ON latest.thread_id = sample.thread_id AND latest.captured_at = sample.captured_at
            ORDER BY sample.thread_updated_at DESC;
            """;
        var threads = new List<ThreadUsage>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            threads.Add(new ThreadUsage
            {
                Id = id,
                Label = $"Codex chat · {id[..Math.Min(8, id.Length)]}",
                Source = "cached",
                UsageCapturedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                TotalTokens = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                LastInputTokens = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                ContextUsedPercent = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                TotalInputTokens = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                TotalOutputTokens = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Model = reader.IsDBNull(8) ? null : reader.GetString(8),
                IsArchived = true
            });
        }
        return threads;
    }

    private async Task<IReadOnlyList<QuotaWindow>> GetLatestQuotasAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT q.bucket_id, q.label, q.used_percent, q.window_minutes, q.resets_at
            FROM quota_samples q JOIN (SELECT bucket_id, MAX(captured_at) captured_at FROM quota_samples GROUP BY bucket_id) latest
            ON latest.bucket_id=q.bucket_id AND latest.captured_at=q.captured_at;
            """;
        var quotas = new List<QuotaWindow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            quotas.Add(new QuotaWindow
            {
                Id = reader.GetString(0), Label = reader.GetString(1),
                UsedPercent = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                WindowDurationMinutes = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                ResetsAt = reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))
            });
        }
        return quotas;
    }

    private async Task<DateTimeOffset?> GetLastCaptureAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(captured_at) FROM (SELECT captured_at FROM quota_samples UNION ALL SELECT captured_at FROM thread_samples);";
        var result = await command.ExecuteScalarAsync();
        return result is long value ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        Directory.CreateDirectory(_directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private async Task EnsureDatabaseIsValidAsync()
    {
        if (!File.Exists(DbPath) || new FileInfo(DbPath).Length == 0) return;
        try
        {
            await ValidateDatabaseAsync(DbPath);
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            AppLogger.Error("UsageHistoryStore", "DatabaseCorruptionDetected", ex,
                $"database={DbPath}; sqliteError={(ex as SqliteException)?.SqliteErrorCode.ToString() ?? "integrity-check"}; automaticRecovery=started");
            var restoredFrom = await RestoreLatestValidBackupAsync();
            AppLogger.Information("UsageHistoryStore", "AutomaticRecoveryCompleted",
                $"database={DbPath}; result={(restoredFrom is null ? "created-empty-database" : "restored-backup")}; backup={restoredFrom ?? "none-available"}");
        }
    }

    private async Task TryCreateBackupAsync(bool force, string reason)
    {
        await _backupGate.WaitAsync();
        try
        {
            var latest = Directory.EnumerateFiles(BackupDirectory, "usage-*.db")
                .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
            if (!force && latest is not null && DateTime.UtcNow - latest.LastWriteTimeUtc < BackupInterval) return;
            await Task.Run(() => CreateBackup(reason));
        }
        catch (Exception ex)
        {
            AppLogger.Error("UsageHistoryStore", "CreateBackup", ex,
                $"database={DbPath}; backupDirectory={BackupDirectory}; reason={reason}; backupFailure=non-fatal");
        }
        finally { _backupGate.Release(); }
    }

    private void CreateBackup(string reason)
    {
        Directory.CreateDirectory(BackupDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(BackupDirectory, $"usage-{stamp}-{Guid.NewGuid():N}.db");
        var temporaryPath = backupPath + ".tmp";
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
            }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false
            }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
                using var checkpoint = destination.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
                checkpoint.ExecuteNonQuery();
            }
            ValidateDatabase(temporaryPath);
            File.Move(temporaryPath, backupPath);
            PruneBackups();
            AppLogger.Information("UsageHistoryStore", "BackupCreated",
                $"database={DbPath}; backup={backupPath}; reason={reason}; bytes={new FileInfo(backupPath).Length}");
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception cleanupException)
            {
                AppLogger.Error("UsageHistoryStore", "CleanupTemporaryBackup", cleanupException,
                    $"temporaryBackup={temporaryPath}; reason={reason}");
            }
            throw;
        }
    }

    private async Task<string?> RestoreLatestValidBackupAsync()
    {
        Directory.CreateDirectory(BackupDirectory);
        var candidates = Directory.EnumerateFiles(BackupDirectory, "usage-*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        string? validBackup = null;
        foreach (var candidate in candidates)
        {
            try
            {
                await ValidateDatabaseAsync(candidate);
                validBackup = candidate;
                break;
            }
            catch (Exception ex) when (IsCorruption(ex))
            {
                AppLogger.Error("UsageHistoryStore", "BackupValidationFailed", ex,
                    $"backup={candidate}; recovery=trying-older-backup");
            }
        }

        if (validBackup is null)
        {
            QuarantineDatabaseFiles();
            return null;
        }

        var restorePath = DbPath + ".restore.tmp";
        try
        {
            File.Copy(validBackup, restorePath, overwrite: true);
            await ValidateDatabaseAsync(restorePath);
            QuarantineDatabaseFiles();
            File.Move(restorePath, DbPath, overwrite: true);
            await ValidateDatabaseAsync(DbPath);
            return validBackup;
        }
        finally
        {
            if (File.Exists(restorePath)) File.Delete(restorePath);
        }
    }

    private void QuarantineDatabaseFiles()
    {
        if (!File.Exists(DbPath) && !File.Exists(DbPath + "-wal") && !File.Exists(DbPath + "-shm")) return;
        var quarantineDirectory = Path.Combine(_directory, "corrupt");
        Directory.CreateDirectory(quarantineDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff'Z'", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = DbPath + suffix;
            if (!File.Exists(source)) continue;
            var destination = Path.Combine(quarantineDirectory, $"usage-corrupt-{stamp}.db{suffix}");
            File.Move(source, destination, overwrite: true);
        }
    }

    private void PruneBackups()
    {
        var backups = Directory.EnumerateFiles(BackupDirectory, "usage-*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        foreach (var oldBackup in backups.Skip(RetainedBackups)) File.Delete(oldBackup);
    }

    private static async Task ValidateDatabaseAsync(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await ValidateDatabaseAsync(connection, path);
    }

    private static void ValidateDatabase(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var rowCount = 0;
        while (reader.Read())
        {
            rowCount++;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite integrity_check failed for '{path}': {reader.GetString(0)}");
        }
        if (rowCount == 0) throw new InvalidDataException($"SQLite integrity_check returned no result for '{path}'.");
    }

    private static async Task ValidateDatabaseAsync(SqliteConnection connection, string path)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        await using var reader = await command.ExecuteReaderAsync();
        var rowCount = 0;
        while (await reader.ReadAsync())
        {
            rowCount++;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite integrity_check failed for '{path}': {reader.GetString(0)}");
        }
        if (rowCount == 0) throw new InvalidDataException($"SQLite integrity_check returned no result for '{path}'.");
    }

    private static bool IsCorruption(Exception ex) => ex is InvalidDataException ||
        ex is SqliteException sqlite && (sqlite.SqliteErrorCode is 11 or 26 || sqlite.SqliteExtendedErrorCode is 11 or 26);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string tableName, string columnName)
    {
        var columns = connection.CreateCommand();
        columns.CommandText = $"PRAGMA table_info({tableName});";
        await using (var reader = await columns.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        if (await HasColumnAsync(connection, tableName, columnName)) return;
        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync();
    }
}
