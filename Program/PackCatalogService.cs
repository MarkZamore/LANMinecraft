using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Minecraft;

public sealed record CatalogSource(string Id, string RelativePath, PackSyncSource Source)
{
    public List<string> LegacyRelativePaths { get; init; } = [];
    public List<PackSyncSource> LegacySources { get; init; } = [];

    public bool Equals(CatalogSource? other) => other is not null &&
        Id == other.Id && RelativePath == other.RelativePath && Source == other.Source &&
        (LegacyRelativePaths is null ? other.LegacyRelativePaths is null :
            other.LegacyRelativePaths is not null && LegacyRelativePaths.SequenceEqual(other.LegacyRelativePaths, StringComparer.Ordinal)) &&
        (LegacySources is null ? other.LegacySources is null :
            other.LegacySources is not null && LegacySources.SequenceEqual(other.LegacySources));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(RelativePath, StringComparer.Ordinal);
        hash.Add(Source);
        hash.Add(LegacyRelativePaths?.Count ?? -1);
        if (LegacyRelativePaths is not null)
            foreach (var path in LegacyRelativePaths) hash.Add(path, StringComparer.Ordinal);
        hash.Add(LegacySources?.Count ?? -1);
        if (LegacySources is not null)
            foreach (var source in LegacySources) hash.Add(source);
        return hash.ToHashCode();
    }
}
public sealed class CatalogMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? ParentId { get; set; }
    public bool IsLibrary { get; set; }
    public bool IsLoadedMod { get; set; } = true;
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
    // Published metadata contains the actual loaded mod IDs, including embedded
    // dependencies. The library flag only controls the presentation category.
    public int ModCount => Content.Mods.Where(mod => mod.IsLoadedMod)
        .Select(mod => mod.Id).Distinct(StringComparer.Ordinal).Count();
    public string VersionLabel
    {
        get
        {
            var count = ModCount;
            var word = count % 100 is >= 11 and <= 14 ? "модов" : (count % 10) switch
            {
                1 => "мод",
                2 or 3 or 4 => "мода",
                _ => "модов"
            };
            return $"Minecraft {Content.MinecraftVersion} ({count} {word})";
        }
    }
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
    private const int MaximumSourceHistoryPerPack = 16;
    private const int MaximumSourceHistoryEntries = 1600;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _http;
    private readonly string _cache;
    private readonly Dictionary<string, string> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CatalogSource> _sources;
    private readonly Dictionary<string, CatalogSource> _effectiveSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownCatalogPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CatalogSource> _sourceHistory = [];
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
            _effectiveSources[entry.RelativePath] = entry;
            _knownCatalogPaths.Add(entry.RelativePath);
            using var stream = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library." + entry.Id + ".zip");
            if (stream is null) continue;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            _packs[entry.RelativePath] = DecodeBundle(entry, bytes.ToArray(), null);
        }
        try
        {
            var indexPath = Path.Combine(_cache, "catalog.json");
            if (File.Exists(indexPath))
            {
                var json = File.ReadAllText(indexPath);
                var entries = ParseIndex(json);
                _effectiveSources.Clear();
                ReplaceIndex(entries);
                RestoreIndexState(json);
            }
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
                    source = PortablePackSyncService.NormalizeTerraFirmaSource(relative, source);
                    // Preserve legacy default-repository repair.
                    if (relative.Equals(PortablePackSyncService.DefaultPackRelativePath, StringComparison.OrdinalIgnoreCase) &&
                        PortablePackSyncService.IsLegacyDefaultSource(source)) continue;
                    _markerSources.Add(new CatalogSource(relative, relative, source));
                }
                catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library source marker: " + ex.Message); }
            }
        }
        foreach (var entry in _sources.ToArray())
        {
            var effective = _effectiveSources[entry.RelativePath];
            if (effective.Source != entry.Source) LoadCachedEntry(effective);
            LoadCachedEntry(entry);
        }
        foreach (var entry in _markerSources) LoadCachedEntry(entry, presentation: false);
    }

    public bool IsReady { get; private set; }
    public bool HasOnlineCatalog { get; private set; }
    public IReadOnlyList<CatalogPack> PacksForDisplay(AppSettings settings)
    {
        lock (_gate)
            return Packs.Where(pack => HasOnlineCatalog || PackMembership.Contains(settings, pack.Entry.RelativePath)).ToArray();
    }
    public IReadOnlyList<CatalogPack> Packs { get { lock (_gate) return _packs.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(); } }
    public IReadOnlyList<CatalogSource> Sources { get { lock (_gate) return _sources.ToArray(); } }
    public PackSyncSource? SourceFor(string relativePath) { lock (_gate) return _effectiveSources.GetValueOrDefault(relativePath)?.Source; }
    public bool IsKnownCatalogPath(string relativePath) { lock (_gate) return _knownCatalogPaths.Contains(relativePath); }
    public bool IsWithdrawn(string relativePath) { lock (_gate) return _knownCatalogPaths.Contains(relativePath) &&
        !_sources.Any(entry => entry.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)); }
    public PackSyncSource ResolveHistoricalSourceMarker(string relativePath, PackSyncSource marker)
    {
        lock (_gate)
        {
            if (!_effectiveSources.TryGetValue(relativePath, out var effective)) return marker;
            return _sourceHistory.Any(old => old.Id == effective.Id &&
                old.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase) && old.Source == marker)
                    ? effective.Source : marker;
        }
    }
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
                lock (_gate)
                {
                    ReplaceIndex(entries);
                    HasOnlineCatalog = true;
                }
                // Membership comes from the whole index, even if one listed
                // pack cannot update. Its last verified source stays effective
                // until that pack's new manifest and bundle validate.
                candidates = entries;
                foreach (var entry in candidates) LoadCachedEntry(entry);
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
                    JsonSerializer.Serialize(IndexSnapshot(), JsonOptions));
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
            if (presentation && IsCurrentEntry(entry))
            {
                PromoteEffectiveSource(entry);
                if (pack is not null) _packs[entry.RelativePath] = pack;
            }
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
            lock (_gate)
            {
                _manifests[Key(entry.Source)] = cached.Manifest;
                if (presentation && IsCurrentEntry(entry))
                {
                    PromoteEffectiveSource(entry);
                    if (pack is not null) _packs[entry.RelativePath] = pack;
                }
            }
        }
        catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library cache " + entry.RelativePath + ": " + ex.Message); }
    }

    private CachedEntry? ReadCache(CatalogSource entry)
    {
        var path = CachePath(entry.Source);
        if (!File.Exists(path) && entry.Id == "terrafirma-rebirth" && entry.Source == PortablePackSyncService.TerraFirmaSource)
            path = CachePath(new PackSyncSource("MarkZamore", "TerraFirma-Rebirth", "pack-latest"));
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
        // The stable pack ID keeps verified pre-rename offline bundles usable.
        if (entry.Id == "terrafirma-rebirth" && content.Id == entry.Id &&
            entry.RelativePath == PortablePackSyncService.TerraFirmaRelativePath &&
            content.RelativePath == PortablePackSyncService.LegacyTerraFirmaRelativePath)
        {
            content.RelativePath = entry.RelativePath;
            content.Name = PackDisplayName.For(entry.RelativePath, content.Name);
        }
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

    private static List<CatalogSource> ParseIndex(string json, bool useEmbeddedAliases = true)
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
            if (entry.LegacyRelativePaths is null || entry.LegacySources is null || entry.LegacyRelativePaths.Count > 16 ||
                entry.LegacySources.Count > 16 || entry.LegacyRelativePaths.Any(path => !ValidFolder(path) || path.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase)) ||
                entry.LegacySources.Any(source => source is null || !ValidSource(source)))
                throw new InvalidDataException("Invalid library rename aliases.");
        }
        // Normalize verified historical folder names without changing index
        // membership. The embedded index is parsed without recursively reading
        // itself to obtain its own aliases.
        IReadOnlyList<CatalogSource> aliases = useEmbeddedAliases ? SeedSources().ToArray() : [];
        var canonical = index.Packs.Select(entry => CanonicalSource(entry, aliases)).ToList();
        if (canonical.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != canonical.Count ||
            canonical.Select(entry => Key(entry.Source)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != canonical.Count)
            throw new InvalidDataException("Invalid/duplicate library rename targets.");
        return canonical;
    }

    public static bool ValidFolder(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 &&
        value is not ("." or "..") && value == value.Trim() && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.EndsWith('.');
    private static bool ValidSource(PackSyncSource source) => !string.IsNullOrWhiteSpace(source.Owner) && !string.IsNullOrWhiteSpace(source.Repo) &&
        !string.IsNullOrWhiteSpace(source.Tag) && Regex.IsMatch(source.Owner, @"^[A-Za-z0-9_-]{1,100}$") &&
        Regex.IsMatch(source.Repo, @"^[A-Za-z0-9_.-]{1,100}$") && Regex.IsMatch(source.Tag, @"^[A-Za-z0-9_.-]{1,100}$");
    private void ReplaceIndex(IReadOnlyList<CatalogSource> entries)
    {
        var previous = _effectiveSources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        _sources.Clear();
        _sources.AddRange(entries);
        _effectiveSources.Clear();
        foreach (var entry in entries)
        {
            _knownCatalogPaths.Add(entry.RelativePath);
            _effectiveSources[entry.RelativePath] = previous.TryGetValue(entry.RelativePath, out var old) && old.Id == entry.Id
                ? old : entry;
        }
        foreach (var path in _packs.Keys.ToArray())
            if (!IsCurrentEntry(_packs[path].Entry)) _packs.Remove(path);
    }

    private bool IsCurrentEntry(CatalogSource entry) => _sources.Any(current => current.Id == entry.Id &&
        current.RelativePath.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase));

    private void PromoteEffectiveSource(CatalogSource entry)
    {
        if (_effectiveSources.TryGetValue(entry.RelativePath, out var old) && old.Id == entry.Id && old.Source != entry.Source)
            RememberSource(old);
        _effectiveSources[entry.RelativePath] = entry;
    }

    private void RememberSource(CatalogSource entry)
    {
        _sourceHistory.RemoveAll(old => old.Id == entry.Id &&
            old.RelativePath.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase) && old.Source == entry.Source);
        _sourceHistory.Add(entry);
        while (_sourceHistory.Count(old => old.Id == entry.Id &&
                   old.RelativePath.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase)) > MaximumSourceHistoryPerPack)
        {
            var index = _sourceHistory.FindIndex(old => old.Id == entry.Id &&
                old.RelativePath.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase));
            _sourceHistory.RemoveAt(index);
        }
        if (_sourceHistory.Count > MaximumSourceHistoryEntries)
            _sourceHistory.RemoveRange(0, _sourceHistory.Count - MaximumSourceHistoryEntries);
    }

    private void RestoreIndexState(string json)
    {
        var index = JsonSerializer.Deserialize<CatalogIndex>(json, JsonOptions)!;
        foreach (var path in index.KnownRelativePaths ?? [])
            if (ValidFolder(path)) _knownCatalogPaths.Add(path);
        if (index.SourceHistory is { Count: <= MaximumSourceHistoryEntries } history)
            foreach (var entry in history)
                if (entry is not null && ValidFolder(entry.RelativePath) && !string.IsNullOrWhiteSpace(entry.Id) &&
                    entry.Id.Length <= 100 && entry.Source is not null && ValidSource(entry.Source)) RememberSource(entry);
        if (index.EffectiveSources is null) return;
        try
        {
            var effective = ParseIndex(JsonSerializer.Serialize(new CatalogIndex
                { SchemaVersion = 1, Packs = index.EffectiveSources }, JsonOptions));
            foreach (var entry in effective)
                if (IsCurrentEntry(entry)) _effectiveSources[entry.RelativePath] = entry;
        }
        catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library effective source cache: " + ex.Message); }
    }

    private CatalogIndex IndexSnapshot()
    {
        lock (_gate) return new CatalogIndex
        {
            SchemaVersion = 1,
            Packs = _sources.ToList(),
            EffectiveSources = _effectiveSources.Values.ToList(),
            KnownRelativePaths = _knownCatalogPaths.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            SourceHistory = _sourceHistory.ToList()
        };
    }
    private static CatalogSource CanonicalSource(CatalogSource entry, IReadOnlyList<CatalogSource> embeddedAliases)
    {
        if (entry.Id == "terrafirma-rebirth" &&
            entry.RelativePath.Equals(PortablePackSyncService.LegacyTerraFirmaRelativePath, StringComparison.OrdinalIgnoreCase))
            entry = entry with
            {
                RelativePath = PortablePackSyncService.TerraFirmaRelativePath,
                Source = PortablePackSyncService.NormalizeTerraFirmaSource(entry.RelativePath, entry.Source)
            };
        var seed = embeddedAliases.FirstOrDefault(candidate => candidate.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) &&
            candidate.LegacyRelativePaths.Contains(entry.RelativePath, StringComparer.OrdinalIgnoreCase));
        if (seed is null) return entry;
        var paths = entry.LegacyRelativePaths.Concat(seed.LegacyRelativePaths)
            .Where(path => !path.Equals(seed.RelativePath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sources = entry.LegacySources.Concat(seed.LegacySources)
            .DistinctBy(Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count > 16 || sources.Count > 16)
            throw new InvalidDataException("Too many merged library rename aliases.");
        // A folder alias does not authorize replacing a newly published source.
        // Redirect only a source explicitly identified by the embedded rename;
        // every other source still goes through the normal verification gate.
        var knownSource = PackRenameMigration.SourceEquals(entry.Source, seed.Source) ||
            seed.LegacySources.Any(source => PackRenameMigration.SourceEquals(source, entry.Source));
        return entry with
        {
            RelativePath = seed.RelativePath,
            Source = knownSource ? seed.Source : entry.Source,
            LegacyRelativePaths = paths,
            LegacySources = sources
        };
    }
    private static bool IsRecoverable(Exception ex) => ex is IOException or InvalidDataException or HttpRequestException or JsonException or
        InvalidOperationException or ArgumentException or OperationCanceledException or NotSupportedException or System.Security.SecurityException or
        KeyNotFoundException or FormatException or OverflowException or UnauthorizedAccessException;
    public static IEnumerable<CatalogSource> SeedSources()
    {
        using var stream = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.catalog.json")
            ?? throw new InvalidDataException("Missing embedded library index.");
        using var reader = new StreamReader(stream);
        return ParseIndex(reader.ReadToEnd(), useEmbeddedAliases: false);
    }
    private sealed class CatalogIndex
    {
        public int SchemaVersion { get; set; }
        public List<CatalogSource> Packs { get; set; } = null!;
        public List<CatalogSource>? EffectiveSources { get; set; }
        public List<string>? KnownRelativePaths { get; set; }
        public List<CatalogSource>? SourceHistory { get; set; }
    }
    private sealed class CachedEntry
    {
        public CatalogSource? Entry { get; set; }
        public string Manifest { get; set; } = "";
        public byte[]? Bundle { get; set; }
    }
}
