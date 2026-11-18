using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

internal sealed record BootstrapComponent(string Kind, string Id, string Root, string EntryPath,
    string Sha256, string ManifestPath, string ManifestSha256);

/// <summary>The native host's frozen, locally verified component selection. Never downloads files.</summary>
internal sealed class BootstrapLaunchContext
{
    internal const string EnvironmentVariable = "LANMINECRAFT_BOOTSTRAP_CONTEXT";
    private static readonly Lazy<BootstrapLaunchContext> Loaded = new(Load);
    private static BootstrapLaunchContext? testContext;
    private readonly IReadOnlyDictionary<string, ComponentFiles> files;
    internal static BootstrapLaunchContext Current => Volatile.Read(ref testContext) ?? Loaded.Value;
    internal string ExePath { get; }
    internal string InstallationRoot => Path.GetDirectoryName(ExePath)!;
    internal string AppRoot { get; }
    internal string AppSha256 => Path.GetFileName(AppRoot);
    internal IReadOnlyList<BootstrapComponent> Components { get; }

    private BootstrapLaunchContext(string exePath, string appRoot, BootstrapComponent[] components, bool fixture)
    {
        ExePath = FullPath(exePath);
        AppRoot = FullPath(appRoot);
        components = components.Select(component => component with
            { Root = FullPath(component.Root), ManifestPath = FullPath(component.ManifestPath) }).ToArray();
        Components = Array.AsReadOnly(components);
        if (components.Length != 3 || components.Select(component => component.Kind).Distinct(StringComparer.Ordinal).Count() != 3 ||
            components.Any(component => component.Kind is not ("desktop-runtime" or "assets" or "steam-native")))
            throw new InvalidDataException("Incomplete native bootstrap component selection.");
        var parsed = new Dictionary<string, ComponentFiles>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            RequireHash(component.Sha256); RequireHash(component.ManifestSha256);
            if (string.IsNullOrWhiteSpace(component.Id)) throw new InvalidDataException("Missing bootstrap component identity.");
            if (!fixture)
            {
                var expected = Path.Combine(InstallationRoot, "Minecraft", "Launcher", "Bootstrap", component.Kind, component.Sha256);
                RequireSame(component.Root, expected);
                RequireSame(component.ManifestPath, expected + ".manifest");
            }
            Ordinary(component.Root); Ordinary(component.ManifestPath);
            var manifest = ReadManifest(component);
            parsed.Add(component.Kind, manifest);
        }
        files = new ReadOnlyDictionary<string, ComponentFiles>(parsed);
    }

    internal string Resolve(string kind, string relativePath)
    {
        if (!files.TryGetValue(kind, out var component)) throw new InvalidDataException("Missing bootstrap component " + kind);
        ValidateRelative(relativePath);
        if (!component.Records.TryGetValue(relativePath, out var record))
            throw new InvalidDataException("Unlisted bootstrap component file: " + relativePath);
        var path = Path.GetFullPath(Path.Combine(component.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(component.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bootstrap asset escapes its component.");
        Ordinary(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != record.Size || Convert.ToHexStringLower(SHA256.HashData(input)) != record.Sha256)
            throw new InvalidDataException("Bootstrap component file failed verification: " + relativePath);
        return path;
    }

    internal string RequiredKindRoot(string kind) => Components.Single(component => component.Kind == kind).Root;

    internal bool ContainsFile(string kind, string relativePath)
    {
        ValidateRelative(relativePath);
        return files.TryGetValue(kind, out var component) && component.Records.ContainsKey(relativePath);
    }
    internal static bool TryGet(out BootstrapLaunchContext? context)
    {
        if (Volatile.Read(ref testContext) is { } fixture) { context = fixture; return true; }
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(EnvironmentVariable))) { context = null; return false; }
        context = Current; return true;
    }

    // Only the friend test assembly installs checked, external component fixtures.
    internal static void SetTestComponents(string fixtureRoot, BootstrapComponent[] components) =>
        Volatile.Write(ref testContext, CreateTestContext(fixtureRoot, components));
    internal static BootstrapLaunchContext CreateTestContext(string fixtureRoot, BootstrapComponent[] components) =>
        new(Path.Combine(fixtureRoot, "fixture.exe"), fixtureRoot, components, fixture: true);

    internal static void InitializeTestComponentsFromEnvironment()
    {
        // Technical hosts may execute through the real bootstrap: never replace that selection.
        if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(EnvironmentVariable))) return;
        var root = System.Environment.GetEnvironmentVariable("LANMINECRAFT_DESKTOP_TEST_COMPONENTS")
            ?? throw new InvalidOperationException("Run tools/Prepare-DesktopComponentTests.ps1 before technical launcher tools.");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "desktop-components.json")));
        var components = document.RootElement.GetProperty("components").EnumerateArray().Select(row =>
        {
            var kind = Text(row, "kind");
            var store = TestComponentStore(root, kind);
            return new BootstrapComponent(kind, Text(row, "id"), Path.Combine(store, kind), Text(row, "entryPath"),
                Text(row, "sha256"), Path.Combine(store, kind + ".manifest"), Text(row, "manifestSha256"));
        }).ToArray();
        SetTestComponents(root, components);
        foreach (var origin in new[] { AppContext.BaseDirectory, root })
        for (var directory = new DirectoryInfo(origin); directory is not null; directory = directory.Parent)
        {
            var icon = Path.Combine(directory.FullName, "Program", "Minecraft.ico");
            if (!File.Exists(icon)) continue;
            LauncherWindowIcon.TestIconPath = icon;
            return;
        }
        throw new FileNotFoundException("The technical launcher icon source was not found.");
    }

    internal static string TestComponentStore(string fixtureRoot, string kind)
    {
        var locations = Path.Combine(fixtureRoot, "locations.json");
        if (!File.Exists(locations)) return fixtureRoot;
        Ordinary(locations);
        using var input = File.OpenRead(locations);
        using var document = JsonDocument.Parse(input);
        var subdirectory = Text(document.RootElement, kind);
        ValidateRelative(subdirectory);
        if (subdirectory.Contains('/')) throw new InvalidDataException("Unexpected desktop test component store.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(fixtureRoot))
            ?? throw new InvalidDataException("Missing desktop test cache parent.");
        var store = Path.Combine(parent, subdirectory);
        Ordinary(store);
        return store;
    }

    private static BootstrapLaunchContext Load()
    {
        var session = FullPath(System.Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? throw new InvalidOperationException("Запустите LANMinecraft.exe для подготовки компонентов лаунчера."));
        var exe = FullPath(System.Environment.ProcessPath ?? throw new InvalidOperationException("Missing outer launcher executable."));
        var bootstrap = Path.Combine(Path.GetDirectoryName(exe)!, "Minecraft", "Launcher", "Bootstrap");
        RequireSame(session, Path.Combine(bootstrap, "sessions", System.Environment.ProcessId + ".json"));
        Ordinary(session);
        using var input = new FileStream(session, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64 * 1024) throw new InvalidDataException("Oversized bootstrap launch context.");
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        SteamComponentCatalog.UniqueProperties(root);
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported bootstrap launch context.");
        var exePath = root.GetProperty("exePath").GetString()!;
        RequireSame(exePath, exe);
        var appRoot = FullPath(root.GetProperty("appRoot").GetString()!);
        RequireHash(Path.GetFileName(appRoot));
        RequireSame(appRoot, Path.Combine(bootstrap, "app", Path.GetFileName(appRoot)));
        Ordinary(appRoot);
        var components = root.GetProperty("components").EnumerateArray().Select(row => new BootstrapComponent(
            Text(row, "kind"), Text(row, "id"), Text(row, "root"), Text(row, "entryPath"),
            Text(row, "sha256"), Text(row, "manifestPath"), Text(row, "manifestSha256"))).ToArray();
        return new BootstrapLaunchContext(exePath, appRoot, components, fixture: false);
    }

    private static ComponentFiles ReadManifest(BootstrapComponent component)
    {
        byte[] bytes;
        using (var input = File.Open(component.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length is < 72 or > 8 * 1024 * 1024) throw new InvalidDataException("Invalid bootstrap manifest bounds.");
            bytes = new byte[input.Length]; input.ReadExactly(bytes);
        }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != component.ManifestSha256)
            throw new InvalidDataException("Bootstrap manifest SHA-256 mismatch.");
        using var reader = new BinaryReader(new MemoryStream(bytes), new UTF8Encoding(false, true));
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "LMHOST01" || reader.ReadUInt32() != 1)
            throw new InvalidDataException("Unsupported bootstrap manifest.");
        var count = reader.ReadUInt32(); var entryLength = reader.ReadUInt32(); var reserved = reader.ReadUInt32();
        var tableLength = reader.ReadUInt64(); var dataLength = reader.ReadUInt64();
        var bodyHash = reader.ReadBytes(32);
        if (count is 0 or > 4096 || reserved != 0 || entryLength is 0 or > 1024 ||
            tableLength != (ulong)bytes.Length - 72 || tableLength < entryLength || dataLength > 512UL * 1024 * 1024 || bodyHash.Length != 32)
            throw new InvalidDataException("Invalid bootstrap manifest header.");
        var entry = ReadRelative(reader, entryLength);
        if (entry != component.EntryPath) throw new InvalidDataException("Bootstrap entry point differs from selection.");
        var records = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        ulong nextOffset = 0;
        for (var index = 0; index < count; index++)
        {
            var path = ReadRelative(reader, reader.ReadUInt32());
            var offset = reader.ReadUInt64(); var size = reader.ReadUInt64();
            var digest = reader.ReadBytes(32);
            if (digest.Length != 32 || offset != nextOffset || size > dataLength - nextOffset || size > long.MaxValue ||
                !records.TryAdd(path, new FileRecord((long)size, Convert.ToHexStringLower(digest))))
                throw new InvalidDataException("Duplicate or invalid bootstrap manifest file.");
            nextOffset += size;
        }
        if (reader.BaseStream.Position != bytes.Length || nextOffset != dataLength || !records.ContainsKey(entry))
            throw new InvalidDataException("Incomplete bootstrap manifest.");
        return new ComponentFiles(Path.GetFullPath(component.Root), records);
    }

    private static string ReadRelative(BinaryReader reader, uint length)
    {
        if (length is 0 or > 1024) throw new InvalidDataException("Invalid bootstrap relative path length.");
        var bytes = reader.ReadBytes((int)length);
        if (bytes.Length != length) throw new InvalidDataException("Truncated bootstrap manifest.");
        var path = new UTF8Encoding(false, true).GetString(bytes); ValidateRelative(path); return path;
    }

    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Any(value => char.IsControl(value) || "\\:<>\"|?*".Contains(value)))
            throw new InvalidDataException("Unsafe bootstrap relative path.");
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0];
            if (part.Length is 0 or > 255 || part is "." or ".." || part[0] == ' ' || part[^1] is ' ' or '.' ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                "0123456789¹²³".Contains(stem[3])) throw new InvalidDataException("Non-canonical bootstrap relative path.");
        }
    }

    private static void RequireSame(string actual, string expected)
    {
        if (!Path.IsPathFullyQualified(actual) || !FullPath(actual).Equals(FullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bootstrap context path does not match the native host.");
    }
    private static void Ordinary(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Redirected bootstrap component path.");
    }
    private static void RequireHash(string hash)
    {
        if (!RuntimeComponentSnapshot.IsHash(hash))
            throw new InvalidDataException("Invalid bootstrap content identity.");
    }
    private static string FullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Bootstrap paths must be absolute.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PublicPath(path)));
        // Expand DOS short spellings, not links. Native directory handles already
        // expose canonical long names; GetFullPath alone leaves 8.3 aliases intact.
        Ordinary(full);
        var query = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        var length = GetLongPathName(query, null, 0);
        // Fixture entry executables and future cache files need not exist yet.
        if (length == 0) return full;
        if (length > 32768) throw new PathTooLongException("Bootstrap path exceeds Windows bounds.");
        var expanded = new StringBuilder((int)length);
        var written = GetLongPathName(query, expanded, length);
        if (written == 0 || written >= length) throw new IOException("Bootstrap path changed during normalization.");
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PublicPath(expanded.ToString())));
        Ordinary(canonical);
        return canonical;
    }
    private static string PublicPath(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
        ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder? longPath, uint bufferLength);
    private static string Text(JsonElement row, string property) => row.GetProperty(property).GetString()
        ?? throw new InvalidDataException("Missing bootstrap context field " + property);
    private sealed record FileRecord(long Size, string Sha256);
    private sealed record ComponentFiles(string Root, IReadOnlyDictionary<string, FileRecord> Records);
}
