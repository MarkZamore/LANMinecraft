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
    private IReadOnlyDictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        // A stable ID or historical path is usable only if it unambiguously
        // identifies one display value. A real current path always wins.
        var aliases = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs)
        {
            var path = NormalizeIdentity(pack.Entry.RelativePath);
            if (!next.TryGetValue(path, out var name)) continue;
            foreach (var alias in new[] { pack.Entry.Id }.Concat(pack.Entry.LegacyRelativePaths ?? []))
            {
                var key = NormalizeIdentity(alias);
                if (key.Length == 0 || next.ContainsKey(key)) continue;
                if (!aliases.TryAdd(key, name) && !string.Equals(aliases[key], name, StringComparison.Ordinal)) aliases[key] = null;
            }
        }
        foreach (var alias in aliases)
            if (alias.Value is { } name) next.Add(alias.Key, name);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = Volatile.Read(ref names);
            if (current.Count == next.Count && current.All(pair => next.TryGetValue(pair.Key, out var value) &&
                    string.Equals(pair.Value, value, StringComparison.Ordinal))) return false;
            Volatile.Write(ref names, next);
            return true;
        }
    }

    public string Resolve(string identity, string fallback)
    {
        var key = NormalizeIdentity(identity);
        var snapshot = Volatile.Read(ref names);
        return snapshot.TryGetValue(key, out var name) ? name : PackDisplayName.For(identity ?? "", fallback ?? "");
    }

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
            Volatile.Write(ref names, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
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
