using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Minecraft;

/// <summary>One version of the launcher and what it changed, in the player's words.</summary>
public sealed record ChangelogEntry(int Version, string Text);

/// <summary>
/// Reads the version history the window shows.
///
/// The history is a hand-written Markdown file embedded in the executable:
/// a <c>## N</c> heading per version, newest first, each followed by one
/// <c>- </c> paragraph in plain Russian - one version reads as one thought,
/// so the list stays skimmable. N is the commit count the release was built from,
/// so a version on screen and a commit in git name the same thing. The
/// release workflow refuses to build a number the file does not know.
///
/// The parser is strict on purpose: a slip in the file must fail the test
/// run, not ship a half-read list.
/// </summary>
public static class ChangelogService
{
    internal const string ResourceName = "Minecraft.Changelog.md";

    /// <summary>Newest first; empty when the resource is missing or malformed. Never throws.</summary>
    public static IReadOnlyList<ChangelogEntry> Load(Logger? logger = null) =>
        Load(typeof(ChangelogService).Assembly, ResourceName, logger);

    internal static IReadOnlyList<ChangelogEntry> Load(Assembly assembly, string resourceName, Logger? logger = null)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource is missing: {resourceName}.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException)
        {
            logger?.Warn($"The changelog could not be read: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Parses the history text. Throws <see cref="FormatException"/> naming the
    /// line for anything that is not a heading, a single paragraph, a blank line
    /// or the file's title.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var entries = new List<ChangelogEntry>();
        int? version = null;
        string? text = null;
        var number = 0;

        void Close()
        {
            if (version is null) return;
            if (text is null)
            {
                throw new FormatException($"Version {version} has no text (before line {number}).");
            }
            entries.Add(new ChangelogEntry(version.Value, text));
            text = null;
        }

        foreach (var raw in markdown.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                var heading = line[3..].Trim();
                if (!int.TryParse(heading, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                {
                    throw new FormatException($"Line {number}: '{heading}' is not a version number.");
                }
                if (entries.Count > 0 && parsed >= entries[^1].Version)
                {
                    throw new FormatException(
                        $"Line {number}: version {parsed} must be below {entries[^1].Version}; the file lists newest first.");
                }
                version = parsed;
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (version is null)
                {
                    throw new FormatException($"Line {number}: a paragraph before any version heading.");
                }
                if (text is not null)
                {
                    throw new FormatException($"Line {number}: version {version} has a second paragraph; one version is one paragraph.");
                }
                var paragraph = line[2..].Trim();
                if (paragraph.Length == 0)
                {
                    throw new FormatException($"Line {number}: an empty paragraph.");
                }
                if (paragraph.Contains('—'))
                {
                    throw new FormatException($"Line {number}: an em dash; the descriptions use a plain hyphen.");
                }
                text = paragraph;
                continue;
            }

            // The file's own title.
            if (line.StartsWith('#') && !line.StartsWith("##", StringComparison.Ordinal)) continue;

            throw new FormatException($"Line {number}: unexpected text '{line}'.");
        }

        Close();
        return entries;
    }
}
