using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Minecraft.Tests;

/// <summary>
/// Puts the test process on the same DPI footing as the launcher, before any
/// test runs.
/// </summary>
/// <remarks>
/// The launcher's manifest declares PerMonitorV2, so everything it measures
/// through Win32 is in real pixels. A test process declares nothing, and would
/// be handed the scaled-down numbers Windows invents for programs that do not
/// know about DPI - until the first test that builds a WPF window, because WPF
/// sets the process awareness itself when it does.
///
/// That switch happening in the middle of a run is what made a monitor test
/// fail beside a window test and pass on its own: one half of a comparison was
/// measured before the flip and the other half after it. Setting it here, once,
/// leaves nothing to flip.
/// </remarks>
internal static class TestDpiAwareness
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, the value the manifest names.
    private static readonly nint PerMonitorV2 = -4;

    [ModuleInitializer]
    internal static void Apply()
    {
        try
        {
            // False when something has already settled it, which is fine: what
            // matters is that it is settled before the first measurement.
            SetProcessDpiAwarenessContext(PerMonitorV2);
        }
        catch (EntryPointNotFoundException)
        {
            // Older than Windows 10 1703. Nothing here needs to work there.
        }
        catch (DllNotFoundException)
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);
}
