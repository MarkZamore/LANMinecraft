using System.Runtime.InteropServices;

namespace Minecraft;

public static class MemorySizingService
{
    public const int MinMemoryGb = 2;
    public const int MaxMemoryGb = 128;
    /// <summary>The smallest heap worth starting with, whatever the budget.</summary>
    public const int MinHeapGb = 2;
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

    /// <summary>
    /// Everything the game needs beside its heap: the class data of nine
    /// hundred mods, the compiled code, four hundred thread stacks, and above
    /// all the buffers Sodium hands the graphics driver. Measured on this pack
    /// it came to almost eight gigabytes over a twelve gigabyte heap.
    ///
    /// No Java setting bounds the last of those, so this is not a limit the
    /// launcher can impose - it is room it keeps out of the budget, so that the
    /// number a player sets is what the game takes altogether rather than what
    /// one part of it takes.
    /// </summary>
    /// <remarks>
    /// Half the budget, and never more than eight: the measurement does not
    /// scale with the heap - a pack of nine hundred mods holds its classes and
    /// its buffers whatever size the heap is - so the reserve saturates instead
    /// of growing, and a large budget spends the whole of the rest on the heap.
    /// </remarks>
    public static int GetNativeReserveGb(int budgetGb) => Math.Clamp(budgetGb / 2, 2, 8);

    /// <summary>The heap a budget leaves: what goes to <c>-Xmx</c>.</summary>
    public static int GetHeapGb(int budgetGb) =>
        Math.Max(MinHeapGb, budgetGb - GetNativeReserveGb(budgetGb));

    /// <summary>
    /// The budget a stored heap size stands for: the smallest one that still
    /// leaves that heap. Settings written before the number meant the whole of
    /// the game are carried across with this, so nobody's game quietly shrinks.
    /// </summary>
    public static int GetBudgetForHeapGb(int heapGb)
    {
        var budget = Math.Max(MinMemoryGb, heapGb);
        while (budget < MaxMemoryGb && GetHeapGb(budget) < heapGb) budget++;
        return budget;
    }

    /// <summary>
    /// What a machine can lend the game without starving itself, counting all
    /// of the game. Limitless 8 needs about 10 GB of heap just to open a world,
    /// and as much again beside it, so the suggestion is generous - but Windows
    /// and Steam keep their quarter.
    /// </summary>
    public static int GetRecommendedDefaultMemoryGb(ulong totalPhysicalMemoryBytes)
    {
        var installedGb = (int)Math.Round(totalPhysicalMemoryBytes / BytesPerGb, MidpointRounding.AwayFromZero);
        var recommended = installedGb switch
        {
            < 12 => 8,
            < 16 => 12,
            < 24 => 16,
            _ => 20
        };
        return Math.Clamp(recommended, MinMemoryGb, GetAllowedMaxMemoryGb(totalPhysicalMemoryBytes));
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
