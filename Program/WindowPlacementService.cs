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
    private readonly string _placementFile;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public WindowPlacementService(AppPaths paths)
    {
        _placementFile = paths.WindowPlacementFile;
    }

    public void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var saved = TryRead();
        window.SourceInitialized += (_, _) =>
        {
            KeepSquare(window);
            if (saved is not null) ApplyAfterSourceInitialized(window, saved);
        };
    }

    /// <summary>
    /// The window is a square: the canvas behind its Viewbox is square, so any
    /// other shape is empty bands. Resizing from an edge sets the other side to
    /// match; from a corner the larger side wins. Maximising cannot be square,
    /// so its button goes.
    /// </summary>
    private static void KeepSquare(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, GwlStyle);
        SetWindowLong(handle, GwlStyle, style & ~WsMaximizeBox);
        HwndSource.FromHwnd(handle)?.AddHook(SquareSizingHook);
    }

    private static IntPtr SquareSizingHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmSizing) return IntPtr.Zero;
        var rect = Marshal.PtrToStructure<NativeRect>(lParam);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width == height) return IntPtr.Zero;

        var edge = (int)wParam;
        var side = edge switch
        {
            SizingLeft or SizingRight => width,
            SizingTop or SizingBottom => height,
            _ => Math.Max(width, height)
        };
        // Grow or shrink away from the edge being dragged, so the opposite
        // edge - the one the player is not holding - is the one that moves.
        if (edge is SizingLeft or SizingTopLeft or SizingBottomLeft) rect.Left = rect.Right - side;
        else rect.Right = rect.Left + side;
        if (edge is SizingTop or SizingTopLeft or SizingTopRight) rect.Top = rect.Bottom - side;
        else rect.Bottom = rect.Top + side;
        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return (IntPtr)1;
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
            var side = Math.Min(saved.Right - saved.Left, saved.Bottom - saved.Top);
            var bounds = ClampToNearestWorkArea(new NativeRect
            {
                Left = saved.Left,
                Top = saved.Top,
                Right = saved.Left + side,
                Bottom = saved.Top + side
            },
            (int)Math.Ceiling(window.MinWidth * dpiScale),
            (int)Math.Ceiling(window.MinHeight * dpiScale));
            if (!IsValid(bounds))
            {
                return;
            }

            var placement = WindowPlacement.Create();
            // A maximised window is not square; it comes back at its normal size.
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
