using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Minecraft;

/// <summary>Reads Windows colours and supplies native dark chrome without persisting settings.</summary>
internal static class WindowsThemeService
{
    private const int DarkModeAttribute = 20;
    private const int SystemBackdropAttribute = 38;
    private static readonly ConditionalWeakTable<Window, WindowThemeAttachment> Attachments = new();
    private static bool _initialized;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeColor { public byte A, R, G, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetColorValue(IntPtr self, int type, out NativeColor color);

    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string text, uint length, out IntPtr value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(IntPtr name, out IntPtr instance);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    public static void Initialize(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        RefreshAccent(application);
        ControlMotion.Initialize();
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(WindowLoaded));
    }

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && e.OriginalSource == window &&
            window.GetType().Assembly == typeof(App).Assembly && !window.AllowsTransparency)
            Attachments.GetValue(window, static owner => new WindowThemeAttachment(owner));
    }

    internal static void RefreshAccent(Application application)
    {
        var fill = (Color)application.Resources["Color.Accent"];
        var text = (Color)application.Resources["Color.AccentText"];
        IntPtr name = IntPtr.Zero, instance = IntPtr.Zero, settings = IntPtr.Zero;
        var initialized = -1;
        try
        {
            initialized = RoInitialize(0);
            const string className = "Windows.UI.ViewManagement.UISettings";
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out name));
            Marshal.ThrowExceptionForHR(RoActivateInstance(name, out instance));
            var interfaceId = new Guid("03021be4-5254-4781-8194-5168f7d06d7b");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(instance, in interfaceId, out settings));
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(settings), 6 * IntPtr.Size);
            var readColor = Marshal.GetDelegateForFunctionPointer<GetColorValue>(method);
            Marshal.ThrowExceptionForHR(readColor(settings, 7, out var light2));
            Marshal.ThrowExceptionForHR(readColor(settings, 8, out var light3));
            fill = Color.FromArgb(light2.A, light2.R, light2.G, light2.B);
            text = Color.FromArgb(light3.A, light3.R, light3.G, light3.B);
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            // The named Fluent fallback also covers Windows versions without UISettings.
        }
        finally
        {
            if (settings != IntPtr.Zero) Marshal.Release(settings);
            if (instance != IntPtr.Zero) Marshal.Release(instance);
            if (name != IntPtr.Zero) _ = WindowsDeleteString(name);
            if (initialized >= 0) RoUninitialize();
        }

        SetColour(application, "Accent", fill);
        SetColour(application, "AccentText", text);
        SetColour(application, "AccentHover", Blend(fill, Colors.White, 0.08));
        SetColour(application, "AccentPressed", Blend(fill, Colors.Black, 0.12));
    }

    private static Color Blend(Color color, Color toward, double fraction) => Color.FromRgb(
        (byte)(color.R + (toward.R - color.R) * fraction),
        (byte)(color.G + (toward.G - color.G) * fraction),
        (byte)(color.B + (toward.B - color.B) * fraction));

    private static void SetColour(Application application, string name, Color color)
    {
        application.Resources["Color." + name] = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        application.Resources["Brush." + name] = brush;
    }

    private sealed class WindowThemeAttachment
    {
        private readonly Window _window;
        private readonly HwndSource? _source;

        public WindowThemeAttachment(Window window)
        {
            _window = window;
            var handle = new WindowInteropHelper(window).Handle;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowMessage);
            window.Closed += WindowClosed;
            ApplyChrome(handle);
        }

        private void ApplyChrome(IntPtr handle)
        {
            var available = false;
            try
            {
                var dark = 1;
                _ = DwmSetWindowAttribute(handle, DarkModeAttribute, ref dark, sizeof(int));
                // MAINWINDOW covers the whole window; extending glass creates a mismatched caption band.
                var margins = new Margins();
                var mica = 2;
                available = DwmExtendFrameIntoClientArea(handle, ref margins) == 0 &&
                    DwmSetWindowAttribute(handle, SystemBackdropAttribute, ref mica, sizeof(int)) == 0;
                if (!available)
                {
                    var none = 1;
                    _ = DwmSetWindowAttribute(handle, SystemBackdropAttribute, ref none, sizeof(int));
                }
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                // Native chrome stays usable when DWM materials are unavailable.
            }
            if (_source?.CompositionTarget is { } target)
                target.BackgroundColor = available ? Colors.Transparent : (Color)_window.FindResource("Color.Canvas");
        }

        private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message is 0x320 or 0x1A && Application.Current is { } application)
                RefreshAccent(application);
            if (message == 0x31E) ApplyChrome(handle);
            return IntPtr.Zero;
        }

        private void WindowClosed(object? sender, EventArgs e)
        {
            _window.Closed -= WindowClosed;
            _source?.RemoveHook(WindowMessage);
            Attachments.Remove(_window);
        }
    }
}
