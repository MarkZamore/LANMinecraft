using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private const int WarmupSeconds = 4;
    private const int SamplingSeconds = 18;
    private const int MaximumLifetimeSeconds = 50;
    private const uint ThreadAccess = 0x0002 | 0x0008 | 0x0040;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Module(ulong Base, ulong Size, string Name, string Path);
    private sealed record Sample(int ThreadId, ulong Address);
    private sealed class ThreadRecord(int id, IntPtr handle, ulong kernel, ulong user)
    {
        public readonly int Id = id;
        public readonly IntPtr Handle = handle;
        public readonly ulong InitialKernel = kernel, InitialUser = user;
        public ulong Kernel = kernel, User = user, PreviousCpu = kernel + user, RecentCpu;
        public int Samples, Errors;
        public double SuspendedMilliseconds;
    }

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("This diagnostic uses the Windows AMD64 CONTEXT layout.");
        if (args.Length == 3 && args[0] == "--resolve")
        {
            var resolveRepository = Path.GetFullPath(args[1]);
            var resolveOutput = Path.GetFullPath(args[2]);
            if (!resolveOutput.StartsWith(Path.Combine(resolveRepository, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Offline samples must remain under this checkout's artifacts.");
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(resolveOutput, "native-samples.json")));
            var root = document.RootElement;
            var priorSamples = root.GetProperty("samples").EnumerateArray().Select(value => new Sample(value.GetProperty("ThreadId").GetInt32(),
                Convert.ToUInt64(value.GetProperty("address").GetString()![2..], 16))).ToList();
            var priorModules = root.GetProperty("modules").EnumerateArray().Select(value => new Module(value.GetProperty("Base").GetUInt64(),
                value.GetProperty("Size").GetUInt64(), value.GetProperty("Name").GetString()!, value.GetProperty("Path").GetString()!)).ToArray();
            Resolve(resolveOutput, priorSamples, priorModules, localOnly: true);
            return 0;
        }
        if (args.Length != 2) throw new ArgumentException("Pass the repository and an artifacts output directory.");
        var repository = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
        var output = Path.GetFullPath(args[1]);
        var artifacts = Path.Combine(repository, "artifacts");
        if (!File.Exists(Path.Combine(repository, "Program", "Minecraft.csproj")) ||
            !output.StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The fixture and all output must belong to this launcher checkout.");
        Directory.CreateDirectory(output);
        var executable = Path.Combine(repository, "tools", "UiPreloadProbe", "bin", "Debug", "net10.0-windows", "win-x64", "UiPreloadProbe.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the existing UiPreloadProbe first.", executable);
        var executableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
        var fixtureOutput = Path.Combine(output, "fixture-frames.json");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // There is deliberately no arbitrary PID, executable, or command-line option.
        foreach (var argument in new[] { repository, fixtureOutput, "--maximized", "--native-dpi", "--backdrop", "prepared",
            "--screen", "overview", "--ticks", "500", "--theme-clear", "transparent", "--left", "200", "--top", "120" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The fixture could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var records = new Dictionary<int, ThreadRecord>();
        var modules = new Dictionary<ulong, Module>();
        var samples = new List<Sample>();
        var errors = new List<string>();
        var lifetime = Stopwatch.StartNew();
        var contextAllocation = Marshal.AllocHGlobal(1232 + 16);
        var context = new IntPtr((contextAllocation.ToInt64() + 15) & ~15L);
        var timeout = false;
        var verified = false;
        try
        {
            var actual = process.MainModule?.FileName;
            if (!string.Equals(Path.GetFullPath(actual ?? ""), executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The created process does not match the workspace fixture executable.");
            verified = true;
            while (!process.HasExited && lifetime.Elapsed.TotalSeconds < WarmupSeconds) Thread.Sleep(50);
            var sampling = Stopwatch.StartNew();
            var refreshAt = TimeSpan.Zero;
            while (!process.HasExited && sampling.Elapsed.TotalSeconds < SamplingSeconds)
            {
                if (sampling.Elapsed >= refreshAt)
                {
                    RefreshThreads(process, records);
                    RefreshModules(process, modules);
                    refreshAt = sampling.Elapsed + TimeSpan.FromMilliseconds(250);
                }
                foreach (var record in records.Values.OrderByDescending(thread => thread.RecentCpu).Take(3))
                {
                    if (record.RecentCpu == 0) continue;
                    Capture(record, context, samples);
                }
                Thread.Sleep(10);
            }
            var sampledMilliseconds = sampling.Elapsed.TotalMilliseconds;
            RefreshThreads(process, records);
            RefreshModules(process, modules);
            while (!process.HasExited && lifetime.Elapsed.TotalSeconds < MaximumLifetimeSeconds) Thread.Sleep(50);
            if (!process.HasExited)
            {
                timeout = true;
                process.Kill(); // Only the verified synthetic process created above.
            }
            process.WaitForExit();
            File.WriteAllText(Path.Combine(output, "fixture.stdout.log"), stdout.GetAwaiter().GetResult());
            File.WriteAllText(Path.Combine(output, "fixture.stderr.log"), stderr.GetAwaiter().GetResult());
            var raw = new
            {
                executable, executableSha256, processId = process.Id, verified, timeout, exitCode = process.ExitCode,
                warmupSeconds = WarmupSeconds, samplingSeconds = sampledMilliseconds / 1000,
                note = "Native RIP snapshots of recently CPU-active fixture threads, including possible waits. These are not ETW CPU samples or FPS acceptance data.",
                threads = records.Values.Select(thread => new
                {
                    thread.Id, kernelMilliseconds = (thread.Kernel - thread.InitialKernel) / 10_000d,
                    userMilliseconds = (thread.User - thread.InitialUser) / 10_000d,
                    cpuMilliseconds = (thread.Kernel + thread.User - thread.InitialKernel - thread.InitialUser) / 10_000d,
                    thread.Samples, thread.Errors, thread.SuspendedMilliseconds
                }).OrderByDescending(thread => thread.cpuMilliseconds),
                modules = modules.Values.OrderBy(module => module.Base),
                samples = samples.Select(sample => new { sample.ThreadId, address = "0x" + sample.Address.ToString("x") }), errors
            };
            File.WriteAllText(Path.Combine(output, "native-samples.json"), JsonSerializer.Serialize(raw, Json));
            var moduleSummary = samples.GroupBy(sample => new
                { sample.ThreadId, Module = modules.Values.FirstOrDefault(module => sample.Address >= module.Base && sample.Address < module.Base + module.Size)?.Name ?? "JIT or unmapped" })
                .Select(group => new { group.Key.ThreadId, group.Key.Module, Count = group.Count() }).OrderByDescending(group => group.Count).ToArray();
            File.WriteAllText(Path.Combine(output, "native-modules.json"), JsonSerializer.Serialize(moduleSummary, Json));
            Console.WriteLine("Fixture exited. Native samples and module counts saved; resolving symbols after all threads have resumed.");
            // The child has exited. No thread is suspended during symbol lookup or downloads.
            Resolve(output, samples, modules.Values.ToArray());
            Console.WriteLine($"Sampled {samples.Count} instruction pointers from {records.Count} fixture threads; fixture exit {process.ExitCode}.");
            return timeout || process.ExitCode != 0 ? 1 : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(contextAllocation);
            foreach (var thread in records.Values) Native.CloseHandle(thread.Handle);
            if (verified && !process.HasExited) process.Kill();
        }
    }

    private static void RefreshThreads(Process process, Dictionary<int, ThreadRecord> records)
    {
        if (process.HasExited) return;
        process.Refresh();
        foreach (ProcessThread thread in process.Threads)
        {
            if (records.ContainsKey(thread.Id)) continue;
            var handle = Native.OpenThread(ThreadAccess, false, (uint)thread.Id);
            if (handle == IntPtr.Zero) continue;
            if (Native.GetProcessIdOfThread(handle) != process.Id ||
                !Native.GetThreadTimes(handle, out _, out _, out var kernel, out var user))
            { Native.CloseHandle(handle); continue; }
            records.Add(thread.Id, new ThreadRecord(thread.Id, handle, kernel, user));
        }
        foreach (var record in records.Values)
        {
            if (!Native.GetThreadTimes(record.Handle, out _, out _, out var kernel, out var user)) continue;
            record.Kernel = kernel; record.User = user;
            var cpu = kernel + user;
            record.RecentCpu = cpu - record.PreviousCpu;
            record.PreviousCpu = cpu;
        }
    }

    private static void RefreshModules(Process process, Dictionary<ulong, Module> modules)
    {
        if (process.HasExited) return;
        foreach (ProcessModule module in process.Modules)
        {
            var address = (ulong)module.BaseAddress.ToInt64();
            modules.TryAdd(address, new Module(address, (ulong)module.ModuleMemorySize, module.ModuleName, module.FileName));
        }
    }

    private static void Capture(ThreadRecord thread, IntPtr context, List<Sample> samples)
    {
        // Windows SDK winnt.h AMD64 CONTEXT: aligned16, ContextFlags+48, Rip+248.
        Marshal.WriteInt32(context, 48, 0x00100001); // CONTEXT_AMD64 | CONTEXT_CONTROL
        var previous = Native.SuspendThread(thread.Handle);
        if (previous == uint.MaxValue) { thread.Errors++; return; }
        var suspended = Stopwatch.GetTimestamp();
        ulong address = 0;
        try
        {
            // Respect an existing suspension owned by another debugger.
            if (previous != 0) return;
            if (!Native.GetThreadContext(thread.Handle, context)) { thread.Errors++; return; }
            address = (ulong)Marshal.ReadInt64(context, 248);
        }
        finally
        {
            var resumed = Native.ResumeThread(thread.Handle);
            thread.SuspendedMilliseconds += Stopwatch.GetElapsedTime(suspended).TotalMilliseconds;
            if (resumed == uint.MaxValue)
                throw new InvalidOperationException($"Could not resume fixture thread {thread.Id}: {Marshal.GetLastWin32Error()}.");
        }
        // Even managed list/object allocation waits until the target is resumed.
        if (address != 0) { samples.Add(new Sample(thread.Id, address)); thread.Samples++; }
    }

    private static void Resolve(string output, List<Sample> samples, Module[] modules, bool localOnly = false)
    {
        var symbols = Path.Combine(output, "symbols");
        Directory.CreateDirectory(symbols);
        var debugger = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Debuggers", "x64");
        var library = Path.Combine(debugger, "dbghelp.dll");
        if (File.Exists(library))
        {
            var symsrv = Path.Combine(debugger, "symsrv.dll");
            if (File.Exists(symsrv)) NativeLibrary.Load(symsrv);
            var handle = NativeLibrary.Load(library);
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) => name == "dbghelp.dll" ? handle : IntPtr.Zero);
        }
        var session = new IntPtr(1); // Offline symbol session: no remote process memory access.
        Native.SymSetOptions(0x00000002 | 0x00000004 | 0x00000200 | 0x00080000);
        if (!Native.SymInitialize(session, localOnly ? symbols : "srv*" + symbols + "*https://msdl.microsoft.com/download/symbols", false))
            throw new InvalidOperationException("Could not initialize symbols: " + Marshal.GetLastWin32Error());
        var names = new Dictionary<ulong, string>();
        var symbolBuffer = Marshal.AllocHGlobal(88 + 1024);
        try
        {
            var busyAddresses = samples.GroupBy(sample => sample.Address).OrderByDescending(group => group.Count()).ToArray();
            var usedModules = modules.Where(module => busyAddresses.Any(group => group.Key >= module.Base && group.Key < module.Base + module.Size)).ToArray();
            // The diagnostic asks which WPF raster routines consume the busy
            // render thread. Other modules remain identified without downloading
            // unrelated CLR, DirectWrite or Windows PDBs.
            foreach (var module in usedModules.Where(module => module.Name.Equals("wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase)))
                Native.SymLoadModuleEx(session, IntPtr.Zero, module.Path, null, module.Base, (uint)module.Size, IntPtr.Zero, 0);
            foreach (var group in busyAddresses)
            {
                var module = usedModules.FirstOrDefault(module => group.Key >= module.Base && group.Key < module.Base + module.Size);
                if (module is null) { names[group.Key] = "JIT or unmapped address"; continue; }
                if (!module.Name.Equals("wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase))
                { names[group.Key] = module.Name + " (module only)"; continue; }
                Marshal.WriteInt32(symbolBuffer, 0, 88);
                Marshal.WriteInt32(symbolBuffer, 80, 1024);
                if (Native.SymFromAddr(session, group.Key, out var displacement, symbolBuffer))
                {
                    var length = Marshal.ReadInt32(symbolBuffer, 76);
                    var name = Marshal.PtrToStringAnsi(symbolBuffer + 84, Math.Clamp(length, 0, 1023));
                    names[group.Key] = module.Name + "!" + name;
                }
                else names[group.Key] = module.Name + "+0x" + (group.Key - module.Base).ToString("x");
            }
            var summary = samples.GroupBy(sample => new { sample.ThreadId, Function = names[sample.Address] })
                .Select(group => new { group.Key.ThreadId, group.Key.Function, Count = group.Count() })
                .OrderByDescending(group => group.Count).ToArray();
            File.WriteAllText(Path.Combine(output, "native-functions.json"), JsonSerializer.Serialize(summary, Json));
            foreach (var row in summary.Take(20)) Console.WriteLine($"T{row.ThreadId}: {row.Count,5} {row.Function}");
        }
        finally { Marshal.FreeHGlobal(symbolBuffer); Native.SymCleanup(session); }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessIdOfThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetThreadTimes(IntPtr thread, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint SuspendThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)] internal static extern bool SymInitialize(IntPtr process, string search, bool invade);
        [DllImport("dbghelp.dll")] internal static extern uint SymSetOptions(uint options);
        [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)] internal static extern ulong SymLoadModuleEx(IntPtr process, IntPtr file, string image, string? module, ulong address, uint size, IntPtr data, uint flags);
        [DllImport("dbghelp.dll", SetLastError = true)] internal static extern bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
        [DllImport("dbghelp.dll")] internal static extern bool SymCleanup(IntPtr process);
    }
}
