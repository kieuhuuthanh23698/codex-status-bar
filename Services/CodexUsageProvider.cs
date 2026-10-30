using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using CodexStatusBar.Models;

namespace CodexStatusBar.Services;

public sealed class CodexUsageProvider : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly ConcurrentDictionary<string, ThreadUsage> _usageByThread = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _modelByThread = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ThreadUsage>> _pendingUsageByThread = new(StringComparer.Ordinal);
    private IReadOnlyList<ThreadUsage>? _cachedThreads;
    private IReadOnlyList<QuotaWindow>? _cachedQuotas;
    private JsonRpcProcessClient? _rpc;
    private bool _initialized;
    private bool? _supportsStateDbOnly;
    private int _disposeStarted;

    public event Action? CatalogChanged;
    public event Action? UsageUpdated;
    public event Action<string>? ThreadDeleted;

    public CodexUsageProvider() => _shutdownToken = _shutdown.Token;

    public Task<UsageSnapshot> ReadSnapshotAsync(string? pinnedThreadId, CancellationToken cancellationToken = default) =>
        ReadSnapshotCoreAsync(pinnedThreadId, refreshCatalog: true, cancellationToken);

    public Task<UsageSnapshot> ReadSelectedThreadSnapshotAsync(string threadId, CancellationToken cancellationToken = default) =>
        ReadSnapshotCoreAsync(threadId, refreshCatalog: false, cancellationToken);

    public async Task<IReadOnlyList<ThreadUsage>> RefreshCatalogAsync(bool useStateDbOnly,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        await _gate.WaitAsync(linkedCancellation.Token);
        try
        {
            var rpc = await GetRpcAsync(linkedCancellation.Token);
            return await ReadThreadsAsync(rpc, useStateDbOnly, linkedCancellation.Token);
        }
        finally { _gate.Release(); }
    }

    private async Task<UsageSnapshot> ReadSnapshotCoreAsync(string? pinnedThreadId, bool refreshCatalog,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        var token = linkedCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            var rpc = await GetRpcAsync(token);

            List<ThreadUsage> threads;
            IReadOnlyList<QuotaWindow> quotas;
            if (!refreshCatalog && _cachedThreads is not null && _cachedQuotas is not null)
            {
                // Chat selection only needs fresh usage for that chat. Reuse the last full catalog
                // and quota snapshot; the timer and Refresh Now still refresh both in full.
                threads = _cachedThreads.Select(CloneThreadMetadata).ToList();
                quotas = _cachedQuotas;
            }
            else
            {
                // The current app-server can stall on thread/list if account rate limits are read first.
                // Fetch chats before quotas on each full refresh to keep the Codex chat list reliable.
                threads = await ReadThreadsAsync(rpc, token);
                quotas = await ReadQuotasAsync(rpc, token);
                _cachedThreads = threads.Select(CloneThreadMetadata).ToArray();
                _cachedQuotas = quotas;
            }
            var preferredId = threads.FirstOrDefault(t => string.Equals(t.Id, pinnedThreadId, StringComparison.Ordinal))?.Id
                ?? threads.FirstOrDefault(t => !t.IsArchived)?.Id
                ?? threads.FirstOrDefault()?.Id;

            // Resume only the selected chat to request its cumulative token usage and current model.
            // excludeTurns keeps conversation content out of the response.
            string? chatUsageWarning = null;
            foreach (var thread in threads.Where(t => t.Id == preferredId && !t.IsArchived))
            {
                token.ThrowIfCancellationRequested();
                TaskCompletionSource<ThreadUsage>? usageWaiter = null;
                var resumed = false;
                try
                {
                    usageWaiter = new TaskCompletionSource<ThreadUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingUsageByThread[thread.Id] = usageWaiter;
                    var resume = await rpc.CallAsync("thread/resume",
                        new { threadId = thread.Id, excludeTurns = true }, TimeSpan.FromSeconds(10), token);
                    resumed = true;
                    var model = GetString(resume, "model") ?? thread.Model;
                    if (!string.IsNullOrWhiteSpace(model)) _modelByThread[thread.Id] = model;
                    try { await usageWaiter.Task.WaitAsync(TimeSpan.FromSeconds(2), token); }
                    catch (TimeoutException) { }

                    if (!usageWaiter.Task.IsCompletedSuccessfully || !HasTokenTotals(usageWaiter.Task.Result))
                    {
                        var localUsage = await ReadLocalThreadUsageAsync(rpc, thread, token);
                        if (localUsage is not null) _usageByThread[thread.Id] = localUsage;
                        else chatUsageWarning = "Codex has not published token usage for this chat yet.";
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var fallbackSucceeded = false;
                    try
                    {
                        var localUsage = await ReadLocalThreadUsageAsync(rpc, thread, token);
                        if (localUsage is not null)
                        {
                            _usageByThread[thread.Id] = localUsage;
                            fallbackSucceeded = true;
                        }
                        else if (ex.Message.Contains("active writer", StringComparison.OrdinalIgnoreCase))
                            chatUsageWarning = "Codex Desktop has this chat open, but no token usage record is available yet.";
                        else if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                            chatUsageWarning = $"Codex app-server does not support token usage for this chat: {ex.Message}";
                        else
                            chatUsageWarning = "Could not read current token usage for the selected chat.";
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception fallbackException)
                    {
                        AppLogger.Error("CodexUsageProvider", "ReadSelectedThreadUsage", ex,
                            "rpcMethod=thread/resume; localFallback=failed");
                        AppLogger.Error("CodexUsageProvider", "ReadLocalThreadUsageFallback", fallbackException,
                            "source=Codex app-server local rollout; responsePayload=omitted");
                        chatUsageWarning = ex.Message.Contains("active writer", StringComparison.OrdinalIgnoreCase)
                            ? "Codex Desktop has this chat open, so the app-server cannot resume it or read its token usage."
                            : "Could not read current token usage for the selected chat.";
                    }
                    if (!fallbackSucceeded)
                        AppLogger.Error("CodexUsageProvider", "ReadSelectedThreadUsage", ex,
                            "rpcMethod=thread/resume; localFallback=unavailable");
                }
                finally
                {
                    if (usageWaiter is not null) _pendingUsageByThread.TryRemove(thread.Id, out _);
                    if (resumed)
                    {
                        try { await rpc.CallAsync("thread/unsubscribe", new { threadId = thread.Id }, TimeSpan.FromSeconds(3), token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) { AppLogger.Error("CodexUsageProvider", "UnsubscribeThread", ex, "rpcMethod=thread/unsubscribe"); }
                    }
                }
            }

            var enriched = threads.Select(t =>
            {
                if (_usageByThread.TryGetValue(t.Id, out var current))
                {
                    current.Label = t.Label;
                    current.Source = t.Source;
                    current.Workspace = t.Workspace;
                    current.UpdatedAt = t.UpdatedAt;
                    current.IsArchived = t.IsArchived;
                    current.Model = _modelByThread.GetValueOrDefault(t.Id) ?? t.Model ?? current.Model;
                    return current;
                }
                t.Model = _modelByThread.GetValueOrDefault(t.Id) ?? t.Model;
                return t;
            }).ToList();
            var selected = enriched.FirstOrDefault(t => t.Id == preferredId);
            var result = new UsageSnapshot
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                LastSuccessfulUpdate = DateTimeOffset.UtcNow,
                Threads = enriched,
                Quotas = quotas
            };
            if (selected is not null && !HasTokenTotals(selected))
                result.WarningMessage = chatUsageWarning ?? "Codex has not published token usage for the selected chat yet.";
            else if (selected is not null && selected.ContextUsedPercent is null)
                result.WarningMessage = "Codex did not provide the context window for the selected chat.";
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLogger.Error("CodexUsageProvider", "ReadSnapshotCoreAsync", ex, $"rpcProcessRunning={_rpc?.IsRunning}");
            if (_rpc is { IsRunning: false }) await ResetRpcAsync();
            return new UsageSnapshot
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorMessage = ToFriendlyMessage(ex)
            };
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonRpcProcessClient> GetRpcAsync(CancellationToken cancellationToken)
    {
        if (_rpc is { IsRunning: true } && _initialized) return _rpc;
        await ResetRpcAsync();
        var rpc = await JsonRpcProcessClient.StartAsync(cancellationToken);
        _rpc = rpc;
        rpc.Notification += (method, parameters) =>
        {
            if (method == "thread/tokenUsage/updated" && TryReadThreadUsage(parameters, out var usage))
            {
                var isRequestedUsage = _pendingUsageByThread.ContainsKey(usage.Id);
                var merged = _usageByThread.AddOrUpdate(usage.Id, usage, (_, previous) => MergeUsage(previous, usage));
                if (HasTokenTotals(usage) && _pendingUsageByThread.TryGetValue(usage.Id, out var pending))
                    pending.TrySetResult(merged);
                if (!isRequestedUsage) UsageUpdated?.Invoke();
                return;
            }

            if (method == "thread/deleted")
            {
                var id = GetString(parameters, "threadId") ?? GetString(parameters, "id");
                if (!string.IsNullOrWhiteSpace(id)) ThreadDeleted?.Invoke(id);
            }
            if (method is "thread/started" or "thread/name/updated" or "thread/archived" or
                "thread/unarchived" or "thread/deleted" or "thread/status/changed")
                CatalogChanged?.Invoke();
        };
        try
        {
            await rpc.CallAsync("initialize", new
            {
                clientInfo = new { name = "codex_status_bar", title = "Codex Status Bar", version = "0.1.0" }
            }, TimeSpan.FromSeconds(30), cancellationToken);
            await rpc.NotifyAsync("initialized", cancellationToken: cancellationToken);
            _initialized = true;
            return rpc;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CodexUsageProvider", "InitializeAppServer", ex, "rpcMethod=initialize");
            await ResetRpcAsync();
            throw;
        }
    }

    private async Task ResetRpcAsync()
    {
        var rpc = _rpc;
        _rpc = null;
        _initialized = false;
        _cachedThreads = null;
        _cachedQuotas = null;
        if (rpc is not null)
        {
            try { await rpc.DisposeAsync(); }
            catch (Exception ex) { AppLogger.Error("CodexUsageProvider", "ResetRpcAsync.Dispose", ex); }
        }
    }

    private static async Task<IReadOnlyList<QuotaWindow>> ReadQuotasAsync(JsonRpcProcessClient rpc, CancellationToken ct)
    {
        var response = await rpc.CallAsync("account/rateLimits/read", cancellationToken: ct);
        var result = new List<QuotaWindow>();
        if (response.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in byId.EnumerateObject())
                AddBucket(result, bucket.Name, bucket.Value);
        }
        if (result.Count == 0 && response.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
            AddBucket(result, "codex", single);
        return result;
    }

    private static void AddBucket(List<QuotaWindow> result, string bucketId, JsonElement bucket)
    {
        var fallbackName = bucket.TryGetProperty("limitName", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() : null;
        if (bucket.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object)
            AddWindow(result, bucketId, fallbackName, primary);
        if (bucket.TryGetProperty("secondary", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
            AddWindow(result, bucketId, fallbackName, secondary);
    }

    private static void AddWindow(List<QuotaWindow> result, string bucketId, string? fallbackName, JsonElement window)
    {
        var duration = GetLong(window, "windowDurationMins");
        var label = duration switch
        {
            300 => "5 Hour",
            10080 => "Weekly",
            _ when !string.IsNullOrWhiteSpace(fallbackName) => fallbackName!,
            _ when duration.HasValue => $"{duration.Value} min",
            _ => "Quota"
        };
        DateTimeOffset? reset = GetLong(window, "resetsAt") is long timestamp
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : null;
        result.Add(new QuotaWindow
        {
            Id = bucketId + ":" + (duration?.ToString() ?? label),
            Label = label,
            WindowDurationMinutes = duration,
            UsedPercent = GetDouble(window, "usedPercent"),
            ResetsAt = reset
        });
    }

    private async Task<List<ThreadUsage>> ReadThreadsAsync(JsonRpcProcessClient rpc, CancellationToken ct) =>
        await ReadThreadsAsync(rpc, useStateDbOnly: false, ct);

    private async Task<List<ThreadUsage>> ReadThreadsAsync(JsonRpcProcessClient rpc, bool useStateDbOnly,
        CancellationToken ct)
    {
        if (useStateDbOnly && _supportsStateDbOnly != false)
        {
            try
            {
                var result = await ReadThreadsAsync(rpc, "recency_at", ct, useStateDbOnly: true);
                _supportsStateDbOnly = true;
                return result;
            }
            catch (InvalidOperationException ex) when (LooksLikeUnsupportedStateDbOnly(ex))
            {
                _supportsStateDbOnly = false;
                AppLogger.Information("CodexUsageProvider", "StateDbOnlyUnsupported",
                    "thread/list does not support useStateDbOnly; falling back to a regular catalog scan.");
            }
        }
        try
        {
            return await ReadThreadsAsync(rpc, "recency_at", ct, useStateDbOnly: false);
        }
        catch (InvalidOperationException)
        {
            // Older installed Codex CLIs reject the newer recency_at sort key.
            // updated_at is supported by both old and current app-server versions.
            return await ReadThreadsAsync(rpc, "updated_at", ct, useStateDbOnly: false);
        }
    }

    private static async Task<List<ThreadUsage>> ReadThreadsAsync(JsonRpcProcessClient rpc, string sortKey,
        CancellationToken ct, bool useStateDbOnly)
    {
        var all = new Dictionary<string, ThreadUsage>(StringComparer.Ordinal);
        foreach (var archived in new[] { false, true })
        {
            string? cursor = null;
            do
            {
                var arguments = new Dictionary<string, object?>
                {
                    ["archived"] = archived,
                    ["limit"] = 100,
                    ["sortKey"] = sortKey,
                    ["sortDirection"] = "desc"
                };
                if (useStateDbOnly) arguments["useStateDbOnly"] = true;
                if (!string.IsNullOrWhiteSpace(cursor)) arguments["cursor"] = cursor;
                var page = await rpc.CallAsync("thread/list", arguments, cancellationToken: ct);
                if (!page.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
                foreach (var item in data.EnumerateArray())
                {
                    var id = GetString(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var source = item.TryGetProperty("source", out var sourceElement) ? sourceElement.ToString() : "unknown";
                    var label = GetString(item, "name");
                    if (string.IsNullOrWhiteSpace(label)) label = $"Untitled chat · {id[..Math.Min(8, id.Length)]}";
                    var cwd = GetString(item, "cwd");
                    var workspace = GetWorkspaceName(cwd);
                    var updated = GetLong(item, "recencyAt") ?? GetLong(item, "updatedAt") ?? GetLong(item, "createdAt") ?? 0;
                    all[id] = new ThreadUsage
                    {
                        Id = id,
                        Label = label,
                        Source = source,
                        Workspace = workspace,
                        Model = GetString(item, "model"),
                        UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, updated)),
                        IsArchived = archived
                    };
                }
                cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            } while (!string.IsNullOrWhiteSpace(cursor));
        }
        return all.Values.OrderByDescending(t => t.UpdatedAt).ToList();
    }

    private static bool LooksLikeUnsupportedStateDbOnly(InvalidOperationException exception) =>
        exception.Message.Contains("useStateDbOnly", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("unknown field", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("unexpected field", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadThreadUsage(JsonElement parameters, out ThreadUsage usage)
    {
        usage = new ThreadUsage();
        if (!parameters.TryGetProperty("threadId", out var id) || id.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("tokenUsage", out var tokenUsage)) return false;
        var total = tokenUsage.TryGetProperty("total", out var totalBreakdown) ? GetLong(totalBreakdown, "totalTokens") : null;
        var totalInput = tokenUsage.TryGetProperty("total", out totalBreakdown) ? GetLong(totalBreakdown, "inputTokens") : null;
        var totalOutput = tokenUsage.TryGetProperty("total", out totalBreakdown) ? GetLong(totalBreakdown, "outputTokens") : null;
        var lastInput = tokenUsage.TryGetProperty("last", out var last) ? GetLong(last, "inputTokens") : null;
        var contextWindow = GetLong(tokenUsage, "modelContextWindow");
        usage = new ThreadUsage
        {
            Id = id.GetString()!,
            UsageCapturedAt = DateTimeOffset.UtcNow,
            TotalTokens = total,
            LastInputTokens = lastInput,
            TotalInputTokens = totalInput,
            TotalOutputTokens = totalOutput,
            ContextWindow = contextWindow,
            ContextUsedPercent = contextWindow is > 0 && lastInput.HasValue
                ? Math.Clamp(100d * lastInput.Value / contextWindow.Value, 0, 100) : null
        };
        return true;
    }

    private static bool HasTokenTotals(ThreadUsage usage) =>
        usage.TotalInputTokens.HasValue && usage.TotalOutputTokens.HasValue;

    private static ThreadUsage CloneThreadMetadata(ThreadUsage thread) => new()
    {
        Id = thread.Id,
        Label = thread.Label,
        Source = thread.Source,
        Workspace = thread.Workspace,
        Model = thread.Model,
        UpdatedAt = thread.UpdatedAt,
        IsArchived = thread.IsArchived
    };

    private static ThreadUsage MergeUsage(ThreadUsage previous, ThreadUsage latest) => new()
    {
        Id = latest.Id,
        Label = latest.Label ?? previous.Label,
        Source = latest.Source ?? previous.Source,
        Workspace = latest.Workspace ?? previous.Workspace,
        Model = latest.Model ?? previous.Model,
        UpdatedAt = latest.UpdatedAt != default ? latest.UpdatedAt : previous.UpdatedAt,
        UsageCapturedAt = latest.UsageCapturedAt ?? previous.UsageCapturedAt,
        TotalTokens = latest.TotalTokens ?? previous.TotalTokens,
        LastInputTokens = latest.LastInputTokens ?? previous.LastInputTokens,
        TotalInputTokens = latest.TotalInputTokens ?? previous.TotalInputTokens,
        TotalOutputTokens = latest.TotalOutputTokens ?? previous.TotalOutputTokens,
        ContextWindow = latest.ContextWindow ?? previous.ContextWindow,
        ContextUsedPercent = latest.ContextUsedPercent ?? previous.ContextUsedPercent,
        IsArchived = latest.IsArchived || previous.IsArchived
    };

    private async Task<ThreadUsage?> ReadLocalThreadUsageAsync(JsonRpcProcessClient rpc, ThreadUsage thread, CancellationToken cancellationToken)
    {
        var response = await rpc.CallAsync("thread/read", new { threadId = thread.Id, includeTurns = false },
            TimeSpan.FromSeconds(10), cancellationToken);
        if (!response.TryGetProperty("thread", out var threadData)) return null;
        var path = GetString(threadData, "path");
        if (string.IsNullOrWhiteSpace(path) || !IsCodexRolloutPath(path)) return null;

        var usage = await Task.Run(() => ReadLatestRolloutTokenUsage(path, thread.Id, cancellationToken), cancellationToken);
        if (usage is null) return null;
        usage.Label = thread.Label;
        usage.Source = thread.Source;
        usage.Workspace = thread.Workspace;
        usage.UpdatedAt = thread.UpdatedAt;
        usage.IsArchived = thread.IsArchived;
        usage.Model = _modelByThread.GetValueOrDefault(thread.Id) ?? thread.Model;
        return usage;
    }

    private static bool IsCodexRolloutPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return Path.GetFileName(fullPath).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetExtension(fullPath), ".jsonl", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // Search from the newest end of the append-only rollout, and parse only token_count records.
    // Other JSON properties are skipped without converting their text into strings or app models.
    private static ThreadUsage? ReadLatestRolloutTokenUsage(string path, string threadId, CancellationToken cancellationToken)
    {
        const int blockSize = 32 * 1024;
        const int maxRecordBytes = 128 * 1024;
        const long maxScanBytes = 32L * 1024 * 1024;
        var marker = Encoding.UTF8.GetBytes("\"type\":\"token_count\"");
        Array.Reverse(marker);
        var buffer = new byte[blockSize];
        var reverseLine = new List<byte>(4096);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, blockSize, FileOptions.RandomAccess);
        using (stream)
        {
            var fileLength = stream.Length;
            var scanStart = Math.Max(0, fileLength - maxScanBytes);
            var cursor = fileLength;
            var candidate = false;
            var tooLong = false;

            ThreadUsage? ProcessCurrentLine()
            {
                if (candidate && !tooLong && reverseLine.Count > 0)
                {
                    var line = reverseLine.ToArray();
                    Array.Reverse(line);
                    if (TryParseRolloutTokenCount(line, threadId, out var usage)) return usage;
                }
                reverseLine.Clear();
                candidate = false;
                tooLong = false;
                return null;
            }

            while (cursor > scanStart)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = Math.Max(scanStart, cursor - blockSize);
                stream.Position = start;
                var read = stream.Read(buffer, 0, (int)(cursor - start));
                if (read <= 0) break;
                for (var index = read - 1; index >= 0; index--)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        var usage = ProcessCurrentLine();
                        if (usage is not null) return usage;
                        continue;
                    }

                    if (!tooLong)
                    {
                        if (reverseLine.Count < maxRecordBytes) reverseLine.Add(value);
                        else tooLong = true;
                        if (!candidate && reverseLine.Count >= marker.Length)
                        {
                            candidate = true;
                            for (var i = 0; i < marker.Length; i++)
                            {
                                if (reverseLine[reverseLine.Count - marker.Length + i] == marker[i]) continue;
                                candidate = false;
                                break;
                            }
                        }
                    }
                }
                cursor = start;
            }

            return ProcessCurrentLine();
        }
    }

    private static bool TryParseRolloutTokenCount(ReadOnlySpan<byte> line, string threadId, out ThreadUsage usage)
    {
        usage = new ThreadUsage();
        try
        {
            var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            string? recordType = null;
            string? payloadType = null;
            RolloutTokenCounts counts = default;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("type"u8))
                {
                    if (!reader.Read()) return false;
                    recordType = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                else if (reader.ValueTextEquals("payload"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName) continue;
                        if (reader.ValueTextEquals("type"u8))
                        {
                            if (!reader.Read()) return false;
                            payloadType = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        }
                        else if (reader.ValueTextEquals("info"u8))
                        {
                            if (!reader.Read()) return false;
                            counts = ReadRolloutUsageInfo(ref reader);
                        }
                        else reader.Skip();
                    }
                }
                else reader.Skip();
            }

            if (recordType != "event_msg" || payloadType != "token_count" ||
                (!counts.InputTokens.HasValue && !counts.OutputTokens.HasValue)) return false;
            usage = new ThreadUsage
            {
                Id = threadId,
                UsageCapturedAt = DateTimeOffset.UtcNow,
                TotalTokens = counts.TotalTokens,
                TotalInputTokens = counts.InputTokens,
                TotalOutputTokens = counts.OutputTokens,
                LastInputTokens = counts.LastInputTokens,
                ContextWindow = counts.ContextWindow,
                ContextUsedPercent = counts.ContextWindow is > 0 && counts.LastInputTokens.HasValue
                    ? Math.Clamp(100d * counts.LastInputTokens.Value / counts.ContextWindow.Value, 0, 100) : null
            };
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static RolloutTokenCounts ReadRolloutUsageInfo(ref Utf8JsonReader reader)
    {
        long? input = null, output = null, total = null, lastInput = null, contextWindow = null;
        if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return default; }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.ValueTextEquals("total_token_usage"u8))
            {
                if (!reader.Read()) break;
                ReadRolloutTokenBreakdown(ref reader, ref input, ref output, ref total, null);
            }
            else if (reader.ValueTextEquals("last_token_usage"u8))
            {
                if (!reader.Read()) break;
                ReadRolloutTokenBreakdown(ref reader, ref lastInput, ref output, ref total, isLast: true);
            }
            else if (reader.ValueTextEquals("model_context_window"u8))
            {
                if (!reader.Read()) break;
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value)) contextWindow = value;
            }
            else reader.Skip();
        }
        return new RolloutTokenCounts(input, output, total, lastInput, contextWindow);
    }

    private static void ReadRolloutTokenBreakdown(ref Utf8JsonReader reader, ref long? input, ref long? output,
        ref long? total, bool? isLast)
    {
        if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var isInput = reader.ValueTextEquals("input_tokens"u8);
            var isOutput = reader.ValueTextEquals("output_tokens"u8);
            var isTotal = reader.ValueTextEquals("total_tokens"u8);
            if (!reader.Read()) break;
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value)) continue;
            if (isInput) input = value;
            else if (isOutput && isLast is null) output = value;
            else if (isTotal && isLast is null) total = value;
        }
    }

    private readonly record struct RolloutTokenCounts(long? InputTokens, long? OutputTokens,
        long? TotalTokens, long? LastInputTokens, long? ContextWindow);

    private static string? GetWorkspaceName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var normalized = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(name) ? cwd : name;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static string ToFriendlyMessage(Exception ex)
    {
        if (ex is TimeoutException) return $"{ex.Message} Showing the last saved data.";
        if (ex.Message.Contains("Codex CLI was not found", StringComparison.OrdinalIgnoreCase))
            return "Codex CLI was not found. Install Codex CLI and add it to PATH.";
        if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("not signed in", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("authentication required", StringComparison.OrdinalIgnoreCase))
            return "Codex is not signed in. Sign in with Codex CLI to load usage.";
        if (ex.Message.Contains("method not found", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("unsupported method", StringComparison.OrdinalIgnoreCase))
            return "This Codex CLI app-server does not support a required usage method. Update Codex CLI and refresh.";
        return "Codex usage is temporarily unavailable. Check Codex CLI and try Refresh Now.";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _shutdown.Cancel();
        await _gate.WaitAsync();
        try
        {
            var rpc = _rpc;
            _rpc = null;
            _initialized = false;
            if (rpc is not null) await rpc.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }
}
