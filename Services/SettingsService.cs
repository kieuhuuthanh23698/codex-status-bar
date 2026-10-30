using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexStatusBar.Models;

namespace CodexStatusBar.Services;

public sealed class SettingsService
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStatusBar", "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsService", "LoadAsync", ex, $"settingsFile={_path}");
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, settings, Options);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsService", "SaveAsync", ex, $"settingsFile={_path}");
            throw;
        }
        finally { _gate.Release(); }
    }
}
