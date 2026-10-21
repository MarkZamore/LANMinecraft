using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Minecraft;

/// <summary>
/// Keeps dialogs and notices readable at the launcher's native text size and
/// within the current monitor's work area.
/// </summary>
internal static class DialogScale
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>
    /// The responsive launcher no longer scales its canvas. Windows supplies
    /// the monitor's DPI scaling for both windows; a narrow owner must not
    /// shrink a confirmation's text along with its width.
    /// </summary>
    public static void MatchOwner(Window dialog, FrameworkElement content)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(content);
        var handle = new WindowInteropHelper(dialog).Handle;
        if (handle == IntPtr.Zero) return;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = MonitorInfo.Create();
        if (!GetMonitorInfo(monitor, ref info) ||
            !GetWindowRect(handle, out var outer) ||
            !GetClientRect(handle, out var client)) return;

        var dpi = VisualTreeHelper.GetDpi(dialog);
        var chromeWidth = outer.Right - outer.Left - (client.Right - client.Left);
        var chromeHeight = outer.Bottom - outer.Top - (client.Bottom - client.Top);
        var available = new Size(
            (info.WorkArea.Right - info.WorkArea.Left - chromeWidth) / dpi.DpiScaleX,
            (info.WorkArea.Bottom - info.WorkArea.Top - chromeHeight) / dpi.DpiScaleY);
        FitToClientArea(dialog, content, available);
        // CenterOwner can leave a dialog over a snapped owner's outer edge.
        // Settle SizeToContent first, then move only the finished window.
        dialog.UpdateLayout();
        if (!GetWindowRect(handle, out var fitted)) return;
        var position = ClampPosition(
            new Rect(fitted.Left, fitted.Top, fitted.Right - fitted.Left, fitted.Bottom - fitted.Top),
            new Rect(info.WorkArea.Left, info.WorkArea.Top,
                info.WorkArea.Right - info.WorkArea.Left, info.WorkArea.Bottom - info.WorkArea.Top));
        if (position.X != fitted.Left || position.Y != fitted.Top)
            _ = SetWindowPos(handle, IntPtr.Zero, (int)position.X, (int)position.Y, 0, 0,
                0x0001 | 0x0004 | 0x0010);
    }

    /// <summary>Screen coordinates; a monitor left of the primary may be negative.</summary>
    internal static Point ClampPosition(Rect bounds, Rect workArea) => new(
        Math.Clamp(bounds.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - bounds.Width)),
        Math.Clamp(bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - bounds.Height)));

    /// <summary>
    /// Reflows text before adding scrolling. Font tokens stay at their native
    /// size, including when an unusually large DPI leaves little work area.
    /// </summary>
    internal static void FitToClientArea(Window dialog, FrameworkElement content, Size available)
    {
        if (available.Width <= 0 || available.Height <= 0 ||
            !double.IsFinite(available.Width) || !double.IsFinite(available.Height)) return;

        var width = Math.Max(1, available.Width - content.Margin.Left - content.Margin.Right);
        content.MaxWidth = Math.Min(content.MaxWidth, width);
        if (!double.IsNaN(content.Width)) content.Width = Math.Min(content.Width, width);
        ReflowButtons(dialog, content, width);
        content.Measure(new Size(available.Width, double.PositiveInfinity));
        if (content.DesiredSize.Height <= available.Height) return;

        // Scroll the whole confirmation so its actions can always be reached.
        // The notification surface also fits here, without changing its fade
        // or the service's placement of notices in the screen corner.
        var scrollbar = dialog.TryFindResource("Size.Scrollbar") as double?
            ?? SystemParameters.VerticalScrollBarWidth;
        var scrollWidth = Math.Max(1, width - scrollbar);
        content.MaxWidth = scrollWidth;
        if (!double.IsNaN(content.Width)) content.Width = Math.Min(content.Width, scrollWidth);
        ReflowButtons(dialog, content, scrollWidth);
        if (dialog.Content is ScrollViewer existing && ReferenceEquals(existing.Content, content))
        {
            existing.MaxWidth = available.Width;
            existing.MaxHeight = available.Height;
            return;
        }
        dialog.Content = null;
        var viewer = new ScrollViewer
        {
            Content = content,
            MaxWidth = available.Width,
            MaxHeight = available.Height,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        SmoothScrollingController.Attach(viewer);
        dialog.Content = viewer;
    }

    private static void ReflowButtons(Window dialog, FrameworkElement content, double width)
    {
        if (content is not Panel panel) return;
        foreach (var actions in panel.Children.OfType<StackPanel>()
                     .Where(row => row.Orientation == Orientation.Horizontal &&
                                   row.Children.OfType<Button>().Any()))
        {
            actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (actions.DesiredSize.Width <= width) continue;
            actions.Orientation = Orientation.Vertical;
            actions.HorizontalAlignment = HorizontalAlignment.Stretch;
            var gap = dialog.TryFindResource("Gap.Above2") is Thickness spacing ? spacing : default;
            var first = true;
            foreach (var button in actions.Children.OfType<Button>())
            {
                button.Margin = first ? default : gap;
                first = false;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;

        public static MonitorInfo Create() => new() { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
    }
}
