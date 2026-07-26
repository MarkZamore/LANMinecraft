using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// Removes user content and credentials from diagnostic text before it leaves the machine.
/// Network addresses, player identities and interface identifiers are intentionally preserved.
/// </summary>
public sealed class SupportLogSanitizer
{
    private const string Redacted = "<REDACTED>";

    private static readonly Regex UserContentLine = new(
        @"(?:\[(?:CHAT|CHAT_PREVIEW|COMMAND)\]|ChatComponent|ServerChatEvent|" +
        @"(?:received|sending|signed|unsigned)\s+chat(?:\s+message)?|" +
        @"(?:^|:\s)(?:\[[^\]\r\n]+\]\s*)?<[^>\r\n]{1,64}>\s+|" +
        @"\b(?:chat|message)\s+from\s+[^:\r\n]+:\s*|" +
        @"[""'](?:chat|chat[_-]?message|command|command[_-]?text|command_history)[""']\s*:|" +
        @"issued\s+(?:a\s+)?server\s+command|execut(?:e|ed|ing)\s+(?:a\s+)?command|" +
        @"command\s+feedback|command_history|^\s*(?:chat|command)\s*:\s*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AuthorizationHeader = new(
        @"(?<prefix>\b(?:proxy-)?authorization\s*:\s*)(?:basic|bearer)?\s*[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CookieHeader = new(
        @"(?<prefix>\b(?:set-cookie|cookie)\s*:\s*)[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BearerCredential = new(
        @"\bbearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex JwtCredential = new(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]{8,})?\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex JsonSecret = new(
        @"(?<prefix>[""']?(?:token|access[_-]?token|refresh[_-]?token|id[_-]?token|" +
        @"client[_-]?(?:token|secret)|session(?:id|[_-]?token)?|password|passwd|authorization|" +
        @"auth[_-]?token|api[_-]?key|oauth[_-]?code|cookie|secret)[""']?\s*:\s*[""']?)(?<value>[^""'\s,}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AssignedSecret = new(
        @"(?<prefix>\b(?:token|access[_-]?token|refresh[_-]?token|id[_-]?token|" +
        @"client[_-]?(?:token|secret)|session(?:id|[_-]?token)?|password|passwd|authorization|" +
        @"auth[_-]?token|api[_-]?key|oauth[_-]?code|cookie|secret)\s*=\s*[""']?)(?<value>[^""'\s,;&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CommandLineSecret = new(
        @"(?<prefix>--(?:accessToken|refreshToken|clientToken|sessionToken|password|" +
        @"authorization|authToken|apiKey|oauthCode|clientSecret|cookie|secret|token)(?:=|\s+))(?<value>[""']?[^""'\s,;&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QuerySecret = new(
        @"(?<prefix>[?&](?:token|access[_-]?token|refresh[_-]?token|id[_-]?token|" +
        @"client[_-]?(?:token|secret)|session(?:id|[_-]?token)?|password|passwd|authorization|" +
        @"auth[_-]?token|api[_-]?key|oauth[_-]?code|cookie|secret)=)(?<value>[^&#\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EnvironmentSecret = new(
        @"(?<prefix>\b[A-Za-z0-9_]*(?:token|password|passwd|cookie|secret|authorization|" +
        @"api[_-]?key)[A-Za-z0-9_]*\s*=\s*)(?<value>[^\s,;&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LabelledSecret = new(
        @"(?<prefix>\b(?:access\s+token|refresh\s+token|session\s+token|password|passwd|" +
        @"cookie|authorization|client\s+secret|api\s+key)\s+(?:is|was)\s+)(?<value>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UriUserInfo = new(
        @"(?<prefix>\bhttps?://)[^/\s:@]+:[^@\s/]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QuotedSecret = new(
        @"(?<prefix>[""']?(?:token|access[_-]?token|refresh[_-]?token|id[_-]?token|" +
        @"client[_-]?(?:token|secret)|session(?:id|[_-]?token)?|password|passwd|authorization|" +
        @"auth[_-]?token|api[_-]?key|oauth[_-]?code|cookie|secret)[""']?\s*[:=]\s*"")" +
        @"(?<value>[^""\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SingleQuotedSecret = new(
        @"(?<prefix>[""']?(?:token|access[_-]?token|refresh[_-]?token|id[_-]?token|" +
        @"client[_-]?(?:token|secret)|session(?:id|[_-]?token)?|password|passwd|authorization|" +
        @"auth[_-]?token|api[_-]?key|oauth[_-]?code|cookie|secret)[""']?\s*[:=]\s*')" +
        @"(?<value>[^'\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IReadOnlyList<PathReplacement> _pathReplacements;

    public SupportLogSanitizer(string? userHome, string? applicationRoot)
    {
        var replacements = new List<PathReplacement>();
        AddPathReplacement(replacements, applicationRoot, "<APP_ROOT>");
        AddPathReplacement(replacements, userHome, "<USER_HOME>");
        _pathReplacements = replacements
            .OrderByDescending(item => item.Value.Length)
            .ToArray();
    }

    public static SupportLogSanitizer CreateDefault(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new SupportLogSanitizer(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            paths.Root);
    }

    /// <summary>
    /// Returns <see langword="null"/> for chat and command lines which must not be shared.
    /// </summary>
    public string? SanitizeLine(string? line)
    {
        if (line is null) return null;
        if (UserContentLine.IsMatch(line)) return null;

        var sanitized = ReplacePaths(line);
        sanitized = AuthorizationHeader.Replace(sanitized, match => match.Groups["prefix"].Value + Redacted);
        sanitized = CookieHeader.Replace(sanitized, match => match.Groups["prefix"].Value + Redacted);
        sanitized = BearerCredential.Replace(sanitized, "Bearer " + Redacted);
        sanitized = JwtCredential.Replace(sanitized, Redacted);
        sanitized = QuotedSecret.Replace(sanitized, ReplaceNamedValue);
        sanitized = SingleQuotedSecret.Replace(sanitized, ReplaceNamedValue);
        sanitized = JsonSecret.Replace(sanitized, ReplaceNamedValue);
        sanitized = AssignedSecret.Replace(sanitized, ReplaceNamedValue);
        sanitized = CommandLineSecret.Replace(sanitized, ReplaceNamedValue);
        sanitized = QuerySecret.Replace(sanitized, ReplaceNamedValue);
        sanitized = EnvironmentSecret.Replace(sanitized, ReplaceNamedValue);
        sanitized = LabelledSecret.Replace(sanitized, ReplaceNamedValue);
        sanitized = UriUserInfo.Replace(sanitized, match => match.Groups["prefix"].Value + Redacted + "@");
        return sanitized;
    }

    public string SanitizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var sanitized = SanitizeLine(line);
            if (sanitized is null) continue;
            builder.AppendLine(sanitized);
        }

        return builder.ToString();
    }

    public string SanitizeMetadataValue(string? value) =>
        SanitizeLine(value ?? string.Empty) ?? "<REMOVED_USER_CONTENT>";

    private string ReplacePaths(string text)
    {
        var result = text;
        foreach (var replacement in _pathReplacements)
        {
            result = result.Replace(replacement.Value, replacement.Placeholder, StringComparison.OrdinalIgnoreCase);
            result = result.Replace(
                replacement.Value.Replace('\\', '/'),
                replacement.Placeholder,
                StringComparison.OrdinalIgnoreCase);
            result = result.Replace(
                replacement.Value.Replace('/', '\\'),
                replacement.Placeholder,
                StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string ReplaceNamedValue(Match match) =>
        match.Groups["prefix"].Value + Redacted;

    private static void AddPathReplacement(
        ICollection<PathReplacement> replacements,
        string? value,
        string placeholder)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(value)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return;
        }

        var root = Path.GetPathRoot(normalized)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (normalized.Length == 0 ||
            string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!replacements.Any(item =>
                string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            replacements.Add(new PathReplacement(normalized, placeholder));
        }
    }

    private sealed record PathReplacement(string Value, string Placeholder);
}
