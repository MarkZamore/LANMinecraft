using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Minecraft;

/// <summary>Shows independent toasts on the foreground application's monitor without activating a window.</summary>
public sealed class PeerNoticeService : IDisposable
{
    private const int MostAtOnce = 3;
    private readonly Window _owner;
    private readonly Logger? _logger;
    private readonly Func<IntPtr> _foregroundWindow;
    private readonly List<PeerNoticeWindow> _open = [];
    private readonly HashSet<PeerNoticeWindow> _retiring = [];
    private readonly DispatcherTimer _raiseTimer;
    private IntPtr _monitor;
    private bool _disposed;
    private bool _restacking;
    private bool _restackQueued;

    public PeerNoticeService(Window owner, Logger? logger = null)
        : this(owner, logger, PeerNoticeNative.GetForegroundWindow) { }

    internal PeerNoticeService(Window owner, Logger? logger, Func<IntPtr> foregroundWindow)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(foregroundWindow);
        _owner = owner;
        _logger = logger;
        _foregroundWindow = foregroundWindow;
        _raiseTimer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _raiseTimer.Tick += RaiseVisibleNotices;
    }

    public void Show(PeerNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (_disposed) return;
        PeerNoticeWindow? window = null;
        try
        {
            _monitor = PeerNoticeNative.SelectMonitor(_foregroundWindow(), new WindowInteropHelper(_owner).Handle);
            while (_open.Count >= MostAtOnce)
            {
                var oldest = _open[0];
                _open.RemoveAt(0);
                _retiring.Add(oldest);
                oldest.Dismiss();
            }
            window = new PeerNoticeWindow(_owner, notice);
            window.Closed += NoticeClosed;
            window.SizeChanged += NoticeSizeChanged;
            window.DpiChanged += NoticeDpiChanged;
            _open.Add(window);
            window.Show();
            PeerNoticeNative.MakeNonactivating(new WindowInteropHelper(window).Handle);
            Restack();
            _raiseTimer.Start();
        }
        catch (Exception ex)
        {
            if (window is not null)
            {
                _open.Remove(window);
                window.CloseNow();
            }
            _logger?.Warn($"Could not show a notice about another player: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _raiseTimer.Stop();
        _raiseTimer.Tick -= RaiseVisibleNotices;
        foreach (var window in _open.Concat(_retiring).ToArray()) window.CloseNow();
        _open.Clear();
        _retiring.Clear();
    }

    private void NoticeClosed(object? sender, EventArgs e)
    {
        if (sender is not PeerNoticeWindow window) return;
        window.Closed -= NoticeClosed;
        window.SizeChanged -= NoticeSizeChanged;
        window.DpiChanged -= NoticeDpiChanged;
        _open.Remove(window);
        _retiring.Remove(window);
        if (_open.Count == 0 && _retiring.Count == 0) _raiseTimer.Stop();
        QueueRestack();
    }

    private void RaiseVisibleNotices(object? sender, EventArgs e)
    {
        foreach (var window in _open.Concat(_retiring).ToArray())
            if (window.IsVisible) PeerNoticeNative.Raise(new WindowInteropHelper(window).Handle);
    }

    private void NoticeSizeChanged(object sender, SizeChangedEventArgs e) => QueueRestack();
    private void NoticeDpiChanged(object sender, DpiChangedEventArgs e) => QueueRestack();

    private void QueueRestack()
    {
        if (_disposed || _restackQueued) return;
        _restackQueued = true;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _restackQueued = false;
            if (!_disposed) Restack();
        }));
    }

    private void Restack()
    {
        if (_disposed || _restacking || _open.Count == 0) return;
        _restacking = true;
        try
        {
            if (!PeerNoticeNative.TryWorkArea(_monitor, out var area))
            {
                _monitor = PeerNoticeNative.SelectMonitor(IntPtr.Zero, new WindowInteropHelper(_owner).Handle);
                if (!PeerNoticeNative.TryWorkArea(_monitor, out area)) return;
            }
            var gap = _owner.TryFindResource("Gap.Canvas") is Thickness margin ? margin.Left : 0;
            var measurements = new List<(PeerNoticeWindow Window, IntPtr Handle, Size Size, double Gap)>();
            foreach (var window in _open)
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (!PeerNoticeNative.TryBounds(handle, out var bounds)) continue;
                // First move to the chosen monitor so WPF receives WM_DPICHANGED before fitting.
                PeerNoticeNative.Position(handle, area.Left, area.Top);
                var scale = PeerNoticeNative.DpiScale(handle);
                var pixelGap = Math.Round(gap * scale);
                var verticalGap = Math.Min(pixelGap, Math.Max(0, (area.Height - _open.Count) / (_open.Count + 1)));
                var horizontalGap = Math.Min(pixelGap, Math.Max(0, (area.Width - 1) / 2));
                window.FitToArea(new Size(Math.Max(1, area.Width - 2 * horizontalGap) / scale,
                    Math.Max(1, (area.Height - verticalGap * (_open.Count + 1)) / _open.Count) / scale));
                if (PeerNoticeNative.TryBounds(handle, out bounds))
                    measurements.Add((window, handle, bounds.Size, pixelGap));
            }
            var positions = PeerNoticeNative.StackPositions(area,
                measurements.Select(item => item.Size).ToArray(), measurements.Select(item => item.Gap).DefaultIfEmpty(0).Max());
            for (var index = measurements.Count - 1; index >= 0; index--)
                PeerNoticeNative.Position(measurements[index].Handle, positions[index].X, positions[index].Y);
        }
        finally { _restacking = false; }
    }
}

internal static class PeerNoticeNative
{
    private const int ExtendedStyleIndex = -20;
    private const long NoActivate = 0x08000000;
    private const long ToolWindow = 0x00000080;
    private const long AppWindow = 0x00040000;
    private static readonly IntPtr Topmost = new(-1);

    internal static void MakeNonactivating(IntPtr handle)
    {
        var style = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        _ = SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr((style | NoActivate | ToolWindow) & ~AppWindow));
        _ = SetWindowLongPtr(handle, -8, IntPtr.Zero); // GWLP_HWNDPARENT: no launcher/hidden WPF owner.
        _ = SetWindowPos(handle, Topmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020);
    }

    internal static IntPtr SelectMonitor(IntPtr foreground, IntPtr owner) =>
        MonitorFromWindow(foreground != IntPtr.Zero ? foreground : owner, 2);

    internal static bool TryWorkArea(IntPtr monitor, out Rect area)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) &&
            info.WorkArea.Right > info.WorkArea.Left && info.WorkArea.Bottom > info.WorkArea.Top)
        {
            area = new Rect(info.WorkArea.Left, info.WorkArea.Top,
                info.WorkArea.Right - info.WorkArea.Left, info.WorkArea.Bottom - info.WorkArea.Top);
            return true;
        }
        area = Rect.Empty;
        return false;
    }

    internal static bool TryBounds(IntPtr handle, out Rect bounds)
    {
        if (GetWindowRect(handle, out var rectangle))
        {
            bounds = new Rect(rectangle.Left, rectangle.Top,
                rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
            return true;
        }
        bounds = Rect.Empty;
        return false;
    }

    internal static double DpiScale(IntPtr handle)
    {
        var dpi = GetDpiForWindow(handle);
        return dpi > 0 ? dpi / 96d : 1;
    }

    internal static void Raise(IntPtr handle) =>
        _ = SetWindowPos(handle, Topmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

    internal static void Position(IntPtr handle, double x, double y) =>
        _ = SetWindowPos(handle, Topmost, (int)Math.Round(x), (int)Math.Round(y), 0, 0, 0x0001 | 0x0010);

    internal static Point[] StackPositions(Rect workArea, IReadOnlyList<Size> sizes, double gap)
    {
        var positions = new Point[sizes.Count];
        var verticalGap = Math.Min(gap, Math.Max(0, (workArea.Height - sizes.Sum(size => size.Height)) / (sizes.Count + 1)));
        var bottom = workArea.Bottom - verticalGap;
        for (var index = sizes.Count - 1; index >= 0; index--)
        {
            var width = sizes[index].Width;
            var height = sizes[index].Height;
            var horizontalGap = Math.Min(gap, Math.Max(0, workArea.Width - width));
            positions[index] = new Point(workArea.Left + horizontalGap, Math.Max(workArea.Top, bottom - height));
            bottom -= height + verticalGap;
        }
        return positions;
    }

    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo { public uint Size; public NativeRect MonitorArea, WorkArea; public uint Flags; }
}
