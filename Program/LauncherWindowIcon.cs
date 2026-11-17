using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace Minecraft;

internal static class LauncherWindowIcon
{
    internal const string ApplicationId = "MarkZamore.LANMinecraft";
    private static BitmapFrame? image;
    internal static string? TestIconPath { get; set; }
    public static BitmapFrame Image
    {
        get
        {
            if (image is not null) return image;
            BitmapFrame result;
            if (TestIconPath is { } fixture)
            {
                using var input = File.OpenRead(fixture);
                result = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
            else
            {
                // Resource 1 belongs to the outer PE, so Explorer and WPF share one icon copy.
                using var input = ReadNativeIcon();
                result = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr instance, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr instance, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr instance, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr resource);

    private static MemoryStream ReadNativeIcon()
    {
        // Rebuild the ICO directory in memory so WPF retains every native DPI variant.
        var module = GetModuleHandle(null);
        var group = ReadResource(module, 14, 1);
        if (group.Length < 6) throw new InvalidDataException("Invalid native launcher icon group.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4));
        if (count is 0 or > 256 || group.Length != 6 + count * 14) throw new InvalidDataException("Invalid native launcher icon entries.");
        var images = new List<byte[]>();
        var result = new MemoryStream();
        using (var writer = new BinaryWriter(result, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(group, 0, 6);
            var offset = checked((uint)(6 + count * 16));
            for (var index = 0; index < count; index++)
            {
                var entry = group.AsSpan(6 + index * 14, 14);
                var image = ReadResource(module, 3, BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]));
                if (image.Length != BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]))
                    throw new InvalidDataException("Native launcher icon length mismatch.");
                writer.Write(entry[..12]); writer.Write(offset);
                images.Add(image); offset = checked(offset + (uint)image.Length);
            }
            foreach (var image in images) writer.Write(image);
        }
        result.Position = 0;
        return result;
    }

    private static byte[] ReadResource(IntPtr module, int type, int id)
    {
        var resource = FindResource(module, new IntPtr(id), new IntPtr(type));
        if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "The launcher icon is missing.");
        var length = SizeofResource(module, resource);
        var data = LockResource(LoadResource(module, resource));
        if (length is 0 or > 16 * 1024 * 1024 || data == IntPtr.Zero)
            throw new InvalidDataException("Invalid native launcher icon resource.");
        var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, bytes.Length); return bytes;
    }
}
