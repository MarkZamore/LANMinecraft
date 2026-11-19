using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

public sealed record RuntimeComponentArtifact(
    string Id, string FileName, Uri DownloadUrl, long SizeBytes, string Sha256, int ProtocolVersion = 1);

internal sealed record RuntimeComponentRule(
    string RuntimeKind, IReadOnlyList<PackLoaderKind> Loaders, int[]? MinimumMinecraftVersion,
    int[]? ExclusiveMaximumMinecraftVersion, int? MinimumJavaMajorVersion,
    int? ExclusiveMaximumJavaMajorVersion, IReadOnlyList<string> ComponentIds);

public sealed class RuntimeComponentSnapshot
{
    internal const int SupportedApiVersion = 1;
    internal const int MaximumCatalogBytes = 8 * 1024 * 1024;
    public string Revision { get; }
    public string SourceCommit { get; }
    public string Sha256 { get; }
    public string CatalogJson { get; }
    public string JavaCatalogJson { get; }
    public string SteamCatalogJson { get; }
    public IReadOnlyList<RuntimeComponentArtifact> Artifacts { get; }
    internal IReadOnlyList<RuntimeComponentRule> Rules { get; }

    private RuntimeComponentSnapshot(string revision, string sourceCommit, string json, string java,
        string steam, RuntimeComponentArtifact[] artifacts, RuntimeComponentRule[] rules)
    {
        Revision = revision;
        SourceCommit = sourceCommit;
        CatalogJson = json;
        Sha256 = Digest(json);
        JavaCatalogJson = java;
        SteamCatalogJson = steam;
        Artifacts = Array.AsReadOnly(artifacts);
        Rules = Array.AsReadOnly(rules);
    }

    public static RuntimeComponentSnapshot Parse(string json, int? launcherRelease = null)
    {
        Require(Encoding.UTF8.GetByteCount(json) <= MaximumCatalogBytes, "Oversized runtime component catalog.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        SteamComponentCatalog.UniqueProperties(root);
        Require(root.GetProperty("schemaVersion").GetInt32() == 1 &&
            root.GetProperty("componentApiVersion").GetInt32() == SupportedApiVersion,
            "Unsupported runtime component catalog or API version.");
        var minimumRelease = root.GetProperty("minimumLauncherRelease").GetInt32();
        Require(minimumRelease > 0 && minimumRelease <= (launcherRelease ?? UpdateService.CurrentReleaseNumber),
            "Обновите лаунчер для этой версии компонентов игры.");
        var revision = Text(root, "revision");
        Require(Regex.IsMatch(revision, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant),
            "Invalid component revision.");
        var source = Text(root, "sourceCommit");
        Require(Regex.IsMatch(source, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant), "Invalid component source commit.");
        var artifacts = root.GetProperty("artifacts").EnumerateArray().Select(ReadArtifact).ToArray();
        Require(artifacts.Length is > 0 and <= 128 && artifacts.Select(artifact => artifact.Id).Distinct(StringComparer.Ordinal).Count() == artifacts.Length,
            "Empty or duplicate component artifacts.");
        var ids = artifacts.Select(artifact => artifact.Id).ToHashSet(StringComparer.Ordinal);
        var rules = root.GetProperty("componentRules").EnumerateArray().Select(row => ReadRule(row, ids)).ToArray();
        Require(rules.Length is > 0 and <= 256, "Empty or oversized component rules.");
        var java = root.GetProperty("javaCatalog").GetRawText();
        var steam = root.GetProperty("steamCatalog").GetRawText();
        JavaRuntimeCatalog.ValidateJson(java);
        _ = SteamComponentCatalog.Parse(steam);
        return new RuntimeComponentSnapshot(revision, source, json, java, steam, artifacts, rules);
    }

    internal IReadOnlyList<RuntimeComponentArtifact> RequiredArtifacts(PackRuntimeDescriptor descriptor)
    {
        var native = descriptor.NativeRuntime is not null || descriptor.Loader.Type == PackLoaderKind.Native;
        if (native && !Rules.Any(rule => rule.RuntimeKind is "native" or "any")) return [];
        using var scope = RuntimeComponentContext.Use(this);
        var java = native ? 0 : JavaRuntimeCatalog.RequiredFor(descriptor).MajorVersion;
        Require(SteamTransportCatalog.TryParseVersion(descriptor.MinecraftVersion, out var version), "Invalid Minecraft version for component selection.");
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            if (rule.RuntimeKind != "any" && rule.RuntimeKind != (native ? "native" : "java") ||
                rule.Loaders.Count != 0 && !rule.Loaders.Contains(descriptor.Loader.Type) ||
                rule.MinimumMinecraftVersion is { } minimum && SteamTransportCatalog.Compare(version, minimum) < 0 ||
                rule.ExclusiveMaximumMinecraftVersion is { } maximum && SteamTransportCatalog.Compare(version, maximum) >= 0 ||
                rule.MinimumJavaMajorVersion is { } minimumJava && java < minimumJava ||
                rule.ExclusiveMaximumJavaMajorVersion is { } maximumJava && java >= maximumJava) continue;
            selected.UnionWith(rule.ComponentIds);
        }
        return Artifacts.Where(artifact => selected.Contains(artifact.Id)).ToArray();
    }

    internal RuntimeComponentArtifact RequireArtifact(string id) => Artifacts.FirstOrDefault(artifact => artifact.Id == id)
        ?? throw new InvalidDataException($"Компонент {id} отсутствует в выбранной поставке.");

    private static RuntimeComponentArtifact ReadArtifact(JsonElement row)
    {
        var id = Text(row, "id");
        var file = Text(row, "fileName");
        var hash = Text(row, "sha256");
        var size = row.GetProperty("sizeBytes").GetInt64();
        Require(Regex.IsMatch(id, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant) &&
            file.Length is > 0 and <= 180 && file == Path.GetFileName(file) &&
            file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !file.EndsWith('.') && !file.EndsWith(' ') &&
            IsHash(hash) && size is > 0 and <= 256L * 1024 * 1024, "Invalid component artifact identity.");
        var protocol = row.TryGetProperty("protocolVersion", out var protocolValue) ? protocolValue.GetInt32() : 1;
        Require(protocol == 1, "Unsupported runtime component protocol version.");
        var uri = new Uri(Text(row, "url"), UriKind.Absolute);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Require(uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && segments.Length == 6 &&
            segments[0] == "MarkZamore" && segments[1] == "LANMinecraft-Components" && segments[2] == "releases" &&
            segments[3] == "download" && segments[4] != "latest" && Uri.UnescapeDataString(segments[5]) == file,
            "Component artifacts require immutable LANMinecraft-Components release URLs.");
        return new RuntimeComponentArtifact(id, file, uri, size, hash, protocol);
    }

    private static RuntimeComponentRule ReadRule(JsonElement row, HashSet<string> knownIds)
    {
        var kind = Text(row, "runtimeKind");
        Require(kind is "java" or "native" or "any", "Invalid component runtime kind.");
        var loaders = row.TryGetProperty("loaders", out var values)
            ? values.EnumerateArray().Select(value => Enum.Parse<PackLoaderKind>(value.GetString()!, true)).ToArray() : [];
        Require(loaders.All(Enum.IsDefined) && loaders.Distinct().Count() == loaders.Length, "Invalid component loaders.");
        var ids = row.GetProperty("componentIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Require(ids.Length > 0 && ids.All(knownIds.Contains) && ids.Distinct().Count() == ids.Length, "Unknown or duplicate rule component.");
        var minimum = Version(row, "minimumMinecraftVersion");
        var maximum = Version(row, "exclusiveMaximumMinecraftVersion");
        var minimumJava = OptionalPositive(row, "minimumJavaMajorVersion");
        var maximumJava = OptionalPositive(row, "exclusiveMaximumJavaMajorVersion");
        Require(minimum is null || maximum is null || SteamTransportCatalog.Compare(minimum, maximum) < 0,
            "Empty component Minecraft compatibility range.");
        Require(minimumJava is null || maximumJava is null || minimumJava < maximumJava,
            "Empty component Java compatibility range.");
        return new RuntimeComponentRule(kind, Array.AsReadOnly(loaders), minimum, maximum,
            minimumJava, maximumJava, Array.AsReadOnly(ids));
    }

    private static int[]? Version(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value)) return null;
        Require(SteamTransportCatalog.TryParseVersion(value.GetString(), out var version), "Invalid component version boundary.");
        return version;
    }

    private static int? OptionalPositive(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value)) return null;
        var result = value.GetInt32();
        Require(result > 0, "Invalid component Java boundary.");
        return result;
    }

    private static string Text(JsonElement row, string name) => row.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"Missing component catalog value {name}.");
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit) && value == value.ToLowerInvariant();
    internal static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
}

public sealed class PreparedComponentSet : IDisposable
{
    private readonly IReadOnlyList<FileStream> _handles;
    private readonly Action _commit;
    private bool _committed;
    private bool _disposed;
    public RuntimeComponentSnapshot Snapshot { get; }
    public string PackRelativePath { get; }
    public string DescriptorHash { get; }
    public IReadOnlyDictionary<string, string> ArtifactPaths { get; }
    public IReadOnlyList<string> ComponentHashes { get; }

    internal PreparedComponentSet(RuntimeComponentSnapshot snapshot, string pack, string descriptor,
        Dictionary<string, string> paths, IReadOnlyList<FileStream> handles, Action commit)
    {
        Snapshot = snapshot;
        PackRelativePath = pack;
        DescriptorHash = descriptor;
        ArtifactPaths = new ReadOnlyDictionary<string, string>(paths);
        ComponentHashes = Array.AsReadOnly(snapshot.Artifacts.Where(artifact => paths.ContainsKey(artifact.Id))
            .Select(artifact => artifact.Sha256).Distinct(StringComparer.Ordinal).ToArray());
        _handles = handles;
        _commit = commit;
    }

    public void Commit()
    {
        lock (_handles)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) return;
            _commit();
            _committed = true;
        }
    }

    public void Dispose()
    {
        lock (_handles)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var handle in _handles) handle.Dispose();
        }
    }
}

public static class RuntimeComponentContext
{
    private sealed record State(RuntimeComponentSnapshot Snapshot, PreparedComponentSet? Prepared);
    private static readonly AsyncLocal<State?> Ambient = new();
    private static RuntimeComponentSnapshot? _default;
    internal static Func<AppPaths, RuntimeComponentArtifact, string>? TestPathResolver { get; set; }
    public static RuntimeComponentSnapshot Current => Ambient.Value?.Snapshot ?? Volatile.Read(ref _default)
        ?? throw new InvalidOperationException("Каталог компонентов ещё не загружен. Для первого запуска требуется интернет.");
    public static PreparedComponentSet? CurrentSet => Ambient.Value?.Prepared;
    public static void SetDefault(RuntimeComponentSnapshot snapshot) => Volatile.Write(ref _default, snapshot);
    public static IDisposable Use(RuntimeComponentSnapshot snapshot) => Enter(new State(snapshot, null));
    public static IDisposable Use(PreparedComponentSet prepared) => Enter(new State(prepared.Snapshot, prepared));

    private static IDisposable Enter(State state)
    {
        var previous = Ambient.Value;
        Ambient.Value = state;
        return new Scope(previous);
    }

    private sealed class Scope(State? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }

    internal static string RequirePath(AppPaths paths, string id)
    {
        var artifact = Current.RequireArtifact(id);
        var path = TestPathResolver?.Invoke(paths, artifact) ?? RuntimeComponentService.ObjectPath(paths, artifact);
        RuntimeComponentService.RequireOrdinaryPath(paths, path);
        RuntimeComponentService.VerifyArtifact(path, artifact);
        return path;
    }
}
