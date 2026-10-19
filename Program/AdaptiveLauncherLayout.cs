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
    }

    /// <summary>Also used by finite layout passes that do not create an HWND.</summary>
    public static void Apply(Window window, double clientWidth, double clientHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!double.IsFinite(clientWidth) || !double.IsFinite(clientHeight) || clientWidth <= 0 || clientHeight <= 0)
            return;
        Apply(window);
    }
}
