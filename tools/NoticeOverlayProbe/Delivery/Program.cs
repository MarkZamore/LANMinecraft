using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// Drive the public delivery API of an existing launcher-started game. This is
// an external QA console, not a mod, injected agent or alternative renderer.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 6 && args[0] == "--launch") return ReplayClient.Run(args[1..]);
        if (args.Length is not (5 or 6)) throw new ArgumentException("root launcher-dll output-directory game-pid delay-ms [repeat-test-ms]");
        var root = Path.GetFullPath(args[0]);
        var dll = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        var pid = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var delay = int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        var repeat = args.Length == 6 ? int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 0;
        if ((!dll.StartsWith(Path.Combine(root, "Minecraft", "Launcher", "Runtime") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
             !dll.StartsWith(Path.Combine(root, "Program", "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(dll) != "LANMinecraft.dll" ||
            !output.StartsWith(Path.Combine(root, "artifacts", "agent-runs") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || delay is < 0 or > 30000 || repeat is < 0 or > 30000)
            throw new ArgumentException("Use a published launcher assembly and a task-owned output directory.");
        Directory.CreateDirectory(output);
        Thread.Sleep(delay);
        var before = GetForegroundWindow();
        _ = GetWindowThreadProcessId(before, out var foregroundPid);
        if (foregroundPid != pid) throw new InvalidOperationException("The requested game must have foreground focus before delivery.");
        var sessions = Path.Combine(root, "Minecraft", "Launcher", "NoticeOverlay", "Sessions");
        var live = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(sessions))
        {
            var metadata = Path.Combine(directory, "session.json");
            if (!File.Exists(metadata)) continue;
            using var json = JsonDocument.Parse(File.ReadAllBytes(metadata));
            var candidatePid = json.RootElement.GetProperty("ProcessId").GetInt32();
            var ticks = json.RootElement.GetProperty("StartedUtcTicks").GetInt64();
            Process process;
            try { process = Process.GetProcessById(candidatePid); }
            catch (ArgumentException) { continue; }
            using (process)
            {
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != ticks) continue;
                // Adopting sessions clears their old messages, so never touch
                // another concurrently running game during this diagnostic.
                if (candidatePid != pid) throw new InvalidOperationException("Another game's notice session is active; coordinate first.");
                live.Add(directory);
            }
        }
        if (live.Count != 1) throw new InvalidOperationException("Expected exactly one live notice session for the requested game.");
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(before, out var bounds) || !GetMonitorInfo(MonitorFromWindow(before, 2), ref info))
            throw new InvalidOperationException("Cannot read the actual game window and monitor bounds.");
        var fillsMonitor = bounds.Equals(info.Monitor);
        var caption = (GetWindowLongPtr(before, -16).ToInt64() & 0x00C00000) != 0;
        if (!fillsMonitor || caption) throw new InvalidOperationException("The actual game window is not fullscreen.");
        var assemblyDirectory = Path.GetDirectoryName(dll)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependency = Path.Combine(assemblyDirectory, name.Name + ".dll");
            return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        Type Type(string name) => assembly.GetType("Minecraft." + name, throwOnError: true)!;
        var paths = Activator.CreateInstance(Type("AppPaths"), root)!;
        var logger = Activator.CreateInstance(Type("Logger"), Path.Combine(output, "delivery.log"))!;
        var service = Activator.CreateInstance(Type("GameNoticeOverlayService"), paths, logger)!;
        var notice = Activator.CreateInstance(Type("PeerNotice"), "Проверка уведомления лаунчера",
            "Java Edition: уведомление поверх полноэкранной игры")!;
        var delivered = (bool)Type("GameNoticeOverlayService").GetMethod("TryShow")!.Invoke(service, [notice])!;
        Thread.Sleep(1000);
        var heartbeat = File.ReadAllLines(Path.Combine(live[0], "heartbeat.properties"));
        var rendered = heartbeat.Any(line => line.StartsWith("renderedCount=", StringComparison.Ordinal) &&
            int.TryParse(line["renderedCount=".Length..], out var count) && count > 0);
        var foregroundUnchanged = before == GetForegroundWindow();
        var evidence = new
        {
            launcherVersion = FileVersionInfo.GetVersionInfo(dll).FileVersion,
            launcherAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant(),
            gamePid = pid, fullscreenWindowFillsMonitor = fillsMonitor, windowCaptionAbsent = !caption,
            deliveredThroughPublicLauncherService = delivered, renderedCountPositive = rendered,
            foregroundUnchanged, heartbeat, capturedAtUtc = DateTimeOffset.UtcNow,
            newModInstalled = false, repeatTestDurationMs = repeat,
            visualEvidence = "Capture the running game's window separately while this notification is visible."
        };
        File.WriteAllText(Path.Combine(output, "delivery.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { delivered, rendered, foregroundUnchanged }));
        var repeatedFor = Stopwatch.StartNew();
        while (repeatedFor.ElapsedMilliseconds < repeat)
        {
            Thread.Sleep(3000);
            _ = Type("GameNoticeOverlayService").GetMethod("TryShow")!.Invoke(service, [notice]);
        }
        // Keep the real production DPI timer alive through the notification's lifetime.
        Thread.Sleep(7500);
        GC.KeepAlive(service);
        return delivered && rendered && foregroundUnchanged ? 0 : 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
