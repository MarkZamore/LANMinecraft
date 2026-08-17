using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace Minecraft;

public sealed class WindowPlacementService
{
    // A cache generation, not a data format: it is bumped to throw the cached
    // work away and redo it, so it is deliberately independent of
    // PortableFormat's version - a release must not cost every player a
    // re-download for an unrelated change.
    private const int CacheGeneration = 1;
    private const uint ShowNormal = 1;
    private const uint ShowMaximized = 3;
    private const uint RestoreToMaximized = 0x0002;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int WmSizing = 0x0214;
    private const int SizingLeft = 1;
    private const int SizingRight = 2;
    private const int SizingTop = 3;
    private const int SizingTopLeft = 4;
    private const int SizingTopRight = 5;
    private const int SizingBottom = 6;
    private const int SizingBottomLeft = 7;
    private const int GwlStyle = -16;
    private const int WsMaximizeBox = 0x00010000;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private readonly string _placementFile;
    private double _clientAspect = 1;
    private int _chromeWidth;
    private int _chromeHeight;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public WindowPlacementService(AppPaths paths)
    {
        _placementFile = paths.WindowPlacementFile;
    }

    /// <summary>
    /// Restores where the window was and keeps its shape. The canvas behind the
    /// Viewbox has one shape, and any other is empty bands, so the window is held
    /// to that ratio while it is dragged.
    /// </summary>
    public void Apply(Window window, double clientAspect)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!double.IsFinite(clientAspect) || clientAspect <= 0) clientAspect = 1;
        _clientAspect = clientAspect;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var saved = TryRead();
        window.SourceInitialized += (_, _) =>
        {
            KeepAspect(window);
            if (saved is not null) ApplyAfterSourceInitialized(window, saved);
        };
    }

    /// <summary>
    /// Holds the window to the shape of the canvas while it is dragged, and takes
    /// the maximise button away - a maximised window is the shape of the screen.
    /// </summary>
    private void KeepAspect(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowLong(handle, GwlStyle, GetWindowLong(handle, GwlStyle) & ~WsMaximizeBox);
        MeasureChrome(handle);
        if (!GetWindowRect(handle, out var outer)) return;
        // The size in the markup is a starting point; the ratio settles the rest.
        var fitted = FitOuterSize(
            outer.Right - outer.Left, outer.Bottom - outer.Top,
            SizingRight, _clientAspect, _chromeWidth, _chromeHeight);
        SetWindowPos(handle, IntPtr.Zero, 0, 0, fitted.Width, fitted.Height, SwpNoMove | SwpNoZOrder);
        HwndSource.FromHwnd(handle)?.AddHook(AspectSizingHook);
    }

    /// <summary>
    /// The outer size whose client area keeps the ratio. The edge being dragged
    /// decides which side leads: a vertical edge sets the height, a horizontal
    /// one sets the width, and a corner follows the width.
    /// </summary>
    internal static (int Width, int Height) FitOuterSize(
        int width, int height, int edge, double clientAspect, int chromeWidth, int chromeHeight)
    {
        if (!double.IsFinite(clientAspect) || clientAspect <= 0) return (width, height);
        if (edge is SizingTop or SizingBottom)
        {
            var clientHeight = Math.Max(1, height - chromeHeight);
            return ((int)Math.Round(clientHeight * clientAspect) + chromeWidth, height);
        }

        var clientWidth = Math.Max(1, width - chromeWidth);
        return (width, (int)Math.Round(clientWidth / clientAspect) + chromeHeight);
    }

    private IntPtr AspectSizingHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmSizing) return IntPtr.Zero;
        var rect = Marshal.PtrToStructure<NativeRect>(lParam);
        var edge = (int)wParam;
        var fitted = FitOuterSize(
            rect.Right - rect.Left, rect.Bottom - rect.Top, edge, _clientAspect, _chromeWidth, _chromeHeight);
        if (fitted.Width == rect.Right - rect.Left && fitted.Height == rect.Bottom - rect.Top) return IntPtr.Zero;

        // Grow or shrink away from the edge being dragged, so the opposite
        // edge - the one the player is not holding - is the one that moves.
        if (edge is SizingLeft or SizingTopLeft or SizingBottomLeft) rect.Left = rect.Right - fitted.Width;
        else rect.Right = rect.Left + fitted.Width;
        if (edge is SizingTop or SizingTopLeft or SizingTopRight) rect.Top = rect.Bottom - fitted.Height;
        else rect.Bottom = rect.Top + fitted.Height;
        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return (IntPtr)1;
    }

    /// <summary>Title bar and borders: the part the client area does not get.</summary>
    private void MeasureChrome(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var outer) || !GetClientRect(handle, out var client)) return;
        _chromeWidth = (outer.Right - outer.Left) - (client.Right - client.Left);
        _chromeHeight = (outer.Bottom - outer.Top) - (client.Bottom - client.Top);
    }

    public void Save(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var placement = WindowPlacement.Create();
            if (!GetWindowPlacement(handle, ref placement))
            {
                return;
            }

            var bounds = placement.NormalPosition;
            if (!IsValid(bounds))
            {
                return;
            }

            var restoreMaximized = placement.ShowCommand == ShowMaximized ||
                                   (IsMinimized(placement.ShowCommand) &&
                                    (placement.Flags & RestoreToMaximized) != 0);
            var state = new SavedWindowPlacement
            {
                CacheGeneration = CacheGeneration,
                Left = bounds.Left,
                Top = bounds.Top,
                Right = bounds.Right,
                Bottom = bounds.Bottom,
                Maximized = restoreMaximized
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_placementFile)!);
            AtomicFile.WriteAllText(_placementFile, JsonSerializer.Serialize(state, _jsonOptions));
        }
        catch
        {
            // Window placement must never block application shutdown.
        }
    }

    private void ApplyAfterSourceInitialized(Window window, SavedWindowPlacement saved)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var dpiScale = Math.Max(1d, GetDpiForWindow(handle) / 96d);
            // A placement saved under an older shape comes back in this one.
            var fitted = FitOuterSize(
                saved.Right - saved.Left, saved.Bottom - saved.Top,
                SizingRight, _clientAspect, _chromeWidth, _chromeHeight);
            var bounds = ClampToNearestWorkArea(new NativeRect
            {
                Left = saved.Left,
                Top = saved.Top,
                Right = saved.Left + fitted.Width,
                Bottom = saved.Top + fitted.Height
            },
            (int)Math.Ceiling(window.MinWidth * dpiScale),
            (int)Math.Ceiling(window.MinHeight * dpiScale));
            if (!IsValid(bounds))
            {
                return;
            }

            // The clamp trims each side on its own, so the shape is settled once
            // more afterwards: the height follows the width, unless that is what
            // did not fit, in which case the width follows the height.
            var height = bounds.Bottom - bounds.Top;
            var shaped = FitOuterSize(
                bounds.Right - bounds.Left, height, SizingRight, _clientAspect, _chromeWidth, _chromeHeight);
            if (shaped.Height > height)
            {
                shaped = FitOuterSize(
                    shaped.Width, height, SizingBottom, _clientAspect, _chromeWidth, _chromeHeight);
            }
            bounds.Right = bounds.Left + shaped.Width;
            bounds.Bottom = bounds.Top + shaped.Height;

            var placement = WindowPlacement.Create();
            // A maximised window is the shape of the screen, not of the canvas;
            // it comes back at its normal size.
            placement.ShowCommand = ShowNormal;
            placement.NormalPosition = bounds;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            SetWindowPlacement(handle, ref placement);
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
            if (!File.Exists(_placementFile))
            {
                return null;
            }

            var saved = JsonSerializer.Deserialize<SavedWindowPlacement>(
                File.ReadAllText(_placementFile),
                _jsonOptions);
            if (saved is null || saved.CacheGeneration != CacheGeneration)
            {
                return null;
            }

            var bounds = new NativeRect
            {
                Left = saved.Left,
                Top = saved.Top,
                Right = saved.Right,
                Bottom = saved.Bottom
            };
            return IsValid(bounds) ? saved : null;
        }
        catch
        {
            return null;
        }
    }

    private static NativeRect ClampToNearestWorkArea(NativeRect bounds, int minimumWidth, int minimumHeight)
    {
        var monitor = MonitorFromRect(ref bounds, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return bounds;
        }

        var monitorInfo = MonitorInfo.Create();
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return bounds;
        }

        var workWidth = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        var workHeight = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        var width = Math.Clamp(bounds.Right - bounds.Left, Math.Min(minimumWidth, workWidth), workWidth);
        var height = Math.Clamp(bounds.Bottom - bounds.Top, Math.Min(minimumHeight, workHeight), workHeight);
        var left = Math.Clamp(bounds.Left, monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Right - width);
        var top = Math.Clamp(bounds.Top, monitorInfo.WorkArea.Top, monitorInfo.WorkArea.Bottom - height);
        return new NativeRect
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        };
    }

    private static bool IsValid(NativeRect bounds) =>
        bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
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

        public static WindowPlacement Create() => new()
        {
            Length = (uint)Marshal.SizeOf<WindowPlacement>()
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;

        public static MonitorInfo Create() => new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
    }

    private sealed class SavedWindowPlacement
    {
        public int CacheGeneration { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public bool Maximized { get; set; }
    }
}
