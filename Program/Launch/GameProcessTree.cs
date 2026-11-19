using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Minecraft;

/// <summary>Identifies launcher-started native children without matching executable names.</summary>
internal static class GameProcessTree
{
    internal static bool Contains(int root, int candidate) => root > 0 && candidate > 0 &&
        (root == candidate || BelongsTo(root, candidate, Snapshot()));

    internal static int[] Expand(IEnumerable<int> roots)
    {
        var result = roots.Where(pid => pid > 0).ToHashSet();
        if (result.Count == 0) return [];
        var parents = Snapshot();
        foreach (var candidate in parents.Keys)
            if (result.Any(root => BelongsTo(root, candidate, parents))) result.Add(candidate);
        return result.ToArray();
    }

    internal static bool BelongsTo(int root, int candidate, IReadOnlyDictionary<int, int> parents)
    {
        if (root <= 0 || candidate <= 0) return false;
        var seen = new HashSet<int>();
        for (var depth = 0; depth < 128 && candidate > 0 && seen.Add(candidate); depth++)
        {
            if (candidate == root) return true;
            if (!parents.TryGetValue(candidate, out candidate)) return false;
        }
        return false;
    }

    private static Dictionary<int, int> Snapshot()
    {
        var result = new Dictionary<int, int>();
        using var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle.IsInvalid) return result;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        if (!Process32First(handle, ref entry)) return result;
        do { result[unchecked((int)entry.ProcessId)] = unchecked((int)entry.ParentProcessId); }
        while (Process32Next(handle, ref entry));
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr Heap;
        public uint Module, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
