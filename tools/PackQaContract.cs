using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Minecraft;

namespace LauncherQa;

// QA fixture choices belong to the pack's source repository. The shared runners
// validate this document; they neither select content nor rewrite game settings.
internal sealed record PackQaContract(
    string SourceFile, string Sha256, string PackId, string PackRelativePath, PackSyncSource? Source,
    string MinecraftVersion, string LoaderType, string LoaderVersion, int DefaultHeapMiB,
    string QaPlayerName, string QaWorldPrefix, IReadOnlyList<RetiredHelperContract> RetiredHelpers)
{
    public static PackQaContract Load(string path)
    {
        var file = Path.GetFullPath(path);
        for (string? current = file; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("QA contract paths cannot pass through links.");
        if (new FileInfo(file).Length is <= 0 or > 65536)
            throw new InvalidDataException("The pack-owned QA contract must be 1..65536 bytes.");
        var bytes = File.ReadAllBytes(file);
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var root = WithOptional(document.RootElement, ["schemaVersion", "pack", "runtime", "qa"], ["retiredHelpers"]);
        if (Integer(root, "schemaVersion") != 1) throw new InvalidDataException("Unsupported pack QA contract schema.");
        var pack = WithOptional(root.GetProperty("pack"), ["relativePath"], ["id", "source"]);
        pack.TryGetProperty("source", out var source);
        var runtime = Object(root.GetProperty("runtime"), "minecraftVersion", "loader");
        var loader = Object(runtime.GetProperty("loader"), "type", "version");
        var qa = Object(root.GetProperty("qa"), "heapMiB", "playerName", "worldPrefix");
        var id = pack.TryGetProperty("id", out _) ? Text(pack, "id", "^[a-z0-9][a-z0-9._-]{0,127}$") : "local-qa";
        var relative = Text(pack, "relativePath");
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part.Length == 0 ||
                part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("The QA pack path must be a safe relative folder.");
        PackSyncSource? syncSource = null;
        if (source.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            Object(source, "owner", "repo", "tag");
            var owner = Text(source, "owner", "^[A-Za-z0-9][A-Za-z0-9-]{0,38}$");
            var repository = Text(source, "repo", "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");
            var tag = Text(source, "tag", "^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$");
            if (tag.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
                throw new InvalidDataException("The QA source tag is unsafe.");
            syncSource = new(owner, repository, tag);
        }
        var minecraft = Text(runtime, "minecraftVersion", "^[0-9][A-Za-z0-9._+-]{0,63}$");
        var loaderType = Text(loader, "type", "^[a-z][a-z0-9-]{0,31}$");
        var loaderVersion = Text(loader, "version", "^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$");
        var heap = Integer(qa, "heapMiB");
        if (heap < 512) throw new InvalidDataException("QA heap must be at least 512 MiB.");
        var player = Text(qa, "playerName", "^[A-Za-z0-9_]{3,16}$");
        var prefix = Text(qa, "worldPrefix", "^[A-Za-z][A-Za-z0-9]{2,15}$");
        if (!prefix.EndsWith("Qa", StringComparison.Ordinal)) throw new InvalidDataException("The disposable world prefix must end in Qa.");
        root.TryGetProperty("retiredHelpers", out var array);
        if (array.ValueKind != JsonValueKind.Undefined && (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 32))
            throw new InvalidDataException("QA retiredHelpers must be an array of at most 32 declarations.");
        var helpers = new List<RetiredHelperContract>();
        foreach (var item in array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [])
        {
            var helper = Object(item, "filePrefix", "expectedCount");
            var filePrefix = Text(helper, "filePrefix", "^mods/[A-Za-z0-9][A-Za-z0-9._-]{0,127}-$");
            var count = Integer(helper, "expectedCount");
            if (count is < 1 or > 32 || helpers.Any(existing => filePrefix.StartsWith(existing.FilePrefix, StringComparison.OrdinalIgnoreCase) ||
                    existing.FilePrefix.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("QA retired helper declarations overlap or have an invalid count.");
            helpers.Add(new RetiredHelperContract(filePrefix, count));
        }
        return new(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), id, relative, syncSource,
            minecraft, loaderType, loaderVersion, heap, player, prefix, helpers.AsReadOnly());
    }

    private static JsonElement Object(JsonElement value, params string[] properties)
        => WithOptional(value, properties, []);

    private static JsonElement WithOptional(JsonElement value, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("QA contract object expected.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if ((!required.Contains(property.Name, StringComparer.Ordinal) && !optional.Contains(property.Name, StringComparer.Ordinal)) || !names.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate QA contract property: " + property.Name);
        if (required.Any(name => !names.Contains(name))) throw new InvalidDataException("Incomplete QA contract object.");
        return value;
    }

    private static string Text(JsonElement value, string name, string? pattern = null)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String) throw new InvalidDataException("QA contract text expected: " + name);
        var text = property.GetString()!;
        if (text.Length is < 1 or > 256 || text != text.Trim() || text.Any(char.IsControl) ||
            pattern is not null && !Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid QA contract value: " + name);
        return text;
    }

    private static int Integer(JsonElement value, string name) => value.GetProperty(name).TryGetInt32(out var number)
        ? number : throw new InvalidDataException("QA contract integer expected: " + name);
}

internal sealed record RetiredHelperContract(string FilePrefix, int ExpectedCount);
