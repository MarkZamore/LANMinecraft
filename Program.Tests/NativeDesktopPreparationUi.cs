using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Minecraft.Tests;

// Native messages address only a newly-created, exact owned fixture process.
// This helper never searches by title, sends desktop input or changes OS scale.
internal static class NativeDesktopPreparationUi
{
    public const int Cancel = 2;
    public const int Retry = 4;
    public const int Phase = 1102;
    public const int Metrics = 1103;
    public const int Progress = 1104;

    public static async Task<nint> Wait(Process process, Func<nint, bool>? ready = null, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"The owned bootstrap exited {process.ExitCode} before its expected preparation UI state.");
            nint found = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                if (pid != process.Id) return true;
                var name = new StringBuilder(256);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() == "LANMinecraft.BootstrapPreparation" && IsWindowVisible(window) && (ready is null || ready(window)))
                    found = window;
                return found == 0;
            }, 0);
            if (found != 0) return found;
            await Task.Delay(25);
        }
        throw new TimeoutException("The owned bootstrap preparation UI did not reach its expected state.");
    }

    public static bool RetryReady(nint window)
    {
        var button = GetDlgItem(window, Retry);
        return button != 0 && IsWindowVisible(button) && IsWindowEnabled(button);
    }

    public static bool HasProgressMetrics(nint window) => Text(GetDlgItem(window, Metrics)).Contains('%');

    public static void Command(Process process, nint window, int id)
    {
        AssertOwned(process, window);
        Send(window, 0x0111, (nuint)id, GetDlgItem(window, id)); // WM_COMMAND
    }

    public static object AssertLayout(Process process, nint window, int dpi)
    {
        var previous = SetThreadDpiAwarenessContext(-4); // Thread-only per-monitor V2; no display settings change.
        if (previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return AssertLayoutCore(process, window, dpi); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private static object AssertLayoutCore(Process process, nint window, int dpi)
    {
        AssertOwned(process, window);
        Assert.Equal("LANMinecraft", Text(window));
        var actualDpi = GetDpiForWindow(window);
        Assert.True(actualDpi >= 96);
        // Always send each requested state: monitor DPI does not change when a
        // previous synthetic message has already changed this window's layout.
        {
            GetWindowRect(window, out var previous);
            var desired = new Rect { Left = previous.Left, Top = previous.Top,
                Right = previous.Left + 600 * dpi / 96, Bottom = previous.Top + 330 * dpi / 96 };
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());
            try
            {
                Marshal.StructureToPtr(desired, pointer, false);
                Send(window, 0x02e0, (nuint)(dpi | dpi << 16), pointer); // WM_DPICHANGED, this owned window only
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        GetClientRect(window, out var parent);
        Assert.True(parent.Right >= 420 * dpi / 96, $"Client width is too narrow at {dpi} DPI: {parent.Right}");
        Assert.True(parent.Bottom >= 240 * dpi / 96, $"Client height is too short at {dpi} DPI: {parent.Bottom}");
        var children = new List<object>();
        foreach (var id in new[] { 1101, Phase, Metrics, Progress, Cancel, Retry })
        {
            var child = GetDlgItem(window, id);
            Assert.NotEqual(0, child);
            if (!IsWindowVisible(child)) continue;
            GetWindowRect(child, out var bounds);
            var topLeft = new Point { X = bounds.Left, Y = bounds.Top };
            var bottomRight = new Point { X = bounds.Right, Y = bounds.Bottom };
            ScreenToClient(window, ref topLeft);
            ScreenToClient(window, ref bottomRight);
            Assert.InRange(topLeft.X, 0, parent.Right);
            Assert.InRange(topLeft.Y, 0, parent.Bottom);
            Assert.InRange(bottomRight.X, topLeft.X + 1, parent.Right);
            Assert.InRange(bottomRight.Y, topLeft.Y + 1, parent.Bottom);
            var text = Text(child);
            var measuredWidth = 0;
            var measuredHeight = 0;
            if (id != Progress && text.Length > 0)
            {
                GetClientRect(child, out var available);
                // GDI font handles belong to their creating process. The test-only
                // scalar hook measures inside the owned bootstrap with its actual font.
                var packed = Send(window, 0x8051, (nuint)id, 0);
                Assert.NotEqual((nint)(-1), packed);
                measuredWidth = (int)((long)packed & 0xffff);
                measuredHeight = (int)(((long)packed >> 16) & 0xffff);
                Assert.True(measuredWidth > 0 && measuredHeight > 0, "The native test hook must measure the selected control font.");
                Assert.True(measuredWidth <= available.Right && measuredHeight <= available.Bottom,
                    $"Control {id} clips '{text}' at {dpi} DPI: {measuredWidth}x{measuredHeight} > {available.Right}x{available.Bottom}");
            }
            children.Add(new { id, text, measuredWidth, measuredHeight, left = topLeft.X, top = topLeft.Y, right = bottomRight.X, bottom = bottomRight.Y });
        }
        return new { requestedDpi = dpi, actualMonitorDpi = actualDpi, width = parent.Right, height = parent.Bottom, children };
    }

    private static void AssertOwned(Process process, nint window)
    {
        Assert.False(process.HasExited);
        GetWindowThreadProcessId(window, out var pid);
        Assert.Equal(process.Id, (int)pid);
    }

    private static nint Send(nint window, uint message, nuint wparam, nint lparam)
    {
        if (SendMessageTimeout(window, message, wparam, lparam, 0x0002, 3000, out var result) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (nint)result;
    }

    private static string Text(nint window)
    {
        var buffer = new StringBuilder(2048);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
}
