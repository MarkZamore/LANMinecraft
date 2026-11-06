using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// One installation's display names, from an already verified catalog snapshot.
/// The resolver never reads an unverified file or changes a pack's identity.
/// </summary>
public sealed class PackDisplayNames : IDisposable
{
    private static readonly ConcurrentDictionary<string, WeakReference<PackDisplayNames>> Installations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> FallbackCaseNames = new(ReadFallbackCaseNames);
    private readonly string packsRoot;
    private readonly object gate = new();
    private NameSnapshot names = NameSnapshot.Empty();
    private bool disposed;

    public PackDisplayNames(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        packsRoot = NormalizeDirectory(paths.Packs);
        foreach (var entry in Installations)
            if (!entry.Value.TryGetTarget(out _)) RemoveRegistration(entry.Key, entry.Value);
        Installations[packsRoot] = new WeakReference<PackDisplayNames>(this);
    }

    /// <summary>Replace, rather than extend, the names from the published verified snapshot.</summary>
    public bool Publish(IEnumerable<CatalogPack> verifiedPacks)
    {
        ArgumentNullException.ThrowIfNull(verifiedPacks);
        var packs = verifiedPacks.ToArray();
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs)
        {
            var path = NormalizeIdentity(pack.Entry.RelativePath);
            if (path.Length == 0 || string.IsNullOrWhiteSpace(pack.Content.Name)) continue;
            // Checked metadata is authoritative, including a case-only rename.
            var name = pack.Content.Name;
            if (!next.TryAdd(path, name) && !string.Equals(next[path], name, StringComparison.Ordinal))
                throw new InvalidDataException("A verified display snapshot has conflicting pack paths.");
        }

        // Historical paths share the path namespace. Stable IDs have a separate
        // namespace: an unrelated local folder may happen to have the same text.
        // A real current path always wins over a historical path.
        var aliases = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs)
        {
            var path = NormalizeIdentity(pack.Entry.RelativePath);
            if (!next.TryGetValue(path, out var name)) continue;
            var id = NormalizeIdentity(pack.Entry.Id);
            if (id.Length > 0) AddAlias(ids, id, name);
            if (pack.Entry.LegacyId is { } legacy)
                AddAlias(ids, NormalizeIdentity(legacy), name);
            foreach (var alias in pack.Entry.LegacyRelativePaths ?? [])
            {
                var key = NormalizeIdentity(alias);
                if (key.Length == 0 || next.ContainsKey(key)) continue;
                AddAlias(aliases, key, name);
            }
        }
        foreach (var alias in aliases)
            if (alias.Value is { } name) next.Add(alias.Key, name);
        var nextIds = ids.Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = Volatile.Read(ref names);
            if (SameNames(current.Paths, next) && SameNames(current.Ids, nextIds)) return false;
            Volatile.Write(ref names, new NameSnapshot(next, nextIds));
            return true;
        }
    }

    /// <summary>Resolve an actual or historical folder path, never a stable catalog ID.</summary>
    public string Resolve(string identity, string fallback)
    {
        var key = NormalizeIdentity(identity);
        var snapshot = Volatile.Read(ref names);
        return snapshot.Paths.TryGetValue(key, out var name) ? name : PackDisplayName.For(identity ?? "", fallback ?? "");
    }

    /// <summary>Resolve a caller's explicitly identified stable catalog ID.</summary>
    public string ResolveId(string id, string fallback) =>
        Volatile.Read(ref names).Ids.TryGetValue(NormalizeIdentity(id), out var name)
            ? name : PackDisplayName.CorrectCase(fallback ?? "");

    /// <summary>Read the same installation resolver from a service that already has AppPaths.</summary>
    public static string ForInstallation(AppPaths paths, string identity, string fallback)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Installations.TryGetValue(NormalizeDirectory(paths.Packs), out var registration) &&
               registration.TryGetTarget(out var resolver)
            ? resolver.Resolve(identity, fallback)
            : PackDisplayName.For(identity ?? "", fallback ?? "");
    }

    /// <summary>Window-title preparation has a pack directory, but does not own the catalog.</summary>
    public static string ResolveDirectory(string packDirectory, string fallback)
    {
        var fullPath = NormalizeDirectory(packDirectory);
        PackDisplayNames? match = null;
        var longestRoot = 0;
        foreach (var entry in Installations)
        {
            if (!entry.Value.TryGetTarget(out var resolver)) { RemoveRegistration(entry.Key, entry.Value); continue; }
            if (entry.Key.Length > longestRoot && fullPath.StartsWith(entry.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                match = resolver;
                longestRoot = entry.Key.Length;
            }
        }
        return match is not null
            ? match.Resolve(Path.GetRelativePath(match.packsRoot, fullPath), fallback)
            : PackDisplayName.CorrectCase(fallback);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Volatile.Write(ref names, NameSnapshot.Empty());
        }
        if (Installations.TryGetValue(packsRoot, out var registration) &&
            registration.TryGetTarget(out var current) && ReferenceEquals(current, this))
            RemoveRegistration(packsRoot, registration);
    }

    private static string NormalizeIdentity(string value) =>
        (value ?? "").Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void AddAlias(Dictionary<string, string?> aliases, string key, string name)
    {
        if (!aliases.TryAdd(key, name) && !string.Equals(aliases[key], name, StringComparison.Ordinal)) aliases[key] = null;
    }

    private static bool SameNames(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

    private sealed record NameSnapshot(IReadOnlyDictionary<string, string> Paths, IReadOnlyDictionary<string, string> Ids)
    {
        public static NameSnapshot Empty() => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private static void RemoveRegistration(string root, WeakReference<PackDisplayNames> registration) =>
        ((ICollection<KeyValuePair<string, WeakReference<PackDisplayNames>>>)Installations)
            .Remove(new KeyValuePair<string, WeakReference<PackDisplayNames>>(root, registration));

    internal static string CorrectFallbackCase(string name) =>
        FallbackCaseNames.Value.TryGetValue(name, out var canonical) ? canonical : name;

    private static IReadOnlyDictionary<string, string> ReadFallbackCaseNames()
    {
        // Generated by tools/build_display_name_snapshot.py from verified library
        // metadata. This tiny embedded snapshot never adds catalog membership,
        // loads images/mod trees or takes precedence over a published name.
        using var stream = typeof(PackDisplayNames).Assembly.GetManifestResourceStream("Minecraft.PackDisplayNames.json")
            ?? throw new InvalidDataException("Missing embedded display-name snapshot.");
        using var snapshot = JsonDocument.Parse(stream);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in snapshot.RootElement.GetProperty("packs").EnumerateArray())
        {
            var name = pack.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Empty embedded display name.");
            if (!result.TryAdd(name, name) && !string.Equals(result[name], name, StringComparison.Ordinal))
                throw new InvalidDataException("Ambiguous embedded display-name casing.");
        }
        return result;
    }
}
