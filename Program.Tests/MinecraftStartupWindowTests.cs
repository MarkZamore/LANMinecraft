using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace Minecraft.Tests;

/// <summary>
/// The game's window has to be the right size the moment it appears.
///
/// It used to arrive in three steps: the pack's early window filled the
/// screen, the game rebuilt it at the 854x480 nobody asked for, and half a
/// second later the launcher stretched it back out. The middle step is the one
/// a player reports, and it is gone only for as long as the launcher keeps
/// telling the game what size to build.
/// </summary>
public sealed class MinecraftStartupWindowTests : IDisposable
{
    // What the game builds a window at when it is told nothing: the size this
    // whole exercise exists to avoid.
    private const int GameDefaultWidth = 854;
    private const int GameDefaultHeight = 480;
    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const uint DecoratedWindowStyle = 0x00CF0000;
    private const uint AppWindowExtendedStyle = 0x00040000;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "startup-window-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void Fills_the_screen_when_nothing_is_remembered()
    {
        var size = SizeFor(remembered: null);

        Assert.Equal(WorkArea(), Outside(size));
    }

    [Fact]
    public void Fills_the_screen_when_the_window_was_left_maximised()
    {
        var work = WorkAreaBounds();

        // A maximised window remembers the smaller size it had before it was
        // maximised. Opening at that one would be the same flash, only inwards.
        var size = SizeFor(Remembered(
            work.Left + 200, work.Top + 200, work.Left + 1000, work.Top + 800, maximised: true));

        Assert.Equal(WorkArea(), Outside(size));
    }

    [Fact]
    public void Opens_at_the_size_the_window_was_left_at()
    {
        var work = WorkAreaBounds();

        var size = SizeFor(Remembered(
            work.Left + 40, work.Top + 40, work.Left + 840, work.Top + 640, maximised: false));

        Assert.Equal((800, 600), Outside(size));
    }

    [Fact]
    public void Never_settles_for_the_size_the_game_would_pick_on_its_own()
    {
        var size = SizeFor(remembered: null);

        Assert.True(
            size.Width > GameDefaultWidth && size.Height > GameDefaultHeight,
            $"A first launch would open at {size.Width}x{size.Height}, " +
            $"which is no better than the game's own {GameDefaultWidth}x{GameDefaultHeight}.");
    }

    [Fact]
    public void The_size_reaches_the_game()
    {
        // Two numbers that are worked out and never passed on would leave the
        // flash exactly where it was, and nothing else in the launcher would
        // notice.
        var source = File.ReadAllText(FindRepositoryFile("Program", "MinecraftProcessService.cs"));

        Assert.Contains("_gameWindowPlacement.StartupClientSize()", source, StringComparison.Ordinal);
        Assert.Contains("ScreenWidth = startupSize?.Width ?? 0", source, StringComparison.Ordinal);
        Assert.Contains("ScreenHeight = startupSize?.Height ?? 0", source, StringComparison.Ordinal);
    }

    private (int Width, int Height) SizeFor(string? remembered)
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.MinecraftWindowPlacementFile)!);
        if (remembered is not null)
        {
            File.WriteAllText(paths.MinecraftWindowPlacementFile, remembered);
        }

        var size = new MinecraftWindowPlacementService(paths, new Logger(paths.LogFile))
            .StartupClientSize();
        Assert.NotNull(size);
        return size.Value;
    }

    private static string Remembered(int left, int top, int right, int bottom, bool maximised) =>
        $$"""
        {
          "cacheGeneration": 1,
          "left": {{left}},
          "top": {{top}},
          "right": {{right}},
          "bottom": {{bottom}},
          "maximized": {{(maximised ? "true" : "false")}}
        }
        """;

    /// <summary>
    /// The window a player will see, from the inside size the game is given.
    /// </summary>
    private static (int Width, int Height) Outside((int Width, int Height) inside)
    {
        var frame = default(NativeRect);
        Assert.True(AdjustWindowRectExForDpi(
            ref frame, DecoratedWindowStyle, false, AppWindowExtendedStyle, MonitorDpi()));
        return (inside.Width + frame.Right - frame.Left, inside.Height + frame.Bottom - frame.Top);
    }

    private static (int Width, int Height) WorkArea()
    {
        var monitorInfo = MonitorInfo.Create();
        Assert.True(GetMonitorInfo(PrimaryMonitor(), ref monitorInfo));
        return (
            monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top);
    }

    private static NativeRect WorkAreaBounds()
    {
        var monitorInfo = MonitorInfo.Create();
        Assert.True(GetMonitorInfo(PrimaryMonitor(), ref monitorInfo));
        return monitorInfo.WorkArea;
    }

    private static uint MonitorDpi() =>
        GetDpiForMonitor(PrimaryMonitor(), 0, out var dpiX, out _) == 0 ? dpiX : 96;

    private static nint PrimaryMonitor() => MonitorFromPoint(default, MonitorDefaultToPrimary);

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(
        ref NativeRect rectangle,
        uint style,
        [MarshalAs(UnmanagedType.Bool)] bool menu,
        uint extendedStyle,
        uint dpi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
