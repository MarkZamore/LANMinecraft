using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft;

/// <summary>Retains the current and previous successful desktop sets and respects native host leases.</summary>
internal static class DesktopBootstrapCache
{
    private static readonly string[] Kinds = ["app", "desktop-runtime", "assets", "steam-native"];
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal sealed record ComponentReference(string Kind, string Id, string Root, string EntryPath,
        string Sha256, string ManifestPath, string ManifestSha256);

    internal sealed record SuccessfulSet(int SchemaVersion, string ExePath, string ExeSha256,
        string AppRoot, string AppSha256, ComponentReference[] Components);

    internal static void CommitSuccessful(AppPaths paths, Logger? logger = null)
    {
        try
        {
            if (!BootstrapLaunchContext.TryGet(out var context) || context is null) return;
            if (!Path.GetFullPath(context.InstallationRoot).Equals(paths.Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Desktop bootstrap context belongs to another installation.");
            var components = context.Components.Select(component => new ComponentReference(component.Kind, component.Id,
                component.Root, component.EntryPath, component.Sha256, component.ManifestPath, component.ManifestSha256)).ToArray();
            RecordSuccessful(paths, new SuccessfulSet(1, context.ExePath, HashFile(context.ExePath),
                context.AppRoot, context.AppSha256, components));
        }
        catch (Exception error) when (Recoverable(error))
        {
            logger?.Warn("The successful desktop component set could not be recorded: " + error.Message);
        }
    }

    internal static void RecordSuccessful(AppPaths paths, SuccessfulSet successful)
    {
        ValidateSet(paths, successful);
        var directory = SetsRoot(paths);
        RequireOrdinaryPath(paths, directory);
        Directory.CreateDirectory(directory);
        using var gate = TryAcquireSetsLock(paths);
        if (gate is null) throw new IOException("The desktop component set is being updated.");
        var currentPath = Path.Combine(directory, "current.json");
        var previousPath = Path.Combine(directory, "previous.json");
        var current = ReadSet(paths, currentPath);
        _ = ReadSet(paths, previousPath);
        if (current is not null && SameSet(current, successful)) return;
        if (current is not null)
            AtomicFile.WriteAllText(previousPath, JsonSerializer.Serialize(current, JsonOptions));
        AtomicFile.WriteAllText(currentPath, JsonSerializer.Serialize(successful, JsonOptions));
    }

    internal static bool HasSuccessfulSet(AppPaths paths)
    {
        try { return ReadSet(paths, Path.Combine(SetsRoot(paths), "current.json")) is not null; }
        catch (Exception error) when (Recoverable(error)) { return false; }
    }

    internal static void CleanupIdle(AppPaths paths, Action? checkpoint = null)
    {
        var root = Root(paths);
        try
        {
            if (!Directory.Exists(SetsRoot(paths))) return;
            RequireOrdinaryPath(paths, root);
            using var ancestors = new GuardedFileSystem.DirectoryLocks();
            if (!ancestors.HoldAncestors(root)) return;
            using var gate = TryAcquireSetsLock(paths);
            if (gate is null) return;
            var protectedHashes = ReadProtectedSets(paths, checkpoint);
            foreach (var kind in Kinds)
            {
                var kindRoot = Path.Combine(root, kind);
                RequireOrdinaryPath(paths, kindRoot);
                if (!Directory.Exists(kindRoot)) continue;
                foreach (var directory in Directory.EnumerateDirectories(kindRoot))
                {
                    checkpoint?.Invoke();
                    var hash = Path.GetFileName(directory);
                    if (!IsHash(hash) || protectedHashes.Contains(Key(kind, hash))) continue;
                    using var lease = PortableRuntimeCache.OpenOwnedLease(kindRoot, hash, kind);
                    if (lease is null) continue;
                    PortableRuntimeCache.TryDeleteGeneration(kindRoot, directory, checkpoint);
                    if (!Directory.Exists(directory))
                        MaintenancePath.TryDelete(paths, Path.Combine(kindRoot, hash + ".manifest"), directory: false);
                }
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            // Unreadable references, an active lease or a changed tree are reconsidered at the next startup.
        }
    }

    private static HashSet<string> ReadProtectedSets(AppPaths paths, Action? checkpoint)
    {
        var protectedHashes = new HashSet<string>(StringComparer.Ordinal);
        var directory = SetsRoot(paths);
        RequireOrdinaryPath(paths, directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            checkpoint?.Invoke();
            var name = Path.GetFileName(file);
            var staged = name.StartsWith("staged-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);
            if (name is not ("current.json" or "previous.json") && !staged)
                throw new InvalidDataException("Unknown desktop bootstrap set record.");
            var set = ReadSet(paths, file) ?? throw new InvalidDataException("Missing desktop bootstrap set record.");
            if (staged && name != "staged-" + set.ExeSha256 + ".json")
                throw new InvalidDataException("Staged desktop bootstrap identity differs from its filename.");
            protectedHashes.Add(Key("app", set.AppSha256));
            foreach (var component in set.Components) protectedHashes.Add(Key(component.Kind, component.Sha256));
        }
        return protectedHashes;
    }

    private static SuccessfulSet? ReadSet(AppPaths paths, string file)
    {
        RequireOrdinaryPath(paths, file);
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 64 * 1024) throw new InvalidDataException("Oversized desktop bootstrap set record.");
        var set = JsonSerializer.Deserialize<SuccessfulSet>(File.ReadAllText(file), JsonOptions)
            ?? throw new InvalidDataException("Empty desktop bootstrap set record.");
        ValidateSet(paths, set);
        return set;
    }

    private static void ValidateSet(AppPaths paths, SuccessfulSet set)
    {
        if (set.SchemaVersion != 1 || !IsHash(set.ExeSha256) || !IsHash(set.AppSha256) ||
            !Path.IsPathFullyQualified(set.ExePath) ||
            !Path.GetDirectoryName(Path.GetFullPath(set.ExePath))!.Equals(paths.Root, StringComparison.OrdinalIgnoreCase) ||
            !SamePath(set.AppRoot, Path.Combine(Root(paths), "app", set.AppSha256)) ||
            set.Components is not { Length: 3 } ||
            set.Components.Any(component => component is null) ||
            set.Components.Select(component => component.Kind).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidDataException("Invalid successful desktop component set.");
        RequireOrdinaryPath(paths, set.AppRoot);
        foreach (var component in set.Components)
        {
            if (component.Kind is not ("desktop-runtime" or "assets" or "steam-native") ||
                string.IsNullOrWhiteSpace(component.Id) || !IsHash(component.Sha256) || !IsHash(component.ManifestSha256))
                throw new InvalidDataException("Invalid desktop component reference.");
            var expectedRoot = Path.Combine(Root(paths), component.Kind, component.Sha256);
            if (!SamePath(component.Root, expectedRoot) ||
                !IsRelativeEntry(component.EntryPath) ||
                !SamePath(component.ManifestPath, expectedRoot + ".manifest"))
                throw new InvalidDataException("Desktop component reference escapes its cache.");
            RequireOrdinaryPath(paths, component.Root);
            RequireOrdinaryPath(paths, Path.Combine(expectedRoot, component.EntryPath.Replace('/', Path.DirectorySeparatorChar)));
            RequireOrdinaryPath(paths, component.ManifestPath);
        }
    }

    private static FileStream? TryAcquireSetsLock(AppPaths paths)
    {
        var path = Path.Combine(SetsRoot(paths), ".lock");
        RequireOrdinaryPath(paths, path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }

    private static bool SameSet(SuccessfulSet left, SuccessfulSet right) =>
        left.ExeSha256 == right.ExeSha256 && left.AppSha256 == right.AppSha256 &&
        SamePath(left.ExePath, right.ExePath) &&
        left.Components.OrderBy(component => component.Kind, StringComparer.Ordinal)
            .Select(component => Key(component.Kind, component.Sha256))
            .SequenceEqual(right.Components.OrderBy(component => component.Kind, StringComparer.Ordinal)
                .Select(component => Key(component.Kind, component.Sha256)), StringComparer.Ordinal);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool SamePath(string path, string expected) => Path.IsPathFullyQualified(path) &&
        Path.GetFullPath(path).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    private static bool IsRelativeEntry(string? value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) &&
        value.Replace('\\', '/').Split('/').All(part => part.Length > 0 && part is not ("." or "..") &&
            !part.EndsWith('.') && !part.EndsWith(' ') && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Key(string kind, string hash) => kind + "/" + hash;
    private static string Root(AppPaths paths) => Path.Combine(paths.Launcher, "Bootstrap");
    private static string SetsRoot(AppPaths paths) => Path.Combine(Root(paths), "sets");
    private static void RequireOrdinaryPath(AppPaths paths, string path) => RuntimeComponentService.RequireOrdinaryPath(paths, path);
    private static bool Recoverable(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or
        ArgumentException or InvalidOperationException or NotSupportedException;
}
