using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodexStatusBar.Services;

/// <summary>
/// Writes local diagnostic logs without allowing logging failures to affect the app.
/// Callers must pass operation names and safe technical context only; never pass chat content.
/// </summary>
public static class AppLogger
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private const int RetainedRotatedFiles = 3;
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexStatusBar", "logs");

    public static string ReadLatestLog()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return $"No error logs have been created yet.\n\nLog folder: {LogDirectory}";
            var latest = Directory.EnumerateFiles(LogDirectory, "CodexStatusBar-*.log*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null) return $"No error logs have been created yet.\n\nLog folder: {LogDirectory}";
            return $"Log file: {latest}{Environment.NewLine}{Environment.NewLine}{File.ReadAllText(latest, Encoding.UTF8)}";
        }
        catch (Exception ex)
        {
            Error("AppLogger", "ReadLatestLog", ex, $"logDirectory={LogDirectory}");
            return $"Could not read the latest log.\n{ex.Message}\n\nLog folder: {LogDirectory}";
        }
    }

    public static void Error(string component, string operation, Exception exception, string? context = null)
    {
        Write("ERROR", component, operation, exception, context);
    }

    public static void Information(string component, string operation, string? context = null)
    {
        Write("INFO", component, operation, null, context);
    }

    private static void Write(string level, string component, string operation, Exception? exception, string? context)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)}] {level}");
        builder.AppendLine($"Component: {SafeField(component)}");
        builder.AppendLine($"Operation: {SafeField(operation)}");
        if (!string.IsNullOrWhiteSpace(context)) builder.AppendLine($"Context: {SafeField(context)}");
        builder.AppendLine($"ProcessId: {Environment.ProcessId}");
        builder.AppendLine($"AppVersion: {typeof(AppLogger).Assembly.GetName().Version}");
        builder.AppendLine($"Runtime: {Environment.Version}; OS: {Environment.OSVersion}");
        if (exception is not null) AppendException(builder, exception, 0);
        builder.AppendLine(new string('-', 80));
        var record = builder.ToString();

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"CodexStatusBar-{DateTime.Now:yyyyMMdd}.log");
                RotateIfNeeded(path, Encoding.UTF8.GetByteCount(record));
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(record);
            }
        }
        catch (Exception loggingException)
        {
            try { Trace.WriteLine($"Failed to write Codex Status Bar diagnostic log: {loggingException}\n{record}"); }
            catch { }
        }
    }

    private static void AppendException(StringBuilder builder, Exception exception, int depth)
    {
        builder.AppendLine($"Exception[{depth}]: {exception.GetType().FullName}");
        builder.AppendLine($"HResult: 0x{exception.HResult:X8}");
        builder.AppendLine($"Message: {SafeField(exception.Message)}");
        if (exception is SqliteException sqlite)
        {
            builder.AppendLine($"SQLite ErrorCode: {sqlite.SqliteErrorCode}");
            builder.AppendLine($"SQLite ExtendedErrorCode: {sqlite.SqliteExtendedErrorCode}");
        }
        var stackTrace = exception.StackTrace ?? Environment.StackTrace;
        if (!string.IsNullOrWhiteSpace(stackTrace))
        {
            builder.AppendLine("StackTrace:");
            builder.AppendLine(stackTrace);
        }
        if (exception.InnerException is not null)
        {
            builder.AppendLine("InnerException:");
            AppendException(builder, exception.InnerException, depth + 1);
        }
    }

    private static string SafeField(string value) => value
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal);

    private static void RotateIfNeeded(string path, int incomingBytes)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaxLogBytes) return;
        var oldest = $"{path}.{RetainedRotatedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = RetainedRotatedFiles - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            if (File.Exists(source)) File.Move(source, $"{path}.{index + 1}");
        }
        File.Move(path, $"{path}.1");
    }
}
