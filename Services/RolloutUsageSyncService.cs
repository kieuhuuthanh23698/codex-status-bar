using System.Collections.Concurrent;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexStatusBar.Models;

namespace CodexStatusBar.Services;

/// <summary>
/// Imports only cumulative token_count records from local rollout files. Other JSONL records are
/// parsed only enough to skip their values and are never retained in the app or database.
/// </summary>
public sealed class RolloutUsageSyncService : IAsyncDisposable
{
    private static readonly Regex ThreadIdInFileName = new(
        "(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int MaxTokenRecordBytes = 128 * 1024;
    private readonly UsageHistoryStore _history;
    private readonly string _codexHome;
    private readonly string _sessionsDirectory;
    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RolloutFileCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly System.Threading.Timer _reconcileTimer;
    private readonly System.Threading.Timer _debounceTimer;
    private FileSystemWatcher? _watcher;
    private int _fullScanRequested;
    private int _started;
    private int _disposed;

    public event Action? SyncRequested;
    public event Action? UsageHistoryChanged;

    public RolloutUsageSyncService(UsageHistoryStore history)
    {
        _history = history;
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        _codexHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Path.GetFullPath(configuredHome);
        _sessionsDirectory = Path.Combine(_codexHome, "sessions");
        _reconcileTimer = new System.Threading.Timer(_ => QueueFullScan(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _debounceTimer = new System.Threading.Timer(state => { _ = DrainPendingSafelyAsync(); }, null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var existing = await _history.GetRolloutFileCursorsAsync();
        foreach (var pair in existing) _cursors[pair.Key] = pair.Value;
        EnsureWatcher();
        _reconcileTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        QueueFullScan();
        await Task.CompletedTask;
    }

    private void EnsureWatcher()
    {
        if (_watcher is not null || !Directory.Exists(_codexHome)) return;
        try
        {
            _watcher = new FileSystemWatcher(_codexHome, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnFileChanged;
            _watcher.Changed += OnFileChanged;
            _watcher.Renamed += (_, args) => OnPathChanged(args.FullPath);
            _watcher.Error += (_, args) =>
            {
                AppLogger.Error("RolloutUsageSync", "FileWatcherError", args.GetException(),
                    $"sessionsDirectory={_sessionsDirectory}; action=full-rescan");
                QueueFullScan();
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error("RolloutUsageSync", "CreateFileWatcher", ex,
                $"codexHome={_codexHome}; watcher=unavailable; periodicRescan=true");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) => OnPathChanged(args.FullPath);

    private void OnPathChanged(string path)
    {
        if (!IsRolloutFile(path) || !IsInSessionsDirectory(path)) return;
        _pendingPaths[path] = 0;
        _debounceTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    private async Task DrainPendingSafelyAsync()
    {
        try
        {
            await DrainPendingAsync(_shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.Error("RolloutUsageSync", "DebouncedFileScan", ex,
                $"pendingFiles={_pendingPaths.Count}; rawJsonl=omitted");
        }
    }

    private void QueueFullScan()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _fullScanRequested, 1);
        EnsureWatcher();
        _debounceTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    private async Task DrainPendingAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _fullScanRequested, 0) == 1)
        {
            if (!Directory.Exists(_sessionsDirectory)) return;
            await _scanGate.WaitAsync(cancellationToken);
            var changed = false;
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-90);
                foreach (var path in Directory.EnumerateFiles(_sessionsDirectory, "rollout-*.jsonl", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff) continue;
                        changed |= await ProcessFileAsync(path, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        AppLogger.Error("RolloutUsageSync", "ScanFile", ex,
                            $"file={path}; tokenRecordsOnly=true; rawJsonl=omitted");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Error("RolloutUsageSync", "EnumerateSessions", ex,
                    $"sessionsDirectory={_sessionsDirectory}; backfill=retry-on-next-scan");
            }
            finally { _scanGate.Release(); }
            if (changed)
            {
                SyncRequested?.Invoke();
                UsageHistoryChanged?.Invoke();
            }
        }

        if (_pendingPaths.IsEmpty) return;
        await _scanGate.WaitAsync(cancellationToken);
        var hadData = false;
        var processedAnyPath = false;
        try
        {
            foreach (var path in _pendingPaths.Keys.ToArray())
            {
                _pendingPaths.TryRemove(path, out _);
                try
                {
                    if (File.Exists(path)) processedAnyPath = true;
                    hadData |= await ProcessFileAsync(path, cancellationToken);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    AppLogger.Error("RolloutUsageSync", "ProcessChangedFile", ex,
                        $"file={path}; tokenRecordsOnly=true; rawJsonl=omitted");
                }
            }
        }
        finally { _scanGate.Release(); }
        if (processedAnyPath) SyncRequested?.Invoke();
        if (hadData) UsageHistoryChanged?.Invoke();
    }

    private async Task<bool> ProcessFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsRolloutFile(path) || !File.Exists(path)) return false;
        var match = ThreadIdInFileName.Match(Path.GetFileNameWithoutExtension(path));
        if (!match.Success) return false;
        var threadId = match.Groups["id"].Value;
        var info = new FileInfo(path);
        var length = info.Length;
        var writeTicks = info.LastWriteTimeUtc.Ticks;
        var creationTicks = info.CreationTimeUtc.Ticks;
        var reset = false;
        var offset = 0L;
        if (_cursors.TryGetValue(path, out var cursor))
        {
            reset = cursor.ByteOffset > length || cursor.CreationUtcTicks != creationTicks ||
                    cursor.LastWriteUtcTicks > writeTicks || !string.Equals(cursor.ThreadId, threadId, StringComparison.OrdinalIgnoreCase);
            if (!reset) offset = cursor.ByteOffset;
        }
        string? currentModel = reset ? null : cursor?.CurrentModel;
        if (!reset && offset >= length) return false;

        var imported = new List<RolloutTokenEvent>();
        long completeOffset = offset;
        long absoluteOffset = offset;
        var lineStartOffset = offset;
        var line = new List<byte>(4096);
        var oversized = false;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            stream.Position = offset;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    absoluteOffset++;
                    if (value == (byte)'\n')
                    {
                        if (!oversized && line.Count > 0 && TryParseRolloutLine(line.ToArray(), threadId, path,
                                lineStartOffset, ref currentModel, out var tokenEvent) && tokenEvent is not null &&
                            tokenEvent.CapturedAt >= DateTimeOffset.UtcNow.AddDays(-90))
                            imported.Add(tokenEvent);
                        line.Clear();
                        oversized = false;
                        completeOffset = absoluteOffset;
                        lineStartOffset = absoluteOffset;
                    }
                    else if (!oversized)
                    {
                        if (line.Count < MaxTokenRecordBytes) line.Add(value);
                        else { line.Clear(); oversized = true; }
                    }
                }
            }
        }

        var after = new FileInfo(path);
        var saved = new RolloutFileCursor(threadId, completeOffset, after.Exists ? after.Length : length,
            after.Exists ? after.LastWriteTimeUtc.Ticks : writeTicks,
            after.Exists ? after.CreationTimeUtc.Ticks : creationTicks, currentModel);
        await _history.SaveRolloutFileProgressAsync(path, saved, imported, reset);
        _cursors[path] = saved;
        return imported.Count > 0;
    }

    private static bool IsRolloutFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return Path.GetFileName(full).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetExtension(full), ".jsonl", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    private bool IsInSessionsDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(_sessionsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseRolloutLine(ReadOnlySpan<byte> line, string threadId, string path, long offset,
        ref string? currentModel, out RolloutTokenEvent? tokenEvent)
    {
        tokenEvent = null;
        try
        {
            var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            string? recordType = null, payloadType = null, timestampText = null, model = null;
            long? input = null, output = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("type"u8))
                {
                    if (!reader.Read()) return false;
                    recordType = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                else if (reader.ValueTextEquals("timestamp"u8))
                {
                    if (!reader.Read()) return false;
                    timestampText = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
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
                        else if (reader.ValueTextEquals("model"u8))
                        {
                            if (!reader.Read()) return false;
                            model = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        }
                        else if (reader.ValueTextEquals("info"u8))
                        {
                            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                                if (reader.ValueTextEquals("total_token_usage"u8))
                                {
                                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                                    {
                                        if (reader.TokenType != JsonTokenType.PropertyName) continue;
                                        var isInput = reader.ValueTextEquals("input_tokens"u8);
                                        var isOutput = reader.ValueTextEquals("output_tokens"u8);
                                        if (!reader.Read()) return false;
                                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value))
                                        {
                                            if (isInput) input = value;
                                            else if (isOutput) output = value;
                                        }
                                        else reader.Skip();
                                    }
                                }
                                else reader.Skip();
                            }
                        }
                        else reader.Skip();
                    }
                }
                else reader.Skip();
            }
            if (recordType == "turn_context" && !string.IsNullOrWhiteSpace(model))
                currentModel = model.Trim();

            if (recordType != "event_msg" || payloadType != "token_count" || input is null or < 0 || output is null or < 0 ||
                !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var capturedAt)) return false;
            tokenEvent = new RolloutTokenEvent(threadId, path, offset, capturedAt, input.Value, output.Value, currentModel);
            return true;
        }
        catch (JsonException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _reconcileTimer.Dispose();
        _debounceTimer.Dispose();
        _watcher?.Dispose();
        await _scanGate.WaitAsync();
        _scanGate.Release();
        _scanGate.Dispose();
        _shutdown.Dispose();
    }
}
