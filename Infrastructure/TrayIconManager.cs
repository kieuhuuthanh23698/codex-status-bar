using System.Drawing;
using System.Windows.Forms;

namespace CodexStatusBar.Infrastructure;

public sealed class TrayIconManager : IDisposable
{
    private static readonly Icon NormalIcon = LoadIcon("CodexStatusBar.Assets.CodexStatusBar.ico");
    private static readonly Icon WarningIcon = LoadIcon("CodexStatusBar.Assets.CodexStatusBar.Warning.ico");
    private static readonly Icon ErrorIcon = LoadIcon("CodexStatusBar.Assets.CodexStatusBar.Error.ico");
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleBar;
    private readonly Action _dashboard, _settings, _toggle, _viewLogs, _exit;
    private readonly Func<Task> _refresh;
    private bool _hasError;
    private int _warningLevel;
    public bool Visible { get => _icon.Visible; set => _icon.Visible = value; }

    public TrayIconManager(Action dashboard, Action settings, Action toggle, Func<Task> refresh, Action viewLogs, Action exit)
    {
        _dashboard = dashboard; _settings = settings; _toggle = toggle; _refresh = refresh; _viewLogs = viewLogs; _exit = exit;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Codex Status", null, (_, _) => _dashboard());
        _toggleBar = new ToolStripMenuItem("Show floating status bar", null, (_, _) => _toggle());
        _toggleBar.Checked = true;
        menu.Items.Add(_toggleBar);
        menu.Items.Add("Refresh Now", null, async (_, _) => await _refresh());
        menu.Items.Add("Settings", null, (_, _) => _settings());
        menu.Items.Add("View Error Log", null, (_, _) => _viewLogs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _exit());
        _icon = new NotifyIcon
        {
            Icon = NormalIcon,
            Text = "Codex Status Bar",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _dashboard();
        };
    }

    public void SetBarVisible(bool visible) => _toggleBar.Checked = visible;
    public void SetStatus(string text, bool isError)
    {
        _icon.Text = text.Length <= 63 ? text : text[..60] + "...";
        _hasError = isError;
        UpdateIcon();
    }
    public void SetWarningLevel(int level)
    {
        _warningLevel = Math.Clamp(level, 0, 3);
        UpdateIcon();
    }
    private void UpdateIcon()
    {
        if (!_icon.Visible) return;
        _icon.Icon = _hasError || _warningLevel >= 3 ? ErrorIcon
            : _warningLevel > 0 ? WarningIcon
            : NormalIcon;
    }

    private static Icon LoadIcon(string resourceName)
    {
        using var stream = typeof(TrayIconManager).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded icon resource '{resourceName}' was not found.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
    public void NotifyAlert(string title, string message)
    {
        if (!_icon.Visible) return;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message.Length > 255 ? message[..252] + "..." : message;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose() => _icon.Dispose();
}
