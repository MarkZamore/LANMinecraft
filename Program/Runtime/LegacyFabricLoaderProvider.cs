using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.ModLoaders.FabricMC;

namespace Minecraft;

/// <summary>Installs Legacy Fabric's own profile, including its mappings and patched LWJGL.</summary>
internal sealed class LegacyFabricLoaderProvider : IPackLoaderProvider
{
    internal const string MetadataHost = "https://meta.legacyfabric.net";
    public PackLoaderKind Kind => PackLoaderKind.LegacyFabric;
    public bool RequiresInstallerJava => false;

    public Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token) =>
        InstallProfileAsync(context.HttpClient, context.Launcher.MinecraftPath,
            context.Descriptor.MinecraftVersion, context.Descriptor.Loader.Version!, context.Logger, token);

    internal static async Task<string> InstallProfileAsync(HttpClient http, MinecraftPath path,
        string minecraftVersion, string loaderVersion, Logger logger, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var installer = new FabricInstaller(http, MetadataHost);
        // Install's versionName parameter does not rewrite the upstream ID in
        // CmlLib 4.0.6. Fetch the official profile, then name it before writing
        // anything: a normal Fabric profile in the shared store must survive.
        var versionName = $"legacyfabric-loader-{loaderVersion}-{minecraftVersion}";
        await using var source = await RuntimeRetry.RunAsync(
            retryToken => installer.GetProfileJson(minecraftVersion, loaderVersion).WaitAsync(retryToken),
            token).ConfigureAwait(false);
        var profile = await JsonNode.ParseAsync(source, cancellationToken: token).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidDataException("Legacy Fabric did not return a launch profile.");
        if (profile["inheritsFrom"]?.GetValue<string>() != minecraftVersion)
            throw new InvalidDataException("Legacy Fabric's profile inherits a different Minecraft version.");
        profile["id"] = versionName;
        profile["jar"] = minecraftVersion;
        AddNativeRepositoryMetadata(profile);
        var directory = Path.Combine(path.Versions, versionName);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(Path.Combine(directory, versionName + ".json"), profile.ToJsonString());
        logger.Info($"Legacy Fabric profile {versionName} prepared from its publisher's metadata.");
        return versionName;
    }

    private static void AddNativeRepositoryMetadata(JsonObject profile)
    {
        if (profile["libraries"] is not JsonArray libraries) return;
        foreach (var library in libraries.OfType<JsonObject>())
        {
            if (library["natives"] is not JsonObject { Count: > 0 } natives ||
                library["url"]?.GetValue<string>() is not { Length: > 0 } repository ||
                library["classifies"] is not null ||
                library["downloads"]?["classifiers"] is not null)
                continue;

            // CmlLib ignores the top-level repository for native downloads.
            // OS keys let it resolve the publisher's classifier and Maven path,
            // including any architecture placeholder, without adding a Java artifact.
            var classifiers = new JsonObject();
            foreach (var (os, _) in natives)
                classifiers[os] = new JsonObject { ["url"] = repository.TrimEnd('/') + "/" };
            var downloads = library["downloads"] as JsonObject ?? new JsonObject();
            downloads["classifiers"] = classifiers;
            if (downloads.Parent is null) library["downloads"] = downloads;
        }
    }
}
