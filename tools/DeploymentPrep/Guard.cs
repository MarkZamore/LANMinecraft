using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

sealed record FileStamp(string Kind, string RelativePath, long SizeBytes, string Sha256, string? SemanticSha256 = null);
sealed record LinkStamp(string RelativePath, string Target);
sealed record InstallationSnapshot(List<FileStamp> Files, List<LinkStamp> Links, long HashedBytes);

static class Guard
{
    public const string WorldMetadata = ".minecraft-portable-world.json";
    public static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static void OrdinaryParents(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A deployment path passes through a link: " + current);
    }
    public static string Under(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new IOException("Expected a relative deployment path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new IOException("Deployment path escaped its root.");
        return path;
    }

    // Command lines are inspected in memory and never written to reports: they
    // can contain launcher session credentials. Process existence alone does
    // not confuse javap/preflight workers with a running Minecraft client.
    public static object[] ActiveProcesses(string root)
    {
        var active = new List<object>();
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string name;
                try { name = process.ProcessName; if (process.SessionId != session) continue; }
                catch { continue; }
                if (name.Equals("LANMinecraft", StringComparison.OrdinalIgnoreCase))
                {
                    string? executable;
                    try { executable = process.MainModule?.FileName; } catch { executable = null; }
                    if (executable is null || executable.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        active.Add(new { processId = process.Id, name, reason = "launcher may write this installation" });
                }
                else if (name.Equals("java", StringComparison.OrdinalIgnoreCase) || name.Equals("javaw", StringComparison.OrdinalIgnoreCase))
                {
                    var command = CommandLine(process.Id);
                    var inRoot = command?.Contains(root, StringComparison.OrdinalIgnoreCase) == true;
                    var game = command is null || command.Contains("--gameDir", StringComparison.OrdinalIgnoreCase) ||
                        command.Contains("--launchTarget", StringComparison.OrdinalIgnoreCase) ||
                        command.Contains("net.minecraft.client.main.Main", StringComparison.Ordinal) ||
                        command.Contains("net.minecraft.server.Main", StringComparison.Ordinal);
                    if (command is null || inRoot && game)
                        active.Add(new { processId = process.Id, name, reason = command is null ? "unreadable Java command line" : "Minecraft process uses this installation" });
                }
            }
        }
        // Read session notes without ClientPresenceService.ReadLiveSessions,
        // which removes stale notes and would make dry-run mutate the root.
        var sessions = Path.Combine(root, "Minecraft", "Personal", "ClientSessions");
        if (Directory.Exists(sessions)) foreach (var file in Directory.EnumerateFiles(sessions, "*.json"))
        {
            var node = JsonNode.Parse(File.ReadAllText(file));
            if (node?["processId"]?.GetValue<int>() is not { } pid) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                var started = node?["startedUtc"]?.GetValue<DateTime>();
                if (process.ProcessName.StartsWith("java", StringComparison.OrdinalIgnoreCase) && started.HasValue &&
                    Math.Abs((process.StartTime.ToUniversalTime() - started.Value.ToUniversalTime()).TotalSeconds) < 1.5)
                    active.Add(new { processId = pid, name = process.ProcessName, reason = "live production session note" });
            }
            catch (ArgumentException) { }
        }
        return active.ToArray();
    }
    public static void RequireStopped(string root)
    { if (ActiveProcesses(root).Length != 0) throw new IOException("Close the launcher and Minecraft before applying deployment."); }

    public static InstallationSnapshot Snapshot(string root, IReadOnlyDictionary<string, string> renames, bool minecraftScope,
        Func<string, string>? normalizeOptions = null, Func<string, bool>? recognizedTransport = null)
    {
        var files = new List<FileStamp>(); var links = new List<LinkStamp>(); long bytes = 0;
        void Stamp(string path, string kind)
        {
            var info = new FileInfo(path); var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            files.Add(new(kind, relative, info.Length, Hash(path), kind == "world" && info.Name == WorldMetadata
                ? SemanticHash(File.ReadAllText(path), renames) : kind == "options" && normalizeOptions is not null
                ? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizeOptions(File.ReadAllText(path))))).ToLowerInvariant() : null));
            bytes += info.Length;
            if (kind == "world" && info.Name == "session.lock")
            { using var available = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        }
        void WorldTree(string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("An unexpected link exists inside world data: " + item);
                if (Directory.Exists(item)) WorldTree(item); else Stamp(item, "world");
            }
        }
        var worlds = Path.Combine(root, "Minecraft", "Worlds");
        OrdinaryParents(worlds);
        WorldTree(worlds);
        var instances = Path.Combine(root, "Minecraft", "Personal", "Instances");
        if (Directory.Exists(instances)) foreach (var instance in Directory.EnumerateDirectories(instances))
        {
            OrdinaryParents(instance);
            var saves = Path.Combine(instance, "saves");
            if (Directory.Exists(saves))
            {
                if ((File.GetAttributes(saves) & FileAttributes.ReparsePoint) != 0)
                {
                    var target = new DirectoryInfo(saves).ResolveLinkTarget(true)?.FullName ?? throw new IOException("Broken saves link.");
                    if (!target.StartsWith(worlds + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Saves link points outside the portable worlds tree.");
                    if (!Normalize(Path.GetRelativePath(root, target).Replace('\\', '/'), renames).Equals(
                        Normalize("Minecraft/Worlds/" + Path.GetFileName(instance), renames), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Saves link points to another profile's worlds.");
                    links.Add(new(Path.GetRelativePath(root, saves).Replace('\\', '/'), target));
                }
                else
                {
                    WorldTree(saves);
                    // Ordinary saves may be migrated by production into the
                    // corresponding shared world root; track the same mapping.
                    links.Add(new(Path.GetRelativePath(root, saves).Replace('\\', '/'), Path.Combine(worlds, Path.GetFileName(instance))));
                }
            }
            foreach (var name in new[] { "options.txt", "usercache.json", "usernamecache.json" })
                if (File.Exists(Path.Combine(instance, name))) Stamp(Path.Combine(instance, name), minecraftScope &&
                    name == "options.txt" && Path.GetFileName(instance) is "Minecraft 26.2" or "Minecraft 26.3" ? "options" : "personal");
        }
        foreach (var parent in new[] { "Minecraft/Packs", "Minecraft/Personal/Instances" })
        {
            var directory = Path.Combine(root, parent);
            if (!Directory.Exists(directory)) continue;
            foreach (var pack in Directory.EnumerateDirectories(directory))
            {
                var mods = Path.Combine(pack, "mods");
                OrdinaryParents(mods);
                if (!Directory.Exists(mods)) continue;
                foreach (var path in Directory.EnumerateFiles(mods, "*.jar"))
                {
                    OrdinaryParents(path);
                    if (Path.GetFileName(path).StartsWith("e4steam", StringComparison.OrdinalIgnoreCase) && recognizedTransport?.Invoke(path) == true) continue;
                    // Only the exact previously shipped EMI is retired by this
                    // specific upgrade; changed/user files remain protected.
                    if (minecraftScope && Path.GetFileName(pack) is "Minecraft 26.2" or "Minecraft 26.3" &&
                        Hash(path) == "72e4e63f3ba39c149d15681a864282c116d3cc064e978f0848958cd1bf71058e") continue;
                    Stamp(path, "protectedJar");
                }
            }
        }
        foreach (var name in new[] { "identity-profiles.properties", "skin-profiles.properties" })
        {
            var path = Path.Combine(root, "Minecraft", "Personal", name);
            if (File.Exists(path)) Stamp(path, "identity");
        }
        var settingsPath = Path.Combine(root, "Minecraft", "Personal", "settings.json");
        if (File.Exists(settingsPath))
        {
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? throw new IOException("Settings JSON is invalid.");
            var skin = settings["skinPath"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(skin))
            {
                var path = Path.IsPathRooted(skin) ? skin : Path.Combine(root, skin);
                if (File.Exists(path)) Stamp(path, "skin");
            }
        }
        return new(files, links, bytes);
    }

    public static object Verify(InstallationSnapshot before, InstallationSnapshot after, string root,
        IReadOnlyDictionary<string, string> renames)
    {
        Dictionary<string, string> Links(InstallationSnapshot snapshot) => snapshot.Links.ToDictionary(
            link => Normalize(link.RelativePath, renames), link => Normalize(Path.GetRelativePath(root, link.Target).Replace('\\', '/'), renames),
            StringComparer.OrdinalIgnoreCase);
        var expectedLinks = Links(before); var actualLinks = Links(after);
        if (expectedLinks.Count != actualLinks.Count || expectedLinks.Any(pair =>
            !actualLinks.TryGetValue(pair.Key, out var target) || !pair.Value.Equals(target, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A saves mapping was added, removed, or changed during deployment.");
        var current = after.Files.ToDictionary(file => Normalize(file.RelativePath, renames), StringComparer.OrdinalIgnoreCase);
        var conflicts = Path.Combine(root, "Minecraft", "Personal", "PackConflicts");
        var preserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(conflicts)) foreach (var file in Directory.EnumerateFiles(conflicts, "*", SearchOption.AllDirectories))
            preserved.TryAdd(Hash(file), Path.GetRelativePath(root, file).Replace('\\', '/'));
        var quarantined = new List<object>();
        foreach (var original in before.Files)
        {
            var key = Normalize(original.RelativePath, renames);
            if (current.TryGetValue(key, out var item) && (original.SemanticSha256 is not null
                ? original.SemanticSha256 == item.SemanticSha256 : original.Sha256 == item.Sha256)) continue;
            if (original.Kind == "protectedJar" && preserved.TryGetValue(original.Sha256, out var moved))
            { quarantined.Add(new { original.RelativePath, preservedAt = moved, original.Sha256 }); continue; }
            throw new IOException("Protected deployment data changed: " + original.RelativePath);
        }
        var priorWorlds = before.Files.Where(file => file.Kind == "world").Select(file => Normalize(file.RelativePath, renames))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = after.Files.Where(file => file.Kind == "world" && !priorWorlds.Contains(Normalize(file.RelativePath, renames))).ToArray();
        if (added.Any(file => Path.GetFileName(file.RelativePath) != WorldMetadata))
            throw new IOException("Unexpected world data was created during deployment.");
        return new { status = "PASS", checkedFiles = before.Files.Count, before.HashedBytes, quarantined,
            addedOwnedWorldMetadata = added.Length, worldFiles = before.Files.Count(file => file.Kind == "world"), checkedSavesMappings = expectedLinks.Count };
    }

    static string Normalize(string relative, IReadOnlyDictionary<string, string> renames)
    {
        var parts = relative.Split('/');
        for (var index = 0; index < parts.Length; index++) if (renames.TryGetValue(parts[index], out var replacement)) parts[index] = replacement;
        if (parts.Length > 5 && parts[0] == "Minecraft" && parts[1] == "Personal" && parts[2] == "Instances" && parts[4] == "saves")
            return "Minecraft/Worlds/" + parts[3] + "/" + string.Join('/', parts.Skip(5));
        return string.Join('/', parts);
    }
    static string SemanticHash(string json, IReadOnlyDictionary<string, string> renames)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new IOException("Invalid owned world metadata.");
        foreach (var property in new[] { "buildRelativePath", "buildName" })
            if (node[property] is JsonValue value && value.TryGetValue<string>(out var name) && renames.TryGetValue(name, out var replacement))
                node[property] = replacement;
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()))).ToLowerInvariant();
    }
    [StructLayout(LayoutKind.Sequential)] struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr handle, int kind, IntPtr buffer, int length, out int needed);
    static string? CommandLine(int id)
    {
        var handle = OpenProcess(0x1000, false, id); if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var needed);
            if (needed is <= 0 or > 1024 * 1024) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, 60, buffer, needed, out _) < 0) return null;
                var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                return Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(handle); }
    }
}
