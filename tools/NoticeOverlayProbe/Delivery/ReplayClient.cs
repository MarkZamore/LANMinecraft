using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Replays a just-observed real client using the candidate's production notice setup.</summary>
internal static class ReplayClient
{
    internal static int Run(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var dll = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        var captured = Path.GetFullPath(args[3]);
        var theme = Path.GetFullPath(args[4]);
        var allowed = Path.Combine(root, "artifacts", "agent-runs") + Path.DirectorySeparatorChar;
        if (!new[] { output, captured, theme }.All(path => path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) ||
            !dll.StartsWith(Path.Combine(root, "Program", "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(dll) != "LANMinecraft.dll") throw new ArgumentException("Use the local candidate and task-owned replay files.");
        using var snapshot = JsonDocument.Parse(File.ReadAllBytes(captured));
        var observed = snapshot.RootElement;
        try
        {
            using var old = Process.GetProcessById(observed.GetProperty("ProcessId").GetInt32());
            if (!old.HasExited) throw new InvalidOperationException("Close the original client normally before replay.");
        }
        catch (ArgumentException) { }
        var executable = observed.GetProperty("ExecutablePath").GetString()!;
        if (!executable.StartsWith(Path.Combine(root, "Minecraft", "Launcher", "JavaRuntimes") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || Path.GetFileName(executable) != "javaw.exe")
            throw new InvalidOperationException("The observed executable is not the launcher-managed Java runtime.");
        var command = SplitCommandLine(observed.GetProperty("CommandLine").GetString()!);
        var gameIndex = Array.IndexOf(command, "--gameDir");
        if (gameIndex < 0 || gameIndex + 1 >= command.Length) throw new InvalidOperationException("The observed client has no game directory.");
        var originalGame = Path.GetFullPath(command[gameIndex + 1]);
        if (!originalGame.StartsWith(Path.Combine(root, "Minecraft", "Personal", "Instances") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected source instance.");
        var game = Path.Combine(output, "game");
        if (Directory.Exists(game)) throw new InvalidOperationException("Use a fresh replay output; existing worlds must not be replaced.");
        Directory.CreateDirectory(game);
        var copied = new List<object>();
        foreach (var folder in new[] { "mods", "config", "defaultconfigs", "resourcepacks" })
        {
            var source = Path.Combine(originalGame, folder);
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(originalGame, file);
                var target = Path.Combine(game, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                copied.Add(new { path = relative, sizeBytes = new FileInfo(target).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant() });
            }
        }
        var options = Path.Combine(originalGame, "options.txt");
        if (File.Exists(options)) File.Copy(options, Path.Combine(game, "options.txt"));
        var assemblyDirectory = Path.GetDirectoryName(dll)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependency = Path.Combine(assemblyDirectory, name.Name + ".dll");
            return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        Type Type(string name) => assembly.GetType("Minecraft." + name, throwOnError: true)!;
        var paths = Activator.CreateInstance(Type("AppPaths"), root)!;
        var logger = Activator.CreateInstance(Type("Logger"), Path.Combine(output, "launcher.log"))!;
        var service = Activator.CreateInstance(Type("GameNoticeOverlayService"), paths, logger)!;
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        using var prepared = (IDisposable)Type("GameNoticeOverlayService").GetMethod("PrepareSession", any)!.Invoke(service, [false])!;
        var preparedType = prepared.GetType();
        var session = (string)preparedType.GetProperty("Directory", any)!.GetValue(prepared)!;
        File.Copy(theme, Path.Combine(session, "style.properties"), overwrite: true);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = game, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        var temporary = Path.Combine(output, "temp");
        var profile = Path.Combine(output, "profile");
        foreach (var directory in new[] { temporary, profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local") })
            Directory.CreateDirectory(directory);
        Type("PortableJavaEnvironment").GetMethod("ConfigureEnvironment", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [start.Environment, temporary, profile]);
        for (var index = 1; index < command.Length; index++)
        {
            var argument = command[index];
            if ((argument.StartsWith("-javaagent:", StringComparison.Ordinal) &&
                 argument.EndsWith("launcher-notice-agent.jar", StringComparison.OrdinalIgnoreCase)) ||
                argument.StartsWith("-Dlanminecraft.notice.directory=", StringComparison.Ordinal)) continue;
            if (index == gameIndex + 1) argument = game;
            if (argument.StartsWith("-Djava.io.tmpdir=", StringComparison.Ordinal)) argument = "-Djava.io.tmpdir=" + temporary;
            if (argument.StartsWith("-Duser.home=", StringComparison.Ordinal)) argument = "-Duser.home=" + profile;
            // The production notice flags must be JVM options, before the game's main class.
            if (index == 1)
                foreach (var option in (IEnumerable<string>)preparedType.GetProperty("JvmArguments", any)!.GetValue(prepared)!)
                    start.ArgumentList.Add(option);
            start.ArgumentList.Add(argument);
        }
        if (!start.ArgumentList.Contains("--fullscreen")) start.ArgumentList.Add("--fullscreen");
        using var process = new Process { StartInfo = start };
        var logGate = new object();
        void NoticeLog(object sender, DataReceivedEventArgs line)
        {
            if (line.Data?.Contains("[LauncherNotice]", StringComparison.Ordinal) == true)
                lock (logGate) File.AppendAllText(Path.Combine(output, "agent.log"), line.Data + Environment.NewLine);
        }
        process.OutputDataReceived += NoticeLog;
        process.ErrorDataReceived += NoticeLog;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The actual Java client did not start.");
            preparedType.GetMethod("BindProcess", any, null, [typeof(Process)], null)!.Invoke(prepared, [process]);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            File.WriteAllText(Path.Combine(output, "client-start.json"), JsonSerializer.Serialize(new
            {
                processId = process.Id, sessionDirectory = session, gameDirectory = game,
                sharedResourcesReused = true, userWorldsCopied = false, candidateAssembly = dll,
                candidateSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant(), copied
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Candidate real client started; metadata is in client-start.json.");
            process.WaitForExit();
            return process.ExitCode;
        }
        finally
        {
            if (process.Id > 0) Type("GameNoticeOverlayService").GetMethod("ProcessExited", any)!.Invoke(service, [process.Id]);
        }
    }

    private static string[] SplitCommandLine(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Cannot parse the captured Windows argument list.");
        try { return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!).ToArray(); }
        finally { _ = LocalFree(pointer); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
