using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Minecraft;

internal static class LauncherWindowIcon
{
    internal const string ApplicationId = "MarkZamore.LANMinecraft";
    private static BitmapFrame? image;
    public static BitmapFrame Image
    {
        get
        {
            if (image is not null) return image;
            var uri = new Uri("pack://application:,,,/LANMinecraft;component/Minecraft.ico", UriKind.Absolute);
            var result = BitmapFrame.Create(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            result.Freeze();
            return image = result;
        }
    }
    public static void Initialize()
    {
        // Establish the taskbar group before WPF creates any HWND.
        _ = SetCurrentProcessExplicitAppUserModelID(ApplicationId);
        _ = Image;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
