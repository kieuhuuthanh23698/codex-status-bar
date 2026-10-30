using Microsoft.Win32;

namespace CodexStatusBar.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexStatusBar";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { AppLogger.Error("StartupService", "Apply", ex, $"enabled={enabled}"); }
    }
}
