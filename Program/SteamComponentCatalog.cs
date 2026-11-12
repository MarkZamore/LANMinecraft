using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>A verified, immutable selection shared by one launcher session.</summary>
internal sealed record SteamComponentCatalogSnapshot(
    string Profile, string SourceCommit, IReadOnlyList<SteamTransportBuild> Builds,
    IReadOnlyList<ManagedComponentDescriptor> SupersededBuilds);

/// <summary>Data validation, not a runtime mod/configuration patcher.</summary>
internal static class SteamComponentCatalog
{
    internal const int MaximumDocumentBytes = 256 * 1024;
    internal const string UiCapability = "native-lan-settings-ui-hidden-or-absent";
    private static readonly string[] RequiredCapabilities =
    [UiCapability, "modern-configured-online-mode-at-native-publish", "author-steam-authentication-retained"];

    internal static string EmbeddedJson() => ReadEmbedded("Minecraft.Steam.components.json");

    /// <summary>Historical provenance/fixture input, never a production fallback.</summary>
    internal static string HistoricalV2Json() => ReadEmbedded("Minecraft.Steam.components-v2.json");

    private static string ReadEmbedded(string resource)
    {
        using var stream = typeof(SteamComponentCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("Missing embedded Steam component catalog.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static SteamComponentCatalogSnapshot Parse(string json)
    {
        Require(System.Text.Encoding.UTF8.GetByteCount(json) <= MaximumDocumentBytes, "Oversized Steam component index.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        UniqueProperties(root);
        Require(root.GetProperty("schemaVersion").GetInt32() == 2 && Text(root, "componentId") == "e4steam" &&
                Text(root, "publisher") == "MarkZamore", "Unknown Steam component publisher/schema.");
        var profile = Text(root, "profile");
        Require(Regex.IsMatch(profile, "^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.CultureInvariant), "Invalid Steam profile.");
        var commit = Text(root, "sourceCommit");
        Require(Regex.IsMatch(commit, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant), "Invalid Steam source revision.");
        var capabilities = root.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ToArray();
        Require(capabilities.Length <= 32 && capabilities.Distinct(StringComparer.Ordinal).Count() == capabilities.Length &&
                RequiredCapabilities.All(capability => capabilities.Contains(capability, StringComparer.Ordinal)),
            "Steam component index lacks the reviewed native UI/authentication capabilities.");
        var rows = root.GetProperty("builds").EnumerateArray().ToArray();
        Require(rows.Length is > 0 and <= 128, "Invalid Steam build count.");
        var java8Rows = root.TryGetProperty("java8Builds", out var java8Document)
            ? java8Document.EnumerateArray().ToArray() : [];
        Require(java8Rows.Length <= 2, "Invalid Java 8 Steam build count.");
        var builds = new List<SteamTransportBuild>();
        foreach (var (row, separateJava8) in rows.Select(row => (row, false)).Concat(java8Rows.Select(row => (row, true))))
        {
            var version = Text(row, "version");
            Require(Regex.IsMatch(version, "^[0-9]+(?:\\.[0-9]+){1,3}(?:[-.][a-zA-Z0-9.-]+)?$", RegexOptions.CultureInvariant), "Invalid Steam release version.");
            var file = Text(row, "fileName");
            var size = row.GetProperty("sizeBytes").GetInt64();
            var digest = Text(row, "sha256");
            ValidatePin(file, size, digest);
            Require(Text(row, "authorVersion") == version && Text(row, "profile") == profile,
                "Inconsistent Steam release/profile provenance.");
            var tag = Text(row, "deliveryTag");
            var uri = OwnReleaseUri(Text(row, "downloadUrl"), file, tag);
            var loaders = row.GetProperty("loaders").EnumerateArray().Select(value =>
            {
                Require(Enum.TryParse<PackLoaderKind>(value.GetString(), false, out var loader) &&
                        loader is PackLoaderKind.Forge or PackLoaderKind.NeoForge or PackLoaderKind.Fabric or PackLoaderKind.Quilt or PackLoaderKind.LegacyFabric,
                    "Unsupported Steam loader.");
                return loader;
            }).ToArray();
            Require(loaders.Length is > 0 and <= 4 && loaders.Distinct().Count() == loaders.Length, "Duplicate Steam loader.");
            var minimum = VersionParts(row.GetProperty("minimumMinecraftVersion"));
            var maximum = VersionParts(row.GetProperty("exclusiveMaximumMinecraftVersion"));
            Require(SteamTransportCatalog.Compare(minimum, maximum) < 0, "Empty Steam compatibility range.");
            var derivation = row.GetProperty("derivation");
            var kind = Text(derivation, "type");
            string? authorFile = null;
            string? upstreamSource = null;
            var java8WorldSharing = kind == "source-built-java8-forge-world-sharing";
            Require(!separateJava8 || java8WorldSharing, "An optional Java 8 build requires reviewed Forge provenance.");
            if (java8WorldSharing || kind == "source-built-legacy-port")
            {
                var verifiedPlatform = java8WorldSharing
                    ? loaders.SequenceEqual([PackLoaderKind.Forge]) &&
                        (minimum.SequenceEqual([1, 12, 2]) && maximum.SequenceEqual([1, 12, 3]) && Text(row, "metadataPath") == "mcmod.info" ||
                         minimum.SequenceEqual([1, 16, 5]) && maximum.SequenceEqual([1, 16, 6]) && Text(row, "metadataPath") == "META-INF/mods.toml")
                    : loaders.SequenceEqual([PackLoaderKind.LegacyFabric]) && minimum.SequenceEqual([1, 6, 4]) &&
                        maximum.SequenceEqual([1, 6, 5]) && Text(row, "metadataPath") == "fabric.mod.json";
                Require(verifiedPlatform &&
                        Regex.IsMatch(Text(derivation, "sourceCommit"), "^[a-f0-9]{40}$", RegexOptions.CultureInvariant),
                    "Unreviewed legacy Steam platform/source revision.");
                Require(!row.TryGetProperty("authorFileName", out _) && !row.TryGetProperty("authorSha256", out _) &&
                        !row.TryGetProperty("authorSizeBytes", out _),
                    "A source-built legacy port must not claim an original author artifact.");
                upstreamSource = Text(derivation, "upstreamSourceCommit");
                Require(Regex.IsMatch(upstreamSource, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant),
                    "Missing legacy Steam upstream source revision.");
                ValidateSourceClassPins(derivation.GetProperty("sourceBuiltClassSha256"));
            }
            else
            {
                Require(!loaders.Contains(PackLoaderKind.LegacyFabric), "Legacy Steam requires reviewed source-port provenance.");
                authorFile = Text(row, "authorFileName");
                ValidatePin(authorFile, row.GetProperty("authorSizeBytes").GetInt64(), Text(row, "authorSha256"));
                if (kind == "byte-identical-author-artifact")
                    Require(derivation.GetProperty("uiAbsent").GetBoolean() && authorFile == file &&
                        Text(row, "authorSha256") == digest && row.GetProperty("authorSizeBytes").GetInt64() == size,
                        "Unreviewed author Steam UI artifact.");
                else Require(kind == "source-built-native-ui-and-lan-online-mode" &&
                        Regex.IsMatch(Text(derivation, "sourceCommit"), "^[a-f0-9]{40}$", RegexOptions.CultureInvariant) &&
                        derivation.GetProperty("allOtherEntriesByteIdentical").GetBoolean(),
                    "Unreviewed Steam derivative.");
            }
            builds.Add(new SteamTransportBuild(version, file, size, digest, CacheId(file, size, digest),
                Array.AsReadOnly(loaders), Array.AsReadOnly(minimum), Array.AsReadOnly(maximum), authorFile, tag,
                NativeLanUiSuppressed: true, VerifiedDownloadUri: uri, UpstreamSourceCommit: upstreamSource,
                PreservesWorldSettingsOnShare: java8WorldSharing));
        }
        Require(builds.Select(build => build.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == builds.Count,
            "Duplicate Steam delivery filename.");
        Require(builds.GroupBy(build => build.CacheFileId).All(group => group.Select(build => build.Sha256).Distinct().Count() == 1),
            "Conflicting Steam cache identities.");
        Require(builds.Where(build => build.PreservesWorldSettingsOnShare)
            .GroupBy(build => string.Join('.', build.MinimumMinecraftVersion)).All(group => group.Count() == 1),
            "Ambiguous Java 8 Steam world sharing build.");
        ValidateCoverage(builds.Where(build => !build.PreservesWorldSettingsOnShare).ToArray());
        return WithHistory(new(profile, commit, builds.AsReadOnly(), []), []);
    }

    internal static SteamComponentCatalogSnapshot WithHistory(SteamComponentCatalogSnapshot snapshot,
        IEnumerable<ManagedComponentDescriptor> known)
    {
        var retired = SteamTransportCatalog.HistoricalAuthorBuilds
            .Concat(SteamTransportCatalog.LegacyV1Builds.Select(ManagedComponentService.DescriptorFor))
            .Concat(known)
            .DistinctBy(row => (row.FileName.ToLowerInvariant(), row.SizeBytes, row.Sha256.ToLowerInvariant()))
            .ToArray();
        Require(retired.Length <= 1024, "Steam component history exceeds its safety limit.");
        return snapshot with { SupersededBuilds = Array.AsReadOnly(retired) };
    }

    internal static void ValidatePin(string name, long size, string digest)
    {
        Require(Regex.IsMatch(name, "^e4steam-[a-zA-Z0-9._+\\-]+\\.jar$", RegexOptions.CultureInvariant) &&
                size is > 0 and <= 128 * 1024 * 1024 &&
                Regex.IsMatch(digest, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant), "Invalid Steam artifact pin.");
    }

    internal static Uri OwnReleaseUri(string value, string name, string? tag = null)
    {
        Require(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
                uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0,
            "Steam delivery must use an immutable publisher release URL.");
        var parts = uri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        Require(parts.Length == 6 && parts[0] == "MarkZamore" && parts[1] == "e4steam" &&
                parts[2] == "releases" && parts[3] == "download" && parts[5] == name &&
                Regex.IsMatch(parts[4], "^[a-zA-Z0-9][a-zA-Z0-9._+\\-]{0,99}$", RegexOptions.CultureInvariant) &&
                (tag is null || parts[4] == tag), "Steam delivery URL escapes its publisher/tag/filename.");
        return uri;
    }

    internal static long CacheId(string file, long size, string digest) =>
        SteamTransportCatalog.LegacyV1Builds.FirstOrDefault(build => build.FileName == file &&
            build.SizeBytes == size && build.Sha256 == digest)?.CacheFileId ??
        long.Parse(digest.AsSpan(0, 15), NumberStyles.HexNumber, CultureInfo.InvariantCulture) + 1;

    private static int[] VersionParts(JsonElement value)
    {
        var parts = value.EnumerateArray().Select(part => part.GetInt32()).ToArray();
        Require(parts.Length is > 0 and <= 4 && parts.All(part => part is >= 0 and <= 9999), "Invalid Steam Minecraft version.");
        return parts;
    }

    private static void ValidateCoverage(IReadOnlyList<SteamTransportBuild> builds)
    {
        Require(builds.Any(build => build.Loaders.Contains(PackLoaderKind.LegacyFabric) &&
                build.MinimumMinecraftVersion.SequenceEqual([1, 6, 4]) &&
                build.ExclusiveMaximumMinecraftVersion.SequenceEqual([1, 6, 5])),
            "Steam component index removes reviewed Legacy Fabric 1.6.4 coverage.");
        foreach (var loader in new[] { PackLoaderKind.Forge, PackLoaderKind.NeoForge, PackLoaderKind.Fabric, PackLoaderKind.Quilt, PackLoaderKind.LegacyFabric })
        {
            var selected = builds.Where(build => build.Loaders.Contains(loader))
                .OrderBy(build => build.MinimumMinecraftVersion, Comparer<IReadOnlyList<int>>.Create(SteamTransportCatalog.Compare)).ToArray();
            for (var index = 1; index < selected.Length; index++)
                Require(SteamTransportCatalog.Compare(selected[index - 1].ExclusiveMaximumMinecraftVersion,
                    selected[index].MinimumMinecraftVersion) <= 0, "Ambiguous Steam compatibility ranges.");
            foreach (var baseline in SteamTransportCatalog.LegacyV1Builds.Where(build => build.Loaders.Contains(loader)))
            {
                var cursor = baseline.MinimumMinecraftVersion;
                foreach (var build in selected)
                {
                    if (SteamTransportCatalog.Compare(build.ExclusiveMaximumMinecraftVersion, cursor) <= 0) continue;
                    if (SteamTransportCatalog.Compare(build.MinimumMinecraftVersion, cursor) > 0) break;
                    cursor = build.ExclusiveMaximumMinecraftVersion;
                    if (SteamTransportCatalog.Compare(cursor, baseline.ExclusiveMaximumMinecraftVersion) >= 0) break;
                }
                Require(SteamTransportCatalog.Compare(cursor, baseline.ExclusiveMaximumMinecraftVersion) >= 0,
                    "Steam component index removes existing Minecraft/loader coverage.");
            }
        }
    }

    private static void ValidateSourceClassPins(JsonElement pins)
    {
        Require(pins.ValueKind == JsonValueKind.Object, "Legacy Steam source class pins must be an object.");
        var entries = pins.EnumerateObject().ToArray();
        Require(entries.Length is > 0 and <= 512 && entries.All(entry =>
                Regex.IsMatch(entry.Name, "^link/e4steam/[A-Za-z0-9_$/]+\\.class$", RegexOptions.CultureInvariant) &&
                entry.Value.ValueKind == JsonValueKind.String &&
                Regex.IsMatch(entry.Value.GetString()!, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)),
            "Invalid or absent legacy Steam source class pins.");
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing Steam catalog text: " + name);

    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), "Duplicate Steam catalog JSON property.");
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
