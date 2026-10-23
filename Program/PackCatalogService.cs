using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Minecraft;

public sealed record CatalogSource(string Id, string RelativePath, PackSyncSource Source);
public sealed class CatalogMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? ParentId { get; set; }
    public bool IsLibrary { get; set; }
    public string? WebsiteUrl { get; set; }
    public bool WebsiteAvailable { get; set; } = true;
}
public sealed class CatalogDescription
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public List<CatalogMod> Mods { get; set; } = [];
}
public sealed class CatalogModNode(CatalogMod mod, string? packId = null)
{
    public string Name => mod.Name;
    public string Version => mod.Version;
    public Uri? WebsiteUri => mod.WebsiteAvailable ? CatalogModPages.Resolve(mod.Id, mod.WebsiteUrl, packId) : null;
    public List<CatalogModNode> Children { get; } = [];
    public bool HasChildren => Children.Count != 0;
    public bool IsExpanded { get; set; }
}
public sealed class CatalogPack
{
    public required CatalogSource Entry { get; init; }
    public required CatalogDescription Content { get; init; }
    public required BitmapImage Preview { get; init; }
    public string Name => PackDisplayName.For(Entry.RelativePath, Content.Name);
    public string VersionLabel => "Minecraft " + Content.MinecraftVersion;
    public string Summary => Content.Summary;
    public string Description => string.Join(" ", Content.Description.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));
    public List<CatalogModNode> Mods { get; } = [];
    public List<CatalogModNode> Libraries { get; } = [];

    public void BuildTree()
    {
        var nodes = Content.Mods.ToDictionary(m => m.Id, m => new CatalogModNode(m, Content.Id), StringComparer.Ordinal);
        foreach (var mod in Content.Mods.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = nodes[mod.Id];
            if (mod.IsLibrary) Libraries.Add(node);
            else if (mod.ParentId is { } parent && nodes.TryGetValue(parent, out var owner)) owner.Children.Add(node);
            else Mods.Add(node);
        }
    }
}

/// <summary>One startup-only discovery pass, followed by an immutable session manifest registry.</summary>
public sealed class PackCatalogService
{
    public static readonly Uri IndexUri = new("https://raw.githubusercontent.com/MarkZamore/LANMinecraft/pack-catalog/catalog.json");
    private const int MaximumBundleBytes = 8 * 1024 * 1024;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _http;
    private readonly string _cache;
    private readonly Dictionary<string, string> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CatalogSource> _sources;
    private readonly List<CatalogSource> _markerSources = [];
    private readonly Dictionary<string, CatalogPack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private Task? _refresh;
    private readonly object _gate = new();

    public PackCatalogService(AppPaths paths, Logger logger, HttpClient? httpClient = null)
    {
        _paths = paths;
        _logger = logger;
        _http = httpClient ?? PortableHttpClient.Shared;
        _cache = Path.Combine(paths.Launcher, "Library");
        _sources = SeedSources().ToList();
        foreach (var entry in _sources)
        {
            using var stream = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library." + entry.Id + ".zip");
            if (stream is null) continue;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            _packs[entry.RelativePath] = DecodeBundle(entry, bytes.ToArray(), null);
        }
        try
        {
            var indexPath = Path.Combine(_cache, "catalog.json");
            if (File.Exists(indexPath)) MergeSources(ParseIndex(File.ReadAllText(indexPath)));
        }
        catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library index cache: " + ex.Message); }
        // A custom pack's explicit source marker keeps its precedence, and is checked at startup too.
        if (Directory.Exists(paths.Packs))
        {
            foreach (var directory in Directory.EnumerateDirectories(paths.Packs))
            {
                try
                {
                    var marker = Path.Combine(directory, PortablePackSyncService.SourceMarkerFileName);
                    if (!File.Exists(marker)) continue;
                    var source = JsonSerializer.Deserialize<PackSyncSource>(File.ReadAllText(marker), JsonOptions);
                    if (source is null || !ValidSource(source)) continue;
                    var relative = Path.GetFileName(directory);
                    // Preserve legacy default-repository repair.
                    if (relative.Equals(PortablePackSyncService.DefaultPackRelativePath, StringComparison.OrdinalIgnoreCase) &&
                        PortablePackSyncService.IsLegacyDefaultSource(source)) continue;
                    _markerSources.Add(new CatalogSource(relative, relative, source));
                }
                catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library source marker: " + ex.Message); }
            }
        }
        foreach (var entry in _sources.ToArray()) LoadCachedEntry(entry);
        foreach (var entry in _markerSources) LoadCachedEntry(entry, presentation: false);
    }

    public bool IsReady { get; private set; }
    public IReadOnlyList<CatalogPack> Packs { get { lock (_gate) return _packs.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(); } }
    public IReadOnlyList<CatalogSource> Sources { get { lock (_gate) return _sources.ToArray(); } }
    public PackSyncSource? SourceFor(string relativePath) { lock (_gate) return _sources.FirstOrDefault(s =>
        s.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))?.Source; }
    public CatalogPack? Find(string relativePath) { lock (_gate) return _packs.GetValueOrDefault(relativePath); }

    public string SessionManifest(PackSyncSource source)
    {
        lock (_gate)
        {
            if (!IsReady) throw new IOException("Проверка сборок при запуске ещё не завершена.");
            return _manifests.TryGetValue(Key(source), out var json) ? json :
                throw new IOException("Версию сборки не удалось проверить при запуске. Перезапустите лаунчер с доступом к интернету.");
        }
    }

    public Task RefreshOnceAsync(CancellationToken token = default)
    {
        lock (_gate) return _refresh ??= RefreshCoreAsync(token);
    }

    private async Task RefreshCoreAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var candidates = Sources;
        try
        {
            try
            {
                var indexBytes = await ReadBoundedAsync(IndexUri, 256 * 1024, ct).ConfigureAwait(false);
                var index = Encoding.UTF8.GetString(indexBytes);
                var entries = ParseIndex(index);
                // Source changes become active only after that pack's manifest and bundle validate.
                candidates = entries.Concat(candidates.Where(old => !entries.Any(next =>
                    next.RelativePath.Equals(old.RelativePath, StringComparison.OrdinalIgnoreCase)))).ToArray();
            }
            catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library discovery: " + ex.Message); }
            using var parallel = new SemaphoreSlim(4);
            var checks = candidates.Select(s => (Entry: s, Presentation: true))
                .Concat(_markerSources.Select(s => (Entry: s, Presentation: false)))
                .DistinctBy(check => Key(check.Entry.Source), StringComparer.OrdinalIgnoreCase).ToArray();
            await Task.WhenAll(checks.Select(async check =>
            {
                try
                {
                    await parallel.WaitAsync(ct).ConfigureAwait(false);
                    try { await RefreshEntryAsync(check.Entry, check.Presentation, ct).ConfigureAwait(false); }
                    finally { parallel.Release(); }
                }
                catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library " + check.Entry.RelativePath + ": " + ex.Message); }
            })).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) IsReady = true;
            try
            {
                AtomicFile.WriteAllTextIfChanged(Path.Combine(_cache, "catalog.json"),
                    JsonSerializer.Serialize(new CatalogIndex { SchemaVersion = 1, Packs = Sources.ToList() }, JsonOptions));
            }
            catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library index could not be saved: " + ex.Message); }
        }
    }

    private async Task RefreshEntryAsync(CatalogSource entry, bool presentation, CancellationToken token)
    {
        var raw = Encoding.UTF8.GetString(await ReadBoundedAsync(AssetUri(entry.Source, "pack-manifest.json"), MaximumManifestBytes, token).ConfigureAwait(false));
        PortablePackSyncService.ValidateSnapshot(raw, _paths.CombineUnderPacks(entry.RelativePath));
        using var doc = JsonDocument.Parse(raw);
        byte[]? bundle = null;
        CatalogPack? pack = null;
        if (presentation && doc.RootElement.TryGetProperty("library", out var descriptor))
        {
            var name = descriptor.GetProperty("name").GetString() ?? "";
            if (!Regex.IsMatch(name, @"^library-[a-f0-9]{64}\.zip$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid library asset name.");
            var size = descriptor.GetProperty("sizeBytes").GetInt64();
            if (size is <= 0 or > MaximumBundleBytes) throw new InvalidDataException("Library bundle size is invalid.");
            CachedEntry? cache = null;
            try { cache = ReadCache(entry); }
            catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Ignoring damaged library cache: " + ex.Message); }
            if (cache?.Bundle is { } cached && VerifyHash(cached, descriptor)) bundle = cached;
            else bundle = await ReadBoundedAsync(AssetUri(entry.Source, name), MaximumBundleBytes, token).ConfigureAwait(false);
            if (!VerifyHash(bundle, descriptor)) throw new InvalidDataException("Library bundle hash/size mismatch.");
            var revision = doc.RootElement.GetProperty("revision").GetString();
            if (descriptor.GetProperty("revision").GetString() != revision) throw new InvalidDataException("Library descriptor revision mismatch.");
            pack = DecodeBundle(entry, bundle, revision);
        }
        // A source may still publish the old game-only format.
        var value = new CachedEntry { Entry = entry, Manifest = raw, Bundle = bundle };
        lock (_gate)
        {
            _manifests[Key(entry.Source)] = raw;
            if (presentation) MergeSources([entry]);
            if (pack is not null) _packs[entry.RelativePath] = pack;
        }
        try { AtomicFile.WriteAllTextIfChanged(CachePath(entry.Source), JsonSerializer.Serialize(value, JsonOptions)); }
        catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library cache could not be saved: " + ex.Message); }
    }

    private void LoadCachedEntry(CatalogSource entry, bool presentation = true)
    {
        try
        {
            var cached = ReadCache(entry);
            if (cached is null) return;
            PortablePackSyncService.ValidateSnapshot(cached.Manifest, _paths.CombineUnderPacks(entry.RelativePath));
            CatalogPack? pack = null;
            using var doc = JsonDocument.Parse(cached.Manifest);
            if (presentation && doc.RootElement.TryGetProperty("library", out var descriptor))
            {
                if (cached.Bundle is null || !VerifyHash(cached.Bundle, descriptor)) throw new InvalidDataException("Corrupt library cache.");
                var revision = doc.RootElement.GetProperty("revision").GetString();
                if (descriptor.GetProperty("revision").GetString() != revision) throw new InvalidDataException("Corrupt library cache revision.");
                pack = DecodeBundle(entry, cached.Bundle, revision);
            }
            _manifests[Key(entry.Source)] = cached.Manifest;
            if (pack is not null) _packs[entry.RelativePath] = pack;
        }
        catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library cache " + entry.RelativePath + ": " + ex.Message); }
    }

    private CachedEntry? ReadCache(CatalogSource entry)
    {
        var path = CachePath(entry.Source);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 3L * MaximumBundleBytes) throw new InvalidDataException("Oversized library cache.");
        return JsonSerializer.Deserialize<CachedEntry>(File.ReadAllText(path), JsonOptions);
    }
    private string CachePath(PackSyncSource source) => Path.Combine(_cache, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key(source)))) + ".json");
    private static string Key(PackSyncSource source) => source.Owner + "/" + source.Repo + "/" + source.Tag;
    public static Uri AssetUri(PackSyncSource source, string name) => new("https://github.com/" + source.Owner + "/" + source.Repo + "/releases/download/" + Uri.EscapeDataString(source.Tag) + "/" + Uri.EscapeDataString(name));

    private async Task<byte[]> ReadBoundedAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Oversized library download.");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Oversized library download.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static bool VerifyHash(byte[] bytes, JsonElement descriptor) => bytes.Length <= MaximumBundleBytes &&
        bytes.LongLength == descriptor.GetProperty("sizeBytes").GetInt64() &&
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(descriptor.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase);

    private static CatalogPack DecodeBundle(CatalogSource entry, byte[] bytes, string? revision)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count != 2 || zip.Entries.Any(e => e.FullName is not ("library.json" or "preview.jpg") || e.Length > MaximumBundleBytes))
            throw new InvalidDataException("Unexpected library bundle contents.");
        var jsonEntry = zip.GetEntry("library.json") ?? throw new InvalidDataException("Missing library metadata.");
        using var json = jsonEntry.Open();
        var content = JsonSerializer.Deserialize<CatalogDescription>(json, JsonOptions) ?? throw new InvalidDataException("Empty library metadata.");
        ValidateContent(entry, content, revision);
        using var preview = zip.GetEntry("preview.jpg")!.Open();
        using var imageBytes = new MemoryStream();
        preview.CopyTo(imageBytes);
        imageBytes.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 1280;
        image.StreamSource = imageBytes;
        image.EndInit();
        image.Freeze();
        var pack = new CatalogPack { Entry = entry, Content = content, Preview = image };
        pack.BuildTree();
        return pack;
    }

    private static void ValidateContent(CatalogSource entry, CatalogDescription doc, string? revision)
    {
        if (doc.SchemaVersion != 1 || doc.Id != entry.Id || doc.RelativePath != entry.RelativePath ||
            (revision is not null && doc.Revision != revision) || string.IsNullOrWhiteSpace(doc.Name) || doc.Name.Length > 120 ||
            string.IsNullOrWhiteSpace(doc.MinecraftVersion) || string.IsNullOrWhiteSpace(doc.Description) ||
            doc.Description.Length > 32000 || doc.Summary is null || doc.Summary.Length > 1000 || doc.Mods is null || doc.Mods.Count > 10000)
            throw new InvalidDataException("Invalid library metadata or revision.");
        var mods = new Dictionary<string, CatalogMod>(StringComparer.Ordinal);
        foreach (var mod in doc.Mods)
            if (mod is null || string.IsNullOrWhiteSpace(mod.Id) || string.IsNullOrWhiteSpace(mod.Name) ||
                (mod.WebsiteUrl is not null && CatalogModPages.Parse(mod.WebsiteUrl) is null) ||
                mod.Name.Length > 256 || !mods.TryAdd(mod.Id, mod)) throw new InvalidDataException("Invalid/duplicate mod.");
        foreach (var mod in doc.Mods)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { mod.Id };
            var parent = mod.ParentId;
            while (parent is not null)
            {
                if (!mods.TryGetValue(parent, out var owner) || !seen.Add(parent) || owner.IsLibrary || seen.Count > 16)
                    throw new InvalidDataException("Invalid addon tree.");
                parent = owner.ParentId;
            }
        }
    }

    private static List<CatalogSource> ParseIndex(string json)
    {
        var index = JsonSerializer.Deserialize<CatalogIndex>(json, JsonOptions);
        if (index is null || index.SchemaVersion != 1 || index.Packs is null || index.Packs.Count > 100)
            throw new InvalidDataException("Unsupported library index.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Packs)
        {
            if (entry is null || !ValidFolder(entry.RelativePath) || string.IsNullOrWhiteSpace(entry.Id) ||
                entry.Id.Length > 100 || !ids.Add(entry.Id) || !names.Add(entry.RelativePath) || entry.Source is null || !ValidSource(entry.Source) || !sources.Add(Key(entry.Source)))
                throw new InvalidDataException("Invalid/duplicate library source.");
        }
        return index.Packs;
    }

    public static bool ValidFolder(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 &&
        value is not ("." or "..") && value == value.Trim() && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.EndsWith('.');
    private static bool ValidSource(PackSyncSource source) => !string.IsNullOrWhiteSpace(source.Owner) && !string.IsNullOrWhiteSpace(source.Repo) &&
        !string.IsNullOrWhiteSpace(source.Tag) && Regex.IsMatch(source.Owner, @"^[A-Za-z0-9_-]{1,100}$") &&
        Regex.IsMatch(source.Repo, @"^[A-Za-z0-9_.-]{1,100}$") && Regex.IsMatch(source.Tag, @"^[A-Za-z0-9_.-]{1,100}$");
    private void MergeSources(IEnumerable<CatalogSource> entries)
    {
        foreach (var entry in entries)
        {
            var index = _sources.FindIndex(s => s.RelativePath.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (index < 0) _sources.Add(entry);
            else _sources[index] = entry;
        }
    }
    private static bool IsRecoverable(Exception ex) => ex is IOException or InvalidDataException or HttpRequestException or JsonException or
        InvalidOperationException or ArgumentException or OperationCanceledException or NotSupportedException or System.Security.SecurityException or
        KeyNotFoundException or FormatException or OverflowException or UnauthorizedAccessException;
    public static IEnumerable<CatalogSource> SeedSources()
    {
        string[] ids = ["ll8-extended", "all-the-mods-10", "terrafirma-rebirth", "minecraft-26-2"];
        return PortablePackSyncService.KnownPacks.Select((p, i) => new CatalogSource(ids[i], p.RelativePath, p.Source));
    }
    private sealed class CatalogIndex
    {
        public int SchemaVersion { get; set; }
        public List<CatalogSource> Packs { get; set; } = [];
    }
    private sealed class CachedEntry
    {
        public CatalogSource? Entry { get; set; }
        public string Manifest { get; set; } = "";
        public byte[]? Bundle { get; set; }
    }
}
