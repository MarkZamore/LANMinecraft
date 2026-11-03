using System.Globalization;
using System.Runtime.InteropServices;

namespace Minecraft;

internal readonly record struct SystemMemorySnapshot(
    ulong PhysicalTotalBytes, ulong PhysicalAvailableBytes, ulong CommitLimitBytes, ulong CommitTotalBytes)
{
    public ulong AvailableCommitBytes => CommitLimitBytes > CommitTotalBytes ? CommitLimitBytes - CommitTotalBytes : 0;
}

/// <summary>A single system-wide snapshot, never a wait, retry, or change to the player's heap.</summary>
internal static class LaunchMemoryCheck
{
    private const ulong MiB = 1024 * 1024;

    // GetPerformanceInfo reports system commit, unlike per-process address-space limits.
    // https://learn.microsoft.com/windows/win32/api/psapi/ns-psapi-performance_information
    public static SystemMemorySnapshot? Read()
    {
        var size = (uint)Marshal.SizeOf<PerformanceInformation>();
        if (!GetPerformanceInfo(out var data, size) || data.PageSize == 0) return null;
        return new((ulong)data.PhysicalTotal * (ulong)data.PageSize,
            (ulong)data.PhysicalAvailable * (ulong)data.PageSize,
            (ulong)data.CommitLimit * (ulong)data.PageSize,
            (ulong)data.CommitTotal * (ulong)data.PageSize);
    }

    // Budget for the selected maximum, the pack's existing native estimate, and
    // 512 MiB of headroom. This is a growth warning, not a reservation or guarantee.
    public static ulong RequiredCommitBytes(int maximumRamMb, int nativeReserveGb) =>
        checked(((ulong)maximumRamMb + (ulong)Math.Max(1, nativeReserveGb) * 1024 + 512) * MiB);

    // Only the initial heap is committed at startup. 1 GiB for early JVM/native
    // allocations plus 512 MiB safety is an admission estimate, not a measured
    // upper bound. Do not block on the entire future pack footprint.
    public static ulong StartupCommitBytes(int maximumRamMb) =>
        checked(((ulong)MinecraftProcessService.InitialHeapMbFor(maximumRamMb) + 1024 + 512) * MiB);

    public static string Describe(SystemMemorySnapshot memory) =>
        $"RAM: {Gb(memory.PhysicalAvailableBytes)} ГБ свободно из {Gb(memory.PhysicalTotalBytes)} ГБ; " +
        $"системный commit: доступно {Gb(memory.AvailableCommitBytes)} ГБ, предел {Gb(memory.CommitLimitBytes)} ГБ.";

    public static string? Rejection(SystemMemorySnapshot memory, int maximumRamMb)
    {
        var needed = StartupCommitBytes(maximumRamMb);
        return memory.AvailableCommitBytes >= needed ? null :
            $"Сейчас недостаточно доступного системного commit для запуска: нужно около {Gb(needed)} ГБ " +
            $"для начального Xms, ранних native-выделений и запаса. {Describe(memory)} " +
            "Закройте ненужные приложения и проверьте свободное место и настройки файла подкачки Windows. " +
            "Выбранный объём RAM сохранён; автоматического повтора не будет.";
    }

    public static string? GrowthWarning(SystemMemorySnapshot memory, int maximumRamMb, int nativeReserveGb) =>
        memory.AvailableCommitBytes >= RequiredCommitBytes(maximumRamMb, nativeReserveGb) ? null :
            $"Сейчас доступного системного commit меньше оценки полного роста игры " +
            $"({RequiredCommitBytes(maximumRamMb, nativeReserveGb) / (1024d * 1024 * 1024):0.0} ГБ с памятью вне heap и запасом). " +
            "Запуск с меньшим Xms разрешён, но память для дальнейшего роста до выбранного Xmx не гарантирована. " +
            "Выбранная RAM сохранена; ожидания освобождения памяти и автоматического повтора нет.";

    private static string Gb(ulong bytes) => (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture);

    public static bool NamesNativeAllocationFailure(string console, string tail)
    {
        var text = console + "\n" + tail;
        return text.Contains("os::commit_memory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Native memory allocation", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("insufficient memory for the Java Runtime Environment", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error=1455", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("errno=1455", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DOS error/errno=1455", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("paging file is too small", StringComparison.OrdinalIgnoreCase);
    }

    public static string FailureDetails(string console, string tail) =>
        (NamesNativeAllocationFailure(console, tail)
            ? Environment.NewLine + "Windows/JVM не смогли выделить системную память (native allocation/commit). " +
              "Это не переполнение Java heap. Доступный commit мог измениться после проверки; " +
              "закройте ненужные приложения и проверьте файл подкачки и свободное место. " +
              "Увеличение Xmx не исправляет этот отказ; выбранная RAM сохранена."
            : "") + console + tail;

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
            SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation information, uint size);
}
