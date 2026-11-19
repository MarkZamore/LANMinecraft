using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// Orders Minecraft versions by their numbers rather than their letters, so
/// that 1.9 comes after 1.10 nowhere.
/// </summary>
internal sealed class VersionOrder : IComparer<string>
{
    public static readonly VersionOrder Instance = new();

    public int Compare(string? left, string? right) => CompareVersions(left, right);

    public static int CompareVersions(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var difference = (index < a.Length ? a[index] : 0) - (index < b.Length ? b[index] : 0);
            if (difference != 0) return Math.Sign(difference);
        }
        return 0;
    }

    /// <summary>The leading numbers, and nothing else: "1.18.2-pre1" is 1.18.2.</summary>
    public static int[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var numbers = new List<int>();
        foreach (var part in value.Trim().Split('.'))
        {
            var digits = 0;
            while (digits < part.Length && char.IsAsciiDigit(part[digits])) digits++;
            if (digits == 0) break;
            if (!int.TryParse(part.AsSpan(0, digits), out var number)) break;
            numbers.Add(number);
            if (digits != part.Length) break;
        }
        return [.. numbers];
    }
}

/// <summary>
/// Whether a version satisfies the range a mod declared, in either of the two
/// notations mods declare them in.
/// </summary>
/// <remarks>
/// Forge and NeoForge write Maven ranges - <c>[1.20.1,1.21)</c>, <c>[1.21.1]</c>,
/// <c>[1.21,)</c> - and Fabric writes the comparison form -
/// <c>&gt;=1.18.2 &lt;1.19</c>, <c>1.18.x</c>, <c>~1.18.2</c>, <c>*</c>. Real
/// jars write both loosely: spaces inside the brackets, a trailing hyphen on a
/// tilde range, an array where a string was expected. Everything here is
/// deliberately forgiving, because the alternative to reading a sloppy range is
/// discarding the jar that wrote it, and there are enough of those to change
/// the answer.
/// </remarks>
internal static class VersionRange
{
    private static readonly char[] Openers = ['[', '('];
    private static readonly char[] Closers = [']', ')'];
    private static readonly Regex Trailing = new(@"[^0-9.].*$", RegexOptions.Compiled);

    public static bool Accepts(string? range, string version)
    {
        if (string.IsNullOrWhiteSpace(range) || string.IsNullOrWhiteSpace(version)) return false;
        foreach (var alternative in range.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (AcceptsOne(alternative, version)) return true;
        }
        return false;
    }

    private static bool AcceptsOne(string range, string version)
    {
        range = range.Trim();
        if (range.Length == 0) return false;
        if (range is "*" or "any") return true;
        return range.IndexOfAny(Openers) >= 0
            ? MavenAccepts(range, version)
            : ComparisonAccepts(range, version);
    }

    /// <summary>
    /// Maven, which may be several bracketed groups meaning "any of these".
    /// </summary>
    private static bool MavenAccepts(string range, string version)
    {
        var index = 0;
        while (index < range.Length)
        {
            var open = range.IndexOfAny(Openers, index);
            if (open < 0) break;
            var close = range.IndexOfAny(Closers, open + 1);
            if (close < 0) break;

            var body = range[(open + 1)..close];
            var comma = body.IndexOf(',');
            if (comma < 0)
            {
                // "[1.21.1]" is that version and no other.
                if (VersionOrder.CompareVersions(version, Clean(body)) == 0) return true;
            }
            else
            {
                var lower = Clean(body[..comma]);
                var upper = Clean(body[(comma + 1)..]);
                var lowerOk = lower.Length == 0 ||
                              (range[open] == '['
                                  ? VersionOrder.CompareVersions(version, lower) >= 0
                                  : VersionOrder.CompareVersions(version, lower) > 0);
                var upperOk = upper.Length == 0 ||
                              (range[close] == ']'
                                  ? VersionOrder.CompareVersions(version, upper) <= 0
                                  : VersionOrder.CompareVersions(version, upper) < 0);
                if (lowerOk && upperOk) return true;
            }

            index = close + 1;
        }
        return false;
    }

    /// <summary>
    /// The comparison form, where a space means "and": ">=1.18.2 &lt;1.19".
    /// </summary>
    private static bool ComparisonAccepts(string range, string version)
    {
        var parts = range.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            if (!TermAccepts(part, version)) return false;
        }
        return true;
    }

    private static bool TermAccepts(string term, string version)
    {
        if (term is "*" or "any") return true;

        if (term.StartsWith(">=", StringComparison.Ordinal)) return VersionOrder.CompareVersions(version, Clean(term[2..])) >= 0;
        if (term.StartsWith("<=", StringComparison.Ordinal)) return VersionOrder.CompareVersions(version, Clean(term[2..])) <= 0;
        if (term.StartsWith('>')) return VersionOrder.CompareVersions(version, Clean(term[1..])) > 0;
        if (term.StartsWith('<')) return VersionOrder.CompareVersions(version, Clean(term[1..])) < 0;
        if (term.StartsWith('~')) return Within(version, Clean(term[1..]), bumpMinor: true);
        if (term.StartsWith('^')) return Within(version, Clean(term[1..]), bumpMinor: false);
        if (term.StartsWith('=')) term = term[1..];

        // "1.18.x" and "1.18.*": that minor line and nothing outside it.
        if (term.EndsWith(".x", StringComparison.OrdinalIgnoreCase) || term.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = Clean(term[..^2]);
            return prefix.Length != 0 &&
                   (VersionOrder.CompareVersions(version, prefix) == 0 ||
                    version.StartsWith(prefix + ".", StringComparison.Ordinal));
        }

        return VersionOrder.CompareVersions(version, Clean(term)) == 0;
    }

    /// <summary>"~1.18.2" is 1.18.2 up to 1.19; "^1.18.2" is 1.18.2 up to 2.0.</summary>
    private static bool Within(string version, string floor, bool bumpMinor)
    {
        var parts = VersionOrder.Parse(floor);
        if (parts.Length == 0) return false;
        if (VersionOrder.CompareVersions(version, floor) < 0) return false;

        var ceiling = bumpMinor && parts.Length >= 2
            ? $"{parts[0]}.{parts[1] + 1}"
            : $"{parts[0] + 1}.0";
        return VersionOrder.CompareVersions(version, ceiling) < 0;
    }

    /// <summary>The version out of whatever else the author wrote around it.</summary>
    private static string Clean(string value) => Trailing.Replace(value.Trim(), "").TrimEnd('.');
}
