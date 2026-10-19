using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using NativeRect = Minecraft.WindowPlacementService.NativeRect;

namespace Minecraft.Tests;

public sealed class WindowPlacementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-window-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void Independent_dimensions_are_preserved_when_the_window_fits()
    {
        var bounds = Rect(120, 80, 760, 720);
        var result = WindowPlacementService.ClampToWorkArea(bounds, Rect(0, 0, 1920, 1040), 480, 300);

        Assert.Equal(bounds, result);
    }

    [Fact]
    public void Only_the_dimension_that_exceeds_the_work_area_is_shrunk()
    {
        var result = WindowPlacementService.ClampToWorkArea(Rect(10, 10, 810, 2010), Rect(0, 0, 1920, 1040), 480, 300);

        Assert.Equal(Rect(10, 0, 810, 1040), result);
    }

    [Fact]
    public void A_disconnected_monitors_rectangle_is_brought_back_into_the_work_area()
    {
        var result = WindowPlacementService.ClampToWorkArea(Rect(3000, -900, 3720, -360), Rect(-1920, 40, 0, 1080), 480, 300);

        Assert.Equal(Rect(-720, 40, 0, 580), result);
    }

    [Fact]
    public void A_work_area_smaller_than_the_minimum_still_contains_the_whole_window()
    {
        var result = WindowPlacementService.ClampToWorkArea(Rect(0, 0, 1000, 800), Rect(20, 40, 420, 240), 480, 300);

        Assert.Equal(Rect(20, 40, 420, 240), result);
    }

    [Fact]
    public void Extreme_saved_dimensions_do_not_overflow_the_clamp()
    {
        var result = WindowPlacementService.ClampToWorkArea(Rect(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue),
            Rect(0, 0, 1920, 1040), 480, 300);

        Assert.Equal(Rect(0, 0, 1920, 1040), result);
    }

    [Theory]
    [InlineData(480, 96, 480)]
    [InlineData(480, 120, 600)]
    [InlineData(320, 144, 480)]
    [InlineData(480.5, 120, 601)]
    public void Minimum_dimensions_use_the_destination_monitors_dpi(double dip, uint dpi, int expected)
    {
        Assert.Equal(expected, WindowPlacementService.MinimumPixels(dip, dpi));
    }

    [Theory]
    [InlineData(0, 0, 40, 24)]
    [InlineData(-1920, -1080, 40, 24)]
    [InlineData(1920, 0, 0, 24)]
    public void Workspace_conversion_includes_top_and_left_taskbars(int monitorLeft, int monitorTop, int taskbarWidth, int taskbarHeight)
    {
        var monitor = Rect(monitorLeft, monitorTop, monitorLeft + 1920, monitorTop + 1080);
        var work = Rect(monitorLeft + taskbarWidth, monitorTop + taskbarHeight, monitor.Right, monitor.Bottom);
        var workspace = Rect(monitorLeft + 100, monitorTop + 100, monitorLeft + 800, monitorTop + 600);

        var screen = WindowPlacementService.ToScreenCoordinates(workspace, monitor, work);

        Assert.Equal(Rect(workspace.Left + taskbarWidth, workspace.Top + taskbarHeight,
            workspace.Right + taskbarWidth, workspace.Bottom + taskbarHeight), screen);
        Assert.Equal(workspace, WindowPlacementService.ToWorkspaceCoordinates(screen, monitor, work));
    }

    [Fact]
    public void The_native_frame_keeps_maximise_and_does_not_force_a_sizing_ratio()
    {
        WithWindow(null, (_, handle, _) =>
        {
            var style = GetWindowLong(handle, -16);
            Assert.NotEqual(0, style & 0x00010000); // WS_MAXIMIZEBOX: Snap Layouts needs this button.
            Assert.NotEqual(0, style & 0x00040000); // WS_THICKFRAME: retain native resizing.
            Assert.Equal(0, GetWindowLong(handle, -20) & 0x00000080); // A normal app uses workspace placement coordinates.

            var proposed = Rect(100, 100, 750, 900);
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
            try
            {
                Marshal.StructureToPtr(proposed, pointer, false);
                _ = SendMessage(handle, 0x0214, 2, pointer); // WM_SIZING / WMSZ_RIGHT
                Assert.Equal(proposed, Marshal.PtrToStructure<NativeRect>(pointer));
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        });
    }

    [Fact]
    public void Legacy_generation_one_restores_independent_dimensions()
    {
        var monitor = PrimaryMonitorInfo();
        var bounds = Rect(monitor.WorkArea.Left + 80, monitor.WorkArea.Top + 80,
            monitor.WorkArea.Left + 680, monitor.WorkArea.Top + 430);
        var workspace = WindowPlacementService.ToWorkspaceCoordinates(bounds, monitor.MonitorArea, monitor.WorkArea);

        WithWindow(Remembered(workspace, false), (_, handle, _) =>
        {
            Assert.True(GetWindowRect(handle, out var actual));
            Assert.Equal(bounds, actual);
        });
    }

    [Fact]
    public void Maximise_and_restore_keep_the_previous_independent_dimensions()
    {
        var monitor = PrimaryMonitorInfo();
        var bounds = Rect(monitor.WorkArea.Left + 80, monitor.WorkArea.Top + 80,
            monitor.WorkArea.Left + 680, monitor.WorkArea.Top + 430);
        var workspace = WindowPlacementService.ToWorkspaceCoordinates(bounds, monitor.MonitorArea, monitor.WorkArea);

        WithWindow(Remembered(workspace, true), (window, handle, service) =>
        {
            Assert.Equal(WindowState.Maximized, window.WindowState);
            var placement = PlacementOf(handle);
            Assert.Equal(3u, placement.ShowCommand);
            Assert.Equal(workspace, placement.NormalPosition);
            service.Save(window);
            using var saved = JsonDocument.Parse(File.ReadAllText(new AppPaths(_root).WindowPlacementFile));
            Assert.True(saved.RootElement.GetProperty("maximized").GetBoolean());
            Assert.Equal(workspace.Right, saved.RootElement.GetProperty("right").GetInt32());

            window.WindowState = WindowState.Normal;
            Assert.True(GetWindowRect(handle, out var actual));
            Assert.Equal(bounds, actual);
        });
    }

    [Fact]
    public void Minimising_a_maximised_window_keeps_the_maximised_preference()
    {
        WithWindow(null, (window, _, service) =>
        {
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            service.Save(window);

            using var saved = JsonDocument.Parse(File.ReadAllText(new AppPaths(_root).WindowPlacementFile));
            Assert.True(saved.RootElement.GetProperty("maximized").GetBoolean());
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("this is not json")]
    [InlineData("{\"cacheGeneration\":1,\"left\":900,\"top\":0,\"right\":20,\"bottom\":300}")]
    public void An_oversized_default_or_invalid_file_opens_inside_the_work_area(string? remembered)
    {
        WithWindow(remembered, (_, handle, _) =>
        {
            Assert.True(GetWindowRect(handle, out var actual));
            AssertInside(actual, MonitorInfoFor(actual).WorkArea);
        }, oversized: true);
    }

    [Fact]
    public void A_saved_position_on_a_missing_monitor_opens_on_an_available_one()
    {
        WithWindow(Remembered(Rect(100000, 100000, 100720, 100540), false), (_, handle, _) =>
        {
            Assert.True(GetWindowRect(handle, out var actual));
            AssertInside(actual, MonitorInfoFor(actual).WorkArea);
        });
    }

    [Fact]
    public void Saving_an_unchanged_window_does_not_rewrite_its_placement()
    {
        WithWindow(null, (window, _, service) =>
        {
            var path = new AppPaths(_root).WindowPlacementFile;
            service.Save(window);
            File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            var originalTime = File.GetLastWriteTimeUtc(path);

            service.Save(window);

            Assert.Equal(originalTime, File.GetLastWriteTimeUtc(path));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        });
    }

    [Fact]
    public void Saving_a_normal_window_remembers_its_current_rectangle()
    {
        WithWindow(null, (window, handle, service) =>
        {
            Assert.True(GetWindowRect(handle, out var actual));
            var monitor = MonitorInfoFor(actual);
            var workspace = WindowPlacementService.ToWorkspaceCoordinates(actual, monitor.MonitorArea, monitor.WorkArea);
            service.Save(window);

            using var saved = JsonDocument.Parse(File.ReadAllText(new AppPaths(_root).WindowPlacementFile));
            Assert.Equal(workspace.Left, saved.RootElement.GetProperty("left").GetInt32());
            Assert.Equal(workspace.Top, saved.RootElement.GetProperty("top").GetInt32());
            Assert.Equal(workspace.Right, saved.RootElement.GetProperty("right").GetInt32());
            Assert.Equal(workspace.Bottom, saved.RootElement.GetProperty("bottom").GetInt32());
            Assert.False(saved.RootElement.GetProperty("maximized").GetBoolean());
            Assert.False(saved.RootElement.TryGetProperty("bounds", out _));
        });
    }

    [Fact]
    public void Independently_resized_dimensions_survive_save_and_reopen()
    {
        var monitor = PrimaryMonitorInfo();
        var bounds = Rect(monitor.WorkArea.Left + 80, monitor.WorkArea.Top + 80,
            monitor.WorkArea.Left + 680, monitor.WorkArea.Top + 430);
        WithWindow(null, (window, handle, service) =>
        {
            Assert.True(SetWindowPos(handle, 0, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0014)); // NOZORDER | NOACTIVATE
            service.Save(window);
        });

        var remembered = File.ReadAllText(new AppPaths(_root).WindowPlacementFile);
        WithWindow(remembered, (_, handle, _) =>
        {
            Assert.True(GetWindowRect(handle, out var actual));
            Assert.Equal(bounds, actual);
        });
    }

    private void WithWindow(string? remembered, Action<Window, nint, WindowPlacementService> inspect, bool oversized = false)
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.WindowPlacementFile)!);
        if (remembered is not null) File.WriteAllText(paths.WindowPlacementFile, remembered);
        WindowCanvasTests.OnAStaThread(() =>
        {
            // A real HWND exercises Windows' frame and state transitions. Keep
            // the test window invisible, out of the taskbar and never active.
            var window = new Window
            {
                Width = oversized ? 10000 : 720,
                Height = oversized ? 10000 : 540,
                MinWidth = 100,
                MinHeight = 100,
                Opacity = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResize
            };
            var foreground = GetForegroundWindow();
            window.Activated += (_, _) => { if (foreground != 0) _ = SetForegroundWindow(foreground); };
            try
            {
                window.SourceInitialized += (_, _) =>
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    _ = SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
                };
                var service = new WindowPlacementService(paths);
                service.Apply(window);
                window.Show();
                inspect(window, new WindowInteropHelper(window).Handle, service);
            }
            finally
            {
                window.Close();
                if (foreground != 0) _ = SetForegroundWindow(foreground);
            }
            return true;
        });
    }

    private static void AssertInside(NativeRect bounds, NativeRect work)
    {
        Assert.InRange(bounds.Left, work.Left, work.Right - 1);
        Assert.InRange(bounds.Top, work.Top, work.Bottom - 1);
        Assert.InRange(bounds.Right, bounds.Left + 1, work.Right);
        Assert.InRange(bounds.Bottom, bounds.Top + 1, work.Bottom);
    }

    private static NativeRect Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static string Remembered(NativeRect bounds, bool maximized) => JsonSerializer.Serialize(new
    {
        cacheGeneration = 1,
        left = bounds.Left,
        top = bounds.Top,
        right = bounds.Right,
        bottom = bounds.Bottom,
        maximized
    });

    private static MonitorInfo PrimaryMonitorInfo() => MonitorInfoFor(Rect(0, 0, 1, 1));

    private static MonitorInfo MonitorInfoFor(NativeRect bounds)
    {
        var monitor = MonitorFromRect(ref bounds, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        Assert.True(GetMonitorInfo(monitor, ref info));
        return info;
    }

    private static WindowPlacement PlacementOf(nint handle)
    {
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        Assert.True(GetWindowPlacement(handle, ref placement));
        return placement;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
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
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rectangle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
