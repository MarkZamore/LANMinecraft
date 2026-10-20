using System.Windows;
using System.Windows.Controls;

namespace Minecraft;

/// <summary>Keeps the report editor finite while WPF lays out the fixed 2:1 columns.</summary>
internal static class AdaptiveLauncherLayout
{
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var report = (TextBox)window.FindName("BugReportMessageTextBox");
        // A ScrollViewer measures its content at infinite height. A finite
        // editor scrolls long input internally rather than growing the page.
        report.Height = (double)window.FindResource("Size.TextArea");
        var content = (FrameworkElement)window.Content;
        var width = content.ActualWidth > 0 ? content.ActualWidth + content.Margin.Left + content.Margin.Right : window.Width;
        ApplyCompactSettings(window, width);
    }

    /// <summary>Also used by finite layout passes that do not create an HWND.</summary>
    public static void Apply(Window window, double clientWidth, double clientHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!double.IsFinite(clientWidth) || !double.IsFinite(clientHeight) || clientWidth <= 0 || clientHeight <= 0)
            return;
        var report = (TextBox)window.FindName("BugReportMessageTextBox");
        report.Height = (double)window.FindResource("Size.TextArea");
        ApplyCompactSettings(window, clientWidth);
    }

    private static void ApplyCompactSettings(Window window, double clientWidth)
    {
        var compact = clientWidth < (double)window.FindResource("Size.CompactBreakpoint");
        var labelWidth = (GridLength)window.FindResource(compact ? "Size.CompactLabelColumn" : "Size.LabelColumn");
        foreach (var name in new[] { "NameSettingsRow", "BuildSettingsRow" })
            ((Grid)window.FindName(name)).ColumnDefinitions[0].Width = labelWidth;
        var edit = (Button)window.FindName("ChangePlayerNameButton");
        edit.Width = (double)window.FindResource(compact ? "Size.ButtonMin" : "Size.ButtonMax");
        edit.Padding = (Thickness)window.FindResource(compact ? "Pad.Small" : "Pad.Control");
        ((TextBox)window.FindName("PlayerNameTextBox")).MinWidth =
            (double)window.FindResource(compact ? "Size.CompactNameField" : "Size.ButtonMin");
    }
}
