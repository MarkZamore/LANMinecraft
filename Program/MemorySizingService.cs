using System.Runtime.InteropServices;

namespace Minecraft;

public static class MemorySizingService
{
    public const int MinMemoryGb = 2;
    public const int MaxMemoryGb = 128;
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    public static int GetAllowedMaxMemoryGb()
    {
        try
        {
            return GetAllowedMaxMemoryGb(GetTotalPhysicalMemoryBytes());
        }
        catch
        {
            return MaxMemoryGb;
        }
    }

    public static int GetRecommendedDefaultMemoryGb()
    {
        try
        {
            return GetRecommendedDefaultMemoryGb(GetTotalPhysicalMemoryBytes());
        }
        catch
        {
            return 16;
        }
    }

    /// <summary>
    /// Installed physical memory, straight from Win32. This used to come from
    /// Microsoft.VisualBasic.Devices.ComputerInfo, which only resolved because
    /// NAudio dragged in the Windows Forms assemblies; the numbers and every
    /// sizing rule below are unchanged.
    /// </summary>
    private static ulong GetTotalPhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("Installed memory could not be read.");
        }
        return status.TotalPhysical;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public static int GetRecommendedDefaultMemoryGb(ulong totalPhysicalMemoryBytes)
    {
        var installedGb = (int)Math.Round(totalPhysicalMemoryBytes / BytesPerGb, MidpointRounding.AwayFromZero);
        var recommended = installedGb switch
        {
            < 12 => 6,
            < 24 => 8,
            _ => 16
        };
        return Math.Clamp(recommended, MinMemoryGb, GetAllowedMaxMemoryGb(totalPhysicalMemoryBytes));
    }

    public static int ClampMemoryGb(int value)
    {
        return Math.Clamp(value, MinMemoryGb, GetAllowedMaxMemoryGb());
    }

    public static int GetAllowedMaxMemoryGb(ulong totalPhysicalMemoryBytes)
    {
        var availableGb = (int)Math.Floor(totalPhysicalMemoryBytes / BytesPerGb);
        return Math.Clamp(availableGb, MinMemoryGb, MaxMemoryGb);
    }
}
