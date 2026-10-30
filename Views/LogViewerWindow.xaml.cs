using System.Windows;
using CodexStatusBar.Services;

namespace CodexStatusBar.Views;

public partial class LogViewerWindow : Window
{
    public LogViewerWindow()
    {
        InitializeComponent();
        LoadLog();
    }

    private void LoadLog()
    {
        LogPathText.Text = $"Files are stored in: {AppLogger.LogDirectory}";
        LogTextBox.Text = AppLogger.ReadLatestLog();
        StatusText.Text = "Log contents stay on this computer.";
        LogTextBox.ScrollToEnd();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadLog();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(LogTextBox.Text);
            StatusText.Text = "Log copied to clipboard.";
        }
        catch (Exception ex)
        {
            AppLogger.Error("LogViewerWindow", "CopyLogToClipboard", ex);
            StatusText.Text = "Could not copy the log. See the log file on this computer.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
