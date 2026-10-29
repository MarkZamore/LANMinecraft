using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

public sealed record LocalAiRequirements(string Model, string ModPath, string ModSha256, int ReserveMemoryMb, string? Language,
    string? ModId = null, string? RecipeFile = null, string? RecipeSha256 = null);
internal sealed record LocalAiPin(string Version, Uri ArchiveUri, long ArchiveSizeBytes, string ArchiveSha256, string ModelManifestSha256, byte[] ModelManifest);
internal sealed record LocalAiBlob(string Sha256, long SizeBytes);

/// <summary>Prepares provider files only. Pack-owned native code owns the backend lifecycle.</summary>
public sealed class LocalAiProvisioningService
{
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly LocalAiPin? _testPin;

    public LocalAiProvisioningService(AppPaths paths) : this(paths, PortableHttpClient.Shared) { }
    internal LocalAiProvisioningService(AppPaths paths, HttpClient http)
    {
        _paths = paths;
        _http = http;
    }
    // Synthetic test distributions only. Production callers must read a pack's
    // pinned recipe; no archive/model distribution is compiled into the launcher.
    internal LocalAiProvisioningService(AppPaths paths, HttpClient http, LocalAiPin pin) : this(paths, http)
        => _testPin = LocalAiRecipeReader.ValidatePin(pin).Pin;

    public static LocalAiRequirements? ReadRequirements(string packDirectory)
    {
        var file = LocalAiFiles.Under(packDirectory, "launcher/local-ai.json");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = LocalAiRecipeReader.ParseMetadata(LocalAiRecipeReader.ReadBoundedFile(file, 65536));
            var root = LocalAiRecipeReader.Object(doc.RootElement, "schemaVersion", "provider", "model", "modId", "modPath", "modSha256",
                "reserveMemoryMb", "language", "recipeFile", "recipeSha256");
            string Text(string name, int maximum = 256) => LocalAiRecipeReader.Text(root, name, maximum);
            var model = Text("model"); _ = LocalAiModelReference.Parse(model);
            var path = Text("modPath"); var sha = Text("modSha256", 64).ToLowerInvariant(); var modId = Text("modId", 64);
            var language = root.TryGetProperty("language", out _) ? Text("language", 64) : null;
            var reserve = LocalAiRecipeReader.Integer(root, "reserveMemoryMb");
            if (LocalAiRecipeReader.Integer(root, "schemaVersion") != 1 || Text("provider", 32) != "ollama" ||
                !Regex.IsMatch(modId, @"\A[a-z][a-z0-9_]{1,63}\z") ||
                !path.StartsWith("mods/", StringComparison.Ordinal) || path.Count(c => c == '/') != 1 ||
                !path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || !LocalAiFiles.IsHash(sha) ||
                language is not null && !Regex.IsMatch(language, @"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*\z") ||
                reserve is < 512 or > 131072) throw new InvalidDataException("Unsupported local AI descriptor.");
            LocalAiRecipeReader.ValidateRelativePath(path); _ = LocalAiFiles.Under(packDirectory, path);
            string? recipeFile = null, recipeHash = null;
            var hasFile = root.TryGetProperty("recipeFile", out _); var hasHash = root.TryGetProperty("recipeSha256", out _);
            if (hasFile != hasHash) throw new InvalidDataException("Local AI recipeFile and recipeSha256 must be supplied together.");
            if (hasFile)
            {
                recipeFile = Text("recipeFile"); recipeHash = Text("recipeSha256", 64).ToLowerInvariant();
                LocalAiRecipeReader.ValidateRelativePath(recipeFile);
                if (!recipeFile.StartsWith("launcher/", StringComparison.Ordinal) || !recipeFile.EndsWith(".json", StringComparison.Ordinal) || !LocalAiFiles.IsHash(recipeHash))
                    throw new InvalidDataException("A local AI recipe must be a pinned JSON file under launcher/.");
                _ = LocalAiFiles.Under(packDirectory, recipeFile);
            }
            return new(model, path, sha, (int)reserve, language, modId, recipeFile, recipeHash);
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("Malformed local AI descriptor.", error); }
    }

    public async Task<LocalAiPreparedLaunch?> PrepareAsync(string packDirectory, string instanceDirectory, string javaHome,
        IProgress<RuntimePreparationProgress>? progress, CancellationToken token)
    {
        var requirements = ReadRequirements(packDirectory);
        if (requirements is null) return null;
        _paths.EnsureUnderRoot(packDirectory); _paths.EnsureUnderRoot(instanceDirectory); _paths.EnsureUnderRoot(javaHome);
        LocalAiRecipe recipe;
        if (_testPin is { } injected)
        {
            var validated = LocalAiRecipeReader.ValidatePin(injected);
            recipe = new(validated.Pin, validated.Blobs, LocalAiModelReference.Parse(requirements.Model),
                new Uri("https://registry.ollama.ai/"), "Ollama");
        }
        else recipe = LocalAiRecipeReader.Read(packDirectory, requirements);
        var pin = recipe.Pin; var blobs = recipe.Blobs;
        EnsureStopped(packDirectory);
        await VerifyModAsync(packDirectory, requirements, token).ConfigureAwait(false);
        await VerifyModAsync(instanceDirectory, requirements, token).ConfigureAwait(false);
        var cache = LocalAiFiles.Under(_paths.Launcher, "LocalAI");
        Directory.CreateDirectory(cache);
        await using var preparation = await AcquireLockAsync(cache, token).ConfigureAwait(false);
        var nativeRoot = LocalAiFiles.Under(javaHome, recipe.InstallDirectory);
        LocalAiFiles.CleanupAbandonedStages(nativeRoot);
        // Fail before a multi-gigabyte transfer when even the compressed
        // distribution cannot fit. Per-file/extraction checks follow as well.
        var missingBytes = 0L;
        if (!await LocalAiFiles.MatchesAsync(LocalAiFiles.Under(cache, "downloads/" + pin.ArchiveSha256 + ".zip"),
                pin.ArchiveSizeBytes, pin.ArchiveSha256, token).ConfigureAwait(false)) missingBytes += pin.ArchiveSizeBytes;
        foreach (var blob in blobs)
            if (!await LocalAiFiles.MatchesAsync(LocalAiFiles.Under(cache, "models/blobs/sha256-" + blob.Sha256),
                    blob.SizeBytes, blob.Sha256, token).ConfigureAwait(false)) missingBytes = checked(missingBytes + blob.SizeBytes);
        if (new DriveInfo(Path.GetPathRoot(cache)!).AvailableFreeSpace < missingBytes + 64L * 1024 * 1024)
            throw new IOException("Недостаточно места для локального ИИ и модели.");
        var store = new LocalAiFiles(cache, _http);
        var archive = await store.GetAsync(pin.ArchiveUri, pin.ArchiveSizeBytes, pin.ArchiveSha256,
            "downloads/" + pin.ArchiveSha256 + ".zip", "Локальный ИИ: Ollama", progress, token).ConfigureAwait(false);
        var executable = await LocalAiFiles.ExtractRuntimeAsync(archive, nativeRoot, progress, token).ConfigureAwait(false);
        var models = LocalAiFiles.Under(cache, "models");
        foreach (var blob in blobs)
            await store.GetAsync(recipe.Model.BlobUri(recipe.Registry, blob.Sha256),
                blob.SizeBytes, blob.Sha256, "models/blobs/sha256-" + blob.Sha256, "Локальный ИИ: модель", progress, token).ConfigureAwait(false);
        var modelManifest = LocalAiFiles.Under(models, recipe.Model.ManifestPath(recipe.Registry));
        if (!await LocalAiFiles.MatchesAsync(modelManifest, pin.ModelManifest.Length, pin.ModelManifestSha256, token).ConfigureAwait(false))
            AtomicFile.WriteAllBytes(modelManifest, pin.ModelManifest);
        EnsureStopped(packDirectory);
        if (ReadRequirements(packDirectory) != requirements) throw new InvalidDataException("Local AI descriptor changed during preparation.");
        await VerifyModAsync(instanceDirectory, requirements, token).ConfigureAwait(false);
        if (_testPin is null) _ = LocalAiRecipeReader.Read(packDirectory, requirements);
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)port.LocalEndpoint).Port);
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
        if (requirement.ModId is null || !Regex.IsMatch(requirement.ModId, @"\A[a-z][a-z0-9_]{1,63}\z") || !File.Exists(path))
            throw new InvalidDataException("The declared local AI mod binding is missing or invalid.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)) != requirement.ModSha256)
            throw new InvalidDataException("The local AI mod JAR differs from launcher/local-ai.json.");
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries.Where(entry => entry.FullName == "META-INF/mods.toml").Take(2).ToArray();
        if (entries.Length != 1 || entries[0].Length > 65536) throw new InvalidDataException("Local AI Forge metadata is missing, duplicated or too large.");
        await using var input = entries[0].Open(); using var content = new MemoryStream(); var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (content.Length + count > 65536) throw new InvalidDataException("Local AI Forge metadata exceeds its byte limit.");
            content.Write(buffer, 0, count);
        }
        try
        {
            if (!LocalAiRecipeReader.DeclaresForgeMod(new UTF8Encoding(false, true).GetString(content.ToArray()), requirement.ModId))
                throw new InvalidDataException("The local AI JAR does not declare its descriptor mod ID in [[mods]].");
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or FormatException)
        { throw new InvalidDataException("Malformed local AI Forge metadata.", error); }
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
