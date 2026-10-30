using CodexStatusBar.Models;

namespace CodexStatusBar.Infrastructure;

public static class QuotaPercentFormatter
{
    public static double? ToDisplayValue(double? usedPercent, QuotaValueMode mode)
    {
        if (usedPercent is not double used) return null;
        used = Math.Clamp(used, 0, 100);
        return mode == QuotaValueMode.Remaining ? 100 - used : used;
    }

    public static string Format(double? usedPercent, QuotaValueMode mode, bool includeMeaning = false)
    {
        if (ToDisplayValue(usedPercent, mode) is not double value) return "—";
        var text = $"{value:0}%";
        if (!includeMeaning) return text;
        return $"{text} {(mode == QuotaValueMode.Remaining ? "remaining" : "used")}";
    }
}
