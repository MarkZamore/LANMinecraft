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

    public static object AssertInitialCompact(Process process, nint window)
    {
        var previous = SetThreadDpiAwarenessContext(-4);
        if (previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            AssertOwned(process, window);
            Assert.False(IsIconic(window), "Preparation must open visibly, even when the caller requests minimization.");
            Assert.False(IsZoomed(window), "Preparation must open compactly, even when the caller requests maximization.");
            var dpi = checked((int)GetDpiForWindow(window));
            GetClientRect(window, out var client);
            Assert.Equal(Scale(336, dpi), client.Right);
            Assert.Equal(Scale(200, dpi), client.Bottom);
            Assert.NotEqual(0, GetWindowLong(window, -16) & 0x02000000); // WS_CLIPCHILDREN
            Assert.NotEqual(0, GetWindowLong(window, -20) & 0x02000000); // WS_EX_COMPOSITED
            return new { dpi, width = client.Right, height = client.Bottom };
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    public static object AssertIdleDoesNotMutateControls(Process process, nint window)
    {
        AssertOwned(process, window);
        Assert.True(RetryReady(window), "Idle repaint checks require a completed, stable retryable failure.");
        Send(window, 0x0113, 1, 0); // WM_TIMER completes the first current-state update.
        var mutationsBefore = Send(window, 0x8052, 0, 0);
        Assert.True((long)mutationsBefore > 0, "The test-only native mutation counter must observe initial control setup.");
        var drawsBefore = Send(window, 0x8052, 1, 0);
        var erasesBefore = Send(window, 0x8052, 2, 0);
        for (var tick = 0; tick < 32; tick++) Send(window, 0x0113, 1, 0);
        var mutationsAfter = Send(window, 0x8052, 0, 0);
        Assert.Equal(mutationsBefore, mutationsAfter);
        // Paints can be requested by Windows itself. The invariant is that an
        // unchanged preparation snapshot does not mutate or invalidate controls.
        return new { mutationsBefore = (long)mutationsBefore, mutationsAfter = (long)mutationsAfter,
            drawsBefore = (long)drawsBefore, drawsAfter = (long)Send(window, 0x8052, 1, 0),
            erasesBefore = (long)erasesBefore, erasesAfter = (long)Send(window, 0x8052, 2, 0) };
    }

    public static void ErrorText(Process process, nint window, int errorCode)
    {
        AssertOwned(process, window);
        Assert.True(RetryReady(window));
        Assert.Equal((nint)1, Send(window, 0x8053, (nuint)errorCode, 0));
    }

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
            // Deliberately request an undersized outer rectangle: the actual
            // window must enforce its compact minimum while reflowing at DPI.
            var desired = new Rect { Left = previous.Left, Top = previous.Top,
                Right = previous.Left + 1, Bottom = previous.Top + 1 };
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());
            try
            {
                Marshal.StructureToPtr(desired, pointer, false);
                Send(window, 0x02e0, (nuint)(dpi | dpi << 16), pointer); // WM_DPICHANGED, this owned window only
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        var minimum = new Rect { Right = Scale(336, dpi), Bottom = Scale(200, dpi) };
        Assert.True(AdjustWindowRectExForDpi(ref minimum, unchecked((uint)GetWindowLong(window, -16)), false,
            unchecked((uint)GetWindowLong(window, -20)), (uint)dpi));
        GetWindowRect(window, out var outer);
        Assert.Equal(minimum.Right - minimum.Left, outer.Right - outer.Left);
        Assert.Equal(minimum.Bottom - minimum.Top, outer.Bottom - outer.Top);
        GetClientRect(window, out var parent);
        // Synthetic WM_DPICHANGED changes application layout, not Windows'
        // actual nonclient DPI. The outer minimum above is exact; client bounds
        // below are measured instead of pretending the OS monitor also moved.
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
            var expectedTop = id switch { 1101 => Scale(16, dpi), Phase => Scale(48, dpi),
                Metrics => Scale(96, dpi), Progress => Scale(128, dpi), _ => parent.Bottom - Scale(52, dpi) };
            var expectedHeight = id switch { 1101 => Scale(28, dpi), Phase => Scale(40, dpi),
                Metrics => Scale(20, dpi), Progress => Scale(8, dpi), _ => Scale(36, dpi) };
            Assert.Equal(expectedTop, topLeft.Y);
            Assert.Equal(expectedHeight, bottomRight.Y - topLeft.Y);
            if (id is Cancel or Retry)
            {
                Assert.Equal(Scale(112, dpi), bottomRight.X - topLeft.X);
                Assert.Equal(parent.Right - Scale(16, dpi) - Scale(112, dpi) -
                    (id == Retry ? Scale(112, dpi) + Scale(8, dpi) : 0), topLeft.X);
            }
            else
            {
                Assert.Equal(Scale(16, dpi), topLeft.X);
                Assert.Equal(parent.Right - Scale(16, dpi), bottomRight.X);
            }
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

    private static int Scale(int value, int dpi) => checked((value * dpi + 48) / 96);

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
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AdjustWindowRectExForDpi(ref Rect rectangle,
        uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
}
