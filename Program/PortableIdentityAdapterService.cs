using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class PortableIdentityAdapterService : IDisposable
{
    // A cache generation, not a data format: it is bumped to throw the cached
    // work away and redo it, so it is deliberately independent of
    // PortableFormat's version - a release must not cost every player a
    // re-download for an unrelated change.
    private const int AdapterCacheGeneration = 14;
    private const string ResourceName = "Minecraft.PortableIdentityAdapter.jar";
    private const string AdapterFileName = "portable-identity-adapter.jar";
    internal const string ManagedTransportFingerprintProperty = "managedTransportFingerprint";
    internal const string SteamNativePublicationMetadataFeature = "steam-native-publication-metadata-v1";
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly IdentityAdapterMappingService _mappings;
    private readonly Func<PackRuntimeDescriptor, SteamTransportBuild?> _transportSelection;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PortableIdentityAdapterService(AppPaths paths, Logger logger)
        : this(paths, logger, SteamTransportCatalog.Find)
    {
    }

    internal PortableIdentityAdapterService(
        AppPaths paths, Logger logger, Func<PackRuntimeDescriptor, SteamTransportBuild?> transportSelection)
    {
        _paths = paths;
        _logger = logger;
        _mappings = new IdentityAdapterMappingService(paths, transportSelection);
        _transportSelection = transportSelection;
    }

    public async Task<IReadOnlyList<string>> PrepareJvmArgumentsAsync(
        PreparedRuntime runtime,
        string gameDirectory,
        CancellationToken token)
    {
        var targetRoot = Path.GetFullPath(gameDirectory);
        _paths.EnsureUnderRoot(targetRoot);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (PackManifestService.UsesJava8SteamWorldSharing(runtime.Descriptor))
            {
                var transport = _transportSelection(runtime.Descriptor);
                if (transport is not { PreservesWorldSettingsOnShare: true } ||
                    !IdentityAdapterMappingService.IsPinnedTransportJar(Path.Combine(targetRoot, "mods", transport.FileName), transport))
                    throw new InvalidOperationException("Для этой сборки нужен проверенный Steam-адаптер для Java 8.");
                return ["-Dlink.e4steam.launcherManagedLan=true"];
            }
            if (runtime.Descriptor.Loader.Type == PackLoaderKind.LegacyFabric &&
                runtime.Descriptor.MinecraftVersion == "1.6.4")
            {
                // This runtime predates authlib. Its pack supplies the native
                // name/skin bridge; passing modern profile transformations here
                // would target classes which do not exist in Minecraft 1.6.4.
                // These paths and the title remain ordinary launch context,
                // independent of any content mod or pack name.
                var packDirectory = _paths.CombineUnderPacks(Path.GetFileName(targetRoot));
                var legacyTitle = WindowTitleSettings.Load(packDirectory, runtime, targetRoot);
                return
                [
                    "-Dminecraft.portable.legacy.enabled=true",
                    $"-Dminecraft.portable.legacy.registry={Path.Combine(_paths.Personal, "legacy-identity-profiles.properties")}",
                    $"-Dminecraft.portable.identity.windowTitleText={legacyTitle.Text}"
                ];
            }

            var directory = Path.Combine(_paths.Launcher, "IdentityAdapters", runtime.Descriptor.DescriptorHash);
            _paths.EnsureUnderRoot(directory);
            Directory.CreateDirectory(directory);
            var adapterPath = ExtractAdapter(directory);
            var adapterHash = HashFile(adapterPath);
            var statePath = Path.Combine(directory, "adapter-state.json");
            var state = ReadState(statePath);
            var managedTransportFingerprint = ComputeManagedTransportFingerprint(
                _transportSelection(runtime.Descriptor), targetRoot);
            WindowTitleSettings titleSettings;
            try
            {
                var packDirectory = _paths.CombineUnderPacks(Path.GetFileName(targetRoot));
                titleSettings = WindowTitleSettings.Load(packDirectory, runtime, targetRoot);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger.Warn("The pack's window title settings could not be read: " + ex.Message);
                titleSettings = WindowTitleSettings.Disabled;
            }
            if (state is not null && IsStateValid(state, runtime, targetRoot, adapterHash) &&
                state.Properties.GetValueOrDefault("windowTitleConfigSha256", "") == titleSettings.ContentSha256 &&
                state.Properties.GetValueOrDefault("windowTitleText", "") == titleSettings.Text &&
                state.Properties.GetValueOrDefault("windowTitleEnabled", "false") == (titleSettings.Enabled ? "true" : "false"))
            {
                return CreateJvmArguments(adapterPath, state.Properties);
            }

            async Task<IReadOnlyList<string>> CompleteAsync(IdentityAdapterConfiguration? core)
            {
                var properties = core?.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["enabled"] = "false",
                        ["identityHooksEnabled"] = "false"
                    };
                var targets = core?.Targets.ToList() ?? [];
                var mappingPath = core?.MappingPath;
                if (runtime.Descriptor.PreserveWorldSettingsOnShare &&
                    (properties.GetValueOrDefault("lanPreserveWorldSettingsEnabled") != "true" ||
                     properties.GetValueOrDefault("lanPublishEnabled") != "true" ||
                     properties.GetValueOrDefault("steamDiscoveryEnabled") != "true" ||
                     !targets.Any(target => IsConfiguredAlias(properties, "integratedServerClasses", target.ClassName)) ||
                     !targets.Any(target => IsConfiguredAlias(properties, "playerListClasses", target.ClassName)) ||
                     runtime.Descriptor.MinecraftVersion == "1.18.2" &&
                     (properties.GetValueOrDefault("pauseLegacyReportingEnabled") != "true" ||
                      !targets.Any(target => IsConfiguredAlias(properties, "pauseScreenClasses", target.ClassName)))))
                    throw new InvalidOperationException("Не удалось подтвердить открытие мира через Steam с сохранением настроек команд.");
                properties[ManagedTransportFingerprintProperty] = managedTransportFingerprint;
                properties["windowTitleEnabled"] = "false";
                properties["windowTitleText"] = titleSettings.Text;
                properties["windowTitleConfigSha256"] = titleSettings.ContentSha256;
                var handshakeMapping = await AddVerifiedForgeHandshakeAsync(runtime, _paths, properties, targets,
                    async handshake =>
                    {
                        foreach (var target in handshake.Targets)
                            await RunPreflightAsync(runtime.JavaPath, adapterPath, handshake.Properties, target, token)
                                .ConfigureAwait(false);
                    }, message => _logger.Warn(message)).ConfigureAwait(false);
                mappingPath ??= handshakeMapping;
                if (titleSettings.Enabled)
                {
                    try
                    {
                        var title = WindowTitleMappingService.Build(runtime, titleSettings);
                        foreach (var target in title.Targets)
                            await RunPreflightAsync(runtime.JavaPath, adapterPath, title.Properties, target, token)
                                .ConfigureAwait(false);
                        foreach (var pair in title.Properties) properties[pair.Key] = pair.Value;
                        foreach (var target in title.Targets)
                            if (!targets.Contains(target)) targets.Add(target);
                        mappingPath ??= title.Targets.Count > 0 ? title.Targets[0].JarPath : null;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                                   or InvalidOperationException or TimeoutException)
                    {
                        _logger.Warn("The pack's stable window title could not be verified; keeping the native title: " + ex.Message);
                    }
                }
                if (targets.Count == 0 || mappingPath is null) return [];
                var verified = new IdentityAdapterConfiguration(mappingPath, properties, targets)
                {
                    PreflightSources = core?.PreflightSources ?? []
                };
                var mappingInfo = new FileInfo(verified.MappingPath);
                var snapshotHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                IdentityAdapterTargetState Snapshot(IdentityAdapterTarget target)
                {
                    var info = new FileInfo(target.JarPath);
                    if (!snapshotHashes.TryGetValue(target.JarPath, out var hash))
                        snapshotHashes[target.JarPath] = hash = HashFile(target.JarPath);
                    return new IdentityAdapterTargetState
                    {
                        JarPath = target.JarPath,
                        ClassName = target.ClassName,
                        SizeBytes = info.Length,
                        LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                        Sha256 = hash
                    };
                }
                var targetStates = verified.Targets.Select(Snapshot).ToList();
                var sourceStates = verified.PreflightSources.Select(Snapshot).ToList();
                state = new IdentityAdapterState
                {
                    SchemaVersion = AdapterCacheGeneration,
                    DescriptorHash = runtime.Descriptor.DescriptorHash,
                    TargetRoot = targetRoot,
                    AdapterSha256 = adapterHash,
                    MappingPath = verified.MappingPath,
                    MappingSizeBytes = mappingInfo.Length,
                    MappingLastWriteUtcTicks = mappingInfo.LastWriteTimeUtc.Ticks,
                    MappingSha256 = HashFile(verified.MappingPath),
                    Properties = verified.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    Targets = targetStates,
                    PreflightSources = sourceStates,
                    VerifiedAtUtc = DateTimeOffset.UtcNow
                };
                state.ConfigurationSha256 = ComputeConfigurationHash(state);
                AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, _jsonOptions));
                _logger.Info(
                    $"Portable UUID adapter verified for {runtime.Descriptor.MinecraftVersion} " +
                    $"{runtime.Descriptor.Loader.Type} {runtime.Descriptor.Loader.Version}. " +
                    $"In the game: {DescribeGameFeatures(state.Properties)}.");
                return CreateJvmArguments(adapterPath, state.Properties);
            }

            IdentityAdapterConfiguration configuration;
            try
            {
                configuration = _mappings.Build(runtime, targetRoot);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                // No hooks for this runtime, which is not a reason to refuse to
                // start it. What the hooks buy is that an offline player's UUID
                // is the one the launcher derives, so a person's progress
                // follows them rather than the machine they sat at. A pack the
                // launcher has no hooks for falls back to the UUID Minecraft
                // derives from the name offline: just as stable, just as
                // playable together, simply a different number, and one that
                // carries nothing over from packs where the hooks did run.
                // Named here rather than left to the exception: only the one
                // thrown for an unsupported runtime carries the version, and the
                // one thrown for a missing class does not, so the warning read
                // "No portable UUID hooks for Required identity mapping class is
                // missing: net/minecraft/client/resources/PlayerSkin" - true,
                // and silent about which pack it was true of.
                _logger.Warn(
                    $"No portable UUID hooks for Minecraft {runtime.Descriptor.MinecraftVersion} " +
                    $"{runtime.Descriptor.Loader.Type} {runtime.Descriptor.Loader.Version}: {ex.Message} " +
                    "The pack starts without them: everyone keeps the UUID Minecraft gives them offline, " +
                    "and the skin chosen in the launcher is not shown, because these hooks are what show it.");
                return await CompleteAsync(null).ConfigureAwait(false);
            }

            if (configuration.Properties.GetValueOrDefault("identityHooksEnabled") == "false")
            {
                // Half the hooks, and the half a player actually looks at. The
                // UUID hooks patch Minecraft's own classes and need the
                // runtime's mappings; the skin hooks are all in
                // com.mojang.authlib, which no loader obfuscates, so they go in
                // regardless. Worth a line of its own: the warning above says
                // skins are lost with the UUIDs, and here they are not.
                _logger.Info(
                    $"Skins only for Minecraft {runtime.Descriptor.MinecraftVersion} " +
                    $"{runtime.Descriptor.Loader.Type} {runtime.Descriptor.Loader.Version}: " +
                    "the skin chosen in the launcher is shown, and a profile still carries the UUID the " +
                    "launcher derives - both of those patches are in com.mojang.authlib. What is left out " +
                    "is the hook that settles the UUID at the door and the refusal of two players under one.");
            }

            async Task<(IdentityAdapterTarget? Target, string? Refusal)> VerifyAsync(IdentityAdapterConfiguration candidate)
            {
                IdentityAdapterTarget? currentTarget = null;
                try
                {
                    foreach (var target in candidate.Targets)
                    {
                        currentTarget = target;
                        await RunPreflightAsync(runtime.JavaPath, adapterPath, candidate.Properties, target, token,
                            candidate.PreflightSources)
                            .ConfigureAwait(false);
                        if (IsConfiguredAlias(candidate.Properties, "textureUrlCheckerClasses", target.ClassName))
                        {
                            await RunSkinSemanticPreflightAsync(
                                    runtime.JavaPath,
                                    adapterPath,
                                    candidate.Properties,
                                    target,
                                    token)
                                .ConfigureAwait(false);
                        }
                    }
                    return (null, null);
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
                                               or TimeoutException or IOException)
                {
                    return (currentTarget, ex.Message);
                }
            }

            Task<(IdentityAdapterConfiguration Configuration, string? Refusal)> PreflightAsync(
                IdentityAdapterConfiguration candidate) => PreflightWithDiscoveryFallbackAsync(
                    candidate, VerifyAsync, message => _logger.Warn(message));

            // The preflight is how the launcher finds out that its hooks do not
            // fit this runtime. Finding that out is not a reason to refuse to
            // start the pack - it is the reason to start it without them, which
            // is what happens when the configuration could not be built at all.
            // It used to stop the launch and put the failure on screen, and a
            // 1.18.2 pack met that as a dialog it could not get past: the
            // adapter was built for Java 21 and 1.18.2 runs on 17, so the check
            // could not even load.
            var preflight = await PreflightAsync(configuration).ConfigureAwait(false);
            configuration = preflight.Configuration;
            var refusal = preflight.Refusal;
            if (refusal is not null)
            {
                // One hook that does not fit used to cost the pack every hook,
                // and the skin hooks are not even in Minecraft: they are in
                // com.mojang.authlib, which no version obfuscates and no
                // preflight of the game's own bytecode can speak for. So the
                // refusal is carried forward and the skins are asked for again
                // on their own - the same answer Build gives when it is a name
                // that is missing rather than the bytecode behind it.
                //
                // Asked without looking at what failed, because "skins only" is
                // not the bare set: it still carries the hook that opens a world
                // to the network, whose class lives in Minecraft like any other.
                // Reading the flag instead of retrying is what let that pack
                // fall straight through to giving up.
                _logger.Warn(
                    $"The UUID hooks do not fit Minecraft {runtime.Descriptor.MinecraftVersion} " +
                    $"{runtime.Descriptor.Loader.Type} {runtime.Descriptor.Loader.Version}: {refusal} " +
                    "Asking for the skin hooks on their own.");
                try
                {
                    configuration = _mappings.BuildSkins(runtime, targetRoot, refusal);
                    preflight = await PreflightAsync(configuration).ConfigureAwait(false);
                    configuration = preflight.Configuration;
                    refusal = preflight.Refusal;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
                {
                    _logger.Warn("Skin hooks could not be prepared; verifying the window title independently: " + ex.Message);
                    return await CompleteAsync(null).ConfigureAwait(false);
                }
            }

            if (refusal is not null)
            {
                _logger.Warn(
                    $"No portable hooks for Minecraft {runtime.Descriptor.MinecraftVersion} " +
                    $"{runtime.Descriptor.Loader.Type} {runtime.Descriptor.Loader.Version}: {refusal} " +
                    "The pack starts without them: everyone keeps the UUID Minecraft gives them offline, " +
                    "and the skin chosen in the launcher is not shown.");
                return await CompleteAsync(null).ConfigureAwait(false);
            }

            return await CompleteAsync(configuration).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // A loader concurrency correction is verified independently of player identity.
    internal static async Task<string?> AddVerifiedForgeHandshakeAsync(
        PreparedRuntime runtime, AppPaths paths, IDictionary<string, string> properties,
        IList<IdentityAdapterTarget> targets, Func<IdentityAdapterConfiguration, Task> verify, Action<string> warning)
    {
        properties["forgeHandshakeSyncEnabled"] = "false";
        try
        {
            var handshake = ForgeHandshakeMappingService.Build(runtime, paths);
            if (handshake is null) return null;
            await verify(handshake).ConfigureAwait(false);
            properties["forgeHandshakeSyncEnabled"] = "true";
            foreach (var target in handshake.Targets)
                if (!targets.Contains(target)) targets.Add(target);
            return handshake.MappingPath;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                       or InvalidOperationException or TimeoutException)
        {
            warning("Forge login acknowledgement synchronization could not be verified: " + ex.Message);
            return null;
        }
    }

    internal static async Task<(IdentityAdapterConfiguration Configuration, string? Refusal)>
        PreflightWithDiscoveryFallbackAsync(
            IdentityAdapterConfiguration candidate,
            Func<IdentityAdapterConfiguration, Task<(IdentityAdapterTarget? Target, string? Refusal)>> verify,
            Action<string> warning)
    {
        var failure = await verify(candidate).ConfigureAwait(false);
        if (failure.Refusal is null) return (candidate, null);
        var clientKeys = new HashSet<string>(StringComparer.Ordinal)
            { "steamDiscoveryScreenClasses", "steamDiscoveryLanListClasses", "steamDiscoveryLanEntryClasses" };
        bool ClientTarget(IdentityAdapterTarget target) =>
            !target.ClassName.StartsWith("com/mojang/authlib/", StringComparison.Ordinal) &&
            clientKeys.Any(key => IsConfiguredAlias(candidate.Properties, key, target.ClassName)) &&
            !candidate.Properties.Keys.Any(key => key.EndsWith("Classes", StringComparison.Ordinal) &&
                !clientKeys.Contains(key) && IsConfiguredAlias(candidate.Properties, key, target.ClassName));
        if (failure.Target is null || !ClientTarget(failure.Target) ||
            candidate.Properties.GetValueOrDefault("steamDiscoveryEnabled") != "true" ||
            candidate.Properties.GetValueOrDefault("steamDiscoveryClientEnabled", "true") == "false")
            return (candidate, failure.Refusal);

        // The local browser and the host publisher are independent families.
        // Drop only failed client instrumentation, then verify every remaining
        // native target again. Never accept an unverified login or publisher.
        var properties = candidate.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        properties["steamDiscoveryClientEnabled"] = "false";
        var fallback = candidate with
        {
            Properties = properties,
            Targets = candidate.Targets.Where(target => !ClientTarget(target)).ToArray()
        };
        var remaining = await verify(fallback).ConfigureAwait(false);
        if (remaining.Refusal is not null) return (candidate, remaining.Refusal);
        warning("The local Minecraft Steam browser could not be verified: " + failure.Refusal +
                " Its client hooks are disabled for this launch; the independently verified remaining hooks stay enabled, including host publication when available. " +
                "The local browser will be checked again on the next launch.");
        return (fallback, null);
    }

    /// <summary>
    /// Which of the game-side patches this runtime actually got.
    ///
    /// Every one of them turns itself off where the names it needs are missing,
    /// which is the right way round - but until this there was no way to tell,
    /// from a player's report, a pack where a feature is absent from one where
    /// it is present and behaving badly. An evening was spent guessing which of
    /// the two a guest's stuttering world was.
    /// </summary>
    private static string DescribeGameFeatures(Dictionary<string, string> properties)
    {
        var present = new List<string>();
        if (Says(properties, "windowTitleEnabled")) present.Add("stable window title");
        if (Says(properties, "forgeHandshakeSyncEnabled")) present.Add("serialized Forge login acknowledgements");
        if (Says(properties, "lanPublishEnabled")) present.Add("one-press LAN");
        if (Says(properties, "steamDiscoveryEnabled")) present.Add(
            properties.GetValueOrDefault("steamDiscoveryClientEnabled", "true") == "false"
                ? "Steam host world publication (local browser unavailable)" : "Steam worlds in Multiplayer");
        if (properties.ContainsKey("setChunkViewDistanceMethods")) present.Add("serve distance");
        if (Says(properties, "perPlayerChunksEnabled")) present.Add("per-player render distance");
        if (Says(properties, "chunkPacingEnabled")) present.Add("paced chunk sending");
        if (Says(properties, "guestMovementEnabled")) present.Add("guests trusted with their own movement");
        return present.Count == 0 ? "nothing beyond skins and the UUID" : string.Join(", ", present);
    }

    private static bool Says(Dictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) &&
        string.Equals(value, "true", StringComparison.Ordinal);

    internal string ExtractAdapter(string directory)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded portable identity adapter is missing.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var bytes = memory.ToArray();
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // Java keeps agent JARs open for its lifetime. A launcher update must
        // not replace the image still used by an earlier process/session.
        var versionDirectory = Path.Combine(directory, expectedHash);
        _paths.EnsureUnderRoot(versionDirectory);
        Directory.CreateDirectory(versionDirectory);
        var destination = Path.Combine(versionDirectory, AdapterFileName);
        if (File.Exists(destination) && string.Equals(HashFile(destination), expectedHash, StringComparison.Ordinal))
        {
            return destination;
        }

        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (!string.Equals(HashFile(temporary), expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Extracted portable identity adapter failed SHA-256 validation.");
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return destination;
    }

    private async Task RunPreflightAsync(
        string configuredJavaPath,
        string adapterPath,
        IReadOnlyDictionary<string, string> properties,
        IdentityAdapterTarget target,
        CancellationToken token,
        IReadOnlyList<IdentityAdapterTarget>? classSources = null)
    {
        var javaPath = ResolveConsoleJava(configuredJavaPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var processFiles = PortableJavaEnvironment.CreateToolScope(
            startInfo, Path.Combine(_paths.Personal, "Temp"));
        foreach (var pair in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add($"-Dminecraft.portable.identity.{pair.Key}={pair.Value}");
        }
        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(adapterPath);
        startInfo.ArgumentList.Add("minecraft.portable.identity.PortableIdentityPreflight");
        startInfo.ArgumentList.Add(target.JarPath);
        startInfo.ArgumentList.Add(target.ClassName);
        foreach (var source in classSources ?? [])
        {
            _paths.EnsureUnderRoot(source.JarPath);
            startInfo.ArgumentList.Add(source.ClassName + "=" + Path.GetFullPath(source.JarPath));
        }

        var process = processFiles.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(token);
        var standardError = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _ = await processFiles.StopAsync().ConfigureAwait(false);
            PortableJavaEnvironment.ObserveOutputCompletion(standardOutput, standardError);
            throw new TimeoutException("Portable identity bytecode preflight timed out.");
        }
        catch
        {
            _ = await processFiles.StopAsync().ConfigureAwait(false);
            PortableJavaEnvironment.ObserveOutputCompletion(standardOutput, standardError);
            throw;
        }

        var output = (await standardOutput.ConfigureAwait(false)).Trim();
        var error = (await standardError.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new NotSupportedException(FormatPreflightFailure(
                target.JarPath, target.ClassName, process.ExitCode, output, error));
        }
    }

    internal static string FormatPreflightFailure(
        string jarPath, string className, int exitCode, string output, string error)
    {
        var details = string.Join(Environment.NewLine,
            new[] { output.Trim(), error.Trim() }.Where(value => value.Length > 0));
        if (details.Length > 2000) details = details[^2000..];
        if (details.Length == 0) details = "Java returned no standard output or standard error.";
        // Native JVM failures can leave both pipes empty. Keep their Windows
        // status as well as the signed process code instead of calling every
        // nonzero exit an unsupported Minecraft bytecode shape.
        return $"Portable UUID bytecode preflight failed for {Path.GetFileName(jarPath)}::{className}. " +
               $"Exit code {exitCode} (0x{unchecked((uint)exitCode):X8}). " + details;
    }

    private async Task RunSkinSemanticPreflightAsync(
        string configuredJavaPath,
        string adapterPath,
        IReadOnlyDictionary<string, string> properties,
        IdentityAdapterTarget target,
        CancellationToken token)
    {
        const string playerUuid = "00000000-0000-4000-8000-000000000001";
        const string skinHash = "1111111111111111111111111111111111111111111111111111111111111111";
        const string otherHash = "2222222222222222222222222222222222222222222222222222222222222222";
        var registeredUrl = $"http://127.0.0.1:{SkinService.PreferredHttpPort}/skin/{playerUuid}/{skinHash}";
        var unregisteredUrl = $"http://127.0.0.1:{SkinService.PreferredHttpPort}/skin/{playerUuid}/{otherHash}";
        const string officialUrl = "https://textures.minecraft.net/texture/portable-preflight";
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveConsoleJava(configuredJavaPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var processFiles = PortableJavaEnvironment.CreateToolScope(
            startInfo, Path.Combine(_paths.Personal, "Temp"));
        var registryPath = Path.Combine(processFiles.RootDirectory, "skin-preflight.properties");
        File.WriteAllText(
            registryPath,
            $"{playerUuid}|{skinHash}|classic|{registeredUrl}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        startInfo.ArgumentList.Add("-Dminecraft.portable.identity.enabled=true");
        startInfo.ArgumentList.Add($"-Dminecraft.portable.skin.registry={registryPath}");
        foreach (var pair in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add($"-Dminecraft.portable.identity.{pair.Key}={pair.Value}");
        }
        startInfo.ArgumentList.Add($"-javaagent:{adapterPath}");
        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(adapterPath + Path.PathSeparator + target.JarPath);
        startInfo.ArgumentList.Add("minecraft.portable.identity.PortableSkinPreflight");
        startInfo.ArgumentList.Add(registeredUrl);
        startInfo.ArgumentList.Add(unregisteredUrl);
        startInfo.ArgumentList.Add(officialUrl);

        var process = processFiles.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(token);
        var standardError = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            _ = await processFiles.StopAsync().ConfigureAwait(false);
            PortableJavaEnvironment.ObserveOutputCompletion(standardOutput, standardError);
            throw;
        }

        var output = (await standardOutput.ConfigureAwait(false)).Trim();
        var error = (await standardError.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, new[] { output, error }.Where(value => value.Length > 0));
            if (details.Length > 2000) details = details[^2000..];
            throw new NotSupportedException(
                $"Portable skin behavior preflight failed for {Path.GetFileName(target.JarPath)}. " + details);
        }
    }

    private static bool IsConfiguredAlias(
        IReadOnlyDictionary<string, string> properties,
        string propertyName,
        string value) =>
        properties.TryGetValue(propertyName, out var aliases) &&
        aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(value, StringComparer.Ordinal);

    private static List<string> CreateJvmArguments(
        string adapterPath,
        IReadOnlyDictionary<string, string> properties)
    {
        var arguments = new List<string>
        {
            "-Dminecraft.portable.identity.enabled=true"
        };
        arguments.AddRange(properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"-Dminecraft.portable.identity.{pair.Key}={pair.Value}"));
        arguments.Add($"-javaagent:{adapterPath}");
        return arguments;
    }

    private bool IsStateValid(
        IdentityAdapterState state,
        PreparedRuntime runtime,
        string targetRoot,
        string adapterHash)
    {
        // A title-only result may follow a transient core/skin preflight failure.
        // Keep its title for this launch, but retry the core on the next one.
        if (state.Properties.GetValueOrDefault("enabled", "true") == "false") return false;
        if (state.Properties.GetValueOrDefault("steamDiscoveryClientEnabled", "true") == "false") return false;
        if (state.Properties.GetValueOrDefault(ManagedTransportFingerprintProperty, "") !=
            ComputeManagedTransportFingerprint(_transportSelection(runtime.Descriptor), targetRoot)) return false;
        if (state.SchemaVersion != AdapterCacheGeneration ||
            !string.Equals(state.DescriptorHash, runtime.Descriptor.DescriptorHash, StringComparison.Ordinal) ||
            !string.Equals(state.TargetRoot, targetRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.AdapterSha256, adapterHash, StringComparison.Ordinal) ||
            state.Properties.Count == 0 ||
            state.Targets.Count == 0 ||
            !FileMatches(state.MappingPath, state.MappingSizeBytes, state.MappingLastWriteUtcTicks))
        {
            return false;
        }
        _paths.EnsureUnderRoot(state.MappingPath);
        foreach (var target in state.Targets)
        {
            _paths.EnsureUnderRoot(target.JarPath);
            if (!FileMatches(target.JarPath, target.SizeBytes, target.LastWriteUtcTicks)) return false;
        }
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in state.PreflightSources)
        {
            _paths.EnsureUnderRoot(source.JarPath);
            if (!FileMatches(source.JarPath, source.SizeBytes, source.LastWriteUtcTicks)) return false;
            if (!sourceHashes.TryGetValue(source.JarPath, out var hash))
                sourceHashes[source.JarPath] = hash = HashFile(source.JarPath);
            if (!string.Equals(hash, source.Sha256, StringComparison.Ordinal)) return false;
        }
        return string.Equals(state.ConfigurationSha256, ComputeConfigurationHash(state), StringComparison.Ordinal);
    }

    private IdentityAdapterState? ReadState(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<IdentityAdapterState>(File.ReadAllText(path), _jsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Portable identity adapter state will be regenerated: {ex.Message}");
            return null;
        }
    }

    private static string ComputeConfigurationHash(IdentityAdapterState state)
    {
        var text = new StringBuilder()
            .AppendLine(state.DescriptorHash)
            .AppendLine(state.TargetRoot)
            .AppendLine(state.AdapterSha256)
            .AppendLine(state.MappingSha256);
        foreach (var pair in state.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(pair.Key).Append('=').AppendLine(pair.Value);
        }
        foreach (var target in state.Targets.OrderBy(target => target.ClassName, StringComparer.Ordinal))
        {
            text.Append(target.ClassName).Append('=').AppendLine(target.Sha256);
        }
        foreach (var source in state.PreflightSources.OrderBy(source => source.ClassName, StringComparer.Ordinal))
            text.Append("source:").Append(source.ClassName).Append('=').Append(source.JarPath)
                .Append('=').AppendLine(source.Sha256);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static bool FileMatches(string path, long size, long lastWriteUtcTicks)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == lastWriteUtcTicks;
    }

    internal static string ComputeManagedTransportFingerprint(SteamTransportBuild? selected, string gameDirectory)
    {
        var text = new StringBuilder("managed-transport-v1\0");
        if (selected is null)
        {
            text.Append("none");
        }
        else
        {
            var actual = "missing";
            try
            {
                var path = Path.Combine(gameDirectory, "mods", selected.FileName);
                if (File.Exists(path)) actual = HashFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                actual = "unreadable";
            }
            // Paths and timestamps are deliberately excluded. Every reuse
            // hashes the actual bytes, including same-size/same-mtime changes.
            text.Append(selected.Version).Append('\0')
                .Append(selected.CacheFileId.ToString(CultureInfo.InvariantCulture)).Append('\0')
                .Append(selected.FileName).Append('\0')
                .Append(selected.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append('\0')
                .Append(selected.Sha256.ToLowerInvariant()).Append('\0')
                .Append(actual);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string ResolveConsoleJava(string configuredPath)
    {
        var directory = Path.GetDirectoryName(configuredPath)
            ?? throw new FileNotFoundException("Java runtime path is invalid.", configuredPath);
        var consoleJava = Path.Combine(directory, "java.exe");
        if (File.Exists(consoleJava)) return consoleJava;
        if (File.Exists(configuredPath)) return configuredPath;
        throw new FileNotFoundException("Java runtime required for identity preflight was not found.", consoleJava);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private sealed class IdentityAdapterState
    {
        public int SchemaVersion { get; set; }
        public string DescriptorHash { get; set; } = "";
        public string TargetRoot { get; set; } = "";
        public string AdapterSha256 { get; set; } = "";
        public string MappingPath { get; set; } = "";
        public long MappingSizeBytes { get; set; }
        public long MappingLastWriteUtcTicks { get; set; }
        public string MappingSha256 { get; set; } = "";
        public string ConfigurationSha256 { get; set; } = "";
        public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);
        public List<IdentityAdapterTargetState> Targets { get; set; } = [];
        public List<IdentityAdapterTargetState> PreflightSources { get; set; } = [];
        public DateTimeOffset VerifiedAtUtc { get; set; }
    }

    private sealed class IdentityAdapterTargetState
    {
        public string JarPath { get; set; } = "";
        public string ClassName { get; set; } = "";
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
