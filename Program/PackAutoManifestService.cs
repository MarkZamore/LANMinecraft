using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft;

/// <summary>
/// Turns a folder of mods into a pack the launcher can start.
/// </summary>
/// <remarks>
/// The whole of what somebody has to do to make a build of their own: put a
/// folder in <c>Minecraft/Packs</c>, put jars in its <c>mods</c> folder, and
/// press Play. Everything a pack used to have to be told is worked out here
/// instead - which loader the mods are for and which Minecraft they were built
/// against, read out of the jars themselves; which build of that loader, asked
/// of the people who publish it; and the game itself, which is downloaded from
/// Mojang like every other pack's.
///
/// The answer is then written into the folder as <c>portable-pack.json</c>,
/// which is the same file a pack author would have written by hand and is read
/// by everything downstream exactly as if they had. That matters for more than
/// tidiness: the file is the pack's identity everywhere else in the launcher -
/// the runtime is keyed by a hash of it, the instance is validated against it,
/// and Steam play is offered on the strength of it - so working it out once and
/// writing it down is what makes a folder of mods a first-class pack rather
/// than a special case threaded through every service that touches one.
///
/// A folder the jars do not agree about gets no file and no guess. The reason
/// is said out loud, because "the mods disagree about the loader (Fabric 92,
/// Forge 66)" is something a person can act on and "could not prepare pack" is
/// not.
/// </remarks>
public sealed class PackAutoManifestService(AppPaths paths, Logger logger, HttpClient httpClient)
{
    private readonly LoaderVersionResolver _loaderVersions = new(httpClient, logger);

    /// <summary>
    /// Makes sure this pack has a manifest, writing one from its mods when it
    /// has none. Returns true when a manifest was written.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The folder holds no pack anyone could name, and the message says why.
    /// </exception>
    public async Task<bool> EnsureAsync(string packRelativePath, CancellationToken token)
    {
        var packDirectory = paths.CombineUnderPacks(packRelativePath);
        if (PackManifestService.HasManifest(packDirectory)) return false;
        // A pack that comes from somewhere brings its own manifest with it, and
        // is not this feature's business. Writing one for it before its first
        // download would also tell the sync it was already installed, which is
        // how it decides whether being offline is a warning or a refusal.
        if (File.Exists(Path.Combine(packDirectory, PortablePackSyncService.SourceMarkerFileName)))
        {
            return false;
        }

        var detected = PackDetector.Detect(packDirectory);
        logger.Info($"Working out what {packRelativePath} is - {detected.Explanation}.");
        if (!detected.IsComplete)
        {
            throw new InvalidDataException(
                $"This folder's mods do not say what pack they are: {detected.Explanation}. " +
                $"Add {PackManifestService.ManifestFileName} naming the Minecraft version and the loader.");
        }

        var loader = detected.Loader!.Value;
        var minecraftVersion = detected.MinecraftVersion!;
        var loaderVersion = await _loaderVersions.ResolveAsync(loader, minecraftVersion, token).ConfigureAwait(false);
        if (loaderVersion is null)
        {
            throw new InvalidDataException(
                $"These mods are for {loader} on Minecraft {minecraftVersion}, and which build of {loader} " +
                $"to use for it could not be looked up. Connect to the internet, or add " +
                $"{PackManifestService.ManifestFileName} naming loader.version yourself.");
        }

        Write(packDirectory, minecraftVersion, loader, loaderVersion);
        logger.Info(
            $"{packRelativePath} is a Minecraft {minecraftVersion} {loader} {loaderVersion} pack, " +
            $"and now says so in its own {PackManifestService.ManifestFileName}.");
        return true;
    }

    /// <summary>
    /// The same file a pack author writes by hand, with one field left out: a
    /// pack that brings no client jar has the official one fetched for it.
    /// </summary>
    private static void Write(
        string packDirectory,
        string minecraftVersion,
        PackLoaderKind loader,
        string loaderVersion)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = PackManifestService.CurrentSchemaVersion,
            ["minecraftVersion"] = minecraftVersion,
            ["loader"] = new JsonObject
            {
                ["type"] = loader.ToString().ToLowerInvariant(),
                ["version"] = loaderVersion
            }
        };
        AtomicFile.WriteAllText(
            Path.Combine(packDirectory, PackManifestService.ManifestFileName),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }
}
