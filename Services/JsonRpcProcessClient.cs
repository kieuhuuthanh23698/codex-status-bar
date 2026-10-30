using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexStatusBar.Services;

public sealed class JsonRpcProcessClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _reader;
    private long _nextId;
    private bool _disposed;
    public bool IsRunning => !_disposed && !_process.HasExited;
    public event Action<string, JsonElement>? Notification;

    private JsonRpcProcessClient(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        _reader = Task.Run(ReadLoopAsync);
    }

    public static Task<JsonRpcProcessClient> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var codexExecutable = FindBundledCodexExecutable();
        if (codexExecutable is null && !CodexCommandExists())
            throw new InvalidOperationException("Codex CLI was not found. Install Codex CLI and add it to PATH.");
        var start = new ProcessStartInfo
        {
            FileName = codexExecutable ?? "cmd.exe",
            Arguments = codexExecutable is null ? "/d /c codex app-server --listen stdio://" : "app-server --listen stdio://",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        start.Environment["NO_COLOR"] = "1";
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not start Codex CLI.");
            return Task.FromResult(new JsonRpcProcessClient(process));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppLogger.Error("JsonRpcProcessClient", "StartAsync", ex, "starting=codex app-server --listen stdio://");
            process.Dispose();
            throw new InvalidOperationException("Codex CLI was not found. Install Codex CLI and add it to PATH.");
        }
    }

    private static bool CodexCommandExists()
    {
        if (FindBundledCodexExecutable() is not null) return true;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
                if (File.Exists(Path.Combine(directory.Trim('"'), "codex" + extension))) return true;
            if (File.Exists(Path.Combine(directory.Trim('"'), "codex"))) return true;
        }
        return false;
    }

    private static string? FindBundledCodexExecutable()
    {
        try
        {
            var bundledBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI", "Codex", "bin");
            if (!Directory.Exists(bundledBin)) return null;
            return Directory.EnumerateFiles(bundledBin, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process.HasExited) throw new InvalidOperationException("Codex app-server exited unexpectedly.");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var message = JsonSerializer.Serialize(new { method, id, @params = parameters ?? new { } });
        await _process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(12));
        try { return await completion.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Codex app-server did not answer '{method}'.");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var message = JsonSerializer.Serialize(new { method, @params = parameters ?? new { } });
        await _process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    private async Task ReadLoopAsync()
    {
        var malformedLineCount = 0;
        Exception? readFailure = null;
        try
        {
            while (!_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException ex)
                {
                    malformedLineCount++;
                    if (malformedLineCount == 1 || malformedLineCount % 100 == 0)
                        AppLogger.Error("JsonRpcProcessClient", "ParseStandardOutput", ex,
                            $"malformedLineCount={malformedLineCount}; rawOutputOmitted=true");
                    continue;
                }
                using (document)
                {
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
                {
                    if (!_pending.TryGetValue(id, out var pending)) continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                        pending.TrySetException(new InvalidOperationException(message ?? "Codex app-server returned an error."));
                    }
                    else if (root.TryGetProperty("result", out var result)) pending.TrySetResult(result.Clone());
                    else pending.TrySetException(new InvalidOperationException("Codex app-server returned an invalid response."));
                    continue;
                }

                if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String && root.TryGetProperty("params", out var parameters))
                {
                    Notification?.Invoke(methodElement.GetString()!, parameters.Clone());
                }
                }
            }
        }
        catch (Exception ex)
        {
            readFailure = ex;
            foreach (var pending in _pending.Values) pending.TrySetException(ex);
        }
        finally
        {
            if (!_disposed)
            {
                if (readFailure is not null)
                    AppLogger.Error("JsonRpcProcessClient", "ReadLoopAsync", readFailure, $"processExitCode={GetExitCode()}; malformedStdoutLines={malformedLineCount}");
                else
                    AppLogger.Error("JsonRpcProcessClient", "ReadLoopAsync", new EndOfStreamException("Codex app-server closed its standard output."),
                        $"processExitCode={GetExitCode()}; malformedStdoutLines={malformedLineCount}");
            }
            var stopped = new InvalidOperationException("Codex app-server connection closed.");
            foreach (var pending in _pending.Values) pending.TrySetException(stopped);
        }
    }

    private string GetExitCode()
    {
        try { return _process.HasExited ? _process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "still-running"; }
        catch (InvalidOperationException) { return "unavailable"; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.StandardInput.Close(); }
        catch (Exception ex) { AppLogger.Error("JsonRpcProcessClient", "CloseStandardInput", ex); }
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { AppLogger.Error("JsonRpcProcessClient", "TerminateProcess", ex); }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        catch (Exception ex) { AppLogger.Error("JsonRpcProcessClient", "WaitForReader", ex); }
        _process.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
