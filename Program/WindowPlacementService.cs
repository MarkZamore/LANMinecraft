using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace Minecraft;

public sealed class WindowPlacementService
{
    // Keep the old placement file: freely resizable windows can use the same
    // rectangle that the earlier, fixed-aspect window left behind.
    private const int CacheGeneration = 1;
    private const uint ShowHidden = 0;
    private const uint ShowNormal = 1;
    private const uint ShowMaximized = 3;
    private const uint RestoreToMaximized = 0x0002;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint DefaultDpi = 96;
    private readonly string _placementFile;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public WindowPlacementService(AppPaths paths)
    {
        _placementFile = paths.WindowPlacementFile;
    }

    /// <summary>
    /// Restores the window's independent dimensions and state. Windows keeps
    /// ownership of resizing, maximising and Snap through its normal frame.
    /// </summary>
    public void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var saved = TryRead();
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyAfterSourceInitialized(window, saved);
        }
        else
        {
            window.SourceInitialized += (_, _) => ApplyAfterSourceInitialized(window, saved);
        }
    }

    public void Save(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var placement = WindowPlacement.Create();
            if (!GetWindowPlacement(handle, ref placement)) return;
            var bounds = placement.NormalPosition;
            var restoreMaximized = placement.ShowCommand == ShowMaximized ||
                                   (IsMinimized(placement.ShowCommand) &&
                                    (placement.Flags & RestoreToMaximized) != 0);

            // Snap can keep a separate restore rectangle. Remember the size the
            // player actually sees, while a maximised/minimised window keeps
            // its restore bounds rather than the monitor or an icon rectangle.
            if (!restoreMaximized && !IsMinimized(placement.ShowCommand) &&
                GetWindowRect(handle, out var screenBounds) &&
                TryGetMonitor(screenBounds, out _, out var monitorInfo))
            {
                bounds = ToWorkspaceCoordinates(screenBounds, monitorInfo.MonitorArea, monitorInfo.WorkArea);
            }
            if (!IsValid(bounds)) return;

            var state = new SavedWindowPlacement
            {
                CacheGeneration = CacheGeneration,
                Left = bounds.Left,
                Top = bounds.Top,
                Right = bounds.Right,
                Bottom = bounds.Bottom,
                Maximized = restoreMaximized
            };
            AtomicFile.WriteAllTextIfChanged(_placementFile, JsonSerializer.Serialize(state, _jsonOptions));
        }
        catch
        {
            // Window placement must never block application shutdown.
        }
    }

    private static void ApplyAfterSourceInitialized(Window window, SavedWindowPlacement? saved)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            NativeRect bounds;
            if (saved is not null)
            {
                bounds = saved.Bounds;
            }
            else if (!GetWindowRect(handle, out bounds))
            {
                return;
            }
            if (!TryGetMonitor(bounds, out var monitor, out var monitorInfo)) return;

            // WINDOWPLACEMENT uses workspace coordinates. Monitor work areas
            // use screen coordinates, so compare only after converting, and
            // convert back before handing the result to SetWindowPlacement.
            if (saved is not null)
            {
                bounds = ToScreenCoordinates(bounds, monitorInfo.MonitorArea, monitorInfo.WorkArea);
            }
            var dpi = DpiOf(monitor, handle);
            bounds = ClampToWorkArea(bounds, monitorInfo.WorkArea,
                MinimumPixels(window.MinWidth, dpi), MinimumPixels(window.MinHeight, dpi));
            if (saved is null)
            {
                bounds = CenterInWorkArea(bounds, monitorInfo.WorkArea);
            }
            if (!IsValid(bounds)) return;

            var placement = WindowPlacement.Create();
            // SourceInitialized runs before WPF shows the window. Do not show
            // or activate it early; WPF applies WindowState when Show follows.
            placement.ShowCommand = window.IsVisible
                ? saved?.Maximized == true ? ShowMaximized : ShowNormal
                : ShowHidden;
            placement.NormalPosition = ToWorkspaceCoordinates(bounds, monitorInfo.MonitorArea, monitorInfo.WorkArea);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (!SetWindowPlacement(handle, ref placement))
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }
            window.WindowState = saved?.Maximized == true ? WindowState.Maximized : WindowState.Normal;
        }
        catch
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private SavedWindowPlacement? TryRead()
    {
        try
        {
            if (!File.Exists(_placementFile)) return null;
            var saved = JsonSerializer.Deserialize<SavedWindowPlacement>(File.ReadAllText(_placementFile), _jsonOptions);
            return saved is not null && saved.CacheGeneration == CacheGeneration && IsValid(saved.Bounds)
                ? saved
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetMonitor(NativeRect bounds, out IntPtr monitor, out MonitorInfo info)
    {
        monitor = MonitorFromRect(ref bounds, MonitorDefaultToNearest);
        info = MonitorInfo.Create();
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static uint DpiOf(IntPtr monitor, IntPtr window)
    {
        // The source HWND may still be on the first monitor. Use the monitor
        // receiving the restored window, including when its old screen is gone.
        if (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) return dpi;
        dpi = GetDpiForWindow(window);
        return dpi > 0 ? dpi : DefaultDpi;
    }

    internal static int MinimumPixels(double minimumDip, uint dpi) =>
        (int)Math.Clamp(Math.Ceiling(minimumDip * (dpi > 0 ? dpi : DefaultDpi) / DefaultDpi), 1, int.MaxValue);

    internal static NativeRect ClampToWorkArea(NativeRect bounds, NativeRect workArea, int minimumWidth, int minimumHeight)
    {
        if (!IsValid(workArea)) return bounds;
        var workWidth = (long)workArea.Right - workArea.Left;
        var workHeight = (long)workArea.Bottom - workArea.Top;
        var width = Math.Clamp((long)bounds.Right - bounds.Left, Math.Clamp(minimumWidth, 1L, workWidth), workWidth);
        var height = Math.Clamp((long)bounds.Bottom - bounds.Top, Math.Clamp(minimumHeight, 1L, workHeight), workHeight);
        var left = Math.Clamp(bounds.Left, workArea.Left, workArea.Right - width);
        var top = Math.Clamp(bounds.Top, workArea.Top, workArea.Bottom - height);
        return new NativeRect
        {
            Left = (int)left,
            Top = (int)top,
            Right = (int)(left + width),
            Bottom = (int)(top + height)
        };
    }

    private static NativeRect CenterInWorkArea(NativeRect bounds, NativeRect workArea)
    {
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var left = workArea.Left + (workArea.Right - workArea.Left - width) / 2;
        var top = workArea.Top + (workArea.Bottom - workArea.Top - height) / 2;
        return new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    internal static NativeRect ToScreenCoordinates(NativeRect bounds, NativeRect monitorArea, NativeRect workArea) =>
        Offset(bounds, workArea.Left - monitorArea.Left, workArea.Top - monitorArea.Top);

    internal static NativeRect ToWorkspaceCoordinates(NativeRect bounds, NativeRect monitorArea, NativeRect workArea) =>
        Offset(bounds, monitorArea.Left - workArea.Left, monitorArea.Top - workArea.Top);

    private static NativeRect Offset(NativeRect bounds, int x, int y) => new()
    {
        Left = checked(bounds.Left + x),
        Top = checked(bounds.Top + y),
        Right = checked(bounds.Right + x),
        Bottom = checked(bounds.Bottom + y)
    };

    private static bool IsValid(NativeRect bounds) => bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    private static bool IsMinimized(uint showCommand) => showCommand is 2 or 6 or 7 or 11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rectangle, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;

        public static WindowPlacement Create() => new() { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;

        public static MonitorInfo Create() => new() { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
    }

    private sealed class SavedWindowPlacement
    {
        public int CacheGeneration { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public bool Maximized { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public NativeRect Bounds => new() { Left = Left, Top = Top, Right = Right, Bottom = Bottom };
    }
}
