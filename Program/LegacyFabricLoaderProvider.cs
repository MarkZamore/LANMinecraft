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
        var directory = Path.Combine(path.Versions, versionName);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(Path.Combine(directory, versionName + ".json"), profile.ToJsonString());
        logger.Info($"Legacy Fabric profile {versionName} prepared from its publisher's metadata.");
        return versionName;
    }
}
