using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>What a folder of mods turned out to be, and how sure of it.</summary>
/// <param name="Loader">The loader family, or null where the jars did not agree.</param>
/// <param name="MinecraftVersion">The version, or null where the jars did not agree.</param>
/// <param name="Explanation">One line, for the log and for the player.</param>
public sealed record PackDetection(
    PackLoaderKind? Loader,
    string? MinecraftVersion,
    string Explanation)
{
    public bool IsComplete => Loader is not null && !string.IsNullOrWhiteSpace(MinecraftVersion);

    public static PackDetection Nothing(string why) => new(null, null, why);
}

/// <summary>
/// Reads a folder of mods and works out what it is a pack of.
/// </summary>
/// <remarks>
/// So that a player can make a build of their own by putting jars in a folder.
/// Every mod already carries the two facts that matter - which loader it is for
/// and which Minecraft it was built against - and a folder of them agrees with
/// itself far more reliably than one person typing the same two facts into a
/// file by hand.
///
/// The rules here are not guesses; each one is the answer to a case that a real
/// pack actually contains, measured across 1101 jars in four packs:
///
/// <list type="bullet">
/// <item>A jar's metadata file does not say which loader the pack is for.
/// Multi-loader mods ship every loader's metadata in one jar - 78 of the 1101
/// carry two families or more, and three jars inside a Forge 1.20.1 pack ship
/// NeoForge metadata. Only a jar that names exactly one family may vote.</item>
/// <item>Forge and NeoForge both ship <c>META-INF/mods.toml</c>. What tells
/// them apart is the dependency they declare: <c>modId="forge"</c> against
/// <c>modId="neoforge"</c>. Never the file name, never the loader version.</item>
/// <item>Quilt cannot be told from Fabric at all: every one of the nine
/// <c>quilt.mod.json</c> jars found also carried <c>fabric.mod.json</c>, and
/// there was not one Quilt-only jar. A Fabric-family answer is Fabric.</item>
/// <item>A NeoForge pack may legitimately hold Fabric mods, through Sinytra
/// Connector. Those jars are supposed to be there, so Connector's presence
/// silences the Fabric vote rather than counting against it.</item>
/// <item>Versions must be voted on, never intersected. Authors write
/// <c>[1.21,1.21.1)</c> meaning "1.21.x" and exclude the very version their
/// own file name carries; intersecting the ranges of Limitless 8 returns
/// nothing at all, over 754 jars, because 48 of them contradict the truth.</item>
/// <item>Roughly one jar in seven declares no version. That is an abstention,
/// never a vote, and a folder without a quorum of them gets no answer.</item>
/// </list>
///
/// The answer is refused rather than guessed when the jars disagree: a folder
/// holding a Forge pack and a Fabric pack at once was measured resolving to one
/// version by a margin of a single jar out of 136, which is the kind of
/// confident wrong answer worth going out of the way to avoid.
/// </remarks>
public static class PackDetector
{
    /// <summary>Below this many jars saying anything about a version, no answer.</summary>
    private const int VersionQuorum = 5;
    private const double VersionQuorumShareOfFolder = 0.2;
    /// <summary>How much of the single-family vote the winner must hold.</summary>
    private const double LoaderSupermajority = 0.8;
    /// <summary>How many of the jars with an opinion must accept the winning version.</summary>
    private const double VersionAgreement = 0.6;

    private static readonly Regex ModsTomlDependencyModId =
        new(@"modId\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex ModsTomlVersionRange =
        new(@"modId\s*=\s*[""']minecraft[""'][^\[]*?versionRange\s*=\s*[""']([^""']+)[""']",
            RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex McmodInfoVersion =
        new(@"[""']mcversion[""']\s*:\s*[""']([^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex VersionToken =
        new(@"\d+(?:\.\d+){1,2}", RegexOptions.Compiled);

    public static PackDetection Detect(string packDirectory)
    {
        var mods = Path.Combine(packDirectory, "mods");
        if (!Directory.Exists(mods)) return PackDetection.Nothing("the folder has no mods");

        string[] jars;
        try
        {
            // Top level only. The four packs measured hold 517 jars nested
            // inside other jars, and their metadata would swamp the vote of the
            // mods actually installed.
            jars = Directory.GetFiles(mods, "*.jar", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PackDetection.Nothing("the mods folder could not be read");
        }

        if (jars.Length == 0) return PackDetection.Nothing("the mods folder is empty");

        var loaderVotes = new Dictionary<PackLoaderKind, int>();
        var ranges = new List<string>();
        var hasConnector = jars.Any(IsConnector);

        foreach (var jar in jars)
        {
            var read = ReadJar(jar);
            if (read is null) continue;
            if (read.Families.Count == 1)
            {
                var family = read.Families.Single();
                // A NeoForge pack running Fabric mods through Connector: those
                // jars belong there and must not be counted against it.
                if (!(hasConnector && family == PackLoaderKind.Fabric))
                {
                    loaderVotes[family] = loaderVotes.GetValueOrDefault(family) + 1;
                }
            }
            if (read.MinecraftRange is not null) ranges.Add(read.MinecraftRange);
        }

        var loader = ResolveLoader(loaderVotes, out var loaderNote);
        var version = ResolveVersion(ranges, jars.Length, out var versionNote);
        return new PackDetection(loader, version, $"{jars.Length} mods: {loaderNote}, {versionNote}");
    }

    private static bool IsConnector(string jarPath)
    {
        var name = Path.GetFileName(jarPath);
        return name.StartsWith("connector-", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("forgified-fabric-api-", StringComparison.OrdinalIgnoreCase);
    }

    private static PackLoaderKind? ResolveLoader(
        Dictionary<PackLoaderKind, int> votes,
        out string note)
    {
        var total = votes.Values.Sum();
        if (total == 0)
        {
            note = "no mod said which loader it is for";
            return null;
        }

        var winner = votes.OrderByDescending(pair => pair.Value).First();
        if (winner.Value < total * LoaderSupermajority)
        {
            note = $"the mods disagree about the loader ({Describe(votes)})";
            return null;
        }

        note = $"{winner.Key} by {winner.Value} of {total}";
        return winner.Key;
    }

    private static string Describe(Dictionary<PackLoaderKind, int> votes) =>
        string.Join(", ", votes.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value}"));

    private static string? ResolveVersion(List<string> ranges, int jarCount, out string note)
    {
        if (ranges.Count < VersionQuorum || ranges.Count < jarCount * VersionQuorumShareOfFolder)
        {
            note = $"only {ranges.Count} of {jarCount} mods named a Minecraft version, which is too few to go on";
            return null;
        }

        // The candidates are the versions the mods themselves name. Inventing
        // the ones in between - every 1.18.x up to 1.18.9 - lets open-ended
        // ranges spread their votes over versions that were never released,
        // and the true answer's margin collapses.
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var range in ranges)
        {
            foreach (Match token in VersionToken.Matches(range)) candidates.Add(token.Value);
        }
        if (candidates.Count == 0)
        {
            note = "no mod named a version that could be read";
            return null;
        }

        var tally = candidates
            .Select(candidate => (Version: candidate, Votes: ranges.Count(range => VersionRange.Accepts(range, candidate))))
            .OrderByDescending(entry => entry.Votes)
            // A tie goes to the later version: a range that ends at 1.21 accepts
            // 1.21 and 1.20.1 alike, and the pack is the newer of the two.
            .ThenByDescending(entry => entry.Version, VersionOrder.Instance)
            .ToList();

        var best = tally[0];
        if (best.Votes < ranges.Count * VersionAgreement)
        {
            note = $"no version suits enough of them (best was {best.Version}, {best.Votes} of {ranges.Count})";
            return null;
        }

        note = $"Minecraft {best.Version} by {best.Votes} of {ranges.Count}";
        return best.Version;
    }

    private sealed record JarMetadata(HashSet<PackLoaderKind> Families, string? MinecraftRange);

    private static JarMetadata? ReadJar(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var families = new HashSet<PackLoaderKind>();
            string? range = null;

            var neoToml = Read(archive, "META-INF/neoforge.mods.toml");
            if (neoToml is not null)
            {
                families.Add(PackLoaderKind.NeoForge);
                range ??= MinecraftRangeFromToml(neoToml);
            }

            var toml = Read(archive, "META-INF/mods.toml");
            if (toml is not null)
            {
                // Both loaders write this file; the dependency inside it is the
                // only thing that says which one meant it.
                var ids = ModsTomlDependencyModId.Matches(toml)
                    .Select(match => match.Groups[1].Value.Trim().ToLowerInvariant())
                    .ToHashSet(StringComparer.Ordinal);
                if (ids.Contains("forge")) families.Add(PackLoaderKind.Forge);
                else if (ids.Contains("neoforge")) families.Add(PackLoaderKind.NeoForge);
                range ??= MinecraftRangeFromToml(toml);
            }

            var fabric = Read(archive, "fabric.mod.json");
            var quilt = Read(archive, "quilt.mod.json");
            if (fabric is not null || quilt is not null)
            {
                // Quilt loads Fabric mods and every Quilt jar measured carried
                // Fabric metadata too, so the family is one family.
                families.Add(PackLoaderKind.Fabric);
                range ??= MinecraftRangeFromFabric(fabric);
            }

            if (families.Count == 0 && fabric is null && quilt is null)
            {
                var mcmod = Read(archive, "mcmod.info");
                if (mcmod is not null)
                {
                    // Only where it stands alone: three 1.21.1 NeoForge jars
                    // still ship one out of habit, so it proves nothing beside
                    // a modern file.
                    families.Add(PackLoaderKind.Forge);
                    var match = McmodInfoVersion.Match(mcmod);
                    if (match.Success) range ??= match.Groups[1].Value;
                }
            }

            return families.Count == 0 && range is null ? null : new JarMetadata(families, range);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? MinecraftRangeFromToml(string toml)
    {
        var match = ModsTomlVersionRange.Match(toml);
        if (!match.Success) return null;
        var range = match.Groups[1].Value.Trim();
        // An unexpanded Gradle token is a build that shipped without being
        // filled in; three jars in Limitless 8 carry one.
        return range.Contains("${", StringComparison.Ordinal) ? null : range;
    }

    private static string? MinecraftRangeFromFabric(string? json)
    {
        if (json is null) return null;
        try
        {
            // Real jars contain control characters inside their JSON strings
            // and Fabric's own loader accepts them, so this one does too. Every
            // one of them, including the newlines and tabs: a raw newline is
            // precisely what better-end-1.1.1.jar has inside a string, and
            // sparing them because they look like whitespace is how this was
            // wrong the first time -
            //
            //   Could not inspect mod metadata in better-end-1.1.1.jar:
            //   '0x0A' is invalid within a JSON string.
            //
            // A space stands in for all of them, which is valid between tokens
            // and harmless inside one.
            var cleaned = new StringBuilder(json.Length);
            foreach (var ch in json) cleaned.Append(char.IsControl(ch) ? ' ' : ch);
            using var document = JsonDocument.Parse(
                cleaned.ToString(),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!document.RootElement.TryGetProperty("depends", out var depends) ||
                depends.ValueKind != JsonValueKind.Object ||
                !depends.TryGetProperty("minecraft", out var minecraft))
            {
                return null;
            }

            return minecraft.ValueKind switch
            {
                JsonValueKind.String => minecraft.GetString(),
                // An array is "any of these"; joined, because every parser here
                // treats a space as "and" and a comma as "or".
                JsonValueKind.Array => string.Join(
                    " || ",
                    minecraft.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Read(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null) return null;
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }
}
