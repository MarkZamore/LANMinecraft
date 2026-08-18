using System.Runtime.InteropServices;

namespace Minecraft;

/// <summary>
/// How much memory the game is allowed, and how that number is divided.
///
/// The number a player sets is everything the game may take: the Java heap and
/// the room beside it - class data, compiled code, thread stacks, and above all
/// the buffers Sodium hands the graphics driver, which no Java setting bounds.
/// Only the heap can be handed to the JVM, so the launcher works out what the
/// pack holds outside it and gives the heap what is left.
///
/// Every rule here reads a <see cref="PackMemoryProfile"/> rather than a
/// constant, because the same launcher runs vanilla on an old version and packs
/// heavier than Limitless 8 on new ones, and those do not need the same room to
/// a factor of eight.
/// </summary>
public static class MemorySizingService
{
    public const int MinMemoryGb = 2;
    public const int MaxMemoryGb = 128;
    /// <summary>The smallest heap worth starting with, whatever the budget.</summary>
    public const int MinHeapGb = 2;
    /// <summary>
    /// Past this a larger heap buys garbage-collection pauses rather than
    /// comfort, so it bounds what the launcher suggests - not what a player may
    /// type.
    /// </summary>
    public const int MaxRecommendedHeapGb = 16;

    private const double BytesPerGb = 1024d * 1024d * 1024d;
    private const double BytesPerMb = 1024d * 1024d;

    // The pack-weight model, in megabytes. Calibrated against the one pack that
    // has been measured: Limitless 8, 874 jars and 1.9 GB of them, held almost
    // eight gigabytes outside a twelve gigabyte heap. The terms are what that
    // memory is made of, so the numbers carry to a pack of another shape: a
    // base every client pays, a per-mod cost (classes, mixins, threads), a
    // share of the jar bytes (class data and the models inside them), and a
    // share of the texture the pack ships loose.
    private const int NativeBaseMb = 1024;
    private const int OlderMinecraftNativeBaseMb = 768;
    private const int NativePerModMb = 5;
    private const double NativePerJarMegabyte = 1.2;
    private const double NativePerAssetMegabyte = 0.5;

    // And what the heap itself wants, which grows with the number of mods
    // rather than with their size: registries, block states, chunk data and the
    // entities of every mod that adds any.
    private const int HeapBaseMb = 2048;
    private const int OlderMinecraftHeapBaseMb = 1024;
    private const int HeapPerModMb = 12;

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

    public static int GetRecommendedDefaultMemoryGb(PackMemoryProfile pack)
    {
        try
        {
            return GetRecommendedDefaultMemoryGb(pack, GetTotalPhysicalMemoryBytes());
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

    /// <summary>
    /// Everything the pack needs beside its heap. No Java setting bounds the
    /// last of it, so this is not a limit the launcher can impose - it is room
    /// it keeps out of the budget, so that the number a player sets is what the
    /// game takes altogether rather than what one part of it takes.
    /// </summary>
    /// <remarks>
    /// The budget is consulted in one case only: a pack the launcher has not
    /// been able to look at. Then it keeps to the rule it used when it could
    /// not tell packs apart at all - half the budget, never more than eight -
    /// rather than guessing at a weight.
    /// </remarks>
    public static int GetNativeReserveGb(PackMemoryProfile pack, int budgetGb) =>
        pack.IsKnown ? GetNativeReserveGb(pack) : Math.Clamp(budgetGb / 2, 2, 8);

    /// <summary>The same room, for a pack that has been measured.</summary>
    public static int GetNativeReserveGb(PackMemoryProfile pack)
    {
        if (!pack.IsKnown) return GetNativeReserveGb(pack, MaxMemoryGb);

        var megabytes =
            (pack.IsModernMinecraft ? NativeBaseMb : OlderMinecraftNativeBaseMb) +
            (double)pack.ModCount * NativePerModMb +
            pack.ModBytes / BytesPerMb * NativePerJarMegabyte +
            pack.AssetBytes / BytesPerMb * NativePerAssetMegabyte;
        // Rounded up: a reserve set too low is spent by the game anyway - over
        // the budget, and into the memory the machine kept for itself.
        return Math.Clamp((int)Math.Ceiling(megabytes / 1024d), 1, MaxMemoryGb - MinHeapGb);
    }

    /// <summary>The heap a budget leaves this pack: what goes to <c>-Xmx</c>.</summary>
    public static int GetHeapGb(PackMemoryProfile pack, int budgetGb) =>
        Math.Max(MinHeapGb, budgetGb - GetNativeReserveGb(pack, budgetGb));

    /// <summary>
    /// The smallest budget that leaves this pack a heap at all. Below it the
    /// heap stops shrinking - it has a floor - and the game simply takes more
    /// than the number says, which is worth telling a player.
    /// </summary>
    public static int GetSmallestUsefulBudgetGb(PackMemoryProfile pack)
    {
        var budget = MinMemoryGb;
        while (budget < MaxMemoryGb && budget - GetNativeReserveGb(pack, budget) < MinHeapGb) budget++;
        return budget;
    }

    /// <summary>
    /// The heap this pack wants: enough to open a world and keep playing in it.
    /// Mods pay for it one by one - each brings its registries, its block states
    /// and its entities - while the size of the jars hardly matters here, which
    /// is why the bytes are absent.
    /// </summary>
    public static int GetRecommendedHeapGb(PackMemoryProfile pack)
    {
        if (!pack.IsKnown) return MaxRecommendedHeapGb / 2;

        var megabytes =
            (pack.IsModernMinecraft ? HeapBaseMb : OlderMinecraftHeapBaseMb) +
            (double)pack.ModCount * HeapPerModMb;
        return Math.Clamp(
            (int)Math.Round(megabytes / 1024d, MidpointRounding.AwayFromZero),
            MinHeapGb,
            MaxRecommendedHeapGb);
    }

    /// <summary>
    /// The budget a stored heap size stands for: the smallest one that still
    /// leaves that heap. Settings written before the number meant the whole of
    /// the game are carried across with this, so nobody has their game quietly
    /// shrink.
    /// </summary>
    public static int GetBudgetForHeapGb(PackMemoryProfile pack, int heapGb)
    {
        var budget = Math.Max(MinMemoryGb, heapGb);
        while (budget < MaxMemoryGb && GetHeapGb(pack, budget) < heapGb) budget++;
        return budget;
    }

    /// <summary>
    /// What the launcher suggests for a pack on this machine: the heap the pack
    /// wants plus the room it holds beside it - and never more than the machine
    /// can lend, whatever the pack would like.
    /// </summary>
    public static int GetRecommendedDefaultMemoryGb(PackMemoryProfile pack, ulong totalPhysicalMemoryBytes)
    {
        var wanted = pack.IsKnown
            ? GetRecommendedHeapGb(pack) + GetNativeReserveGb(pack)
            : GetRecommendedDefaultForAnUnseenPack(totalPhysicalMemoryBytes);
        return Math.Clamp(wanted, MinMemoryGb, GetAllowedMaxMemoryGb(totalPhysicalMemoryBytes));
    }

    /// <summary>
    /// Before any pack is installed there is nothing to weigh, and the launcher
    /// answers as it did when it weighed nothing at all: generously, because the
    /// pack it downloads on the first press of Play is a large one. The first
    /// launch that can see a pack replaces this with that pack's own number.
    /// </summary>
    private static int GetRecommendedDefaultForAnUnseenPack(ulong totalPhysicalMemoryBytes)
    {
        var installedGb = (int)Math.Round(totalPhysicalMemoryBytes / BytesPerGb, MidpointRounding.AwayFromZero);
        return installedGb switch
        {
            < 12 => 8,
            < 16 => 12,
            < 24 => 16,
            _ => 20
        };
    }

    public static int ClampMemoryGb(int value)
    {
        return Math.Clamp(value, MinMemoryGb, GetAllowedMaxMemoryGb());
    }

    /// <summary>
    /// The largest budget a machine may be asked for - all of the game, heap
    /// and everything beside it. A quarter of the machine is kept back, and
    /// never less than four gigabytes: a game that fits the installed memory
    /// exactly is a machine that pages, and a paging machine spends whole
    /// seconds inside one tick.
    /// </summary>
    public static int GetAllowedMaxMemoryGb(ulong totalPhysicalMemoryBytes)
    {
        var installedGb = (int)Math.Floor(totalPhysicalMemoryBytes / BytesPerGb);
        var reserved = Math.Max(4, installedGb / 4);
        return Math.Clamp(installedGb - reserved, MinMemoryGb, MaxMemoryGb);
    }
}
