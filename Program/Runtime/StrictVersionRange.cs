using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>Version predicates used for automatic runtime selection. Unsupported syntax is an error.</summary>
internal static class StrictVersionRange
{
    private static readonly Regex VersionPattern = new(
        @"^(?<numbers>\d+(?:\.\d+){0,7})(?:-(?<pre>[0-9A-Za-z.-]*))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TermPattern = new(
        @"\G\s*(?<op>>=|<=|>|<|=|~|\^)?\s*(?<value>[^\s,]+)\s*(?:,\s*)?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool Accepts(string range, string exactVersion)
    {
        var version = ParseVersion(exactVersion);
        if (string.IsNullOrWhiteSpace(range) || range.Length > 4096)
            throw Invalid(range);
        var alternatives = range.Split("||", StringSplitOptions.TrimEntries);
        if (alternatives.Length > 128 || alternatives.Any(string.IsNullOrEmpty)) throw Invalid(range);
        // Parse every branch, even after a match, so a malformed branch cannot be hidden by '*'.
        var matches = alternatives.Select(branch => branch[0] is '[' or '('
            ? Maven(branch, version) : Comparisons(branch, version)).ToArray();
        return matches.Any(match => match);
    }

    private static bool Maven(string range, NumericVersion version)
    {
        var index = 0;
        var matched = false;
        var groups = 0;
        while (index < range.Length)
        {
            while (index < range.Length && char.IsWhiteSpace(range[index])) index++;
            if (index >= range.Length || range[index] is not ('[' or '(')) throw Invalid(range);
            var open = range[index++];
            var end = range.IndexOfAny([']', ')'], index);
            if (end < 0 || ++groups > 128) throw Invalid(range);
            var close = range[end];
            var body = range[index..end];
            var bounds = body.Split(',', StringSplitOptions.TrimEntries);
            if (bounds.Length == 1)
            {
                if (open != '[' || close != ']' || bounds[0].Length == 0) throw Invalid(range);
                matched |= Compare(version, ParseVersion(bounds[0])) == 0;
            }
            else if (bounds.Length == 2)
            {
                var lower = bounds[0].Length == 0 ? null : ParseVersion(bounds[0]);
                var upper = bounds[1].Length == 0 ? null : ParseVersion(bounds[1]);
                if ((lower is null && open != '(') || (upper is null && close != ')')) throw Invalid(range);
                if (lower is not null && upper is not null && Compare(lower, upper) > 0) throw Invalid(range);
                var lowerComparison = lower is null ? 1 : Compare(version, lower);
                var upperComparison = upper is null ? -1 : Compare(version, upper);
                matched |= (lowerComparison > 0 || (lowerComparison == 0 && open == '[')) &&
                           (upperComparison < 0 || (upperComparison == 0 && close == ']'));
            }
            else throw Invalid(range);
            index = end + 1;
            while (index < range.Length && char.IsWhiteSpace(range[index])) index++;
            if (index == range.Length) break;
            if (range[index++] != ',') throw Invalid(range);
            if (index == range.Length) throw Invalid(range);
        }
        return matched;
    }

    private static bool Comparisons(string range, NumericVersion version)
    {
        var index = 0;
        var accepted = true;
        var terms = 0;
        while (index < range.Length)
        {
            var match = TermPattern.Match(range, index);
            if (!match.Success || match.Index != index || ++terms > 128) throw Invalid(range);
            var operation = match.Groups["op"].Value;
            var value = match.Groups["value"].Value;
            index += match.Length;
            if (index == range.Length && range.TrimEnd().EndsWith(',')) throw Invalid(range);
            accepted &= Term(operation, value, version);
        }
        return terms != 0 ? accepted : throw Invalid(range);
    }

    private static bool Term(string operation, string value, NumericVersion version)
    {
        var parts = value.Split('.');
        var wildcard = Array.FindIndex(parts, part => part is "*" or "x" or "X");
        if (wildcard >= 0)
        {
            if (operation is not ("" or "=") || parts.Skip(wildcard).Any(part => part is not ("*" or "x" or "X")))
                throw Invalid(value);
            if (wildcard == 0) return true;
            var prefix = ParseVersion(string.Join('.', parts.Take(wildcard)));
            for (var index = 0; index < prefix.Numbers.Length; index++)
                if (Number(version, index) != prefix.Numbers[index]) return false;
            return true;
        }
        var boundary = ParseVersion(value);
        var comparison = Compare(version, boundary);
        if (operation is "~" or "^")
        {
            var upper = (long[])boundary.Numbers.Clone();
            var bump = operation == "~" ? Math.Min(1, upper.Length - 1) :
                Array.FindIndex(upper, number => number != 0);
            if (bump < 0) bump = upper.Length - 1;
            upper[bump] = checked(upper[bump] + 1);
            Array.Clear(upper, bump + 1, upper.Length - bump - 1);
            // Fabric's next-line boundary includes the '-' prerelease sentinel.
            return comparison >= 0 && Compare(version, new NumericVersion(upper, "")) < 0;
        }
        return operation switch
        {
            "" or "=" => comparison == 0,
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            _ => throw Invalid(value)
        };
    }

    private sealed record NumericVersion(long[] Numbers, string? Prerelease);

    private static NumericVersion ParseVersion(string text)
    {
        if (text is null || text.Length > 256) throw Invalid(text);
        var match = VersionPattern.Match(text.Trim());
        if (!match.Success) throw Invalid(text);
        var values = match.Groups["numbers"].Value.Split('.');
        var numbers = new long[values.Length];
        for (var index = 0; index < values.Length; index++)
            if (!long.TryParse(values[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]) || numbers[index] == long.MaxValue)
                throw Invalid(text);
        var prerelease = match.Groups["pre"];
        if (prerelease.Success && prerelease.Value.Length > 0 && prerelease.Value.Split('.').Any(string.IsNullOrEmpty)) throw Invalid(text);
        return new NumericVersion(numbers, prerelease.Success ? prerelease.Value : null);
    }

    private static long Number(NumericVersion value, int index) => index < value.Numbers.Length ? value.Numbers[index] : 0;

    private static int Compare(NumericVersion left, NumericVersion right)
    {
        for (var index = 0; index < Math.Max(left.Numbers.Length, right.Numbers.Length); index++)
        {
            var comparison = Number(left, index).CompareTo(Number(right, index));
            if (comparison != 0) return comparison;
        }
        if (left.Prerelease is null) return right.Prerelease is null ? 0 : 1;
        if (right.Prerelease is null) return -1;
        if (left.Prerelease.Length == 0 || right.Prerelease.Length == 0)
            return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
        var a = left.Prerelease.Split('.');
        var b = right.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(a.Length, b.Length); index++)
        {
            var an = long.TryParse(a[index], NumberStyles.None, CultureInfo.InvariantCulture, out var av);
            var bn = long.TryParse(b[index], NumberStyles.None, CultureInfo.InvariantCulture, out var bv);
            var comparison = an && bn ? av.CompareTo(bv) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[index], b[index]);
            if (comparison != 0) return comparison;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static InvalidDataException Invalid(string? value) => new($"Unsupported or malformed version range: {value}");
}
