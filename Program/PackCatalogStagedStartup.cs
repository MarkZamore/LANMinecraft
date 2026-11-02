using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed partial class PackCatalogService
{
    private readonly Dictionary<string, Task> _launchTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _launchReadyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CatalogSource> _launchSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _launchManifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<string>> _remoteManifestTasks = new(StringComparer.OrdinalIgnoreCase);
    private Task? _indexStartup;
    private Task? _libraryStartup;

    public bool IsStaged { get; }
    public bool IsIndexReady { get; private set; }

    public string GetDisplayName(string relativePath)
    {
        lock (_gate)
            return _packs.TryGetValue(relativePath, out var pack) ? pack.Name : PackDisplayName.For(relativePath, relativePath);
    }

    public bool IsLaunchReady(string relativePath)
    {
        lock (_gate) return IsStaged ? _launchReadyPaths.Contains(relativePath) : IsReady;
    }

    private CatalogSource? RuntimeSource(string relativePath) =>
        IsStaged && _launchReadyPaths.Contains(relativePath)
            ? _launchSources.GetValueOrDefault(relativePath) : _effectiveSources.GetValueOrDefault(relativePath);

    /// <summary>One small authoritative index request, with the last checked index as its offline fallback.</summary>
    public Task InitializeIndexAsync(CancellationToken token = default)
    {
        if (!IsStaged) return RefreshOnceAsync(token);
        lock (_gate) return _indexStartup ??= Task.Run(() => InitializeIndexCoreAsync(token), token);
    }

    private async Task InitializeIndexCoreAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var bytes = await ReadBoundedAsync(MutableMetadataUri(IndexUri), 256 * 1024, timeout.Token).ConfigureAwait(false);
            var entries = ParseIndex(Encoding.UTF8.GetString(bytes));
            lock (_gate) { ReplaceIndex(entries); HasOnlineCatalog = true; }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            token.ThrowIfCancellationRequested();
            _logger.Warn("Library discovery: " + ex.Message);
        }
        token.ThrowIfCancellationRequested();
        lock (_gate) IsIndexReady = true;
        SaveStagedIndex();
    }

    /// <summary>
    /// Validate and freeze only this pack's game plan. A different pack or a
    /// preview/mod-list download never occupies this task's scheduling slot.
    /// </summary>
    public Task EnsureLaunchReadyAsync(string relativePath, CancellationToken token = default)
    {
        if (!IsStaged) return RefreshOnceAsync(token);
        if (!ValidFolder(relativePath)) throw new InvalidDataException("Invalid launch pack folder.");
        lock (_gate)
        {
            if (_launchTasks.TryGetValue(relativePath, out var existing)) return existing;
            return _launchTasks[relativePath] = Task.Run(() => EnsureLaunchCoreAsync(relativePath, token), token);
        }
    }

    private async Task EnsureLaunchCoreAsync(string relativePath, CancellationToken token)
    {
        var networkClock = Stopwatch.StartNew();
        await InitializeIndexAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _ = _paths.CombineUnderPacks(relativePath);
        if (IsWithdrawn(relativePath)) throw new IOException("Эта сборка удалена из каталога GitHub.");

        // Reuse the existing rules for explicit custom forks, renamed markers
        // and historical default repositories, reading only the selected marker.
        var source = new PortablePackSyncService(_paths, _logger) { Catalog = this }.TryResolveSource(relativePath);
        CatalogSource? entry = null;
        lock (_gate)
        {
            if (source is not null)
            {
                var known = _effectiveSources.GetValueOrDefault(relativePath);
                entry = known is not null && known.Source == source ? known :
                    new CatalogSource(known?.Id ?? relativePath, relativePath, source);
            }
        }
        if (entry is null)
        {
            lock (_gate) _launchReadyPaths.Add(relativePath);
            return;
        }

        string raw;
        try { raw = await GameManifestAsync(entry, TimeSpan.FromSeconds(15) - networkClock.Elapsed, token).ConfigureAwait(false); }
        catch (IOException ex) when (!token.IsCancellationRequested &&
            PackManifestService.HasManifest(_paths.CombineUnderPacks(relativePath)) &&
            !PortablePackSyncService.IsUpdatePending(_paths.CombineUnderPacks(relativePath)))
        {
            // Preserve the sync engine's existing installed-pack offline mode.
            // No remote plan is invented: SessionManifest still fails, so its
            // ordinary fallback and interrupted-update guards remain in charge.
            lock (_gate)
            {
                _launchSources[relativePath] = entry;
                _launchReadyPaths.Add(relativePath);
            }
            _logger.Warn("Installed pack remains available offline: " + ex.Message);
            return;
        }
        token.ThrowIfCancellationRequested();
        string frozen;
        lock (_gate)
        {
            _launchManifests.TryAdd(Key(entry.Source), raw);
            frozen = _launchManifests[Key(entry.Source)];
        }
        // Another folder using this source may have frozen the fallback while
        // this folder was validating. Readiness belongs to the actual session
        // snapshot, including its path/link checks against this folder.
        if (!string.Equals(raw, frozen, StringComparison.Ordinal))
            PortablePackSyncService.ValidateSnapshot(frozen, _paths.CombineUnderPacks(entry.RelativePath), token);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _launchSources[relativePath] = entry;
            _launchReadyPaths.Add(relativePath);
        }
        SaveLaunchCache(entry, frozen);
        LogStagedManifest(entry, frozen);
    }

    private Task<string> RemoteManifestOnceAsync(PackSyncSource source, CancellationToken token) =>
        RemoteManifestOnceAsync(source, TimeSpan.FromSeconds(15), token);

    private Task<string> RemoteManifestOnceAsync(PackSyncSource source, TimeSpan networkBudget, CancellationToken token)
    {
        lock (_gate)
        {
            var key = Key(source);
            if (_remoteManifestTasks.TryGetValue(key, out var existing)) return existing;
            return _remoteManifestTasks[key] = ReadRemoteManifestAsync(source, networkBudget, token);
        }
    }

    private async Task<string> ReadRemoteManifestAsync(PackSyncSource source, TimeSpan networkBudget, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(networkBudget);
        return Encoding.UTF8.GetString(await ReadBoundedAsync(
            MutableMetadataUri(AssetUri(source, "pack-manifest.json")), MaximumManifestBytes, timeout.Token).ConfigureAwait(false));
    }

    private async Task<string> GameManifestAsync(CatalogSource entry, TimeSpan networkBudget, CancellationToken token)
    {
        string? frozen;
        lock (_gate) _launchManifests.TryGetValue(Key(entry.Source), out frozen);
        if (frozen is not null)
        {
            PortablePackSyncService.ValidateSnapshot(frozen, _paths.CombineUnderPacks(entry.RelativePath), token);
            return frozen;
        }
        try
        {
            // Index discovery and this selected request share one network wait.
            // A later selection gets a fresh budget; game/path validation does
            // not expire with network time and still checks every fallback.
            if (networkBudget <= TimeSpan.Zero) throw new TimeoutException("The launch network budget expired during index discovery.");
            var raw = await RemoteManifestOnceAsync(entry.Source, networkBudget, token)
                .WaitAsync(networkBudget, token).ConfigureAwait(false);
            PortablePackSyncService.ValidateSnapshot(raw, _paths.CombineUnderPacks(entry.RelativePath), token);
            return raw;
        }
        catch (Exception ex) when (IsRecoverable(ex) || ex is TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            _logger.Warn("Launch manifest " + entry.RelativePath + ": " + ex.Message);
        }
        foreach (var raw in ReadLaunchFallbacks(entry))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                PortablePackSyncService.ValidateSnapshot(raw, _paths.CombineUnderPacks(entry.RelativePath), token);
                return raw;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            { token.ThrowIfCancellationRequested(); _logger.Warn("Launch cache " + entry.RelativePath + ": " + ex.Message); }
        }
        throw new IOException("Версию выбранной сборки не удалось проверить. Перезапустите лаунчер с доступом к интернету.");
    }

    private IEnumerable<string> ReadLaunchFallbacks(CatalogSource entry)
    {
        // These files are examined only for the requested pack. JsonDocument
        // skips the old cache's base64 bundle without decoding a single image.
        var launchFile = LaunchCachePath(entry.Source);
        foreach (var file in new[] { launchFile, LegacyLibraryCachePath(entry) })
        {
            string? raw = null;
            try
            {
                if (!File.Exists(file)) continue;
                if (new FileInfo(file).Length > 3L * MaximumManifestBytes) throw new InvalidDataException("Oversized launch cache.");
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (file.Equals(launchFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (!root.TryGetProperty("source", out var sourceJson)) throw new InvalidDataException("Missing launch cache source.");
                    var source = sourceJson.Deserialize<PackSyncSource>(JsonOptions);
                    if (source is null || !ValidSource(source) || source != entry.Source)
                        throw new InvalidDataException("Launch cache source mismatch.");
                }
                else
                {
                    if (!root.TryGetProperty("entry", out var descriptor) || descriptor.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Missing library cache identity.");
                    var cached = descriptor.Deserialize<CatalogSource>(JsonOptions);
                    if (cached is null || !ValidFolder(cached.RelativePath) || string.IsNullOrWhiteSpace(cached.Id) ||
                        cached.Source is null || !ValidSource(cached.Source) || cached.LegacyRelativePaths is null ||
                        cached.LegacySources is null || cached.LegacySources.Any(source => source is null || !ValidSource(source)))
                        throw new InvalidDataException("Invalid launch cache identity.");
                    var canonical = CanonicalSource(cached, SeedSources().ToArray());
                    if (canonical.Source != entry.Source ||
                        (IsCurrentEntry(entry) && (cached.Id != entry.Id ||
                         !canonical.RelativePath.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase))))
                        throw new InvalidDataException("Launch cache identity mismatch.");
                }
                if (root.TryGetProperty("manifest", out var manifest) && manifest.ValueKind == JsonValueKind.String) raw = manifest.GetString();
            }
            catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Launch cache could not be read: " + ex.Message); }
            if (raw is not null) yield return raw;
        }
    }

    private string LaunchCachePath(PackSyncSource source) => Path.Combine(_cache, "Launch", Path.GetFileName(CachePath(source)));
    private string LegacyLibraryCachePath(CatalogSource entry)
    {
        var path = CachePath(entry.Source);
        return !File.Exists(path) && entry.Id == "terrafirma-rebirth" && entry.Source == PortablePackSyncService.TerraFirmaSource
            ? CachePath(new PackSyncSource("MarkZamore", "TerraFirma-Rebirth", "pack-latest")) : path;
    }

    private void SaveLaunchCache(CatalogSource entry, string raw)
    {
        try { AtomicFile.WriteAllTextIfChanged(LaunchCachePath(entry.Source), JsonSerializer.Serialize(new LaunchCachedEntry { Source = entry.Source, Manifest = raw }, JsonOptions)); }
        catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Launch cache could not be saved: " + ex.Message); }
    }

    public Task PrepareLibraryAsync(CancellationToken token = default)
    {
        if (!IsStaged) return RefreshOnceAsync(token);
        lock (_gate) return _libraryStartup ??= Task.Run(() => PrepareLibraryCoreAsync(token), token);
    }

    private async Task PrepareLibraryCoreAsync(CancellationToken token)
    {
        await InitializeIndexAsync(token).ConfigureAwait(false);
        // Only advertised packs need presentation. Custom source markers are
        // read on selection, independently of unrelated library preparation.
        var candidates = Sources;
        using var parallel = new SemaphoreSlim(4);
        try
        {
            await Task.WhenAll(candidates.Select(async entry =>
            {
                await parallel.WaitAsync(token).ConfigureAwait(false);
                try { await PrepareLibraryEntryAsync(entry, token).ConfigureAwait(false); }
                catch (Exception ex) when (IsRecoverable(ex))
                { token.ThrowIfCancellationRequested(); _logger.Warn("Library " + entry.RelativePath + ": " + ex.Message); }
                finally { parallel.Release(); }
            })).ConfigureAwait(false);
        }
        finally
        {
            if (!token.IsCancellationRequested) lock (_gate) IsReady = true;
            SaveStagedIndex();
        }
    }

    private async Task PrepareLibraryEntryAsync(CatalogSource entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CatalogSource effective;
        lock (_gate) effective = _effectiveSources.GetValueOrDefault(entry.RelativePath) ?? entry;
        LoadStagedSeed(entry);
        LoadCachedEntry(effective, token: token);
        if (effective.Source != entry.Source) LoadCachedEntry(entry, token: token);

        // Launch tasks bypass the background semaphore. If this source is also
        // selected, both consumers share its one rolling metadata response.
        try { await EnsureLaunchReadyAsync(entry.RelativePath, token).ConfigureAwait(false); }
        catch (Exception ex) when (IsRecoverable(ex))
        { token.ThrowIfCancellationRequested(); _logger.Warn("Library launch fallback " + entry.RelativePath + ": " + ex.Message); }
        var raw = await RemoteManifestOnceAsync(entry.Source, token).ConfigureAwait(false);
        PortablePackSyncService.ValidateSnapshot(raw, _paths.CombineUnderPacks(entry.RelativePath), token);
        using var document = JsonDocument.Parse(raw);
        byte[]? bundle = null;
        CatalogPack? pack = null;
        if (document.RootElement.TryGetProperty("library", out var descriptor))
        {
            var name = descriptor.GetProperty("name").GetString() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^library-[a-f0-9]{64}\.zip$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid library asset name.");
            if (descriptor.GetProperty("sizeBytes").GetInt64() is <= 0 or > MaximumBundleBytes)
                throw new InvalidDataException("Library bundle size is invalid.");
            CachedEntry? cached = null;
            try { cached = ReadCache(entry); }
            catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Ignoring damaged library cache: " + ex.Message); }
            bundle = cached?.Bundle is { } bytes && VerifyHash(bytes, descriptor) ? bytes :
                await ReadBoundedAsync(AssetUri(entry.Source, name), MaximumBundleBytes, token).ConfigureAwait(false);
            if (!VerifyHash(bundle, descriptor)) throw new InvalidDataException("Library bundle hash/size mismatch.");
            var revision = document.RootElement.TryGetProperty("revision", out var declared) ? declared.GetString() : null;
            if (descriptor.GetProperty("revision").GetString() != revision) throw new InvalidDataException("Library descriptor revision mismatch.");
            pack = DecodeBundle(entry, bundle, revision);
        }
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _manifests[Key(entry.Source)] = raw;
            if (IsCurrentEntry(entry))
            {
                PromoteEffectiveSource(entry);
                if (pack is not null) _packs[entry.RelativePath] = pack;
            }
        }
        try { AtomicFile.WriteAllTextIfChanged(CachePath(entry.Source), JsonSerializer.Serialize(new CachedEntry { Entry = entry, Manifest = raw, Bundle = bundle }, JsonOptions)); }
        catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library cache could not be saved: " + ex.Message); }
        SaveStagedIndex();
    }

    private void LoadStagedSeed(CatalogSource entry)
    {
        try
        {
            lock (_gate) if (_packs.ContainsKey(entry.RelativePath)) return;
            using var stream = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library." + entry.Id + ".zip");
            if (stream is null) return;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var pack = DecodeBundle(entry, bytes.ToArray(), null);
            lock (_gate) if (IsCurrentEntry(entry)) _packs.TryAdd(entry.RelativePath, pack);
        }
        catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library fallback: " + ex.Message); }
    }

    private void SaveStagedIndex()
    {
        try
        {
            lock (_gate) AtomicFile.WriteAllTextIfChanged(Path.Combine(_cache, "catalog.json"), JsonSerializer.Serialize(IndexSnapshot(), JsonOptions));
        }
        catch (Exception ex) when (IsRecoverable(ex)) { _logger.Warn("Library index could not be saved: " + ex.Message); }
    }

    private void LogStagedManifest(CatalogSource entry, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var revision = document.RootElement.TryGetProperty("revision", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (revision is { Length: > 160 }) revision = revision[..160] + "...";
        _logger.Info("Launch " + entry.RelativePath + ": verified startup manifest revision " + JsonSerializer.Serialize(revision) +
            ", SHA256 " + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw))) + ".");
    }

    private sealed class LaunchCachedEntry
    {
        public PackSyncSource? Source { get; set; }
        public string Manifest { get; set; } = "";
    }
}
