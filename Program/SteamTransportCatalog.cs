using System.Globalization;

namespace Minecraft;

/// <summary>
/// One published build of the Steam transport mod: which packs it serves, and
/// the exact bytes that are it.
/// </summary>
/// <param name="Version">The mod release this build belongs to.</param>
/// <param name="FileName">The delivered filename, kept as the name in the instance.</param>
/// <param name="SizeBytes">What a complete download weighs.</param>
/// <param name="Sha256">What a correct download hashes to.</param>
/// <param name="CacheFileId">
/// The folder this build is cached under. Synthetic: upstream has no numeric
/// file id, so the release and the loader are encoded into one.
/// </param>
/// <param name="Loaders">The loaders that can load it.</param>
/// <param name="MinimumMinecraftVersion">The first Minecraft it declares.</param>
/// <param name="ExclusiveMaximumMinecraftVersion">The first it no longer declares.</param>
public sealed record SteamTransportBuild(
    string Version,
    string FileName,
    long SizeBytes,
    string Sha256,
    long CacheFileId,
    IReadOnlyList<PackLoaderKind> Loaders,
    IReadOnlyList<int> MinimumMinecraftVersion,
    IReadOnlyList<int> ExclusiveMaximumMinecraftVersion,
    string? AuthorFileName = null,
    string? DeliveryTag = null,
    bool NativeLanUiSuppressed = false,
    Uri? VerifiedDownloadUri = null,
    string? UpstreamSourceCommit = null,
    bool PreservesWorldSettingsOnShare = false)
{
    /// <summary>The original author artifact or source revision, never a delivery fallback.</summary>
    public Uri UpstreamDownloadUri { get; } = new(
        UpstreamSourceCommit is not null
            ? $"https://github.com/Kamilhik/e4steam/tree/{UpstreamSourceCommit}"
            : $"https://github.com/Kamilhik/e4steam/releases/download/v{Version}/{AuthorFileName ?? FileName}",
        UriKind.Absolute);

    /// <summary>The single shared delivery source in the launcher's own repository.</summary>
    public IReadOnlyList<Uri> DownloadUris { get; } = Array.AsReadOnly<Uri>(
    [
        VerifiedDownloadUri ?? new Uri(
            $"https://github.com/MarkZamore/e4steam/releases/download/{DeliveryTag ?? "v" + Version}/{FileName}",
            UriKind.Absolute)
    ]);
}

/// <summary>
/// The selected native-UI delivery profile for each loader and Minecraft branch.
/// Metadata and SHA-256 pins belong to each artifact independently; modern
/// releases do not imply that a new retro JAR was published alongside them.
/// </summary>
public static class SteamTransportCatalog
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RuntimeComponentSnapshot, SteamComponentCatalogSnapshot> Parsed = new();
    private static readonly AsyncLocal<SteamComponentCatalogSnapshot?> Override = new();

    internal static SteamComponentCatalogSnapshot Current => Override.Value ??
        Parsed.GetValue(RuntimeComponentContext.Current, snapshot => SteamComponentCatalog.Parse(snapshot.SteamCatalogJson));

    internal static void Activate(SteamComponentCatalogSnapshot snapshot) => Override.Value = snapshot;

    public static string Version => Current.Builds.MaxBy(build => ReleaseVersion(build.Version))!.Version;

    private static System.Version ReleaseVersion(string value) =>
        System.Version.TryParse(value.Split('-')[0], out var version) ? version : new System.Version(0, 0);

    public static IReadOnlyList<SteamTransportBuild> Builds => Current.Builds;
    internal static IReadOnlyList<ManagedComponentDescriptor> SupersededBuilds => Current.SupersededBuilds;

    /// <summary>
    /// The build for this pack, or null when no published build serves it.
    /// </summary>
    public static SteamTransportBuild? Find(PackRuntimeDescriptor? descriptor)
    {
        if (descriptor is null) return null;
        if (!TryParseVersion(descriptor.MinecraftVersion, out var version)) return null;
        var java8WorldSharing = PackManifestService.UsesJava8SteamWorldSharing(descriptor);

        foreach (var build in Builds)
        {
            if (build.PreservesWorldSettingsOnShare != java8WorldSharing) continue;
            if (!build.Loaders.Contains(descriptor.Loader.Type)) continue;
            if (Compare(version, build.MinimumMinecraftVersion) < 0) continue;
            if (Compare(version, build.ExclusiveMaximumMinecraftVersion) >= 0) continue;
            return build;
        }
        return null;
    }

    /// <summary>Whether any build at all declares this Minecraft.</summary>
    public static bool CoversMinecraftVersion(string? minecraftVersion)
    {
        if (!TryParseVersion(minecraftVersion, out var version)) return false;
        foreach (var build in Builds)
        {
            if (Compare(version, build.MinimumMinecraftVersion) >= 0 &&
                Compare(version, build.ExclusiveMaximumMinecraftVersion) < 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Every cache folder a current build uses, for sweeping the rest.</summary>
    public static IReadOnlyCollection<string> CacheFileIds =>
        Builds.Select(build => build.CacheFileId.ToString(CultureInfo.InvariantCulture)).ToArray();

    internal static bool TryParseVersion(string? value, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(value)) return false;

        var segments = value.Split('.', StringSplitOptions.TrimEntries);
        var parsed = new int[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            if (!int.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[index]))
            {
                return false;
            }
        }

        parts = parsed;
        return parts.Length > 0;
    }

    internal static int Compare(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var leftPart = index < left.Count ? left[index] : 0;
            var rightPart = index < right.Count ? right[index] : 0;
            if (leftPart != rightPart) return leftPart.CompareTo(rightPart);
        }
        return 0;
    }
}
