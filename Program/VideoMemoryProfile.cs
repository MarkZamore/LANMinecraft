using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Minecraft;

/// <summary>
/// The graphics card as memory sizing sees it: how many gigabytes it holds of
/// its own.
///
/// Everything the game draws has to be in video memory, and what does not fit
/// is not dropped - the driver keeps a copy in system memory and moves it
/// across as it is wanted. That copy is charged to the game, so the same pack
/// on the same machine holds gigabytes more beside its heap when the card is
/// small. Two players on identical laptops, one with sixteen gigabytes of video
/// memory and one with eight, are not the same machine to a heap: the second
/// one was measured at 28.5 GB of its 31.6 in use, with full garbage
/// collections of 2.2 seconds - a heap partly in the page file - while the
/// first, same processor and same pack, played the same evening without them.
/// </summary>
public readonly record struct VideoMemoryProfile(int DedicatedGb)
{
    /// <summary>
    /// A card nobody could measure - no driver key, a machine that answers
    /// nothing, a remote session. Sizing charges nothing for it, so a card that
    /// cannot be read never costs anybody heap.
    /// </summary>
    public static VideoMemoryProfile Unknown { get; } = new(0);

    /// <summary>False for <see cref="Unknown"/> alone.</summary>
    public bool IsKnown => DedicatedGb > 0;

    private const string DisplayAdapterClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    private static VideoMemoryProfile? _measured;

    /// <summary>
    /// This machine's card. Read once and kept: nothing about it changes while
    /// the launcher is open, and every place that divides the memory number has
    /// to divide it the same way.
    /// </summary>
    public static VideoMemoryProfile Measure() => _measured ??= ReadTheCard();

    private static VideoMemoryProfile ReadTheCard()
    {
        try
        {
            return FromAdapterBytes(ReadAdapterMemory(ReadPresentAdapterNames()));
        }
        catch
        {
            return Unknown;
        }
    }

    /// <summary>
    /// The largest card a machine has, in whole gigabytes. Largest, because a
    /// laptop answers with two - the processor's own graphics and the card
    /// beside it - and the game runs on the one that can hold it. Rounded to
    /// the nearest, because a card sold as eight gigabytes reports 7.996 of
    /// them.
    /// </summary>
    internal static VideoMemoryProfile FromAdapterBytes(IEnumerable<long> dedicatedBytes)
    {
        var largest = dedicatedBytes.DefaultIfEmpty(0).Max();
        if (largest <= 0) return Unknown;

        var gigabytes = (int)Math.Round(largest / BytesPerGb, MidpointRounding.AwayFromZero);
        return gigabytes > 0 ? new VideoMemoryProfile(gigabytes) : Unknown;
    }

    /// <summary>
    /// What the display drivers say they have. The class key is the only place
    /// Windows keeps the true size: the number WMI offers is a 32-bit field and
    /// answers 4 GB for every card above it.
    /// </summary>
    private static List<long> ReadAdapterMemory(HashSet<string> present)
    {
        using var adapters = Registry.LocalMachine.OpenSubKey(DisplayAdapterClass);
        if (adapters is null) return [];

        var found = new List<(string Name, long Bytes)>();
        foreach (var name in adapters.GetSubKeyNames())
        {
            // The cards are numbered - 0000, 0001 - and the class key holds two
            // more of its own beside them, Configuration and Properties, which a
            // player is not allowed to open at all. Reading one of those threw
            // and took the whole answer with it, so a card is looked at only
            // where a card can be, and one that will not open is skipped rather
            // than believed to be the end of the list.
            if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;

            try
            {
                using var adapter = adapters.OpenSubKey(name);
                if (adapter is null) continue;

                var bytes = ReadSize(adapter, "HardwareInformation.qwMemorySize");
                // The older value is a 32-bit one and stops at four gigabytes,
                // so it is a floor rather than a size - taken only when the card
                // does not offer the wide one at all.
                if (bytes <= 0) bytes = ReadSize(adapter, "HardwareInformation.MemorySize");
                if (bytes <= 0) continue;

                found.Add((adapter.GetValue("DriverDesc") as string ?? "", bytes));
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
            {
            }
        }

        // A card that has been swapped out leaves its key behind - this very
        // machine still lists the 4070 it does not have - so the list is cut
        // down to what Windows is driving now. When no name can be matched, the
        // whole list is used rather than none of it: a card read too large only
        // leaves the sizing where it was before this existed.
        var driven = found.Where(entry => present.Contains(entry.Name)).ToList();
        return (driven.Count > 0 ? driven : found).Select(entry => entry.Bytes).ToList();
    }

    private static long ReadSize(RegistryKey adapter, string value) => adapter.GetValue(value) switch
    {
        long qword => qword,
        int dword => dword & 0xFFFFFFFFL,
        byte[] raw when raw.Length >= 8 => BitConverter.ToInt64(raw, 0),
        byte[] raw when raw.Length >= 4 => BitConverter.ToUInt32(raw, 0),
        _ => 0,
    };

    private static HashSet<string> ReadPresentAdapterNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; index < 32; index++)
        {
            var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if (!string.IsNullOrWhiteSpace(device.DeviceString)) names.Add(device.DeviceString.Trim());
        }
        return names;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);
}
