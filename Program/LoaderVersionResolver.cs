using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Minecraft;

internal interface IAutoRuntimeCatalog
{
    Task<IReadOnlyList<string>> MinecraftVersionsAsync(CancellationToken token);
    Task<IReadOnlyList<string>> LoaderVersionsAsync(PackLoaderKind loader, string minecraft, CancellationToken token);
    Task<string?> FmlVersionAsync(PackLoaderKind loader, string minecraft, string loaderVersion, CancellationToken token);
    Task<string?> FabricApiVersionAsync(string minecraft, string quiltVersion, CancellationToken token);
}

/// <summary>Official release metadata, cached for one launcher session.</summary>
public sealed class LoaderVersionResolver(HttpClient httpClient, Logger? logger = null, AppPaths? paths = null) : IAutoRuntimeCatalog
{
    private const string MinecraftManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string NeoMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
    private static readonly Regex Stable = new(@"^\d+(?:\.\d+)+$", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, Task<string>> _responses = new(StringComparer.Ordinal);
    private readonly QuiltFabricApiResolver _quiltFabricApi = new(httpClient, paths);
    private readonly TimeSpan _downloadStallTimeout = ManagedComponentService.DownloadStallTimeout;

    internal LoaderVersionResolver(HttpClient httpClient, Logger? logger, AppPaths? paths, TimeSpan downloadStallTimeout)
        : this(httpClient, logger, paths)
    {
        if (downloadStallTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadStallTimeout));
        _downloadStallTimeout = downloadStallTimeout;
    }

    public Task<string?> FabricApiVersionAsync(string minecraft, string quiltVersion, CancellationToken token) =>
        _quiltFabricApi.ResolveAsync(minecraft, quiltVersion, token);

    public async Task<string?> ResolveAsync(PackLoaderKind loader, string minecraftVersion, CancellationToken token)
    {
        try { return (await LoaderVersionsAsync(loader, minecraftVersion, token).ConfigureAwait(false)).FirstOrDefault(); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
        {
            token.ThrowIfCancellationRequested();
            logger?.Warn("Could not resolve " + loader + " for Minecraft " + minecraftVersion + ": " + ex.Message);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> MinecraftVersionsAsync(CancellationToken token)
    {
        using var json = JsonDocument.Parse(await ReadAsync(MinecraftManifest, token).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Официальный каталог Minecraft не содержит список выпусков.");
        return versions.EnumerateArray().Where(entry => entry.TryGetProperty("type", out var type) && type.GetString() == "release")
            .Select(entry => entry.GetProperty("id").GetString()).Where(version => version is not null && Stable.IsMatch(version))
            .Select(version => version!).Distinct(StringComparer.Ordinal).OrderByDescending(version => version, VersionOrder.Instance).ToArray();
    }

    public async Task<IReadOnlyList<string>> LoaderVersionsAsync(PackLoaderKind loader, string minecraft, CancellationToken token)
    {
        if (!Stable.IsMatch(minecraft)) throw new InvalidDataException("Для автоподбора нужен стабильный выпуск Minecraft.");
        if (loader is PackLoaderKind.Fabric or PackLoaderKind.Quilt)
        {
            var endpoint = loader == PackLoaderKind.Fabric ? "https://meta.fabricmc.net/v2/versions/loader/" : "https://meta.quiltmc.org/v3/versions/loader/";
            using var document = JsonDocument.Parse(await ReadAsync(endpoint + Uri.EscapeDataString(minecraft), token).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Некорректный каталог загрузчика " + loader + ".");
            return document.RootElement.EnumerateArray().Where(entry => entry.TryGetProperty("loader", out _))
                .Select(entry => entry.GetProperty("loader"))
                .Where(entry => entry.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String && Stable.IsMatch(version.GetString()!) &&
                    // Quilt has no stable flag. Numeric releases exclude -beta/-rc;
                    // an explicit false in either publisher's response still wins.
                    (entry.TryGetProperty("stable", out var stable) ? stable.ValueKind == JsonValueKind.True : loader == PackLoaderKind.Quilt))
                .Select(entry => entry.GetProperty("version").GetString()!).Distinct(StringComparer.Ordinal)
                .OrderByDescending(version => version, VersionOrder.Instance).ToArray();
        }
        if (loader is not (PackLoaderKind.Forge or PackLoaderKind.NeoForge)) return [];
        var endpointMaven = loader == PackLoaderKind.Forge ? ForgeMaven : NeoMaven;
        var xml = XDocument.Parse(await ReadAsync(endpointMaven + "maven-metadata.xml", token).ConfigureAwait(false));
        var published = xml.Descendants().Where(element => element.Name.LocalName == "version").Select(element => element.Value.Trim());
        if (loader == PackLoaderKind.NeoForge)
        {
            var parts = VersionOrder.Parse(minecraft);
            var prefix = parts[0] == 1
                ? parts[1] + "." + (parts.Length > 2 ? parts[2] : 0) + "."
                : string.Join('.', parts.Length == 2 ? parts.Concat([0]) : parts) + ".";
            return published.Where(version => Stable.IsMatch(version) && version.StartsWith(prefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).OrderByDescending(version => version, VersionOrder.Instance).ToArray();
        }
        return published.Where(version => version.StartsWith(minecraft + "-", StringComparison.Ordinal))
            .Select(version => version[(minecraft.Length + 1)..])
            .Where(version => Stable.IsMatch(ComparisonVersion(loader, minecraft, version)))
            .Distinct(StringComparer.Ordinal).OrderByDescending(version => ComparisonVersion(loader, minecraft, version), VersionOrder.Instance).ToArray();
    }

    public async Task<string?> FmlVersionAsync(PackLoaderKind loader, string minecraft, string loaderVersion, CancellationToken token)
    {
        if (loader is not (PackLoaderKind.Forge or PackLoaderKind.NeoForge)) return null;
        var artifact = loader == PackLoaderKind.Forge && !loaderVersion.StartsWith(minecraft + "-", StringComparison.Ordinal)
            ? minecraft + "-" + loaderVersion : loaderVersion;
        var endpoint = (loader == PackLoaderKind.Forge ? ForgeMaven : NeoMaven) + Uri.EscapeDataString(artifact) + "/";
        var stem = (loader == PackLoaderKind.Forge ? "forge-" : "neoforge-") + artifact;
        var pom = XDocument.Parse(await ReadAsync(endpoint + Uri.EscapeDataString(stem) + ".pom", token).ConfigureAwait(false));
        foreach (var dependency in pom.Descendants().Where(element => element.Name.LocalName == "dependency"))
        {
            string? Part(string name) => dependency.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
            var group = Part("groupId");
            var name = Part("artifactId");
            var version = Part("version");
            if (group == "net.neoforged.fancymodloader" && name == "loader" && version is not null && Stable.IsMatch(version)) return version;
            if (name == "javafmllanguage" && group is "net.minecraftforge" or "net.neoforged" && version is not null)
            {
                var language = version.StartsWith(minecraft + "-", StringComparison.Ordinal) ? version[(minecraft.Length + 1)..] : version;
                if (Stable.IsMatch(language)) return language;
            }
        }
        // Forge's official POM often contains no dependencies at all. Its
        // installer still names the exact language loader in install_profile.json.
        return await InstallerFmlVersionAsync(loader, minecraft, artifact, endpoint, stem, token).ConfigureAwait(false);
    }

    private async Task<string?> InstallerFmlVersionAsync(PackLoaderKind loader, string minecraft, string artifact,
        string endpoint, string stem, CancellationToken token)
    {
        var fileName = stem + "-installer.jar";
        var uri = endpoint + Uri.EscapeDataString(fileName);
        var checksum = (await ReadAsync(uri + ".sha1", token).ConfigureAwait(false)).Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!Regex.IsMatch(checksum, "^[0-9a-fA-F]{40}$")) throw new InvalidDataException("Некорректная контрольная сумма установщика загрузчика.");
        string? cache = paths is null ? null : Path.Combine(paths.SharedRuntime, "installers", loader.ToString().ToLowerInvariant(), artifact, fileName);
        if (cache is not null) ValidateCachePath(cache);
        byte[]? bytes = null;
        if (cache is not null && File.Exists(cache) && new FileInfo(cache).Length <= MaximumInstallerBytes)
        {
            var existing = await File.ReadAllBytesAsync(cache, token).ConfigureAwait(false);
            if (InstallerMatches(existing, checksum)) bytes = existing;
        }
        if (bytes is null)
        {
            bytes = await DownloadAsync(uri, MaximumInstallerBytes, token).ConfigureAwait(false);
            if (!InstallerMatches(bytes, checksum)) throw new InvalidDataException("Установщик загрузчика не совпал с контрольной суммой издателя.");
            if (cache is not null)
            {
                ValidateCachePath(cache);
                AtomicFile.WriteAllBytes(cache, bytes);
            }
        }
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var name in new[] { "version.json", "install_profile.json" })
        {
            var entry = archive.GetEntry(name);
            if (entry is null) continue;
            if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Метаданные установщика слишком большие.");
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            foreach (var owner in new[] {json.RootElement}.Concat(json.RootElement.TryGetProperty("versionInfo", out var info) ? [info] : []))
            {
                if (!owner.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array) continue;
                foreach (var library in libraries.EnumerateArray())
                {
                    if (!library.TryGetProperty("name", out var coordinate) || coordinate.ValueKind != JsonValueKind.String) continue;
                    var pieces = coordinate.GetString()!.Split(':');
                    if (pieces.Length < 3) continue;
                    if (pieces[0] == "net.neoforged.fancymodloader" && pieces[1] == "loader" && Stable.IsMatch(pieces[2])) return pieces[2];
                    if (pieces[1] != "javafmllanguage" || pieces[0] is not ("net.minecraftforge" or "net.neoforged")) continue;
                    var language = pieces[2].StartsWith(minecraft + "-", StringComparison.Ordinal)
                        ? pieces[2][(minecraft.Length + 1)..] : pieces[2];
                    if (Stable.IsMatch(language)) return language;
                }
            }
        }
        return null;
    }

    private const long MaximumInstallerBytes = 96L * 1024 * 1024;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Forge and NeoForge publish SHA-1 checksums for their installer artifacts.")]
    private static bool InstallerMatches(byte[] bytes, string sha1) =>
        string.Equals(Convert.ToHexString(SHA1.HashData(bytes)), sha1, StringComparison.OrdinalIgnoreCase);

    private void ValidateCachePath(string cache)
    {
        paths!.EnsureUnderRoot(cache);
        for (var current = cache; !string.Equals(current, paths.Root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Кеш загрузчика не должен проходить через ссылки.");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }

    internal static string ComparisonVersion(PackLoaderKind loader, string minecraft, string version) =>
        loader == PackLoaderKind.Forge && version.EndsWith("-" + minecraft, StringComparison.Ordinal)
            ? version[..^(minecraft.Length + 1)] : version;

    private async Task<string> ReadAsync(string uri, CancellationToken token)
    {
        var task = _responses.GetOrAdd(uri, _ => FetchAsync(uri, token));
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch { _responses.TryRemove(new KeyValuePair<string, Task<string>>(uri, task)); throw; }
    }

    private async Task<string> FetchAsync(string uri, CancellationToken token)
    {
        var bytes = await DownloadAsync(uri, 8 * 1024 * 1024, token).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> DownloadAsync(string uri, long maximum, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_downloadStallTimeout);
        try
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Данные загрузчика слишком большие.");
            deadline.CancelAfter(_downloadStallTimeout);
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            deadline.CancelAfter(_downloadStallTimeout);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > maximum) throw new InvalidDataException("Данные загрузчика слишком большие.");
                output.Write(buffer, 0, count);
                deadline.CancelAfter(_downloadStallTimeout);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new HttpRequestException("Загрузка остановилась: источник не передаёт данные загрузчика.", exception);
        }
    }
}
