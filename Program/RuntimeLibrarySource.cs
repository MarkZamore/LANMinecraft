using System.IO;
using System.Text.Json;

namespace Minecraft;

internal sealed record RuntimeLibrarySource(Uri Url, string Sha256)
{
    internal const string Feature = "runtime-library-sources-v1";

    internal static IReadOnlyDictionary<string, RuntimeLibrarySource>? Load(string packDirectory)
    {
        var path = Path.Combine(packDirectory, "launcher", "runtime-libraries.json");
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 128 * 1024)
            throw new InvalidDataException("Runtime library source list is too large.");

        using var document = JsonDocument.Parse(stream);
        var sources = new Dictionary<string, RuntimeLibrarySource>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var sha256 = entry.Value.GetProperty("sha256").GetString();
            if (!IsHash(entry.Name, 40) || !IsHash(sha256, 64)
                || !Uri.TryCreate(entry.Value.GetProperty("url").GetString(), UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" || !url.IsDefaultPort
                || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0
                || url.Segments.Length != 7 || url.Segments[3] != "releases/" || url.Segments[4] != "download/"
                || !url.AbsolutePath.EndsWith(".jar", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid runtime library source.");

            if (!sources.TryAdd(entry.Name, new RuntimeLibrarySource(url, sha256!)))
                throw new InvalidDataException("Duplicate runtime library source.");
        }
        return sources;
    }

    private static bool IsHash(string? value, int length) =>
        value?.Length == length && value.All(Uri.IsHexDigit);
}
