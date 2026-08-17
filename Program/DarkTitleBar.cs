using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Minecraft;

/// <summary>
/// Asks Windows to draw a window's title bar in its dark colours. The window
/// itself is painted by us; the bar above it is painted by the OS, and a
/// white bar over an obsidian window is the one seam the theme cannot close
/// from XAML. Windows 10 20H1 and later know the attribute; older ones ignore
/// it and keep their light bar, which is no worse than before.
/// </summary>
internal static class DarkTitleBar
{
    // The attribute moved between builds: 20 since Windows 10 20H1, 19 before it.
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>Applies once the window has a handle; safe to call before it has one.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            Apply(handle);
            return;
        }
        window.SourceInitialized += (_, _) => Apply(new WindowInteropHelper(window).Handle);
    }

    private static void Apply(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var dark = 1;
        // Either attribute number may be the one this build understands; a
        // refused call is just a light title bar.
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
