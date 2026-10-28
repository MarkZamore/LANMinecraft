using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

public sealed record LocalAiRequirements(string Model, string ModPath, string ModSha256, int ReserveMemoryMb, string? Language);
internal sealed record LocalAiPin(string Version, Uri ArchiveUri, long ArchiveSizeBytes, string ArchiveSha256, string ModelManifestSha256, byte[] ModelManifest);
internal sealed record LocalAiBlob(string Sha256, long SizeBytes);

/// <summary>Prepares files only. Verity, inside Java, owns its native Ollama process.</summary>
public sealed class LocalAiProvisioningService
{
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly LocalAiPin _pin;
    private readonly LocalAiBlob[] _blobs;

    public LocalAiProvisioningService(AppPaths paths) : this(paths, PortableHttpClient.Shared, LocalAiPins.Official) { }
    internal LocalAiProvisioningService(AppPaths paths, HttpClient http, LocalAiPin pin)
    {
        _paths = paths;
        _http = http;
        if (pin.ArchiveUri.Scheme != "https" || pin.ArchiveSizeBytes <= 0 || !LocalAiFiles.IsHash(pin.ArchiveSha256) ||
            pin.Version.Length is < 1 or > 32 || pin.Version.Any(c => !char.IsAsciiDigit(c) && c != '.') ||
            pin.ModelManifest.Length is < 1 or > 65536 || !LocalAiFiles.IsHash(pin.ModelManifestSha256) ||
            Convert.ToHexStringLower(SHA256.HashData(pin.ModelManifest)) != pin.ModelManifestSha256)
            throw new InvalidDataException("Invalid pinned local AI distribution.");
        _pin = pin with { ModelManifest = pin.ModelManifest.ToArray() };
        using var manifest = JsonDocument.Parse(_pin.ModelManifest);
        _blobs = new[] { manifest.RootElement.GetProperty("config") }
            .Concat(manifest.RootElement.GetProperty("layers").EnumerateArray()).Select(item =>
            {
                var digest = item.GetProperty("digest").GetString() ?? "";
                var size = item.GetProperty("size").GetInt64();
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !LocalAiFiles.IsHash(digest[7..]) || size <= 0)
                    throw new InvalidDataException("Invalid local AI model layer.");
                return new LocalAiBlob(digest[7..], size);
            }).ToArray();
        if (_blobs.Length is < 2 or > 32 || _blobs.Select(x => x.Sha256).Distinct().Count() != _blobs.Length)
            throw new InvalidDataException("Invalid or duplicated local AI model layers.");
    }

    public static LocalAiRequirements? ReadRequirements(string packDirectory)
    {
        var file = LocalAiFiles.Under(packDirectory, "launcher/local-ai.json");
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 65536) throw new InvalidDataException("Local AI descriptor is too large.");
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 8 });
        var root = doc.RootElement;
        var allowed = new HashSet<string>(["schemaVersion", "provider", "model", "modId", "modPath", "modSha256", "reserveMemoryMb", "language"], StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !allowed.Contains(p.Name) || !names.Add(p.Name)))
            throw new InvalidDataException("Unsupported or duplicate local AI descriptor fields.");
        string String(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
            v.GetString() is { Length: > 0 and <= 256 } s ? s : throw new InvalidDataException("Invalid local AI field: " + name);
        var model = String("model"); var path = String("modPath"); var sha = String("modSha256").ToLowerInvariant();
        var language = root.TryGetProperty("language", out _) ? String("language") : null;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || version != 1 ||
            String("provider") != "ollama" || model != "qwen3:1.7b" || String("modId") != "verity" ||
            !path.StartsWith("mods/", StringComparison.Ordinal) || path.Count(c => c == '/') != 1 || path.Contains('\\') ||
            !path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || !LocalAiFiles.IsHash(sha) ||
            language is not null and not "ru" ||
            !root.TryGetProperty("reserveMemoryMb", out var memory) || memory.ValueKind != JsonValueKind.Number ||
            !memory.TryGetInt32(out var reserve) || reserve is < 512 or > 131072)
            throw new InvalidDataException("Unsupported local AI descriptor.");
        _ = LocalAiFiles.Under(packDirectory, path);
        return new LocalAiRequirements(model, path, sha, reserve, language);
    }

    public async Task<LocalAiPreparedLaunch?> PrepareAsync(string packDirectory, string instanceDirectory, string javaHome,
        IProgress<RuntimePreparationProgress>? progress, CancellationToken token)
    {
        var requirements = ReadRequirements(packDirectory);
        if (requirements is null) return null;
        _paths.EnsureUnderRoot(packDirectory); _paths.EnsureUnderRoot(instanceDirectory); _paths.EnsureUnderRoot(javaHome);
        EnsureStopped(packDirectory);
        await VerifyModAsync(packDirectory, requirements, token).ConfigureAwait(false);
        await VerifyModAsync(instanceDirectory, requirements, token).ConfigureAwait(false);
        var cache = LocalAiFiles.Under(_paths.Launcher, "LocalAI");
        Directory.CreateDirectory(cache);
        await using var preparation = await AcquireLockAsync(cache, token).ConfigureAwait(false);
        var nativeRoot = LocalAiFiles.Under(javaHome, "AppData/Local/Verity/ollama");
        LocalAiFiles.CleanupAbandonedStages(nativeRoot);
        // Fail before a multi-gigabyte transfer when even the compressed
        // distribution cannot fit. Per-file/extraction checks follow as well.
        var missingBytes = 0L;
        if (!await LocalAiFiles.MatchesAsync(LocalAiFiles.Under(cache, "downloads/" + _pin.ArchiveSha256 + ".zip"),
                _pin.ArchiveSizeBytes, _pin.ArchiveSha256, token).ConfigureAwait(false)) missingBytes += _pin.ArchiveSizeBytes;
        foreach (var blob in _blobs)
            if (!await LocalAiFiles.MatchesAsync(LocalAiFiles.Under(cache, "models/blobs/sha256-" + blob.Sha256),
                    blob.SizeBytes, blob.Sha256, token).ConfigureAwait(false)) missingBytes = checked(missingBytes + blob.SizeBytes);
        if (new DriveInfo(Path.GetPathRoot(cache)!).AvailableFreeSpace < missingBytes + 64L * 1024 * 1024)
            throw new IOException("Недостаточно места для локального ИИ и модели.");
        var store = new LocalAiFiles(cache, _http);
        var archive = await store.GetAsync(_pin.ArchiveUri, _pin.ArchiveSizeBytes, _pin.ArchiveSha256,
            "downloads/" + _pin.ArchiveSha256 + ".zip", "Локальный ИИ: Ollama", progress, token).ConfigureAwait(false);
        var executable = await LocalAiFiles.ExtractRuntimeAsync(archive, nativeRoot, progress, token).ConfigureAwait(false);
        var models = LocalAiFiles.Under(cache, "models");
        foreach (var blob in _blobs)
            await store.GetAsync(new Uri("https://registry.ollama.ai/v2/library/qwen3/blobs/sha256:" + blob.Sha256),
                blob.SizeBytes, blob.Sha256, "models/blobs/sha256-" + blob.Sha256, "Локальный ИИ: модель", progress, token).ConfigureAwait(false);
        var modelManifest = LocalAiFiles.Under(models, "manifests/registry.ollama.ai/library/qwen3/1.7b");
        if (!await LocalAiFiles.MatchesAsync(modelManifest, _pin.ModelManifest.Length, _pin.ModelManifestSha256, token).ConfigureAwait(false))
            AtomicFile.WriteAllBytes(modelManifest, _pin.ModelManifest);
        EnsureStopped(packDirectory);
        if (ReadRequirements(packDirectory) != requirements) throw new InvalidDataException("Local AI descriptor changed during preparation.");
        await VerifyModAsync(instanceDirectory, requirements, token).ConfigureAwait(false);
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)port.LocalEndpoint).Port);
        LocalAiConfiguration.Apply(instanceDirectory, endpoint, requirements.Model);
        progress?.Report(new(RuntimePreparationStage.Ready, "Локальный ИИ: файлы готовы", 1));
        return new LocalAiPreparedLaunch(endpoint, models, executable);
    }

    private void EnsureStopped(string packDirectory)
    {
        var relative = Path.GetRelativePath(_paths.Packs, packDirectory).Replace('\\', '/');
        if (PortablePackSyncService.IsUpdatePending(packDirectory) || new ClientPresenceService(_paths).ReadLiveSessions().Any(
            s => s.PackRelativePath.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Закройте игру и завершите обновление сборки перед подготовкой локального ИИ.");
    }

    private static async Task<FileStream> AcquireLockAsync(string root, CancellationToken token)
    {
        var file = LocalAiFiles.Under(root, "prepare.lock");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(15));
        while (true)
        {
            limit.Token.ThrowIfCancellationRequested();
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(250, limit.Token).ConfigureAwait(false); }
        }
    }

    internal static async Task VerifyModAsync(string directory, LocalAiRequirements requirement, CancellationToken token)
    {
        var path = LocalAiFiles.Under(directory, requirement.ModPath);
        if (!await LocalAiFiles.MatchesAsync(path, null, requirement.ModSha256, token).ConfigureAwait(false))
            throw new InvalidDataException("Verity JAR differs from launcher/local-ai.json.");
        using var zip = ZipFile.OpenRead(path);
        var metadata = zip.GetEntry("META-INF/mods.toml");
        if (metadata is null || metadata.Length > 65536) throw new InvalidDataException("Verity Forge metadata is missing.");
        using var reader = new StreamReader(metadata.Open());
        if (!Regex.IsMatch(await reader.ReadToEndAsync(token).ConfigureAwait(false), @"(?m)^\s*modId\s*=\s*[""']verity[""']\s*(?:#.*)?$"))
            throw new InvalidDataException("The local AI JAR does not declare the Verity mod ID.");
    }
}

/// <summary>Environment bindings only; no process handle or launcher lifetime dependency.</summary>
public sealed record LocalAiPreparedLaunch(Uri Endpoint, string ModelsDirectory, string RuntimeExecutable)
{
    public void ConfigureEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.Where(k => k.StartsWith("OLLAMA_", StringComparison.OrdinalIgnoreCase)).ToArray()) environment.Remove(key);
        environment["OLLAMA_HOST"] = Endpoint.Authority;
        environment["OLLAMA_MODELS"] = ModelsDirectory;
        environment["OLLAMA_NO_CLOUD"] = "true";
        environment["OLLAMA_NUM_PARALLEL"] = "1";
        environment["OLLAMA_MAX_LOADED_MODELS"] = "1";
    }
}
