using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft;

internal readonly record struct LoaderInstallationFile(string Path, string? ExpectedSha1);

/// <summary>Files produced by the Forge-family client installer and loaded by the game.</summary>
internal static class LoaderInstallationFiles
{
    private static readonly string[] ClientOutputKeys = ["PATCHED", "MC_EXTRA", "MC_SLIM", "MC_SRG"];

    internal static string GetInstallerPath(string sharedRoot, PackRuntimeDescriptor descriptor)
    {
        var (family, artifact, version) = InstallerIdentity(descriptor);
        return Path.Combine(sharedRoot, "installers", family, version, $"{artifact}-{version}-installer.jar");
    }

    internal static Uri GetInstallerUri(PackRuntimeDescriptor descriptor)
    {
        var (family, artifact, version) = InstallerIdentity(descriptor);
        var escapedVersion = Uri.EscapeDataString(version);
        var repository = family == "forge"
            ? "https://maven.minecraftforge.net/net/minecraftforge/forge/"
            : "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
        return new Uri(repository + $"{escapedVersion}/{artifact}-{escapedVersion}-installer.jar");
    }

    private static (string Family, string Artifact, string Version) InstallerIdentity(PackRuntimeDescriptor descriptor)
    {
        var version = descriptor.Loader.Version ?? throw new InvalidDataException("Missing installer version.");
        if (!IsSegment(version) || !IsSegment(descriptor.MinecraftVersion))
            throw new InvalidDataException("Invalid installer coordinates.");
        return descriptor.Loader.Type switch
        {
            PackLoaderKind.Forge => ("forge", "forge",
                version.StartsWith(descriptor.MinecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
                    ? version : descriptor.MinecraftVersion + "-" + version),
            PackLoaderKind.NeoForge => ("neoforge", "neoforge", version),
            _ => throw new InvalidDataException("This loader does not have a Forge-family installer.")
        };
    }

    /// <summary>Reads the client recipe from an installer whose distribution hash the caller has verified.</summary>
    internal static IReadOnlyList<LoaderInstallationFile> Read(string installerPath, string librariesRoot,
        PackRuntimeDescriptor descriptor, string? expectedProfileId = null)
    {
        try
        {
            return ReadCore(installerPath, librariesRoot, descriptor, expectedProfileId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Malformed loader installer recipe.", ex);
        }
    }

    private static IReadOnlyList<LoaderInstallationFile> ReadCore(string installerPath, string librariesRoot,
        PackRuntimeDescriptor descriptor, string? expectedProfileId)
    {
        using var zip = ZipFile.OpenRead(installerPath);
        using var installDocument = ReadJson(zip, "install_profile.json");
        var install = installDocument.RootElement;
        var legacy = install.TryGetProperty("install", out var details);
        var game = ReadString(legacy ? details : install, "minecraft");
        var installerProfile = ReadString(legacy ? details : install, legacy ? "target" : "version");
        var profileEntry = ReadString(install, "json")?.TrimStart('/');
        using var versionDocument = profileEntry is null ? null : ReadJson(zip, profileEntry);
        var profile = versionDocument?.RootElement ?? install.GetProperty("versionInfo");
        ValidateProfile(descriptor, profile, game, installerProfile, expectedProfileId);

        var (_, artifact, version) = InstallerIdentity(descriptor);
        var group = descriptor.Loader.Type == PackLoaderKind.Forge ? "net.minecraftforge" : "net.neoforged";
        var core = group + ":" + artifact + ":" + version;
        var files = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var clientLibraries = ReadArray(profile, "libraries");
        var coreLibraries = ReadArray(install, "libraries").Concat(clientLibraries)
            .Where(library => ReadString(library, "name") is { } name &&
                (name == core + ":universal" || name == core));
        foreach (var library in coreLibraries)
        {
            var name = ReadString(library, "name")!;
            if (!TryResolveCoordinate(name, librariesRoot, out var path, out var relative))
                throw new InvalidDataException("Invalid core installer library coordinate.");
            var publishedHash = library.TryGetProperty("downloads", out var downloads) &&
                downloads.TryGetProperty("artifact", out var metadata)
                ? ParseSha1(ReadString(metadata, "sha1"))
                : null;
            var embedded = zip.GetEntry("maven/" + relative);
            if (embedded is null && legacy && name == core && ReadString(details, "path") == core &&
                ReadString(details, "filePath") is { } legacyEntry)
                embedded = zip.GetEntry(legacyEntry);
            Add(files, path, ExpectedHash(embedded, publishedHash));
        }

        if (files.Count == 0) throw new InvalidDataException("Installer does not declare its core client library.");

        var launchCoordinates = clientLibraries.Select(library => ReadString(library, "name"))
            .ToHashSet(StringComparer.Ordinal);
        if (install.TryGetProperty("data", out var data))
        {
            foreach (var key in ClientOutputKeys)
            {
                var value = ClientData(data, key);
                if (string.IsNullOrWhiteSpace(value)) continue;
                var pinnedHash = ParseSha1(ClientData(data, key + "_SHA"));
                if (key is "MC_SLIM" or "MC_SRG" &&
                    pinnedHash is null && !launchCoordinates.Contains(value.Trim('[', ']'))) continue;
                if (!TryResolveCoordinate(value, librariesRoot, out var path, out _))
                    throw new InvalidDataException("Invalid installer client output coordinate: " + key);
                Add(files, path, pinnedHash);
            }
        }

        var processors = ReadArray(install, "processors");
        var hasClientProcessors = false;
        foreach (var processor in processors)
        {
            if (processor.TryGetProperty("sides", out var sides) &&
                !sides.EnumerateArray().Any(side => side.GetString() == "client"))
                continue;
            hasClientProcessors = true;
            if (!processor.TryGetProperty("outputs", out var outputs)) continue;
            foreach (var output in outputs.EnumerateObject())
            {
                if (!TryResolveCoordinate(ResolveDataReference(data, output.Name), librariesRoot, out var path, out _))
                    throw new InvalidDataException("Invalid installer processor output coordinate.");
                var expected = ResolveDataReference(data, output.Value.GetString());
                Add(files, path, ParseSha1(expected) ??
                    throw new InvalidDataException("Invalid checksum for a client installer output."));
            }
        }

        // A new processing recipe with no known client-jar path cannot prove completeness.
        if (hasClientProcessors && !TryResolveCoordinate(ClientData(data, "PATCHED"), librariesRoot, out _, out _))
            throw new InvalidDataException("Installer does not declare a supported patched client.");
        return files.Select(file => new LoaderInstallationFile(file.Key, file.Value)).ToArray();
    }

    private static void ValidateProfile(PackRuntimeDescriptor descriptor, JsonElement profile, string? game,
        string? installerProfile, string? expectedProfileId)
    {
        var version = descriptor.MinecraftVersion;
        var id = ReadString(profile, "id");
        var (_, _, artifactVersion) = InstallerIdentity(descriptor);
        var expectedId = descriptor.Loader.Type == PackLoaderKind.Forge
            ? version + "-forge-" + artifactVersion[(version.Length + 1)..]
            : "neoforge-" + artifactVersion;
        var alternateId = version + "-forge" + artifactVersion;
        if (game != version || ReadString(profile, "inheritsFrom") != version ||
            id is null || installerProfile != id ||
            (!string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase) &&
             (descriptor.Loader.Type != PackLoaderKind.Forge ||
              !string.Equals(id, alternateId, StringComparison.OrdinalIgnoreCase))) ||
            (expectedProfileId is not null && !string.Equals(id, expectedProfileId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Installer profile does not match the requested Minecraft and loader.");
    }

    private static JsonDocument ReadJson(ZipArchive zip, string name)
    {
        const int maximumMetadataBytes = 4 * 1024 * 1024;
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Missing installer metadata: " + name);
        if (entry.Length > maximumMetadataBytes) throw new InvalidDataException("Installer metadata exceeds its size limit.");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static JsonElement[] ReadArray(JsonElement value, string name) =>
        value.TryGetProperty(name, out var children) ? children.EnumerateArray().ToArray() : [];

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child.GetString() : null;

    private static string? ClientData(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var sides) ? ReadString(sides, "client") : null;

    private static string? ResolveDataReference(JsonElement data, string? value) =>
        value is { Length: > 2 } && value[0] == '{' && value[^1] == '}'
            ? ClientData(data, value[1..^1]) : value;

    private static string? ParseSha1(string? value)
    {
        if (value is null) return null;
        value = value.Trim('\'');
        if (value.Length != 40 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("Malformed installer artifact SHA-1.");
        return value.ToLowerInvariant();
    }

    private static string? ExpectedHash(ZipArchiveEntry? embedded, string? published)
    {
        if (embedded is null) return published;
        using var bytes = embedded.Open();
        var hash = Convert.ToHexStringLower(SHA1.HashData(bytes));
        if (published is not null && !string.Equals(published, hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Embedded installer library differs from its published checksum.");
        return hash;
    }

    private static void Add(Dictionary<string, string?> files, string path, string? hash)
    {
        if (files.TryGetValue(path, out var previous) && previous is not null)
        {
            if (hash is not null && !string.Equals(hash, previous, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Conflicting checksums for an installer artifact.");
            return;
        }
        files[path] = hash;
    }

    private static bool TryResolveCoordinate(string? value, string root, out string path, out string relative)
    {
        path = relative = "";
        if (value is null) return false;
        var extensionSplit = value.Trim('[', ']').Split('@');
        var parts = extensionSplit[0].Split(':');
        var extension = extensionSplit.Length == 2 ? extensionSplit[1] : "jar";
        if (extensionSplit.Length > 2 || parts.Length is < 3 or > 4 ||
            !IsSegment(extension) || !parts.Skip(1).All(IsSegment)) return false;
        var group = parts[0].Split('.');
        if (!group.All(IsSegment)) return false;
        var classifier = parts.Length == 4 ? "-" + parts[3] : "";
        relative = string.Join('/', group) + "/" + parts[1] + "/" + parts[2] +
            "/" + parts[1] + "-" + parts[2] + classifier + "." + extension;
        path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return true;
    }

    private static bool IsSegment(string value) =>
        value.Length > 0 && char.IsAsciiLetterOrDigit(value[0]) && value[^1] != '.' &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');
}
