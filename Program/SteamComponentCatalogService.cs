using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

/// <summary>Fetch once at startup; keep one checked selection for the whole session.</summary>
internal sealed class SteamComponentCatalogService
{
    internal static readonly Uri IndexUri = new("https://raw.githubusercontent.com/MarkZamore/e4steam/main/components.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumCacheBytes = 2 * 1024 * 1024;
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private Task<SteamComponentCatalogSnapshot>? _load;
    internal string CachePath { get; }

    internal SteamComponentCatalogService(AppPaths paths, Logger logger, HttpClient? http = null, TimeSpan? timeout = null)
    {
        _paths = paths;
        _logger = logger;
        _http = http ?? PortableHttpClient.Shared;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        CachePath = Path.Combine(paths.Launcher, "ComponentCatalog", "e4steam.json");
    }

    internal Task<SteamComponentCatalogSnapshot> LoadAsync(CancellationToken token = default)
    {
        lock (_gate) return _load ??= LoadCoreAsync(token);
    }

    private async Task<SteamComponentCatalogSnapshot> LoadCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var fallbackJson = SteamComponentCatalog.EmbeddedJson();
        var fallback = SteamComponentCatalog.Parse(fallbackJson);
        var known = new List<ManagedComponentDescriptor>();
        try
        {
            CheckCachePath();
            if (File.Exists(CachePath))
            {
                SteamComponentCatalog.Require(new FileInfo(CachePath).Length <= MaximumCacheBytes, "Oversized Steam catalog cache.");
                var saved = JsonSerializer.Deserialize<CacheEnvelope>(File.ReadAllText(CachePath), JsonOptions)
                    ?? throw new InvalidDataException("Empty Steam catalog cache.");
                SteamComponentCatalog.Require(saved.SchemaVersion == 1 && saved.Catalog is not null &&
                    saved.CatalogSha256 == Digest(saved.Catalog) && saved.KnownBuilds is { Count: <= 1024 },
                    "Invalid Steam catalog cache envelope.");
                var cached = SteamComponentCatalog.Parse(saved.Catalog!);
                var restored = saved.KnownBuilds!.Select(ValidateKnown).ToArray();
                fallback = SteamComponentCatalog.WithHistory(cached, restored);
                known.AddRange(restored);
                _logger.Info($"Steam component catalog: checked cached profile {fallback.Profile}.");
            }
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            token.ThrowIfCancellationRequested();
            _logger.Warn("Steam component catalog cache rejected: " + ex.Message);
        }
        known.AddRange(fallback.Builds.Select(ManagedComponentService.DescriptorFor));

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(_timeout);
            // A mutable raw-branch index needs a fresh URL as well as HTTP headers:
            // cached successful responses must not freeze a rolling component catalog.
            var uri = new UriBuilder(IndexUri) { Query = "launcherRefresh=" + Guid.NewGuid().ToString("N") }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            SteamComponentCatalog.Require(response.Content.Headers.ContentLength is null or <= SteamComponentCatalog.MaximumDocumentBytes,
                "Oversized Steam component index response.");
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, budget.Token).ConfigureAwait(false);
                if (count == 0) break;
                SteamComponentCatalog.Require(bytes.Length + count <= SteamComponentCatalog.MaximumDocumentBytes,
                    "Oversized Steam component index body.");
                bytes.Write(buffer, 0, count);
            }
            var json = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            var snapshot = SteamComponentCatalog.WithHistory(SteamComponentCatalog.Parse(json), known);
            token.ThrowIfCancellationRequested();
            Save(json, snapshot);
            _logger.Info($"Steam component catalog: verified network profile {snapshot.Profile}; {snapshot.Builds.Count} builds.");
            return snapshot;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            token.ThrowIfCancellationRequested();
            _logger.Warn("Steam component catalog network unavailable/rejected; keeping checked fallback: " + ex.Message);
            return SteamComponentCatalog.WithHistory(fallback, known);
        }
    }

    private void Save(string json, SteamComponentCatalogSnapshot snapshot)
    {
        try
        {
            CheckCachePath();
            var rows = snapshot.SupersededBuilds.Concat(snapshot.Builds.Select(ManagedComponentService.DescriptorFor))
                .Where(row => row.DownloadUris.Count == 1 && row.DownloadUris[0].AbsolutePath.StartsWith("/MarkZamore/e4steam/releases/download/", StringComparison.Ordinal))
                .DistinctBy(row => (row.FileName.ToLowerInvariant(), row.SizeBytes, row.Sha256))
                .Select(row => new KnownBuild(row.FileName, row.SizeBytes, row.Sha256, row.FileId, row.DownloadUris[0].AbsoluteUri)).ToArray();
            SteamComponentCatalog.Require(rows.Length <= 1024, "Steam component history is too large.");
            var contents = JsonSerializer.Serialize(new CacheEnvelope(1, json, Digest(json), rows.ToList()), JsonOptions);
            SteamComponentCatalog.Require(Encoding.UTF8.GetByteCount(contents) <= MaximumCacheBytes, "Steam catalog cache is too large.");
            AtomicFile.WriteAllText(CachePath, contents);
        }
        catch (Exception ex) when (Recoverable(ex)) { _logger.Warn("Steam catalog cache could not be saved: " + ex.Message); }
    }

    private static ManagedComponentDescriptor ValidateKnown(KnownBuild? row)
    {
        if (row is null) throw new InvalidDataException("Null Steam catalog history entry.");
        SteamComponentCatalog.ValidatePin(row.FileName, row.SizeBytes, row.Sha256);
        SteamComponentCatalog.Require(row.CacheFileId == SteamComponentCatalog.CacheId(row.FileName, row.SizeBytes, row.Sha256),
            "Steam history cache identity changed.");
        return new(ManagedComponentService.E4steamComponentId, row.CacheFileId, row.FileName,
            [SteamComponentCatalog.OwnReleaseUri(row.DownloadUrl, row.FileName)], row.SizeBytes, row.Sha256);
    }

    private void CheckCachePath()
    {
        _paths.EnsureUnderRoot(CachePath);
        for (var path = CachePath; path is not null && Path.GetFullPath(path).Length >= Path.GetFullPath(_paths.Root).Length;
             path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked Steam catalog cache path is refused.");
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_paths.Root), StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or
        JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or
        OverflowException or OperationCanceledException or System.Security.SecurityException or DecoderFallbackException;
    private sealed record CacheEnvelope(int SchemaVersion, string? Catalog, string? CatalogSha256, List<KnownBuild>? KnownBuilds);
    private sealed record KnownBuild(string FileName, long SizeBytes, string Sha256, long CacheFileId, string DownloadUrl);
}
