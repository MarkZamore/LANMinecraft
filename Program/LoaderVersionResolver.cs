using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// Asks each loader's own publisher which version of it to use for a version of
/// Minecraft.
/// </summary>
/// <remarks>
/// The other half of letting somebody build a pack by putting jars in a folder.
/// The mods say which loader and which Minecraft; nothing in them says which
/// build of that loader, and there is no sensible way for a player to know
/// either - Fabric is on 0.19.3 while NeoForge for the same game is on 21.1.248
/// and Forge on 47.4.10, and none of those numbers can be guessed from the
/// others.
///
/// Each publisher answers a different way, so each is asked its own way:
/// Fabric and Quilt both serve a JSON list of loader builds for a given game
/// version, newest first, with a <c>stable</c> flag; NeoForge publishes a Maven
/// metadata file whose versions carry the game version in their own numbering
/// (Minecraft 1.21.1 is NeoForge 21.1.x); and Forge publishes a promotions file
/// keyed by game version, where "recommended" is the build it stands behind and
/// "latest" is the newest one.
///
/// Nothing here is cached to disk. A pack is resolved once, when its manifest is
/// written, and from then on the manifest holds the answer - which is the point
/// of writing one.
/// </remarks>
public sealed class LoaderVersionResolver(HttpClient httpClient, Logger? logger = null)
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2/versions/loader/";
    private const string QuiltMeta = "https://meta.quiltmc.org/v3/versions/loader/";
    private const string NeoForgeMaven =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    private const string ForgePromotions =
        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    private static readonly Regex MavenVersion = new(@"<version>([^<]+)</version>", RegexOptions.Compiled);

    /// <summary>
    /// The version of <paramref name="loader"/> to use for
    /// <paramref name="minecraftVersion"/>, or null when its publisher has none
    /// or could not be reached.
    /// </summary>
    public async Task<string?> ResolveAsync(
        PackLoaderKind loader,
        string minecraftVersion,
        CancellationToken token)
    {
        if (loader == PackLoaderKind.Vanilla) return null;
        try
        {
            var version = loader switch
            {
                PackLoaderKind.Fabric => await FromLoaderMetaAsync(FabricMeta, minecraftVersion, token),
                PackLoaderKind.Quilt => await FromLoaderMetaAsync(QuiltMeta, minecraftVersion, token),
                PackLoaderKind.NeoForge => await FromNeoForgeAsync(minecraftVersion, token),
                PackLoaderKind.Forge => await FromForgeAsync(minecraftVersion, token),
                _ => null
            };
            if (version is not null)
            {
                logger?.Info($"{loader} for Minecraft {minecraftVersion} is {version}, as its publisher lists it.");
            }
            else
            {
                logger?.Warn($"{loader} publishes no build for Minecraft {minecraftVersion}.");
            }
            return version;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger?.Warn($"Which {loader} to use for Minecraft {minecraftVersion} could not be looked up: {ex.Message}");
            return null;
        }
    }

    /// <summary>Fabric and Quilt: newest stable, or newest of any kind.</summary>
    private async Task<string?> FromLoaderMetaAsync(string endpoint, string minecraftVersion, CancellationToken token)
    {
        var json = await httpClient.GetStringAsync(endpoint + Uri.EscapeDataString(minecraftVersion), token)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? newestOfAny = null;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("loader", out var loader) ||
                !loader.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = version.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            newestOfAny ??= value;
            // The list is newest first, so the first stable one is the answer.
            if (loader.TryGetProperty("stable", out var stable) && stable.ValueKind == JsonValueKind.True)
            {
                return value;
            }
        }
        return newestOfAny;
    }

    /// <summary>
    /// NeoForge numbers itself after the game: Minecraft 1.21.1 is NeoForge
    /// 21.1.x, and 1.21 with no patch is 21.0.x.
    /// </summary>
    private async Task<string?> FromNeoForgeAsync(string minecraftVersion, CancellationToken token)
    {
        var parts = VersionOrder.Parse(minecraftVersion);
        if (parts.Length < 2 || parts[0] != 1) return null;
        var prefix = $"{parts[1]}.{(parts.Length > 2 ? parts[2] : 0)}.";

        var xml = await httpClient.GetStringAsync(NeoForgeMaven, token).ConfigureAwait(false);
        string? best = null;
        foreach (Match match in MavenVersion.Matches(xml))
        {
            var version = match.Groups[1].Value.Trim();
            if (!version.StartsWith(prefix, StringComparison.Ordinal)) continue;
            // Betas are published beside releases and are not what a pack that
            // was never told anything should be given.
            if (version.Contains("beta", StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || VersionOrder.CompareVersions(version, best) > 0) best = version;
        }
        return best;
    }

    /// <summary>Forge: the build it recommends, else the newest it has.</summary>
    private async Task<string?> FromForgeAsync(string minecraftVersion, CancellationToken token)
    {
        var json = await httpClient.GetStringAsync(ForgePromotions, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("promos", out var promos) ||
            promos.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var suffix in new[] { "-recommended", "-latest" })
        {
            if (promos.TryGetProperty(minecraftVersion + suffix, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var version = value.GetString();
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }
        }
        return null;
    }
}
