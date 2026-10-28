using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>Reads the Fabric API level that an exact official Quilt release provides.</summary>
internal sealed class QuiltFabricApiResolver(HttpClient httpClient, AppPaths? paths = null, TimeSpan? downloadStallTimeout = null)
{
    private const string Maven = "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-loader/";
    private const long MaximumJarBytes = 32L * 1024 * 1024;
    private const long MaximumMetadataBytes = 1024 * 1024;
    private readonly ConcurrentDictionary<string, Task<string?>> _versions = new(StringComparer.Ordinal);
    private readonly TimeSpan _stallTimeout = ValidStallTimeout(downloadStallTimeout);

    public async Task<string?> ResolveAsync(string minecraft, string quiltVersion, CancellationToken token)
    {
        if (!StableVersion(minecraft) || !StableVersion(quiltVersion))
            throw new InvalidDataException("Для проверки совместимости Quilt нужны точные стабильные версии.");
        // The alias belongs to the loader artifact, independently of Minecraft.
        var task = _versions.GetOrAdd(quiltVersion, _ => ResolveCoreAsync(quiltVersion, token));
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch { _versions.TryRemove(new KeyValuePair<string, Task<string?>>(quiltVersion, task)); throw; }
    }

    private async Task<string?> ResolveCoreAsync(string version, CancellationToken token)
    {
        var name = "quilt-loader-" + version + ".jar";
        var uri = Maven + version + "/" + name;
        // Use this artifact's own checksum. The Quilt meta API can describe a
        // differently packed copy of the same release; those hashes cannot be
        // mixed with bytes downloaded from Maven.
        var checksumBytes = await ReadLimitedAsync(uri + ".sha256", 1024, token).ConfigureAwait(false);
        var checksum = Encoding.UTF8.GetString(checksumBytes).Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!Regex.IsMatch(checksum, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new InvalidDataException("Некорректная SHA-256 официального Quilt Loader.");

        var cache = paths is null ? null : Path.Combine(SharedRuntimeStore.Libraries(paths),
            "org", "quiltmc", "quilt-loader", version, name);
        if (cache is not null)
        {
            ValidateCachePath(cache);
            if (File.Exists(cache) && new FileInfo(cache).Length <= MaximumJarBytes)
            {
                var existing = await File.ReadAllBytesAsync(cache, token).ConfigureAwait(false);
                if (Matches(existing, checksum)) return ReadFabricAlias(existing, version);
            }
        }
        var bytes = await ReadLimitedAsync(uri, MaximumJarBytes, token).ConfigureAwait(false);
        if (!Matches(bytes, checksum))
            throw new InvalidDataException("Quilt Loader не совпал с SHA-256 официального издателя.");
        var alias = ReadFabricAlias(bytes, version);
        if (cache is not null)
        {
            ValidateCachePath(cache);
            AtomicFile.WriteAllBytes(cache, bytes);
        }
        return alias;
    }

    private static string? ReadFabricAlias(byte[] bytes, string expectedVersion)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count > 100000) throw new InvalidDataException("Слишком много записей в Quilt Loader.");
        var entries = archive.Entries.Where(entry => entry.FullName.Equals("quilt.mod.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].FullName != "quilt.mod.json" || entries[0].Length > MaximumMetadataBytes)
            throw new InvalidDataException("Quilt Loader должен содержать один корневой quilt.mod.json допустимого размера.");
        using var stream = entries[0].Open();
        using var contents = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (contents.Length + read > MaximumMetadataBytes) throw new InvalidDataException("Метаданные Quilt Loader слишком большие.");
            contents.Write(buffer, 0, read);
        }
        using var document = JsonDocument.Parse(contents.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = Object(document.RootElement);
        if (!root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
            !root.TryGetProperty("quilt_loader", out var rawLoader))
            throw new InvalidDataException("Неизвестная схема метаданных Quilt Loader.");
        var loader = Object(rawLoader);
        if (!loader.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != "quilt_loader" ||
            !loader.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != expectedVersion)
            throw new InvalidDataException("Метаданные принадлежат другой версии или другому компоненту Quilt.");
        if (!loader.TryGetProperty("provides", out var provides)) return null;
        if (provides.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Некорректный список provides в Quilt Loader.");
        string? alias = null;
        var aliases = 0;
        foreach (var provided in provides.EnumerateArray())
        {
            // A shorthand without an explicit version proves no Fabric API
            // level. Never substitute the Quilt release number for it.
            if (provided.ValueKind == JsonValueKind.String)
            {
                if (provided.GetString() == "fabricloader") aliases++;
                continue;
            }
            var entry = Object(provided);
            if (!entry.TryGetProperty("id", out var providedId) || providedId.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("У provides отсутствует идентификатор.");
            if (providedId.GetString() != "fabricloader") continue;
            aliases++;
            if (!entry.TryGetProperty("version", out var providedVersion) || providedVersion.ValueKind != JsonValueKind.String)
                continue;
            alias = providedVersion.GetString()!;
            _ = StrictVersionRange.Accepts("*", alias);
        }
        if (aliases > 1) throw new InvalidDataException("Quilt Loader содержит неоднозначную версию fabricloader.");
        return alias;
    }

    private async Task<byte[]> ReadLimitedAsync(string uri, long maximum, CancellationToken token)
    {
        // ResponseHeadersRead ends HttpClient.Timeout's coverage at the headers.
        // Keep the body cancellable too, and restart the stall deadline whenever
        // bytes arrive, just as for the launcher's managed component downloads.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_stallTimeout);
        try
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Артефакт Quilt Loader слишком большой.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            deadline.CancelAfter(_stallTimeout);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > maximum) throw new InvalidDataException("Артефакт Quilt Loader слишком большой.");
                output.Write(buffer, 0, read);
                deadline.CancelAfter(_stallTimeout);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new HttpRequestException("Загрузка Quilt Loader остановилась: источник не передаёт данные.", exception);
        }
    }

    private static TimeSpan ValidStallTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? ManagedComponentService.DownloadStallTimeout;
        if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return value;
    }

    private void ValidateCachePath(string cache)
    {
        paths!.EnsureUnderRoot(cache);
        for (var current = cache; !string.Equals(current, paths.Root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Кеш Quilt Loader не должен проходить через ссылки.");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }

    private static bool StableVersion(string version) => version is not null && version.Length <= 128 &&
        Regex.IsMatch(version, @"^\d+(?:\.\d+)+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool Matches(byte[] bytes, string checksum) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), checksum, StringComparison.OrdinalIgnoreCase);

    private static JsonElement Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Метаданные Quilt Loader должны содержать объект JSON.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Повторённое поле метаданных Quilt Loader: " + property.Name);
        return value;
    }
}
