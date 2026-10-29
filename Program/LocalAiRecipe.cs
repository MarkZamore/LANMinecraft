using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

internal sealed record LocalAiModelReference(string Reference, string Repository, string Tag, string? RegistryHost)
{
    internal static LocalAiModelReference Parse(string value)
    {
        if (value.Length is < 3 or > 256 || value.Any(char.IsWhiteSpace) || value.Contains('\\') || value.Contains('%'))
            throw new InvalidDataException("Invalid local AI model reference.");
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || value[..colon].Contains(':') || !SafeSegment(value[(colon + 1)..], tag: true))
            throw new InvalidDataException("A local AI model reference must declare a repository and tag.");
        var parts = value[..colon].Split('/');
        if (parts.Length is < 1 or > 8 || parts.Any(part => !SafeSegment(part, tag: false)))
            throw new InvalidDataException("Unsafe local AI model repository.");
        string? host = null;
        if (parts.Length > 1 && parts[0].Contains('.'))
        {
            host = parts[0]; parts = parts[1..];
            if (Uri.CheckHostName(host) != UriHostNameType.Dns) throw new InvalidDataException("Invalid model registry host.");
        }
        var repository = parts.Length == 1 ? "library/" + parts[0] : string.Join('/', parts);
        return new(value, repository, value[(colon + 1)..], host);
    }
    private static bool SafeSegment(string value, bool tag)
    {
        if (value.Length is < 1 or > 128 || value.EndsWith('.') ||
            !Regex.IsMatch(value, tag ? @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z" : @"\A[a-z0-9][a-z0-9_.-]*\z")) return false;
        var stem = value.Split('.')[0];
        return !Regex.IsMatch(stem, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    internal string ManifestPath(Uri registry) => "manifests/" + registry.IdnHost.ToLowerInvariant() + "/" + Repository + "/" + Tag;
    internal Uri BlobUri(Uri registry, string hash) => new(registry, "v2/" + Repository + "/blobs/sha256:" + hash);
}

internal sealed record LocalAiRecipe(LocalAiPin Pin, LocalAiBlob[] Blobs, LocalAiModelReference Model,
    Uri Registry, string InstallDirectory);

/// <summary>Reads immutable provider recipe data owned and pinned by a pack.</summary>
internal static class LocalAiRecipeReader
{
    internal const int MaximumRecipeBytes = 128 * 1024;
    internal const int MaximumManifestBytes = 65536;

    internal static byte[] ReadBoundedFile(string file, int maximum)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximum) throw new InvalidDataException("Local AI metadata exceeds its byte limit.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Local AI metadata changed beyond its byte limit.");
        return bytes;
    }
    internal static JsonDocument ParseMetadata(byte[] bytes)
    {
        // File.ReadAllText historically accepted a UTF-8 BOM in pack metadata.
        // Recipe SHA verification still covers the original bytes, including it.
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 8 });
    }
    internal static LocalAiRecipe Read(string packDirectory, LocalAiRequirements requirements)
    {
        if (requirements.RecipeFile is null || requirements.RecipeSha256 is null)
            throw new InvalidDataException("The pack must supply recipeFile and recipeSha256 in launcher/local-ai.json before local AI preparation.");
        var file = LocalAiFiles.Under(packDirectory, requirements.RecipeFile);
        if (!File.Exists(file)) throw new InvalidDataException("The pinned local AI recipe is missing from the pack.");
        var bytes = ReadBoundedFile(file, MaximumRecipeBytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != requirements.RecipeSha256)
            throw new InvalidDataException("The local AI recipe differs from its descriptor SHA256.");
        try
        {
            using var document = ParseMetadata(bytes);
            var root = Object(document.RootElement, "schemaVersion", "provider", "runtime", "model");
            if (Integer(root, "schemaVersion") != 1 || Text(root, "provider", 32) != "ollama")
                throw new InvalidDataException("Unsupported local AI recipe provider or version.");
            var runtime = Object(root.GetProperty("runtime"), "version", "archiveUrl", "sizeBytes", "sha256", "installDirectory");
            var model = Object(root.GetProperty("model"), "reference", "registryUrl", "manifestSha256", "manifestBase64");
            var reference = LocalAiModelReference.Parse(Text(model, "reference", 256));
            if (reference.Reference != requirements.Model)
                throw new InvalidDataException("The descriptor model and recipe model reference differ.");
            var registry = Https(Text(model, "registryUrl", 2048), originOnly: true);
            if (reference.RegistryHost is not null && !reference.RegistryHost.Equals(registry.IdnHost, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The model reference registry differs from the pinned registry origin.");
            var install = Text(runtime, "installDirectory", 256); ValidateRelativePath(install);
            var base64 = Text(model, "manifestBase64", (MaximumManifestBytes + 2) / 3 * 4);
            var raw = Convert.FromBase64String(base64);
            if (Convert.ToBase64String(raw) != base64) throw new InvalidDataException("Model manifest Base64 must use canonical encoding.");
            var pin = new LocalAiPin(Text(runtime, "version", 64), Https(Text(runtime, "archiveUrl", 2048), originOnly: false),
                Integer(runtime, "sizeBytes"), Hash(runtime, "sha256"), Hash(model, "manifestSha256"), raw);
            var validated = ValidatePin(pin);
            ValidateRelativePath(reference.ManifestPath(registry));
            return new(validated.Pin, validated.Blobs, reference, registry, install);
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("Malformed pinned local AI recipe.", error); }
    }

    internal static (LocalAiPin Pin, LocalAiBlob[] Blobs) ValidatePin(LocalAiPin pin)
    {
        _ = Https(pin.ArchiveUri.AbsoluteUri, originOnly: false);
        if (pin.ArchiveSizeBytes <= 0 || !LocalAiFiles.IsHash(pin.ArchiveSha256) ||
            pin.Version.Length is < 1 or > 64 || !Regex.IsMatch(pin.Version, @"\A[A-Za-z0-9][A-Za-z0-9._+-]*\z") ||
            pin.ModelManifest.Length is < 1 or > MaximumManifestBytes || !LocalAiFiles.IsHash(pin.ModelManifestSha256) ||
            Convert.ToHexStringLower(SHA256.HashData(pin.ModelManifest)) != pin.ModelManifestSha256)
            throw new InvalidDataException("Invalid pinned local AI distribution.");
        try
        {
            using var document = JsonDocument.Parse(pin.ModelManifest, new JsonDocumentOptions { MaxDepth = 8 });
            var root = Object(document.RootElement, "schemaVersion", "mediaType", "config", "layers");
            if (Integer(root, "schemaVersion") != 2 || !root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Unsupported local AI model manifest.");
            if (root.TryGetProperty("mediaType", out _)) _ = Text(root, "mediaType", 256);
            var blobs = new[] { root.GetProperty("config") }.Concat(layers.EnumerateArray()).Select(item =>
            {
                var data = Object(item, "mediaType", "digest", "size");
                if (data.TryGetProperty("mediaType", out _)) _ = Text(data, "mediaType", 256);
                var digest = Text(data, "digest", 71); var size = Integer(data, "size");
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !LocalAiFiles.IsHash(digest[7..]) ||
                    digest[7..].Any(character => character is >= 'A' and <= 'F') || size <= 0)
                    throw new InvalidDataException("Invalid local AI model layer.");
                return new LocalAiBlob(digest[7..], size);
            }).ToArray();
            if (blobs.Length is < 2 or > 32 || blobs.Select(blob => blob.Sha256).Distinct().Count() != blobs.Length)
                throw new InvalidDataException("Invalid or duplicated local AI model layers.");
            var total = blobs.Aggregate(0L, (sum, blob) => checked(sum + blob.SizeBytes));
            _ = checked(pin.ArchiveSizeBytes + total + 64L * 1024 * 1024);
            return (pin with { ModelManifest = pin.ModelManifest.ToArray() }, blobs);
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("Malformed pinned local AI model manifest.", error); }
    }
    internal static JsonElement Object(JsonElement value, params string[] allowed)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(field => !allowed.Contains(field.Name, StringComparer.Ordinal) || !names.Add(field.Name)))
            throw new InvalidDataException("Unsupported or duplicate local AI metadata fields.");
        return value;
    }
    internal static string Text(JsonElement root, string name, int maximum)
        => root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 } text && text.Length <= maximum
            ? text : throw new InvalidDataException("Invalid local AI field: " + name);
    internal static long Integer(JsonElement root, string name)
        => root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var number)
            ? number : throw new InvalidDataException("Invalid local AI number: " + name);
    private static string Hash(JsonElement root, string name)
    {
        var value = Text(root, name, 64);
        if (!LocalAiFiles.IsHash(value) || value.Any(character => character is >= 'A' and <= 'F')) throw new InvalidDataException("Invalid local AI SHA256: " + name);
        return value;
    }
    internal static void ValidateRelativePath(string value)
    {
        if (value.Length is < 1 or > 256 || value.Contains('\\') || value.Contains(':') || value.StartsWith('/') ||
            value.Split('/').Any(segment => segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(char.IsControl) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(segment.Split('.')[0], @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Unsafe local AI relative path.");
    }
    private static Uri Https(string value, bool originOnly)
    {
        if (value.Any(char.IsControl) || value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback ||
            uri.HostNameType != UriHostNameType.Dns ||
            originOnly && (uri.AbsolutePath != "/" || uri.Query.Length != 0 || !uri.IsDefaultPort))
            throw new InvalidDataException("Local AI artifacts require an HTTPS URL; registries require a plain HTTPS origin.");
        return uri;
    }

    internal static bool DeclaresForgeMod(string metadata, string expected)
    {
        var declarations = new HashSet<string>(StringComparer.Ordinal); bool inMod = false, hasId = false;
        foreach (var statement in ModRuntimeMetadata.TomlStatements(metadata.TrimStart('\uFEFF')))
        {
            if (statement.StartsWith('['))
            {
                inMod = Regex.IsMatch(statement, "\\A\\[\\[\\s*(?:mods|\"mods\"|'mods')\\s*\\]\\]\\z");
                hasId = false; continue;
            }
            if (!inMod) continue;
            var equals = statement.IndexOf('='); if (equals <= 0) throw new InvalidDataException("Malformed mod metadata assignment.");
            if (statement[..equals].Trim().Trim('"', '\'') != "modId") continue;
            if (hasId) throw new InvalidDataException("Duplicate modId in Forge mod declaration.");
            hasId = true; var raw = statement[(equals + 1)..].Trim(); string? id = null;
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' && !raw.StartsWith("\"\"\"", StringComparison.Ordinal)) id = JsonSerializer.Deserialize<string>(raw);
            else if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'' && !raw.StartsWith("'''", StringComparison.Ordinal)) id = raw[1..^1];
            if (id is null || !Regex.IsMatch(id, @"\A[a-z][a-z0-9_]{1,63}\z") || !declarations.Add(id))
                throw new InvalidDataException("Invalid or duplicate Forge mod declaration ID.");
        }
        return declarations.Contains(expected);
    }
}
