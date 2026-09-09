using System.Runtime.InteropServices;

namespace Minecraft;

/// <summary>
/// Tells Windows that the launcher's own file has changed. An update puts a new
/// executable at a path that keeps its name, and the shell answers for that path
/// from a picture it drew earlier: the desktop, the taskbar and the folder all go
/// on showing the icon of the version that was replaced. Nothing inside the file
/// can undo that, because the shell does not look again until it is told to.
/// </summary>
internal static class ShellIconCache
{
    private const int ItemChanged = 0x00002000;          // SHCNE_UPDATEITEM
    private const uint ItemIsAWidePath = 0x0005;         // SHCNF_PATHW
    private const uint DoNotWaitForListeners = 0x2000;   // SHCNF_FLUSHNOWAIT

    /// <summary>
    /// Announces the running executable. One call, nothing waited on; when the
    /// file is the one the shell already drew, it finds nothing to redraw.
    /// </summary>
    public static void AnnounceRunningExecutable()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            SHChangeNotify(ItemChanged, ItemIsAWidePath | DoNotWaitForListeners, path, IntPtr.Zero);
        }
        catch
        {
            // A stale icon is not worth failing a launch over.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item, IntPtr unused);
}
