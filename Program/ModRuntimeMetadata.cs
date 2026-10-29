using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

internal sealed record ModRuntimeOption(
    PackLoaderKind Loader,
    IReadOnlyList<string> MinecraftRanges,
    IReadOnlyList<string> LoaderRanges)
{
    // javafml/lowcodefml loaderVersion is the language loader API, not the platform version.
    public IReadOnlyList<string> FmlRanges { get; init; } = [];
    public string? FmlLanguage { get; init; }
    public IReadOnlyList<string> FabricApiRanges { get; init; } = [];
}

internal sealed record ModRuntimeJar(string FileName, IReadOnlyList<ModRuntimeOption> Options)
{
    public bool IsNeutralLibrary { get; init; }
}

/// <summary>Reads runtime constraints from untrusted, top-level mod JARs without loading any code.</summary>
internal static class ModRuntimeMetadata
{
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumOptions = 128;
    private static readonly HashSet<string> MetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "META-INF/mods.toml", "META-INF/neoforge.mods.toml", "fabric.mod.json", "quilt.mod.json", "mcmod.info", "META-INF/MANIFEST.MF"
    };

    public static IReadOnlyList<ModRuntimeJar> Read(string packDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packDirectory);
        var mods = Path.Combine(Path.GetFullPath(packDirectory), "mods");
        if (!Directory.Exists(mods)) return [];
        RejectLink(mods);
        var files = Directory.EnumerateFiles(mods, "*", SearchOption.TopDirectoryOnly)
            .Where(file => Path.GetExtension(file).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 10000) throw new InvalidDataException("Too many mod JARs to inspect.");
        return files.Select(ReadJar).ToArray();
    }

    private static ModRuntimeJar ReadJar(string path)
    {
        RejectLink(path);
        try
        {
            using var file = File.OpenRead(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            if (archive.Entries.Count > 100000) throw new InvalidDataException("Too many ZIP entries.");
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (!MetadataNames.Contains(entry.FullName)) continue;
                if (metadata.ContainsKey(entry.FullName)) throw new InvalidDataException($"Duplicate metadata entry {entry.FullName}.");
                if (entry.Length > MaximumMetadataBytes) throw new InvalidDataException($"Metadata {entry.FullName} is too large.");
                using var stream = entry.Open();
                using var bounded = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = stream.Read(buffer)) != 0)
                {
                    if (bounded.Length + count > MaximumMetadataBytes) throw new InvalidDataException("Metadata exceeds its size limit.");
                    bounded.Write(buffer, 0, count);
                }
                metadata.Add(entry.FullName, new UTF8Encoding(false, true).GetString(bounded.ToArray()).TrimStart('\uFEFF'));
            }
            var options = new List<ModRuntimeOption>();
            if (metadata.TryGetValue("META-INF/neoforge.mods.toml", out var neo)) options.Add(ReadToml(neo, PackLoaderKind.NeoForge));
            if (metadata.TryGetValue("META-INF/mods.toml", out var forge)) options.Add(ReadToml(forge, PackLoaderKind.Forge));
            if (metadata.TryGetValue("fabric.mod.json", out var fabric)) options.Add(ReadFabric(fabric));
            if (metadata.TryGetValue("quilt.mod.json", out var quilt)) options.AddRange(ReadQuilt(quilt));
            if (options.Count == 0 && metadata.TryGetValue("mcmod.info", out var legacy)) options.Add(ReadLegacy(legacy));
            return new ModRuntimeJar(Path.GetFileName(path), options)
            {
                IsNeutralLibrary = options.Count == 0 && IsExplicitLibrary(metadata, archive)
            };
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or DecoderFallbackException or FormatException)
        {
            throw new InvalidDataException($"Cannot infer runtime from {Path.GetFileName(path)}: {error.Message}", error);
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Mod metadata cannot be read through a symbolic link: {Path.GetFileName(path)}");
    }

    private static bool IsExplicitLibrary(IReadOnlyDictionary<string, string> metadata, ZipArchive archive)
    {
        if (!metadata.TryGetValue("META-INF/MANIFEST.MF", out var manifest)) return false;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var line in manifest.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) break; // Main attributes only; per-entry sections cannot declare a library.
            if (line[0] == ' ')
            {
                if (previous is null) return false;
                fields[previous] += line[1..];
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) return false;
            previous = line[..colon];
            if (!fields.TryAdd(previous, line[(colon + 1)..].Trim())) return false;
        }
        if (!fields.TryGetValue("FMLModType", out var type) || !type.Equals("LIBRARY", StringComparison.OrdinalIgnoreCase)) return false;
        string[] codeMarkers = ["FMLCorePlugin", "FMLCorePluginContainsFMLMod", "ForceLoadAsMod", "TweakClass", "Premain-Class", "Agent-Class", "Launcher-Agent-Class", "MixinConfigs", "MixinConnector", "FMLAT"];
        if (codeMarkers.Any(fields.ContainsKey)) return false;
        return !archive.Entries.Any(entry =>
            entry.FullName.Equals("META-INF/coremods.json", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.StartsWith("META-INF/services/", StringComparison.OrdinalIgnoreCase));
    }

    private static ModRuntimeOption ReadToml(string text, PackLoaderKind defaultLoader)
    {
        var top = new Dictionary<string, string>(StringComparer.Ordinal);
        var dependencies = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = top;
        foreach (var statement in TomlStatements(text))
        {
            if (statement.StartsWith('['))
            {
                if (statement.StartsWith("[[dependencies.", StringComparison.Ordinal) && statement.EndsWith("]]", StringComparison.Ordinal))
                {
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                    dependencies.Add(current);
                }
                else current = null;
                continue;
            }
            var equals = statement.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("Malformed TOML assignment.");
            var key = statement[..equals].Trim().Trim('"', '\'');
            if (current is null || key is not ("modLoader" or "loaderVersion" or "modId" or "mandatory" or "type" or "versionRange")) continue;
            if (!current.TryAdd(key, statement[(equals + 1)..].Trim())) throw new InvalidDataException($"Duplicate TOML field {key}.");
        }
        var applicable = dependencies.Where(IsPositiveRuntimeDependency).ToArray();
        var ids = applicable
            .Select(dependency => TomlString(dependency["modId"])).ToArray();
        var loader = ids.Contains("neoforge", StringComparer.Ordinal) ? PackLoaderKind.NeoForge : defaultLoader;
        if (ids.Contains("forge", StringComparer.Ordinal) && ids.Contains("neoforge", StringComparer.Ordinal))
            throw new InvalidDataException("One TOML metadata file requires both Forge and NeoForge.");
        var minecraft = new List<string>();
        var platform = new List<string>();
        foreach (var dependency in applicable)
        {
            var id = TomlString(dependency["modId"]);
            var range = dependency.TryGetValue("versionRange", out var rawRange) ? TomlString(rawRange) : "*";
            Validate(range);
            (id == "minecraft" ? minecraft : platform).Add(range);
        }
        var fml = new List<string>();
        var language = top.TryGetValue("modLoader", out var rawLanguage) ? TomlString(rawLanguage) : null;
        if ((language is "javafml" or "lowcodefml") &&
            top.TryGetValue("loaderVersion", out var rawFml))
        {
            var range = TomlString(rawFml);
            Validate(range);
            fml.Add(range);
        }
        return new ModRuntimeOption(loader, minecraft, platform) { FmlRanges = fml, FmlLanguage = language };
    }

    private static bool IsPositiveRuntimeDependency(Dictionary<string, string> dependency)
    {
        if (!dependency.TryGetValue("modId", out var rawId) || TomlString(rawId) is not ("minecraft" or "forge" or "neoforge")) return false;
        if (dependency.TryGetValue("mandatory", out var mandatory) && mandatory is not ("true" or "false"))
            throw new InvalidDataException("Invalid mandatory dependency flag.");
        if (!dependency.TryGetValue("type", out var rawType)) return true;
        var type = TomlString(rawType).ToLowerInvariant();
        if (type == "discouraged") return false;
        if (type is "required" or "optional") return true;
        throw new InvalidDataException($"Runtime dependency type {type} requires an explicit portable-pack.json.");
    }

    // Split only outside quoted strings/arrays. In particular, a description cannot manufacture a dependency table.
    internal static IEnumerable<string> TomlStatements(string text)
    {
        var statement = new StringBuilder();
        char quote = '\0';
        var triple = false;
        var escaped = false;
        var brackets = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                statement.Append(character);
                if (escaped) { escaped = false; continue; }
                if (quote == '"' && character == '\\') { escaped = true; continue; }
                if (character != quote) continue;
                if (triple)
                {
                    if (index + 2 >= text.Length || text[index + 1] != quote || text[index + 2] != quote) continue;
                    statement.Append(quote, 2);
                    index += 2;
                }
                quote = '\0';
                triple = false;
                continue;
            }
            if (character == '#')
            {
                while (index + 1 < text.Length && text[index + 1] is not ('\r' or '\n')) index++;
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                triple = index + 2 < text.Length && text[index + 1] == quote && text[index + 2] == quote;
                statement.Append(character);
                if (triple) { statement.Append(character, 2); index += 2; }
                continue;
            }
            if (character is '[' or '{') brackets++;
            if (character is ']' or '}') brackets--;
            if (brackets < 0) throw new InvalidDataException("Unbalanced TOML delimiters.");
            if ((character is '\r' or '\n') && brackets == 0)
            {
                var value = statement.ToString().Trim();
                if (value.Length != 0) yield return value;
                statement.Clear();
            }
            else statement.Append(character);
        }
        if (quote != '\0' || brackets != 0) throw new InvalidDataException("Unterminated TOML string or array.");
        var last = statement.ToString().Trim();
        if (last.Length != 0) yield return last;
    }

    private static string TomlString(string value)
    {
        if (value.Length < 2) throw new InvalidDataException("Runtime TOML value must be a string.");
        if (value[0] == '\'' && value[^1] == '\'' && !value.StartsWith("'''", StringComparison.Ordinal)) return value[1..^1];
        if (value[0] == '"' && value[^1] == '"' && !value.StartsWith("\"\"\"", StringComparison.Ordinal))
            return JsonSerializer.Deserialize<string>(value) ?? throw new InvalidDataException("Null runtime TOML value.");
        throw new InvalidDataException("Unsupported runtime TOML string.");
    }

    private static ModRuntimeOption ReadFabric(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        var root = Object(document.RootElement);
        CheckSchema(root, "schemaVersion");
        if (root.TryGetProperty("breaks", out var breaks))
        {
            Object(breaks);
            if (breaks.EnumerateObject().Any(dependency => dependency.Name is "minecraft" or "fabricloader"))
                throw new InvalidDataException("Fabric runtime exclusions require an explicit portable-pack.json.");
        }
        var minecraft = new List<string>();
        var loader = new List<string>();
        if (root.TryGetProperty("depends", out var depends))
        {
            Object(depends);
            foreach (var dependency in depends.EnumerateObject())
            {
                if (dependency.Name is not ("minecraft" or "fabricloader")) continue;
                var alternatives = StringAlternatives(dependency.Value, quilt: false);
                (dependency.Name == "minecraft" ? minecraft : loader).Add(string.Join(" || ", alternatives));
            }
        }
        return new ModRuntimeOption(PackLoaderKind.Fabric, minecraft, loader);
    }

    private sealed record Constraints(IReadOnlyList<string> Minecraft, IReadOnlyList<string> Loader, IReadOnlyList<string> FabricApi);

    private static IReadOnlyList<ModRuntimeOption> ReadQuilt(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        var root = Object(document.RootElement);
        CheckSchema(root, "schema_version");
        if (!root.TryGetProperty("quilt_loader", out var rawLoader)) throw new InvalidDataException("Missing quilt_loader metadata.");
        var loader = Object(rawLoader);
        if (loader.TryGetProperty("breaks", out var breaks) && ContainsQuiltRuntime(breaks))
            throw new InvalidDataException("Quilt runtime exclusions require an explicit portable-pack.json.");
        var options = new List<Constraints> { new([], [], []) };
        if (loader.TryGetProperty("depends", out var depends))
        {
            if (depends.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Quilt depends must be an array.");
            foreach (var dependency in depends.EnumerateArray()) options = And(options, QuiltDependency(dependency));
        }
        return options.Select(option => new ModRuntimeOption(PackLoaderKind.Quilt, option.Minecraft, option.Loader)
        {
            FabricApiRanges = option.FabricApi
        }).ToArray();
    }

    private static List<Constraints> QuiltDependency(JsonElement dependency)
    {
        if (dependency.ValueKind == JsonValueKind.Array)
        {
            var alternatives = dependency.EnumerateArray().SelectMany(QuiltDependency).ToList();
            if (alternatives.Count == 0 || alternatives.Count > MaximumOptions) throw new InvalidDataException("Invalid Quilt dependency alternatives.");
            if (alternatives.Any(option => option.Minecraft.Count + option.Loader.Count + option.FabricApi.Count > 0) &&
                alternatives.Any(option => option.Minecraft.Count + option.Loader.Count + option.FabricApi.Count == 0))
                throw new InvalidDataException("Quilt runtime alternatives depend on an unverified non-runtime dependency.");
            return alternatives;
        }
        if (dependency.ValueKind == JsonValueKind.String)
            return QuiltConstraint(dependency.GetString()!, [["*"]]);
        Object(dependency);
        if (!dependency.TryGetProperty("id", out var rawId) || rawId.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing Quilt dependency id.");
        var id = rawId.GetString()!;
        if (RuntimeId(id) is not ("minecraft" or "quilt_loader" or "fabricloader")) return [new([], [], [])];
        if (dependency.TryGetProperty("unless", out _)) throw new InvalidDataException("Conditional Quilt runtime dependencies cannot be inferred safely.");
        // Minecraft and the selected loader are always present, so optional still constrains their versions.
        var versions = dependency.TryGetProperty("versions", out var rawVersions) ? QuiltVersions(rawVersions) : [["*"]];
        return QuiltConstraint(id, versions);
    }

    private static List<Constraints> QuiltConstraint(string id, List<List<string>> versions) => RuntimeId(id) switch
    {
        "minecraft" => versions.Select(ranges => new Constraints(ranges, [], [])).ToList(),
        "quilt_loader" => versions.Select(ranges => new Constraints([], ranges, [])).ToList(),
        "fabricloader" => versions.Select(ranges => new Constraints([], [], ranges)).ToList(),
        _ => [new([], [], [])]
    };

    private static string RuntimeId(string id) => id[(id.LastIndexOf(':') + 1)..];

    private static bool ContainsQuiltRuntime(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(ContainsQuiltRuntime);
        if (value.ValueKind == JsonValueKind.String) return RuntimeId(value.GetString()!) is "minecraft" or "quilt_loader" or "fabricloader";
        Object(value);
        if (!value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid Quilt exclusion dependency.");
        return RuntimeId(id.GetString()!) is "minecraft" or "quilt_loader" or "fabricloader";
    }

    private static List<List<string>> QuiltVersions(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var range = QuiltRange(value.GetString()!);
            Validate(range);
            return [[range]];
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = value.EnumerateArray().SelectMany(QuiltVersions).ToList();
            if (result.Count == 0 || result.Count > MaximumOptions) throw new InvalidDataException("Invalid Quilt version alternatives.");
            return result;
        }
        Object(value);
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != 1 || properties[0].Name is not ("any" or "all") || properties[0].Value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unsupported Quilt version group.");
        var children = properties[0].Value.EnumerateArray().ToArray();
        if (children.Length == 0) throw new InvalidDataException("Empty Quilt version group.");
        if (properties[0].Name == "any")
        {
            var result = children.SelectMany(QuiltVersions).ToList();
            if (result.Count > MaximumOptions) throw new InvalidDataException("Too many Quilt alternatives.");
            return result;
        }
        var combined = new List<List<string>> { new() };
        foreach (var child in children)
        {
            var alternatives = QuiltVersions(child);
            if (combined.Count * alternatives.Count > MaximumOptions) throw new InvalidDataException("Too many Quilt alternatives.");
            combined = combined.SelectMany(left => alternatives.Select(right => left.Concat(right).ToList())).ToList();
        }
        return combined;
    }

    private static List<Constraints> And(List<Constraints> left, List<Constraints> right)
    {
        if (left.Count * right.Count > MaximumOptions) throw new InvalidDataException("Too many Quilt runtime alternatives.");
        return left.SelectMany(a => right.Select(b => new Constraints(a.Minecraft.Concat(b.Minecraft).ToArray(),
            a.Loader.Concat(b.Loader).ToArray(), a.FabricApi.Concat(b.FabricApi).ToArray()))).ToList();
    }

    private static IEnumerable<string> StringAlternatives(JsonElement value, bool quilt)
    {
        var values = value.ValueKind switch
        {
            JsonValueKind.String => new[] { value.GetString()! },
            JsonValueKind.Array => value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new InvalidDataException("Version alternatives must be strings.")).ToArray(),
            _ => throw new InvalidDataException("Version dependency must be a string or an array of strings.")
        };
        if (values.Length == 0 || values.Length > MaximumOptions) throw new InvalidDataException("Invalid version alternatives.");
        return values.Select(range =>
        {
            var normalized = quilt ? QuiltRange(range) : range;
            Validate(normalized);
            return normalized;
        }).ToArray();
    }

    private static string QuiltRange(string value) => Regex.IsMatch(value, @"^\d+(?:\.\d+)*(?:-[\w.-]*)?(?:\+[\w.-]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
        ? "^" + value : value;

    private static ModRuntimeOption ReadLegacy(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            Object(root);
            if (root.TryGetProperty("modList", out var list)) root = list;
        }
        var entries = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        var minecraft = new List<string>();
        var loader = new List<string>();
        foreach (var entry in entries)
        {
            Object(entry);
            if (entry.TryGetProperty("mcversion", out var version))
            {
                if (version.ValueKind != JsonValueKind.String) throw new InvalidDataException("Legacy mcversion must be a string.");
                var value = version.GetString()!;
                if (value.Length != 0) { Validate(value); minecraft.Add(value); }
            }
            foreach (var name in new[] { "dependencies", "requiredMods" })
            {
                if (!entry.TryGetProperty(name, out var dependencies)) continue;
                var strings = dependencies.ValueKind == JsonValueKind.String ? new[] { dependencies.GetString()! } :
                    dependencies.ValueKind == JsonValueKind.Array ? dependencies.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("Invalid legacy dependency.")).ToArray() :
                    throw new InvalidDataException("Invalid legacy dependency list.");
                foreach (var dependency in strings.SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                {
                    var match = Regex.Match(dependency, @"^(?:(?:required-after|required-before|after|before):)?(?<id>[^@]+)(?:@(?<range>.+))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (!match.Success) throw new InvalidDataException("Malformed legacy dependency.");
                    var id = match.Groups["id"].Value;
                    if (!id.Equals("forge", StringComparison.OrdinalIgnoreCase) && !id.Equals("minecraft", StringComparison.OrdinalIgnoreCase)) continue;
                    if (dependency.StartsWith("after:", StringComparison.Ordinal) || dependency.StartsWith("before:", StringComparison.Ordinal)) continue;
                    var range = match.Groups["range"].Success ? match.Groups["range"].Value : "*";
                    Validate(range);
                    (id.Equals("minecraft", StringComparison.OrdinalIgnoreCase) ? minecraft : loader).Add(range);
                }
            }
        }
        return new ModRuntimeOption(PackLoaderKind.Forge, minecraft, loader);
    }

    private static JsonElement Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Metadata must contain a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate JSON field {property.Name}.");
        return value;
    }

    private static void CheckSchema(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var schema) &&
            (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1))
            throw new InvalidDataException($"Unsupported mod metadata {name}.");
    }

    private static void Validate(string range) => _ = StrictVersionRange.Accepts(range, "0.0.0");
}
