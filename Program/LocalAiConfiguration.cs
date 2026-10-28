using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>Changes only local provider choices, preserving the player's other settings.</summary>
internal static class LocalAiConfiguration
{
    internal static void Apply(string instance, Uri endpoint, string model)
    {
        if (endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.Port < 1 || model != "qwen3:1.7b")
            throw new InvalidDataException("Invalid local Verity endpoint or model.");
        var path = LocalAiFiles.Under(instance, "config/verity-common.toml");
        if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Verity configuration is too large.");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["AISettings"] = new() { ["aiProvider"] = "\"OLLAMA\"", ["aiModel"] = JsonSerializer.Serialize(model),
                ["aiEndpoint"] = JsonSerializer.Serialize(endpoint.AbsoluteUri.TrimEnd('/')), ["aiThink"] = "false" },
            ["VoiceSettings"] = new() { ["ttsProvider"] = "\"LOCAL\"", ["useTTS"] = "true" },
            ["SpeechSettings"] = new() { ["sttProvider"] = "\"NATIVE\"" }
        };
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        string? table = null, multiline = null;
        var seenTables = new HashSet<string>(StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        void CompleteTable()
        {
            if (table is null || !values.TryGetValue(table, out var required)) return;
            result.AddRange(required.Where(pair => !seenKeys.Contains(table + "." + pair.Key)).Select(pair => pair.Key + " = " + pair.Value));
        }
        foreach (var line in lines)
        {
            var wasMultiline = multiline is not null;
            var comment = FindComment(line, ref multiline);
            if (wasMultiline) { result.Add(line); continue; }
            var code = (comment >= 0 ? line[..comment] : line).Trim();
            var header = Regex.Match(code, "^\\[([^\\[\\]]+)\\]$");
            if (header.Success)
            {
                CompleteTable(); table = header.Groups[1].Value.Trim().Trim('"', '\'');
                if (values.ContainsKey(table) && !seenTables.Add(table)) throw new InvalidDataException("Repeated Verity settings table.");
                result.Add(line); continue;
            }
            if (code.StartsWith('[')) { CompleteTable(); table = null; result.Add(line); continue; }
            var key = Regex.Match(code, "^(?<quote>[\"']?)(?<key>[A-Za-z0-9_]+)\\k<quote>\\s*=");
            if (table is not null && values.TryGetValue(table, out var settings) && key.Success &&
                settings.TryGetValue(key.Groups["key"].Value, out var replacement))
            {
                if (!seenKeys.Add(table + "." + key.Groups["key"].Value))
                    throw new InvalidDataException("Ambiguous local AI setting in Verity configuration.");
                // Voice is opt-in at the player's discretion. Supply defaults
                // for a fresh config, then keep every existing voice choice.
                if (table != "AISettings") { result.Add(line); continue; }
                if (multiline is not null) throw new InvalidDataException("Multiline local AI settings cannot be rewritten automatically.");
                var indent = line[..(line.Length - line.TrimStart().Length)];
                result.Add(indent + key.Groups["key"].Value + " = " + replacement + (comment >= 0 ? " " + line[comment..] : ""));
            }
            else
            {
                if (values.Keys.Any(name => code.StartsWith(name + ".", StringComparison.Ordinal) ||
                    code.StartsWith("\"" + name + "\".", StringComparison.Ordinal) || code.StartsWith("'" + name + "'.", StringComparison.Ordinal)))
                    throw new InvalidDataException("Dotted Verity settings cannot be rewritten automatically.");
                result.Add(line);
            }
        }
        if (multiline is not null) throw new InvalidDataException("Unclosed TOML multiline string.");
        CompleteTable();
        foreach (var missing in values.Where(pair => !seenTables.Contains(pair.Key)))
        {
            result.Add("[" + missing.Key + "]");
            result.AddRange(missing.Value.Select(pair => pair.Key + " = " + pair.Value));
        }
        var updated = string.Join(newline, result).TrimEnd('\r', '\n') + newline;
        AtomicFile.WriteAllTextIfChanged(path, updated);
    }

    private static int FindComment(string line, ref string? multiline)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            if (multiline is not null)
            {
                if (line.AsSpan(i).StartsWith(multiline, StringComparison.Ordinal)) { multiline = null; i += 2; }
                else if (multiline == "\"\"\"" && line[i] == '\\') i++;
            }
            else if (quote != '\0')
            {
                if (quote == '"' && line[i] == '\\') i++;
                else if (line[i] == quote) quote = '\0';
            }
            else if (line[i] == '#') return i;
            else if (line[i] is '"' or '\'')
            {
                if (i + 2 < line.Length && line[i + 1] == line[i] && line[i + 2] == line[i])
                { multiline = new string(line[i], 3); i += 2; }
                else quote = line[i];
            }
        }
        return -1;
    }
}
