using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Minecraft;

/// <summary>Java selections from the immutable component snapshot used by this launch.</summary>
internal static class JavaRuntimeCatalog
{
    private static readonly ConditionalWeakTable<RuntimeComponentSnapshot, Catalog> Parsed = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };
    private static Catalog Current => Parsed.GetValue(RuntimeComponentContext.Current,
        snapshot => Parse(snapshot.JavaCatalogJson));

    public static int DefaultMajorVersion => Current.DefaultMajorVersion;
    public static IReadOnlyList<JavaRuntimePin> Releases => Current.Releases;
    public static JavaRuntimePin? ForMajorVersion(int majorVersion) =>
        Releases.FirstOrDefault(release => release.MajorVersion == majorVersion);

    public static JavaRuntimePin RequiredFor(PackRuntimeDescriptor? descriptor)
    {
        var major = descriptor?.JavaMajorVersion ?? MajorVersionFor(descriptor?.MinecraftVersion);
        return ForMajorVersion(major)
            ?? throw new InvalidDataException($"The component catalog has no Java runtime for feature release {major}.");
    }

    internal static int MajorVersionFor(string? minecraftVersion)
    {
        var catalog = Current;
        if (!SteamTransportCatalog.TryParseVersion(minecraftVersion, out var version))
            return catalog.DefaultMajorVersion;
        foreach (var rule in catalog.Rules)
            if (SteamTransportCatalog.Compare(version, rule.MinimumMinecraftVersion) >= 0 &&
                (rule.ExclusiveMaximumMinecraftVersion is null ||
                 SteamTransportCatalog.Compare(version, rule.ExclusiveMaximumMinecraftVersion) < 0))
                return rule.MajorVersion;
        return catalog.DefaultMajorVersion;
    }

    public static IReadOnlyCollection<string> InstallDirectoryNames =>
        Releases.Select(release => release.InstallDirectoryName).ToArray();
    internal static IReadOnlyCollection<string> LegacyInstallDirectoryNames =>
        Releases.Select(release => release.LegacyInstallDirectoryName).ToArray();
    public static IReadOnlyCollection<string> CacheDirectoryNames =>
        Releases.Select(release => release.RuntimeId.Replace('+', '_')).ToArray();

    internal static bool SupportsWorldSharing(PackRuntimeDescriptor descriptor) =>
        Current.WorldSharingRules.Any(rule =>
            rule.Loader == descriptor.Loader.Type.ToString() && rule.MinecraftVersion == descriptor.MinecraftVersion &&
            rule.JavaMajorVersion == RequiredFor(descriptor).MajorVersion);

    internal static bool UseGameJavaForInstaller(PackRuntimeDescriptor descriptor) =>
        Current.InstallerJavaRules.Any(rule =>
            rule.Loader == descriptor.Loader.Type.ToString() && rule.MinecraftVersion == descriptor.MinecraftVersion &&
            rule.LoaderVersions.Contains(descriptor.Loader.Version, StringComparer.Ordinal) &&
            rule.JavaMajorVersion == RequiredFor(descriptor).MajorVersion);

    internal static void ValidateJson(string json) => _ = Parse(json);

    private static Catalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty Java component catalog.");
        if (catalog.SchemaVersion != 1 || catalog.Releases is not { Count: > 0 and <= 64 } ||
            catalog.Rules is not { Count: > 0 and <= 128 } ||
            catalog.WorldSharingRules is not { Count: <= 128 } ||
            catalog.InstallerJavaRules is not { Count: <= 128 } ||
            catalog.Releases.Any(pin => pin is null) ||
            catalog.Releases.Select(pin => pin.MajorVersion).Distinct().Count() != catalog.Releases.Count ||
            catalog.Releases.Select(pin => pin.RuntimeId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Releases.Count ||
            catalog.Releases.All(pin => pin.MajorVersion != catalog.DefaultMajorVersion))
            throw new InvalidDataException("Invalid Java component catalog schema, runtimes or default.");
        foreach (var pin in catalog.Releases)
        {
            try { _ = PortableJavaRuntimeService.ValidatePin(pin); }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Java component catalog contains an invalid runtime pin.", exception);
            }
            if (pin.MajorVersion <= 0 || pin.RequiredFreeSpaceBytes < pin.ArchiveSizeBytes ||
                pin.DownloadUris.Any(uri => !IsOfficialSource(uri)))
                throw new InvalidDataException("Java component catalog contains an invalid runtime/source.");
        }
        foreach (var rule in catalog.Rules)
            if (rule is null || !ValidVersion(rule.MinimumMinecraftVersion) ||
                rule.ExclusiveMaximumMinecraftVersion is { } maximum &&
                    (!ValidVersion(maximum) || SteamTransportCatalog.Compare(rule.MinimumMinecraftVersion, maximum) >= 0) ||
                catalog.Releases.All(pin => pin.MajorVersion != rule.MajorVersion))
                throw new InvalidDataException("Invalid Java compatibility rule.");
        foreach (var rule in catalog.WorldSharingRules)
            if (rule is null || !Enum.TryParse<PackLoaderKind>(rule.Loader, false, out var loader) ||
                loader is PackLoaderKind.Native or PackLoaderKind.Vanilla ||
                !SteamTransportCatalog.TryParseVersion(rule.MinecraftVersion, out _) ||
                catalog.Releases.All(pin => pin.MajorVersion != rule.JavaMajorVersion))
                throw new InvalidDataException("Invalid world-sharing compatibility rule.");
        foreach (var rule in catalog.InstallerJavaRules)
            if (rule is null || !Enum.TryParse<PackLoaderKind>(rule.Loader, false, out var loader) ||
                loader is PackLoaderKind.Native or PackLoaderKind.Vanilla ||
                !SteamTransportCatalog.TryParseVersion(rule.MinecraftVersion, out _) ||
                rule.LoaderVersions is not { Length: > 0 and <= 32 } ||
                rule.LoaderVersions.Any(string.IsNullOrWhiteSpace) ||
                catalog.Releases.All(pin => pin.MajorVersion != rule.JavaMajorVersion))
                throw new InvalidDataException("Invalid installer Java compatibility rule.");
        var ordered = catalog.Rules.OrderBy(rule => rule.MinimumMinecraftVersion,
            Comparer<int[]>.Create(SteamTransportCatalog.Compare)).ToArray();
        for (var index = 1; index < ordered.Length; index++)
            if (ordered[index - 1].ExclusiveMaximumMinecraftVersion is not { } previousEnd ||
                SteamTransportCatalog.Compare(previousEnd, ordered[index].MinimumMinecraftVersion) > 0)
                throw new InvalidDataException("Ambiguous Java compatibility rules.");
        return catalog;
    }

    private static bool ValidVersion(int[]? version) =>
        version is { Length: > 0 and <= 4 } && version.All(part => part >= 0);

    private static bool IsOfficialSource(Uri uri) =>
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/adoptium/", StringComparison.Ordinal) &&
            uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal) ||
         uri.Host == "api.adoptium.net" && uri.AbsolutePath.StartsWith("/v3/binary/version/", StringComparison.Ordinal));

    private sealed record Catalog(int SchemaVersion, int DefaultMajorVersion,
        IReadOnlyList<JavaRuntimePin> Releases, IReadOnlyList<JavaRule> Rules,
        IReadOnlyList<WorldSharingRule> WorldSharingRules, IReadOnlyList<InstallerJavaRule> InstallerJavaRules);
    private sealed record JavaRule(int[] MinimumMinecraftVersion, int[]? ExclusiveMaximumMinecraftVersion, int MajorVersion);
    private sealed record WorldSharingRule(string Loader, string MinecraftVersion, int JavaMajorVersion);
    private sealed record InstallerJavaRule(string Loader, string MinecraftVersion, string[] LoaderVersions, int JavaMajorVersion);
}
