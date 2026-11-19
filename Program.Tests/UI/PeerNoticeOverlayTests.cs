using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Minecraft.Tests;

[Collection("Native desktop focus")]
public sealed class PeerNoticeOverlayTests
{
    [Fact]
    public void PresentationChoiceUsesTheWholeDisplayRatherThanGameVersionOrWorkArea()
    {
        var monitor = new Rect(-2560, -240, 2560, 1440);
        Assert.True(PeerNoticeNative.UsesFullscreenPresentation(monitor, monitor, hasCaption: false));
        Assert.True(PeerNoticeNative.UsesFullscreenPresentation(new Rect(-2559, -240, 2559, 1440), monitor, false));
        Assert.False(PeerNoticeNative.UsesFullscreenPresentation(monitor, monitor, hasCaption: true));
        Assert.False(PeerNoticeNative.UsesFullscreenPresentation(new Rect(-2400, -200, 1000, 700), monitor, false));
        Assert.False(PeerNoticeNative.UsesFullscreenPresentation(new Rect(-2560, -240, 2560, 1400), monitor, false));
        Assert.False(PeerNoticeNative.UsesFullscreenPresentation(Rect.Empty, monitor, false));
        Assert.False(PeerNoticeNative.UsesFullscreenPresentation(monitor, Rect.Empty, false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void NativeStack_KeepsNegativeMonitorCoordinatesAndPhysicalDpiGaps(double scale)
    {
        var area = new Rect(-2560, -240, 2560, 1440);
        var sizes = new[] { new Size(320 * scale, 70 * scale), new Size(320 * scale, 100 * scale) };
        var positions = PeerNoticeNative.StackPositions(area, sizes, 24 * scale);
        Assert.Equal(area.Left + 24 * scale, positions[1].X);
        Assert.Equal(area.Bottom - 24 * scale - sizes[1].Height, positions[1].Y);
        Assert.Equal(positions[1].Y - 24 * scale - sizes[0].Height, positions[0].Y);
        for (var index = 0; index < sizes.Length; index++)
            Assert.True(area.Contains(new Rect(positions[index], sizes[index])));
    }

    [Fact]
    public void Toasts_StayVisibleAboveAMockWhenLauncherMinimizesWithoutTakingFocus()
    {
        foreach (var gameRunning in new[] { true, false })
        foreach (var fullscreen in new[] { true, false })
        WindowCanvasTests.OnAStaThread(() =>
        {
            var owner = WindowCanvasTests.LoadWindow();
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            owner.Left = -32000;
            owner.Top = -32000;
            owner.ShowActivated = false;
            owner.ShowInTaskbar = false;
            var mock = new Window
            {
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
                Topmost = true, Background = Brushes.Transparent, AllowsTransparency = true,
                Opacity = 0, Width = 800, Height = 600
            };
            PeerNoticeService? service = null;
            try
            {
                owner.Show();
                mock.Show();
                var mockHandle = new WindowInteropHelper(mock).Handle;
                var monitors = new List<IntPtr>();
                Assert.True(EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                    (IntPtr handle, IntPtr _, ref NativeRect rectangle, IntPtr data) => { monitors.Add(handle); return true; }, IntPtr.Zero));
                var target = monitors[^1];
                Assert.True(PeerNoticeNative.TryWorkArea(target, out var area));
                Assert.True(PeerNoticeNative.TryDisplayArea(target, out var display));
                Assert.True(PeerNoticeNative.TryWorkArea(monitors[0], out var ownerArea));
                Assert.True(SetWindowPos(new WindowInteropHelper(owner).Handle, IntPtr.Zero,
                    (int)ownerArea.Left, (int)ownerArea.Top, 400, 300, 0x0010));
                Assert.True(SetWindowPos(mockHandle, new IntPtr(-1),
                    (int)(fullscreen ? display.Left : area.Left + area.Width / 2), (int)(fullscreen ? display.Top : area.Top),
                    (int)(fullscreen ? display.Width : area.Width / 2), (int)(fullscreen ? display.Height : area.Height), 0x0010));
                Assert.Equal(fullscreen, PeerNoticeNative.IsFullscreenWindow(mockHandle));
                var foregroundBefore = PeerNoticeNative.GetForegroundWindow();
                var gameHandle = gameRunning ? mockHandle : IntPtr.Zero;
                service = new PeerNoticeService(owner, null, () => gameHandle);
                if (!gameRunning) area = ownerArea;
                service.Show(new PeerNotice("Друг", "Зашёл в сборку"));
                Frame(60);
                var first = Assert.Single(Notices());
                var firstHandle = new WindowInteropHelper(first).Handle;
                Assert.Null(first.Owner);
                Assert.Equal(IntPtr.Zero, GetWindow(firstHandle, 4));
                var styles = GetWindowLongPtr(firstHandle, -20).ToInt64();
                Assert.NotEqual(0, styles & 0x08000000); // WS_EX_NOACTIVATE
                Assert.NotEqual(0, styles & 0x00000080); // WS_EX_TOOLWINDOW
                Assert.NotEqual(0, styles & 0x00000008); // WS_EX_TOPMOST
                Assert.Equal(0, styles & 0x00040000); // no taskbar application window
                Assert.Equal(new IntPtr(3), SendMessage(firstHandle, 0x21, mockHandle, IntPtr.Zero));
                Assert.Equal(foregroundBefore, PeerNoticeNative.GetForegroundWindow());
                Assert.True(PeerNoticeNative.TryBounds(firstHandle, out var bounds));
                Assert.True(area.Contains(bounds));
                var gap = 24 * PeerNoticeNative.DpiScale(firstHandle);
                Assert.InRange(Math.Abs(bounds.Left - area.Left - gap), 0, 1);
                Assert.InRange(Math.Abs(area.Bottom - bounds.Bottom - gap), 0, 1);
                Assert.True(IsAbove(firstHandle, mockHandle));

                if (gameRunning)
                {
                    if (!fullscreen)
                    {
                        Assert.True(PeerNoticeNative.TryBounds(mockHandle, out var gameBounds));
                        Assert.True(bounds.Left < gameBounds.Left); // display anchored, outside the small game window
                    }
                    mock.WindowState = WindowState.Minimized;
                    Frame(40);
                    Assert.False(PeerNoticeNative.IsFullscreenWindow(mockHandle));
                    Assert.Equal(target, PeerNoticeNative.SelectMonitor(mockHandle, new WindowInteropHelper(owner).Handle));
                    Assert.True(IsWindowVisible(firstHandle));
                    mock.WindowState = WindowState.Normal;
                    Frame(30);
                }

                // Already visible toasts must return to the launcher when the game closes,
                // even while another application keeps desktop focus.
                gameHandle = IntPtr.Zero;
                Frame(350);
                Assert.True(PeerNoticeNative.TryBounds(firstHandle, out var returnedBounds));
                Assert.True(ownerArea.Contains(returnedBounds));
                Assert.Equal(PeerNoticeNative.SelectMonitor(IntPtr.Zero, new WindowInteropHelper(owner).Handle),
                    PeerNoticeNative.SelectMonitor(new IntPtr(-123), new WindowInteropHelper(owner).Handle));
                Assert.Equal(IntPtr.Zero, PeerNoticeNative.FindGameWindow([]));

                owner.WindowState = WindowState.Minimized;
                Frame(30);
                Assert.True(IsWindowVisible(firstHandle));
                foregroundBefore = PeerNoticeNative.GetForegroundWindow();
                service.Show(new PeerNotice("Второй", "Зашёл в LANMinecraft"));
                Frame(30);
                Assert.Equal(2, Notices().Length);
                Assert.All(Notices(), toast => Assert.True(IsWindowVisible(new WindowInteropHelper(toast).Handle)));
                Assert.Equal(foregroundBefore, PeerNoticeNative.GetForegroundWindow());
                Assert.Equal(WindowState.Minimized, owner.WindowState);

                service.Show(new PeerNotice("Третий", "Зашёл в LANMinecraft"));
                service.Show(new PeerNotice("Четвёртый", "Зашёл в LANMinecraft"));
                var handles = Notices().Select(toast => new WindowInteropHelper(toast).Handle).ToArray();
                service.Dispose();
                service = null;
                Assert.All(handles, handle => Assert.False(IsWindow(handle)));
                Assert.Empty(Notices());
                return true;
            }
            finally
            {
                service?.Dispose();
                mock.Close();
                owner.Close();
            }
        });
    }

    private static PeerNoticeWindow[] Notices() => PresentationSource.CurrentSources.OfType<HwndSource>()
        .Select(source => source.RootVisual).OfType<PeerNoticeWindow>().ToArray();

    private static bool IsAbove(IntPtr notice, IntPtr mock)
    {
        for (var handle = GetTopWindow(IntPtr.Zero); handle != IntPtr.Zero; handle = GetWindow(handle, 2))
        {
            if (handle == notice) return true;
            if (handle == mock) return false;
        }
        return false;
    }

    private static void Frame(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += Stop;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= Stop; }
        void Stop(object? sender, EventArgs e) => frame.Continue = false;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}

// The foreground-window assertion must not race other collections' real WPF
// windows. Keep every focus/style assertion; only isolate their desktop use.
[CollectionDefinition("Native desktop focus", DisableParallelization = true)]
public sealed class NativeDesktopFocusCollection { }
