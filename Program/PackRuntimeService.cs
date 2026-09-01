using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using CmlLib.Core.Version;
using CmlLib.Core.VersionLoader;

namespace Minecraft;

public sealed class PackRuntimeService : IDisposable
{

    /// <summary>
    /// How a child process's console is read. Java writes UTF-8; .NET would
    /// otherwise decode the pipe in the console's own code page.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    // Bumped to 3 when the game moved to the launcher-managed Java 25 runtime:
    // the recorded java path had to be re-resolved for everyone.
    // A cache generation, not a data format: it is bumped to throw the cached
    // work away and redo it, so it is deliberately independent of
    // PortableFormat's version - a release must not cost every player a
    // re-download for an unrelated change.
    internal const int RuntimeCacheGeneration = 4;
    private const string RuntimeStateFileName = ".portable-runtime.json";
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _httpClient;
    private readonly PortableJavaRuntimeService _javaRuntime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<PackLoaderKind, IPackLoaderProvider> _providers;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public PackRuntimeService(
        AppPaths paths,
        Logger logger,
        HttpClient? httpClient = null,
        PortableJavaRuntimeService? javaRuntime = null)
    {
        _paths = paths;
        _logger = logger;
        _httpClient = httpClient ?? PortableHttpClient.Shared;
        _javaRuntime = javaRuntime ?? new PortableJavaRuntimeService(paths, logger, _httpClient);
        IPackLoaderProvider[] providers =
        [
            new VanillaLoaderProvider(),
            new FabricLoaderProvider(),
            new QuiltLoaderProvider(),
            new ForgeLoaderProvider(),
            new NeoForgeLoaderProvider()
        ];
        _providers = providers.ToDictionary(provider => provider.Kind);
    }

    public async Task<PreparedRuntime> PrepareAsync(
        string packRelativePath,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await PrepareCoreAsync(packRelativePath, progress, token).ConfigureAwait(false);
        }
        finally
        {
            CleanupTemporaryFiles(packRelativePath);
            _gate.Release();
        }
    }

    private async Task<PreparedRuntime> PrepareCoreAsync(
        string packRelativePath,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        var packDirectory = _paths.CombineUnderPacks(packRelativePath);
        var descriptor = PackManifestService.Load(packDirectory);
        // A pack may bring the client jar or leave it to be fetched. One that
        // names a jar must have that jar - it was named for a reason, and a
        // missing one is a broken pack rather than a silent download. One that
        // names none is a folder of mods somebody assembled, and Mojang's own
        // client for the version the mods ask for is exactly right for it.
        var sourceClientJar = descriptor.ClientJar.Length == 0
            ? ""
            : PackManifestService.ResolveClientJarPath(packDirectory, descriptor);
        if (sourceClientJar.Length != 0 && !File.Exists(sourceClientJar))
        {
            throw new FileNotFoundException("The client jar declared by portable-pack.json is missing.", sourceClientJar);
        }
        RejectUnexpectedMinecraftJars(packDirectory, sourceClientJar);

        var runtimeRoot = _paths.CombineUnderRuntimes(packRelativePath);
        var temporaryRoot = Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads", SafePackName(packRelativePath));
        _paths.EnsureUnderRoot(temporaryRoot);
        Directory.CreateDirectory(runtimeRoot);

        progress?.Report(new RuntimePreparationProgress(RuntimePreparationStage.Checking, "Проверка"));
        var statePath = Path.Combine(runtimeRoot, RuntimeStateFileName);
        var state = ReadState(statePath);
        // A runtime prepared before the launcher knew to say which jar is the
        // game is mended where it stands: one field in one file, rather than
        // half a gigabyte downloaded again to write it.
        if (state is not null && RepairLoaderProfile(runtimeRoot, descriptor, state))
        {
            AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, _jsonOptions));
        }
        // The Java this pack needs, not the one the launcher pins for itself.
        // Which Java a pack runs on became a property of its Minecraft - 17 for
        // 1.18.2 and 1.20.1, 21 from 1.20.5 - but this comparison was left
        // measuring every runtime against the single old constant. A pack on 17
        // could therefore never match what it had prepared, so every launch of
        // All The Fabric 3 and RPG Ars Nouveau threw away a good runtime and
        // built it again from Mojang's metadata. That was merely slow until the
        // day those hosts were unreachable, and then it was a pack that would
        // not start at all with everything it needed already on the disk.
        var requiredJava = JavaRuntimeCatalog.RequiredFor(descriptor);
        if (state is not null &&
            string.Equals(state.DescriptorHash, descriptor.DescriptorHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(state.JavaRuntimeId, requiredJava.RuntimeId, StringComparison.Ordinal) &&
            ValidateSourceClientJarState(sourceClientJar, state) &&
            ValidateState(runtimeRoot, state))
        {
            CleanupUntrackedRuntimeFiles(runtimeRoot, state);
            var clientJar = ResolveStatePath(Anchor, state.ClientJarRelativePath);
            // Repairs a deleted or damaged JDK without paying for a full re-prepare.
            var cachedJava = await _javaRuntime
                .EnsureAsync(_paths.JavaRuntimes, requiredJava, progress, token)
                .ConfigureAwait(false);
            await EnsureMojangMappingsAsync(descriptor, token).ConfigureAwait(false);
            progress?.Report(new RuntimePreparationProgress(RuntimePreparationStage.Ready, "Готовится к запуску", 1));
            return new PreparedRuntime(runtimeRoot, state.ProfileId, cachedJava.JavaWPath, clientJar, descriptor);
        }

        Directory.CreateDirectory(temporaryRoot);
        // What the button says while each set of files comes down. The base
        // game is Minecraft's own; everything after it belongs to the loader,
        // however many rounds that takes.
        var loaderName = LoaderDisplayName(descriptor.Loader.Type);
        var launcher = CreateLauncher(
            runtimeRoot,
            temporaryRoot,
            progress,
            localOnly: false,
            subject: "Minecraft");
        IVersion baseVersion;
        try
        {
            baseVersion = await RuntimeRetry.RunAsync(
                retryToken => launcher.GetVersionAsync(descriptor.MinecraftVersion, retryToken).AsTask(),
                token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException(
                $"Minecraft version '{descriptor.MinecraftVersion}' could not be resolved from official metadata. " +
                "Connect to the internet or use an already prepared runtime.", ex);
        }

        var clientFile = await ResolveClientFileAsync(launcher, baseVersion, token).ConfigureAwait(false);
        if (sourceClientJar.Length != 0)
        {
            ValidateClientJar(sourceClientJar, clientFile);
            CopyClientJar(sourceClientJar, clientFile.Path!);
        }

        await FromMojangAsync(
            $"Minecraft {descriptor.MinecraftVersion}",
            () => RuntimeRetry.RunAsync(
                retryToken => launcher.InstallAsync(baseVersion, cancellationToken: retryToken).AsTask(),
                token),
            token).ConfigureAwait(false);
        // Mojang's component keeps running the loader installer, which NeoForge
        // 21.1.244 was built against; only the game moves to the pinned runtime.
        var installerJavaPath = launcher.GetJavaPath(baseVersion) ?? launcher.GetDefaultJavaPath();
        if (string.IsNullOrWhiteSpace(installerJavaPath) || !File.Exists(installerJavaPath))
        {
            throw new FileNotFoundException("The Java runtime required by this Minecraft version was not prepared.", installerJavaPath);
        }
        var gameJava = await _javaRuntime
            .EnsureAsync(_paths.JavaRuntimes, JavaRuntimeCatalog.RequiredFor(descriptor), progress, token)
            .ConfigureAwait(false);

        progress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.InstallingLoader,
            loaderName));
        if (!_providers.TryGetValue(descriptor.Loader.Type, out var provider))
        {
            throw new NotSupportedException($"Unsupported loader: {descriptor.Loader.Type}");
        }

        launcher = CreateLauncher(
            runtimeRoot,
            temporaryRoot,
            progress,
            localOnly: false,
            subject: loaderName);

        var context = new PackLoaderInstallationContext(
            descriptor,
            runtimeRoot,
            _paths.SharedRuntime,
            temporaryRoot,
            baseVersion.Id,
            installerJavaPath,
            _httpClient,
            launcher,
            progress,
            _logger);
        var profileId = await provider.InstallAsync(context, token).ConfigureAwait(false);

        launcher = CreateLauncher(
            runtimeRoot,
            temporaryRoot,
            progress,
            localOnly: true,
            subject: loaderName);
        var profile = await RuntimeRetry.RunAsync(
            retryToken => launcher.GetVersionAsync(profileId, retryToken).AsTask(),
            token).ConfigureAwait(false);
        await FromMojangAsync(
            $"the files {loaderName} needs",
            () => RuntimeRetry.RunAsync(
                retryToken => launcher.InstallAsync(profile, cancellationToken: retryToken).AsTask(),
                token),
            token).ConfigureAwait(false);

        progress?.Report(new RuntimePreparationProgress(RuntimePreparationStage.Verifying, "Проверка"));
        WindowIconAssetService.Apply(SharedRuntimeStore.Assets(_paths), profile);
        var mappings = await EnsureMojangMappingsAsync(descriptor, token).ConfigureAwait(false);
        var requiredFiles = await EnumerateRequiredFilesAsync(launcher, profile, clientFile.Path!, token).ConfigureAwait(false);
        if (mappings is not null) requiredFiles = [.. requiredFiles, mappings];
        var newState = CreateState(
            runtimeRoot,
            descriptor,
            profileId,
            gameJava.RuntimeId,
            gameJava.JavaVersion,
            sourceClientJar,
            clientFile.Path!,
            requiredFiles,
            token);
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(newState, _jsonOptions));
        CleanupUntrackedRuntimeFiles(runtimeRoot, newState);
        progress?.Report(new RuntimePreparationProgress(RuntimePreparationStage.Ready, "Готовится к запуску", 1));
        _logger.Info(
            $"Runtime prepared for {packRelativePath}: Minecraft {descriptor.MinecraftVersion}, " +
            $"{LoaderDisplayName(descriptor.Loader.Type)} {descriptor.Loader.Version}, profile {profileId}.");
        return new PreparedRuntime(runtimeRoot, profileId, gameJava.JavaWPath, clientFile.Path!, descriptor);
    }

    private void CleanupTemporaryFiles(string packRelativePath)
    {
        var runtimeDownloads = Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads");
        var temporaryRoot = Path.Combine(runtimeDownloads, SafePackName(packRelativePath));
        TryDeleteDirectory(temporaryRoot);
        TryDeleteDirectoryIfEmpty(runtimeDownloads);
        TryDeleteDirectoryIfEmpty(Path.Combine(_paths.Personal, "Temp"));
    }

    public MinecraftLauncher CreateLocalLauncher(PreparedRuntime runtime)
    {
        var temporaryRoot = Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads", "launch");
        return CreateLauncher(runtime.RuntimeRoot, temporaryRoot, progress: null, localOnly: true, subject: "");
    }

    internal void CleanupLaunchTemporaryFiles()
    {
        var runtimeDownloads = Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads");
        TryDeleteDirectory(Path.Combine(runtimeDownloads, "launch"));
        TryDeleteDirectoryIfEmpty(runtimeDownloads);
        TryDeleteDirectoryIfEmpty(Path.Combine(_paths.Personal, "Temp"));
    }

    private MinecraftLauncher CreateLauncher(
        string runtimeRoot,
        string temporaryRoot,
        IProgress<RuntimePreparationProgress>? progress,
        bool localOnly,
        string subject)
    {
        var minecraftPath = new MinecraftPath(runtimeRoot);
        // Everything a version is made of is the same bytes for every build
        // that asks for that version, so it is fetched once and found by the
        // rest. What stays under the build is what is about the build: its
        // state file, and the natives it unpacks to run.
        minecraftPath.Assets = SharedRuntimeStore.Assets(_paths);
        minecraftPath.Library = SharedRuntimeStore.Libraries(_paths);
        minecraftPath.Versions = SharedRuntimeStore.Versions(_paths);
        minecraftPath.Runtime = SharedRuntimeStore.Runtime(_paths);
        minecraftPath.Resource = SharedRuntimeStore.Resources(_paths);
        minecraftPath.CreateDirs();
        var parameters = MinecraftLauncherParameters.CreateDefault(minecraftPath, _httpClient);
        var extractors = parameters.FileExtractors ?? throw new InvalidOperationException("CmlLib file extractors were not initialized.");
        foreach (var extractor in extractors.OfType<ClientFileExtractor>().ToArray())
        {
            extractors.Remove(extractor);
        }
        parameters.GameInstaller = new PortableGameInstaller(
            _httpClient,
            SharedRuntimeStore.Anchor(_paths),
            temporaryRoot,
            progress,
            subject);
        if (localOnly)
        {
            parameters.VersionLoader = new LocalJsonVersionLoader(minecraftPath);
        }
        else if (parameters.VersionLoader is MojangJsonVersionLoaderV2 mojangLoader)
        {
            mojangLoader.UseLocalManifestWhenError = true;
        }
        return new MinecraftLauncher(parameters);
    }

    private static async Task<GameFile> ResolveClientFileAsync(
        MinecraftLauncher launcher,
        IVersion baseVersion,
        CancellationToken token)
    {
        var extractor = new ClientFileExtractor();
        var files = await extractor.Extract(launcher.MinecraftPath, baseVersion, launcher.RulesContext, token);
        var clientFile = files.SingleOrDefault();
        if (clientFile is null || string.IsNullOrWhiteSpace(clientFile.Path))
        {
            throw new InvalidDataException("Official Minecraft metadata does not contain a client jar artifact.");
        }
        return clientFile;
    }

    private static void ValidateClientJar(string sourcePath, GameFile expected)
    {
        var info = new FileInfo(sourcePath);
        if (expected.Size > 0 && info.Length != expected.Size)
        {
            throw new InvalidDataException(
                $"Client jar size does not match official Minecraft metadata: {info.Length} instead of {expected.Size} bytes.");
        }
        if (!string.IsNullOrWhiteSpace(expected.Hash) &&
            !string.Equals(ComputeSha1(sourcePath), expected.Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Client jar SHA-1 does not match official Minecraft metadata.");
        }

        using var archive = ZipFile.OpenRead(sourcePath);
        if (archive.GetEntry("net/minecraft/client/main/Main.class") is null)
        {
            throw new InvalidDataException("Client jar does not contain the Minecraft client entry point.");
        }
    }

    private static void CopyClientJar(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath) &&
            new FileInfo(destinationPath).Length == new FileInfo(sourcePath).Length &&
            string.Equals(ComputeSha1(destinationPath), ComputeSha1(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var temporaryPath = destinationPath + ".local-client.part";
        File.Copy(sourcePath, temporaryPath, overwrite: true);
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Mojang's own mappings for this Minecraft, where the loader does not
    /// bring them.
    /// </summary>
    /// <remarks>
    /// Forge and NeoForge install this file themselves, beside the client jar,
    /// because their own toolchain needs it. Fabric and Quilt install nothing
    /// of the kind: what they ship is intermediary, which says that a class is
    /// class_3218 and never that it is ServerLevel. Composing the two is the
    /// only way to reach a name the launcher can ask for, so the missing half
    /// is fetched once per runtime and left beside the libraries, exactly where
    /// the other loaders put theirs.
    ///
    /// Everything about it is already on disk except the bytes: the version
    /// manifest the game was installed from names the URL, the size and the
    /// sha1. A failure here is not a failure to launch - it costs the hooks
    /// that need names and nothing else, which is what a runtime without the
    /// file has now.
    /// </remarks>
    /// <returns>The mappings file, or null when there was nothing to fetch.</returns>
    private async Task<string?> EnsureMojangMappingsAsync(
        PackRuntimeDescriptor descriptor,
        CancellationToken token)
    {
        try
        {
            var clientLibraries = Path.Combine(
                SharedRuntimeStore.Libraries(_paths), "net", "minecraft", "client");
            if (Directory.Exists(clientLibraries) &&
                Directory.EnumerateFiles(clientLibraries, "*mappings*.txt", SearchOption.AllDirectories).Any())
            {
                return null;
            }

            var versionJson = Path.Combine(
                SharedRuntimeStore.Versions(_paths),
                descriptor.MinecraftVersion,
                descriptor.MinecraftVersion + ".json");
            if (!File.Exists(versionJson)) return null;
            var mappings = JsonNode.Parse(await File.ReadAllTextAsync(versionJson, token).ConfigureAwait(false))
                ?["downloads"]?["client_mappings"];
            var url = mappings?["url"]?.GetValue<string>();
            var sha1 = mappings?["sha1"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sha1)) return null;

            var destination = Path.Combine(
                clientLibraries,
                descriptor.MinecraftVersion,
                $"client-{descriptor.MinecraftVersion}-mappings.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var payload = await _httpClient.GetByteArrayAsync(url, token).ConfigureAwait(false);
            var actual = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
            if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn(
                    $"Mojang's mappings for {descriptor.MinecraftVersion} did not match the manifest's sha1; " +
                    "the hooks that need names are left out rather than built on the wrong file.");
                return null;
            }
            await File.WriteAllBytesAsync(destination, payload, token).ConfigureAwait(false);
            _logger.Info(
                $"Mojang's mappings for {descriptor.MinecraftVersion} were fetched for a runtime that ships none " +
                $"({payload.Length / 1024 / 1024} MB).");
            return destination;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                       or UnauthorizedAccessException or TaskCanceledException)
        {
            _logger.Warn($"Mojang's mappings could not be fetched: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Runs a step that fetches from Mojang, and says something a player can do
    /// something about when those hosts cannot be reached.
    ///
    /// What a refused connection carries is Windows' own sentence: it arrives
    /// in the system language, names a host and a port, and tells nobody what
    /// to do about it. It was reaching players unchanged, in a system dialog
    /// this launcher is not supposed to show at all.
    ///
    /// A cancelled launch is not a network failure and must keep its own
    /// meaning, so the token is asked before the blame is placed.
    /// </summary>
    private static async Task FromMojangAsync(string what, Func<Task> step, CancellationToken token)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is HttpRequestException ||
                (ex is TaskCanceledException && !token.IsCancellationRequested))
        {
            throw new InvalidOperationException(
                $"{what} could not be downloaded: Mojang's servers did not answer. Check the connection, " +
                "or start a pack that is already prepared.", ex);
        }
    }

    private static async Task<IReadOnlyCollection<string>> EnumerateRequiredFilesAsync(
        MinecraftLauncher launcher,
        IVersion profile,
        string clientJarPath,
        CancellationToken token)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { clientJarPath };
        foreach (var version in profile.EnumerateToParent())
        {
            foreach (var file in await launcher.ExtractFiles(version, token))
            {
                if (!string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)) files.Add(Path.GetFullPath(file.Path));
            }
            var versionJson = launcher.MinecraftPath.GetVersionJsonPath(version.Id);
            if (File.Exists(versionJson)) files.Add(versionJson);
        }
        var localManifest = Path.Combine(launcher.MinecraftPath.Versions, "version_manifest_v2.json");
        if (File.Exists(localManifest)) files.Add(localManifest);
        return files;
    }

    private RuntimeState CreateState(
        string runtimeRoot,
        PackRuntimeDescriptor descriptor,
        string profileId,
        string javaRuntimeId,
        string javaVersion,
        string sourceClientJarPath,
        string clientJarPath,
        IReadOnlyCollection<string> requiredFiles,
        CancellationToken token)
    {
        var state = new RuntimeState
        {
            SchemaVersion = RuntimeCacheGeneration,
            DescriptorHash = descriptor.DescriptorHash,
            ProfileId = profileId,
            // Empty on purpose: Java lives in the shared store now, which is
            // outside this root and therefore has no path relative to it. The
            // field stays because the cleanup of two long-dead layouts still
            // reads it, and an empty string matches neither of them.
            JavaPathRelativePath = "",
            JavaRuntimeId = javaRuntimeId,
            JavaVersion = javaVersion,
            ClientJarRelativePath = ToRelativePath(Anchor, clientJarPath),
            SourceClientJarSizeBytes = sourceClientJarPath.Length == 0 ? 0 : new FileInfo(sourceClientJarPath).Length,
            SourceClientJarLastWriteUtcTicks =
                sourceClientJarPath.Length == 0 ? 0 : File.GetLastWriteTimeUtc(sourceClientJarPath).Ticks,
            SourceClientJarSha1 = sourceClientJarPath.Length == 0 ? "" : ComputeSha1(sourceClientJarPath),
            PreparedAtUtc = DateTimeOffset.UtcNow
        };
        foreach (var path in requiredFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            state.Files[ToRelativePath(Anchor, path)] = new RuntimeFileState
            {
                SizeBytes = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = ComputeSha256(path)
            };
        }
        return state;
    }

    private bool ValidateState(string runtimeRoot, RuntimeState state)
    {
        if (state.SchemaVersion != RuntimeCacheGeneration ||
            string.IsNullOrWhiteSpace(state.ProfileId) ||
            state.Files.Count == 0)
        {
            return false;
        }

        foreach (var (relativePath, expected) in state.Files)
        {
            string path;
            try
            {
                path = ResolveStatePath(Anchor, relativePath);
            }
            catch
            {
                return false;
            }
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expected.SizeBytes) return false;
            if (info.LastWriteTimeUtc.Ticks != expected.LastWriteUtcTicks &&
                !string.Equals(ComputeSha256(path), expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        try
        {
            // The game JDK is deliberately not checked here: EnsureAsync repairs
            // a deleted or damaged install from the cached archive, which is far
            // cheaper than the full re-prepare a failed validation causes.
            return File.Exists(ResolveStatePath(Anchor, state.ClientJarRelativePath));
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateSourceClientJarState(string sourceClientJarPath, RuntimeState state)
    {
        // A pack that ships no jar has nothing here to have changed.
        if (sourceClientJarPath.Length == 0) return state.SourceClientJarSizeBytes == 0;
        var info = new FileInfo(sourceClientJarPath);
        if (!info.Exists || info.Length != state.SourceClientJarSizeBytes) return false;
        return info.LastWriteTimeUtc.Ticks == state.SourceClientJarLastWriteUtcTicks ||
               string.Equals(ComputeSha1(sourceClientJarPath), state.SourceClientJarSha1, StringComparison.OrdinalIgnoreCase);
    }

    private RuntimeState? ReadState(string statePath)
    {
        try
        {
            if (!File.Exists(statePath)) return null;
            return JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(statePath), _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes what preparation leaves behind and does not track: the copies of
    /// the game this build used to keep for itself, and the loader installer
    /// jar that was downloaded to be run once.
    /// </summary>
    private void CleanupUntrackedRuntimeFiles(string runtimeRoot, RuntimeState state)
    {
        // What the build used to keep for itself and now reads out of the
        // shared store. A build that has just been prepared against the store
        // has no use for its own copy, and the copy is most of a gigabyte.
        foreach (var directoryName in new[]
                 {
                     "assets", "libraries", "versions", "runtime", "resources", "java21-windows-x86-64"
                 })
        {
            TryDeleteDirectory(Path.Combine(runtimeRoot, directoryName));
        }

        // The installer jar the loader leaves behind, which is downloaded to
        // run once and never read again. It lives in the shared libraries with
        // everything else, so an installer another build still lists is left
        // alone - hence the check against this build's files is not enough on
        // its own, and the store's own sweep is what finally takes it.
        var neoForgeRoot = Path.Combine(
            SharedRuntimeStore.Libraries(_paths), "net", "neoforged", "neoforge");
        if (!Directory.Exists(neoForgeRoot)) return;
        foreach (var installer in Directory.EnumerateFiles(
                     neoForgeRoot,
                     "neoforge-*-installer.jar",
                     SearchOption.AllDirectories))
        {
            var relative = ToRelativePath(Anchor, installer);
            if (!state.Files.ContainsKey(relative)) TryDeleteFile(installer);
        }
    }

    private static void RejectUnexpectedMinecraftJars(string packDirectory, string selectedClientJar)
    {
        var selected = selectedClientJar.Length == 0 ? "" : Path.GetFullPath(selectedClientJar);
        var unexpected = Directory.EnumerateFiles(packDirectory, "*.jar", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(packDirectory, path).Replace('\\', '/');
                var name = Path.GetFileName(path);
                return !string.Equals(Path.GetFullPath(path), selected, StringComparison.OrdinalIgnoreCase) &&
                       (name.Equals("server.jar", StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith("libraries/com/mojang/minecraft/", StringComparison.OrdinalIgnoreCase) ||
                        !relative.Contains('/') &&
                        name.StartsWith("minecraft-", StringComparison.OrdinalIgnoreCase) &&
                        (name.Contains("-client", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("-server", StringComparison.OrdinalIgnoreCase)));
            })
            .Take(10)
            .ToArray();
        if (unexpected.Length > 0)
        {
            throw new InvalidDataException(
                "Pack contains unexpected Minecraft client/server jar files:" + Environment.NewLine +
                string.Join(Environment.NewLine, unexpected));
        }
    }

    private static string LoaderDisplayName(PackLoaderKind kind) => kind switch
    {
        PackLoaderKind.NeoForge => "NeoForge",
        PackLoaderKind.Forge => "Forge",
        PackLoaderKind.Fabric => "Fabric",
        PackLoaderKind.Quilt => "Quilt",
        _ => "Minecraft"
    };

    private static string SafePackName(string packRelativePath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(packRelativePath.Select(ch => invalid.Contains(ch) || ch is '\\' or '/' ? '_' : ch));
    }

    /// <summary>
    /// Writes the missing <c>jar</c> field into a Fabric or Quilt profile that
    /// was prepared without it, and brings the runtime's own record of that
    /// file back in step. Returns true when something was written.
    /// </summary>
    private bool RepairLoaderProfile(string runtimeRoot, PackRuntimeDescriptor descriptor, RuntimeState state)
    {
        if (descriptor.Loader.Type is not (PackLoaderKind.Fabric or PackLoaderKind.Quilt)) return false;
        if (string.IsNullOrWhiteSpace(state.ProfileId)) return false;

        var path = Path.Combine(
            SharedRuntimeStore.Versions(_paths), state.ProfileId, state.ProfileId + ".json");
        if (!KnotGameJar.NameIt(path, descriptor.MinecraftVersion, _logger)) return false;

        var relative = ToRelativePath(Anchor, path);
        var info = new FileInfo(path);
        state.Files[relative] = new RuntimeFileState
        {
            SizeBytes = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            Sha256 = ComputeSha256(path)
        };
        return true;
    }

    /// <summary>
    /// What a path in a runtime state is relative to. It is the launcher
    /// folder rather than the build's own, because a state now names files in
    /// two places: the shared store the game itself lives in, and the build's
    /// folder beside it.
    /// </summary>
    private string Anchor => SharedRuntimeStore.Anchor(_paths);

    private static string ToRelativePath(string runtimeRoot, string path)
    {
        var relative = Path.GetRelativePath(runtimeRoot, Path.GetFullPath(path));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"Runtime state path escapes runtime root: {path}");
        }
        return relative.Replace('\\', '/');
    }

    private static string ResolveStatePath(string runtimeRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Runtime state contains an invalid path.");
        }
        var root = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Runtime state path escapes runtime root.");
        }
        return full;
    }

    [SuppressMessage("Security", "CA5350", Justification = "SHA-1 is required to verify Mojang artifact identities from official metadata.")]
    private static string ComputeSha1(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); } catch { }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private sealed class RuntimeState
    {
        public int SchemaVersion { get; set; } = RuntimeCacheGeneration;
        public string DescriptorHash { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public string JavaPathRelativePath { get; set; } = "";

        /// <summary>
        /// What the runtime this pack was prepared against calls itself.
        /// Written down rather than read back off disk: a support report
        /// used to find it by walking up from the java path, which only
        /// worked while the JDK lived inside the pack's own folder.
        /// </summary>
        public string JavaVersion { get; set; } = "";
        public string JavaRuntimeId { get; set; } = "";
        public string ClientJarRelativePath { get; set; } = "";
        public long SourceClientJarSizeBytes { get; set; }
        public long SourceClientJarLastWriteUtcTicks { get; set; }
        public string SourceClientJarSha1 { get; set; } = "";
        public DateTimeOffset PreparedAtUtc { get; set; }
        public Dictionary<string, RuntimeFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RuntimeFileState
    {
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}

internal sealed class VanillaLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.Vanilla;
    public Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token) =>
        Task.FromResult(context.BaseVersionId);
}

internal sealed class FabricLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.Fabric;
    public async Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var installer = new FabricInstaller(context.HttpClient);
        var profileId = await RuntimeRetry.RunAsync(
            retryToken => installer.Install(
                context.Descriptor.MinecraftVersion,
                context.Descriptor.Loader.Version!,
                context.Launcher.MinecraftPath).WaitAsync(retryToken),
            token);
        KnotGameJar.NameIt(
            Path.Combine(context.Launcher.MinecraftPath.Versions, profileId, profileId + ".json"),
            context.Descriptor.MinecraftVersion,
            context.Logger);
        return profileId;
    }
}

internal sealed class QuiltLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.Quilt;
    public async Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var installer = new QuiltInstaller(context.HttpClient);
        var profileId = await RuntimeRetry.RunAsync(
            retryToken => installer.Install(
                context.Descriptor.MinecraftVersion,
                context.Descriptor.Loader.Version!,
                context.Launcher.MinecraftPath).WaitAsync(retryToken),
            token);
        KnotGameJar.NameIt(
            Path.Combine(context.Launcher.MinecraftPath.Versions, profileId, profileId + ".json"),
            context.Descriptor.MinecraftVersion,
            context.Logger);
        return profileId;
    }
}

/// <summary>
/// Tells the profile which jar is the game, which Fabric and Quilt do not.
/// </summary>
/// <remarks>
/// A loader profile has no jar of its own: it inherits the base version and
/// runs out of the jar that version brought. The launcher format has a field
/// for saying so - <c>jar</c> - and Forge and NeoForge write it while the
/// Fabric and Quilt installers leave it out, because their own launcher works
/// it out from <c>inheritsFrom</c> instead.
///
/// CmlLib does not. With no <c>jar</c> it takes the profile's own id, puts
/// <c>versions/fabric-loader-0.14.10-1.18.2/fabric-loader-0.14.10-1.18.2.jar</c>
/// on the class path, and that file has never existed. Forge and NeoForge
/// survive the same omission because BootstrapLauncher never wanted the game
/// on the class path anyway; Fabric's Knot finds the game by looking there and
/// nowhere else, so it found nothing:
///
///   Minecraft game provider couldn't locate the game!
///
/// One field, written where the installer did not write it. Nothing else in
/// the profile is touched, and a profile that already names a jar is left
/// exactly as it is.
/// </remarks>
internal static class KnotGameJar
{
    /// <summary>
    /// Writes <c>jar</c> into the profile at <paramref name="profilePath"/>
    /// unless it already names one. True when the file was changed.
    /// </summary>
    public static bool NameIt(string profilePath, string minecraftVersion, Logger logger)
    {
        try
        {
            if (!File.Exists(profilePath)) return false;
            if (JsonNode.Parse(File.ReadAllText(profilePath)) is not JsonObject profile) return false;
            if (profile.TryGetPropertyValue("jar", out var existing) &&
                !string.IsNullOrWhiteSpace(existing?.GetValue<string>()))
            {
                return false;
            }

            profile["jar"] = minecraftVersion;
            AtomicFile.WriteAllText(profilePath, profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            logger.Info(
                $"The {Path.GetFileNameWithoutExtension(profilePath)} profile now names Minecraft " +
                $"{minecraftVersion} as its jar, so the loader can find the game on the class path.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            logger.Warn($"The loader profile could not be told which jar is the game: {ex.Message}");
            return false;
        }
    }
}

internal sealed class ForgeLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.Forge;
    public Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token)
    {
        var minecraftVersion = context.Descriptor.MinecraftVersion;
        var loaderVersion = context.Descriptor.Loader.Version!;
        var artifactVersion = loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? loaderVersion
            : minecraftVersion + "-" + loaderVersion;
        var shortLoaderVersion = loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? loaderVersion[(minecraftVersion.Length + 1)..]
            : loaderVersion;
        var escapedVersion = Uri.EscapeDataString(artifactVersion);
        var installerName = $"forge-{artifactVersion}-installer.jar";
        var uri = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{escapedVersion}/{Uri.EscapeDataString(installerName)}";
        var expectedProfile = minecraftVersion + "-forge-" + shortLoaderVersion;
        return OfficialLoaderInstaller.InstallAsync(
            context,
            Path.Combine("installers", "forge", artifactVersion, installerName),
            uri,
            "--installClient",
            [expectedProfile],
            (id, json) => id.Contains("forge", StringComparison.OrdinalIgnoreCase) &&
                          !id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                          json.Contains(minecraftVersion, StringComparison.OrdinalIgnoreCase) &&
                          json.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase),
            token);
    }
}

internal sealed class NeoForgeLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.NeoForge;

    public async Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token)
    {
        var version = context.Descriptor.Loader.Version!;
        var profileId = "neoforge-" + version;
        var installerName = $"neoforge-{version}-installer.jar";
        var uri = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(installerName)}";
        return await OfficialLoaderInstaller.InstallAsync(
            context,
            Path.Combine("installers", "neoforge", version, installerName),
            uri,
            "--install-client",
            [profileId],
            (id, json) => id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                          json.Contains(context.Descriptor.MinecraftVersion, StringComparison.OrdinalIgnoreCase) &&
                          json.Contains(version, StringComparison.OrdinalIgnoreCase),
            token);
    }
}

internal static class OfficialLoaderInstaller
{
    /// <summary>
    /// How the installer's console is read. It is Java, so UTF-8, whatever the
    /// code page of the console this launcher happens to have.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task<string> InstallAsync(
        PackLoaderInstallationContext context,
        string installerRelativePath,
        string installerUri,
        string installArgument,
        IReadOnlyCollection<string> expectedProfileIds,
        Func<string, string, bool> profileMatcher,
        CancellationToken token)
    {
        var installerPath = Path.Combine(context.GameRoot, installerRelativePath);
        var sha1 = await DownloadChecksumAsync(context.HttpClient, installerUri + ".sha1", token);
        var gameFile = new GameFile(Path.GetFileName(installerPath))
        {
            Path = installerPath,
            Url = installerUri,
            Hash = sha1
        };
        await context.Launcher.GameInstaller.Install([gameFile], null, null, token);
        await RuntimeRetry.RunAsync(
            retryToken => RunInstallerAsync(context, installerPath, installArgument, retryToken),
            token);

        return FindProfile(context.GameRoot, expectedProfileIds, profileMatcher)
            ?? throw new FileNotFoundException(
                $"Loader installer completed but did not create a launch profile for {context.Descriptor.Loader.Version}.");
    }

    private static async Task<string> DownloadChecksumAsync(HttpClient httpClient, string uri, CancellationToken token)
    {
        var value = await RuntimeRetry.RunAsync(async retryToken =>
        {
            using var response = await httpClient.GetAsync(uri, retryToken);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(retryToken)).Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }, token);
        if (value.Length != 40 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("NeoForge Maven SHA-1 checksum is invalid.");
        }
        return value.ToLowerInvariant();
    }

    private static async Task RunInstallerAsync(
        PackLoaderInstallationContext context,
        string installerPath,
        string installArgument,
        CancellationToken token)
    {
        Directory.CreateDirectory(context.TemporaryRoot);
        var javaTemp = Path.Combine(context.TemporaryRoot, "Java");
        Directory.CreateDirectory(javaTemp);
        var profilesPath = Path.Combine(context.GameRoot, "launcher_profiles.json");
        var profilesBackup = File.Exists(profilesPath) ? File.ReadAllBytes(profilesPath) : null;
        AtomicFile.WriteAllText(
            profilesPath,
            $"{{\"profiles\":{{\"portable\":{{\"name\":\"Portable\",\"type\":\"custom\",\"lastVersionId\":\"{EscapeJson(context.BaseVersionId)}\"}}}},\"settings\":{{}},\"version\":3}}");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = context.JavaPath,
                WorkingDirectory = context.TemporaryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Java, so UTF-8 whatever the console's code page is.
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom
            };
            startInfo.ArgumentList.Add($"-Djava.io.tmpdir={javaTemp}");
            startInfo.ArgumentList.Add("-jar");
            startInfo.ArgumentList.Add(installerPath);
            startInfo.ArgumentList.Add(installArgument);
            startInfo.ArgumentList.Add(context.GameRoot);
            startInfo.Environment["TEMP"] = javaTemp;
            startInfo.Environment["TMP"] = javaTemp;

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Loader installer process could not be started.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await Task.WhenAll(stdoutTask, stderrTask);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (details.Length > 3000) details = details[^3000..];
                throw new InvalidOperationException(
                    $"Loader installer failed with exit code {process.ExitCode}." + Environment.NewLine + details.Trim());
            }
        }
        finally
        {
            if (profilesBackup is null)
            {
                try { if (File.Exists(profilesPath)) File.Delete(profilesPath); } catch { }
            }
            else
            {
                File.WriteAllBytes(profilesPath, profilesBackup);
            }
        }
    }

    private static string? FindProfile(
        string gameRoot,
        IReadOnlyCollection<string> expectedProfileIds,
        Func<string, string, bool> matcher)
    {
        var versionsRoot = Path.Combine(gameRoot, "versions");
        if (!Directory.Exists(versionsRoot)) return null;
        foreach (var id in expectedProfileIds)
        {
            if (File.Exists(Path.Combine(versionsRoot, id, id + ".json"))) return id;
        }
        foreach (var file in Directory.EnumerateFiles(versionsRoot, "*.json", SearchOption.AllDirectories))
        {
            var json = File.ReadAllText(file);
            var id = Path.GetFileNameWithoutExtension(file);
            if (matcher(id, json)) return id;
        }
        return null;
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

internal static class RuntimeRetry
{
    private const int MaximumAttempts = 3;

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken token)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await action(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaximumAttempts)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), token).ConfigureAwait(false);
            }
        }

        throw lastError ?? new InvalidOperationException("Runtime operation failed without an exception.");
    }

    public static Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token) =>
        RunAsync(async retryToken =>
        {
            await action(retryToken).ConfigureAwait(false);
            return true;
        }, token);
}

internal static class WindowIconAssetService
{
    private const string ResourceName = "Minecraft.WindowIconAssets.jar";

    [SuppressMessage("Security", "CA5350", Justification = "Minecraft asset object names use SHA-1 by protocol.")]
    public static void Apply(string assetsRoot, IVersion profile)
    {
        var assetId = profile.GetInheritedProperty(version => version.AssetIndex?.Id);
        if (string.IsNullOrWhiteSpace(assetId)) return;
        var indexPath = Path.Combine(assetsRoot, "indexes", assetId + ".json");
        if (!File.Exists(indexPath)) return;

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (resource is null) return;
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.RootElement.GetRawText())!;
        if (!root.TryGetValue("objects", out var objectsElement)) return;
        var objects = JsonSerializer.Deserialize<Dictionary<string, AssetEntry>>(objectsElement.GetRawText())
            ?? new Dictionary<string, AssetEntry>(StringComparer.Ordinal);
        var changed = false;
        foreach (var iconName in new[] { "icon_16x16.png", "icon_32x32.png", "icon_48x48.png", "icon_128x128.png", "icon_256x256.png" })
        {
            var entry = archive.GetEntry("icons/" + iconName) ?? archive.GetEntry("assets/minecraft/icons/" + iconName);
            if (entry is null) continue;
            using var memory = new MemoryStream();
            using (var input = entry.Open()) input.CopyTo(memory);
            var bytes = memory.ToArray();
            var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            var objectPath = Path.Combine(assetsRoot, "objects", hash[..2], hash);
            Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
            if (!File.Exists(objectPath)) File.WriteAllBytes(objectPath, bytes);
            foreach (var key in new[] { "icons/" + iconName, "icons/snapshot/" + iconName })
            {
                if (!objects.ContainsKey(key)) continue;
                objects[key] = new AssetEntry { Hash = hash, Size = bytes.Length };
                changed = true;
            }
        }
        if (!changed) return;
        root["objects"] = JsonSerializer.SerializeToElement(objects);
        AtomicFile.WriteAllText(indexPath, JsonSerializer.Serialize(root));
    }

    private sealed class AssetEntry
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
