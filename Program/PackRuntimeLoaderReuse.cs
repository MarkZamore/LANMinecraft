using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Version;

namespace Minecraft;

public sealed partial class PackRuntimeService
{
    private const string LoaderReceiptName = ".portable-loader.json";
    private const int LoaderReceiptGeneration = 1;

    internal static string? LoaderProfileId(PackRuntimeDescriptor descriptor)
    {
        var minecraft = descriptor.MinecraftVersion;
        var loader = descriptor.Loader.Version;
        return descriptor.Loader.Type switch
        {
            PackLoaderKind.Forge => $"{minecraft}-forge-{CanonicalLoaderVersion(descriptor)}",
            PackLoaderKind.NeoForge => $"neoforge-{loader}",
            PackLoaderKind.Fabric => $"fabric-loader-{loader}-{minecraft}",
            PackLoaderKind.LegacyFabric => $"legacyfabric-loader-{loader}-{minecraft}",
            PackLoaderKind.Quilt => $"quilt-loader-{loader}-{minecraft}",
            _ => null
        };
    }

    internal static string? CanonicalLoaderVersion(PackRuntimeDescriptor descriptor)
    {
        var version = descriptor.Loader.Version;
        return descriptor.Loader.Type == PackLoaderKind.Forge && version is not null &&
            version.StartsWith(descriptor.MinecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? version[(descriptor.MinecraftVersion.Length + 1)..] : version;
    }

    private static LoaderIdentity LoaderKey(PackRuntimeDescriptor descriptor) =>
        new(descriptor.MinecraftVersion, descriptor.Loader.Type, CanonicalLoaderVersion(descriptor));

    private static string LibrarySourceKey(IReadOnlyDictionary<string, RuntimeLibrarySource>? sources)
    {
        if (sources is not { Count: > 0 }) return "";
        var identity = string.Join('\n', sources.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key.ToLowerInvariant()} {pair.Value.Sha256.ToLowerInvariant()} {pair.Value.Url.AbsoluteUri}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private IEnumerable<string> PossibleLoaderProfiles(PackRuntimeDescriptor descriptor)
    {
        if (LoaderProfileId(descriptor) is not { } name) yield break;
        yield return name;
        // Old Forge installers also spell the game version inside their profile ID.
        if (descriptor.Loader.Type == PackLoaderKind.Forge)
            yield return $"{descriptor.MinecraftVersion}-forge{descriptor.MinecraftVersion}-{CanonicalLoaderVersion(descriptor)}";
    }

    private async Task<ReusedLoader?> TryReuseLoaderAsync(
        PackRuntimeDescriptor descriptor,
        IReadOnlyDictionary<string, RuntimeLibrarySource>? librarySources,
        string sourceClientJar,
        string runtimeRoot,
        string temporaryRoot,
        CancellationToken token)
    {
        var sourceKey = LibrarySourceKey(librarySources);
        foreach (var id in PossibleLoaderProfiles(descriptor))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var receiptPath = LoaderReceiptPath(id);
                if (IsOrdinarySharedFile(receiptPath) && new FileInfo(receiptPath).Length <= 16 * 1024 * 1024)
                {
                    using var file = File.OpenRead(receiptPath);
                    var receipt = JsonSerializer.Deserialize<LoaderReceipt>(file, _jsonOptions);
                    if (receipt?.SchemaVersion == LoaderReceiptGeneration &&
                        receipt.Identity == LoaderKey(descriptor) && receipt.ProfileId == id &&
                        receipt.LibrarySourceKey == sourceKey && receipt.LoaderFiles is not null && receipt.Files is not null)
                    {
                        var loaderFiles = receipt.LoaderFiles.Select(relative => ResolveStatePath(Anchor, relative)).ToArray();
                        var reused = await VerifyLoaderAsync(descriptor, id, sourceClientJar, runtimeRoot, temporaryRoot,
                            receipt.Files, loaderFiles, null, token).ConfigureAwait(false);
                        if (reused is not null) return reused;
                    }
                }
            }
            catch (Exception ex) when (IsLoaderCacheError(ex))
            {
                token.ThrowIfCancellationRequested();
                _logger.Warn($"Shared loader receipt for {id} could not be used: {ex.Message}");
            }
        }

        // Old state has no record of alternative library sources. Only adopt a state for
        // the standard distribution and whose current descriptor still matches the state.
        if (sourceKey.Length != 0) return null;
        foreach (var directory in Directory.EnumerateDirectories(_paths.Runtimes))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                var donorPath = _paths.CombineUnderPacks(Path.GetFileName(directory));
                if (!PackManifestService.HasManifest(donorPath)) continue;
                var donor = PackManifestService.Load(donorPath);
                if (donor.NativeRuntime is not null || LoaderKey(donor) != LoaderKey(descriptor) ||
                    RuntimeLibrarySource.Load(donorPath) is { Count: > 0 }) continue;
                var statePath = Path.Combine(directory, RuntimeStateFileName);
                if (!File.Exists(statePath) || (File.GetAttributes(statePath) & FileAttributes.ReparsePoint) != 0) continue;
                var state = ReadState(statePath);
                if (state?.SchemaVersion != RuntimeCacheGeneration || state.Files is not { Count: > 0 } ||
                    donor.DescriptorHash != state.DescriptorHash ||
                    !PossibleLoaderProfiles(descriptor).Contains(state.ProfileId, StringComparer.Ordinal)) continue;

                var requiredLoaderFiles = await ReadLegacyLoaderFilesAsync(descriptor, state.ProfileId, token)
                    .ConfigureAwait(false);
                var reused = await VerifyLoaderAsync(descriptor, state.ProfileId, sourceClientJar, runtimeRoot,
                    temporaryRoot, state.Files, requiredLoaderFiles.Select(file => file.Path).ToArray(),
                    requiredLoaderFiles, token).ConfigureAwait(false);
                if (reused is not null) return reused;
            }
            catch (Exception ex) when (IsLoaderCacheError(ex) ||
                                       ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                _logger.Warn($"Existing runtime for {Path.GetFileName(directory)} could not prove shared loader integrity: {ex.Message}");
            }
        }

        return null;
    }

    private async Task<ReusedLoader?> VerifyLoaderAsync(
        PackRuntimeDescriptor descriptor,
        string id,
        string sourceClientJar,
        string runtimeRoot,
        string temporaryRoot,
        IReadOnlyDictionary<string, RuntimeFileState> recorded,
        IReadOnlyCollection<string> loaderFiles,
        IReadOnlyList<LoaderInstallationFile>? fromInstaller,
        CancellationToken token)
    {
        if (UsesOfficialInstaller(descriptor) && loaderFiles.Count == 0) return null;

        var checkedFiles = new Dictionary<string, RuntimeFileState>(StringComparer.OrdinalIgnoreCase);
        var versions = SharedRuntimeStore.Versions(_paths);
        var minecraft = descriptor.MinecraftVersion;
        var baseJson = Path.Combine(versions, minecraft, minecraft + ".json");
        var profileJson = Path.Combine(versions, id, id + ".json");
        // Check the data that drives local profile resolution before handing it to CmlLib.
        if (!VerifyFile(baseJson) || !VerifyFile(profileJson)) return null;
        using (var profileDocument = JsonDocument.Parse(File.ReadAllBytes(profileJson)))
        {
            var json = profileDocument.RootElement;
            if (json.GetProperty("id").GetString() != id ||
                json.GetProperty("inheritsFrom").GetString() != minecraft) return null;
        }

        var launcher = CreateLauncher(runtimeRoot, temporaryRoot, progress: null, localOnly: true,
            subject: "Проверка загрузчика", includeInstallerJava: false);
        var baseVersion = await launcher.GetVersionAsync(minecraft, token).ConfigureAwait(false);
        var client = await ResolveClientFileAsync(launcher, baseVersion, token).ConfigureAwait(false);
        if (!VerifyFile(client.Path!) || !IsOfficialClientArtifact(client) || new FileInfo(client.Path!).Length != client.Size ||
            !string.Equals(ComputeSha1(client.Path!), client.Hash, StringComparison.OrdinalIgnoreCase)) return null;
        if (sourceClientJar.Length != 0) ValidateClientJar(sourceClientJar, client);

        var profile = await launcher.GetVersionAsync(id, token).ConfigureAwait(false);
        // The asset extractor may read the index to calculate the complete list. Check
        // it before extracting so a missing index returns a cache miss without HTTP.
        var assetId = profile.GetInheritedProperty(version => version.AssetIndex?.Id);
        if (!string.IsNullOrWhiteSpace(assetId) &&
            !VerifyFile(Path.Combine(launcher.MinecraftPath.Assets, "indexes", assetId + ".json"))) return null;
        var files = await EnumerateRequiredFilesAsync(launcher, profile, client.Path!, token, verifyShared: true)
            .ConfigureAwait(false);
        foreach (var file in files.Concat(loaderFiles).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!VerifyFile(file)) return null;

        return new ReusedLoader(id, client.Path!, checkedFiles.Keys.Select(path => ResolveStatePath(Anchor, path)).ToArray(),
            checkedFiles, loaderFiles);

        bool VerifyFile(string path)
        {
            token.ThrowIfCancellationRequested();
            if (!IsOrdinarySharedFile(path)) return false;
            var relative = ToRelativePath(Anchor, path);
            if (!IsShareableRuntimePath(relative)) return false;
            if (checkedFiles.ContainsKey(relative)) return true;
            var info = new FileInfo(path);
            var pin = fromInstaller?.FirstOrDefault(file =>
                string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)).ExpectedSha1;
            if (pin is not null && !string.Equals(ComputeSha1(path), pin, StringComparison.OrdinalIgnoreCase))
                return false;
            var hash = ComputeSha256(path);
            if (recorded.TryGetValue(relative, out var expected) && expected is not null)
            {
                if (info.Length != expected.SizeBytes ||
                    !string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (pin is null) return false;

            checkedFiles.Add(relative, new RuntimeFileState
            {
                SizeBytes = info.Length, LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks, Sha256 = hash
            });
            return true;
        }
    }

    private async Task<IReadOnlyList<LoaderInstallationFile>> ReadLegacyLoaderFilesAsync(
        PackRuntimeDescriptor descriptor, string profileId, CancellationToken token)
    {
        if (!UsesOfficialInstaller(descriptor)) return [];
        var installer = LoaderInstallationFiles.GetInstallerPath(_paths.SharedRuntime, descriptor);
        if (!IsOrdinarySharedFile(installer)) throw new InvalidDataException("The original loader installer is unavailable for cache verification.");
        using var checksumTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        checksumTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var pin = await OfficialLoaderInstaller.ReadVerifiedInstallerPinAsync(_httpClient, descriptor, installer, checksumTimeout.Token)
            .ConfigureAwait(false);
        if (!string.Equals(ComputeSha1(installer), pin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cached loader installer does not match the official checksum.");
        OfficialLoaderInstaller.RememberVerifiedInstallerPin(installer, pin, _logger);
        return LoaderInstallationFiles.Read(installer, SharedRuntimeStore.Libraries(_paths), descriptor, profileId);
    }

    private IReadOnlyCollection<string>? GetInstalledLoaderFiles(PackRuntimeDescriptor descriptor, string profileId)
    {
        if (!UsesOfficialInstaller(descriptor)) return [];
        IReadOnlyList<LoaderInstallationFile> files;
        try
        {
            var path = LoaderInstallationFiles.GetInstallerPath(_paths.SharedRuntime, descriptor);
            if (!IsOrdinarySharedFile(path)) return null;
            files = LoaderInstallationFiles.Read(path, SharedRuntimeStore.Libraries(_paths), descriptor, profileId);
        }
        catch (Exception ex) when (IsLoaderCacheError(ex))
        {
            // Some older installers use recipes that can run but cannot prove a
            // complete reusable inventory. Preserve normal launch for those.
            _logger.Warn($"Cannot record a reusable loader profile for {profileId}: {ex.Message}");
            return null;
        }

        foreach (var file in files)
            if (!IsOrdinarySharedFile(file.Path) || (file.ExpectedSha1 is { } pin &&
                !string.Equals(ComputeSha1(file.Path), pin, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The loader did not install a valid client file: " + Path.GetFileName(file.Path));
        return files.Select(file => file.Path).ToArray();
    }

    private void RegisterPreparedRuntime(string statePath, PackRuntimeDescriptor descriptor, RuntimeState state,
        IReadOnlyDictionary<string, RuntimeLibrarySource>? librarySources, IReadOnlyCollection<string>? loaderFiles)
    {
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, _jsonOptions));
        if (descriptor.Loader.Type != PackLoaderKind.Vanilla && loaderFiles is not null &&
            PossibleLoaderProfiles(descriptor).Contains(state.ProfileId, StringComparer.Ordinal))
        {
            var files = state.Files.Where(pair => IsShareableRuntimePath(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var outputs = loaderFiles.Select(path => ToRelativePath(Anchor, path)).ToArray();
            var path = LoaderReceiptPath(state.ProfileId);
            try
            {
                var profileJson = Path.Combine(Path.GetDirectoryName(path)!, state.ProfileId + ".json");
                if (!IsOrdinarySharedFile(profileJson) || (File.Exists(path) && !IsOrdinarySharedFile(path)))
                    throw new IOException("The shared loader receipt path is redirected.");
                var receipt = new LoaderReceipt(LoaderReceiptGeneration, LoaderKey(descriptor), state.ProfileId,
                    LibrarySourceKey(librarySources), outputs, files);
                AtomicFile.WriteAllTextIfChanged(path, JsonSerializer.Serialize(receipt, _jsonOptions));
            }
            catch (Exception ex) when (IsLoaderCacheError(ex))
            {
                _logger.Warn($"Could not save a receipt for loader {state.ProfileId}: {ex.Message}");
            }
        }
        var verifiedFiles = state.Files.Keys.Select(path => ResolveStatePath(Anchor, path)).ToArray();
        SharedRuntimeStore.RecordProfileOwnership(_paths, descriptor.MinecraftVersion, descriptor, verifiedFiles, _logger);
        if (state.ProfileId != descriptor.MinecraftVersion)
            SharedRuntimeStore.RecordProfileOwnership(_paths, state.ProfileId, descriptor, verifiedFiles, _logger);
        PortableJavaRuntimeService.SweepUnusedSuperseded(_paths, _logger);
    }

    private void EnsureSharedGameMayBeRepaired(PackRuntimeDescriptor requested)
    {
        foreach (var session in new ClientPresenceService(_paths, _logger).ReadLiveSessions())
        {
            if (session.ExecutablePath is not null) continue;
            try
            {
                var pack = PackManifestService.Load(_paths.CombineUnderPacks(session.PackRelativePath));
                if (pack.MinecraftVersion != requested.MinecraftVersion) continue;
            }
            catch (Exception ex) when (IsLoaderCacheError(ex))
            {
                _logger.Warn($"Could not identify the shared runtime of a running game: {ex.Message}");
            }
            throw new InvalidOperationException("Закройте запущенную игру перед восстановлением общей среды Minecraft.");
        }
    }

    private string LoaderReceiptPath(string profileId) =>
        Path.Combine(SharedRuntimeStore.Versions(_paths), profileId, LoaderReceiptName);

    private bool IsOrdinarySharedFile(string path)
    {
        var relative = ToRelativePath(Anchor, path);
        if (!relative.StartsWith("Shared/", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        var current = Path.GetFullPath(path);
        while (!current.Equals(Anchor, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            current = Path.GetDirectoryName(current)!;
        }
        return (File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0;
    }

    private static bool IsShareableRuntimePath(string relative) =>
        (relative.StartsWith("Shared/assets/", StringComparison.OrdinalIgnoreCase) ||
         relative.StartsWith("Shared/libraries/", StringComparison.OrdinalIgnoreCase) ||
         relative.StartsWith("Shared/versions/", StringComparison.OrdinalIgnoreCase)) &&
        !relative.Equals("Shared/versions/version_manifest_v2.json", StringComparison.OrdinalIgnoreCase);

    private static bool UsesOfficialInstaller(PackRuntimeDescriptor descriptor) =>
        descriptor.Loader.Type is PackLoaderKind.Forge or PackLoaderKind.NeoForge;

    private static bool IsLoaderCacheError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException or
            JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or NotSupportedException;

    private sealed record LoaderIdentity(string MinecraftVersion, PackLoaderKind Type, string? Version);
    private sealed record LoaderReceipt(int SchemaVersion, LoaderIdentity Identity, string ProfileId,
        string LibrarySourceKey, string[] LoaderFiles, Dictionary<string, RuntimeFileState> Files);
    private sealed record ReusedLoader(string ProfileId, string ClientPath, IReadOnlyCollection<string> Files,
        IReadOnlyDictionary<string, RuntimeFileState> Proof, IReadOnlyCollection<string> LoaderFiles);
}
