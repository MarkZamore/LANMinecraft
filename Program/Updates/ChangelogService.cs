using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;

namespace Minecraft;

/// <summary>One version of the launcher and what it changed, in the player's words.</summary>
public sealed record ChangelogEntry(int Version, string Text);

/// <summary>Loads GitHub history and retains a validated local copy for offline reading.</summary>
public static class ChangelogService
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    private const string SourceUrl = "https://raw.githubusercontent.com/MarkZamore/LANMinecraft/main/Program/Changelog.md";

    public static IReadOnlyList<ChangelogEntry> LoadCached(string cachePath, Logger? logger = null)
    {
        try
        {
            if (!File.Exists(cachePath)) return [];
            if (new FileInfo(cachePath).Length > MaximumBytes) throw new IOException("History exceeds the size limit.");
            return Validate(File.ReadAllText(cachePath, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            logger?.Warn($"The cached changelog could not be read: {ex.Message}");
            return [];
        }
    }

    public static async Task<IReadOnlyList<ChangelogEntry>> RefreshAsync(string cachePath,
        CancellationToken token, Logger? logger = null, HttpClient? client = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl + "?fresh=" + Guid.NewGuid().ToString("N"));
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var response = await (client ?? PortableHttpClient.Shared).SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new IOException("History exceeds the size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + read > MaximumBytes) throw new IOException("History exceeds the size limit.");
                bytes.Write(buffer, 0, read);
            }
            var markdown = new UTF8Encoding(false, true).GetString(bytes.ToArray()).TrimStart('\uFEFF');
            var entries = Validate(markdown);
            token.ThrowIfCancellationRequested();
            try { AtomicFile.WriteAllTextIfChanged(cachePath, markdown); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Warn($"The changelog could not be cached: {ex.Message}");
            }
            return entries;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or DecoderFallbackException ||
                                   ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            logger?.Warn($"The changelog could not be refreshed: {ex.Message}");
            return LoadCached(cachePath, logger);
        }
    }

    private static IReadOnlyList<ChangelogEntry> Validate(string markdown)
    {
        var entries = Parse(markdown);
        if (entries.Count == 0 || entries[^1].Version != 1 || entries[0].Version != entries.Count)
            throw new FormatException("The changelog must contain every version through the first release.");
        return entries;
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
