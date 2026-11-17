using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// Remembers, on disk, which game processes this launcher started.
///
/// The launcher knows a game is running because it holds the process it
/// started. Close the launcher while the game plays and that knowledge goes
/// with it: the next launcher offers "Играть" over a game already on screen,
/// and a second client would fight the first for the same instance folder. A
/// small file per process outlives the launcher, and a process id alone is not
/// enough to trust - ids are reused - so the moment it started is written down
/// beside it and has to match.
/// </summary>
/// <param name="paths">Where the launcher keeps its own data.</param>
/// <param name="logger">Where adopted or abandoned sessions are reported.</param>
public sealed class ClientPresenceService(AppPaths paths, Logger? logger = null)
{
    private const string DirectoryName = "ClientSessions";

    /// <summary>One running game, as the file records it.</summary>
    /// <param name="ProcessId">The process the launcher started.</param>
    /// <param name="StartedUtc">When it started, to the second; ids are reused, moments are not.</param>
    /// <param name="PackRelativePath">Which pack it plays, so the window can say so.</param>
    public sealed record ClientSession(int ProcessId, DateTime StartedUtc, string PackRelativePath)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? ExecutablePath { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ComponentHashes { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? ComponentRevision { get; init; }
    }

    private string Root => Path.Combine(paths.Personal, DirectoryName);

    /// <summary>Writes the note that says this process is playing.</summary>
    public void Remember(Process process, string packRelativePath, string? executablePath = null,
        IReadOnlyCollection<string>? componentHashes = null, string? componentRevision = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (componentHashes?.Any(hash => !IsComponentHash(hash)) == true)
            throw new ArgumentException("Invalid component hash in game session.", nameof(componentHashes));
        try
        {
            var session = new ClientSession(process.Id, process.StartTime.ToUniversalTime(), packRelativePath)
            {
                ExecutablePath = executablePath,
                ComponentHashes = componentHashes?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ComponentRevision = componentRevision
            };
            Directory.CreateDirectory(Root);
            AtomicFile.WriteAllText(
                PathFor(process.Id),
                JsonSerializer.Serialize(session, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger?.Warn($"The running game could not be written down ({ex.Message}); a launcher restarted now would not see it.");
        }
    }

    /// <summary>Forgets one process, whether it ended well or not.</summary>
    public void Forget(int processId)
    {
        try
        {
            var path = PathFor(processId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"A finished game could not be forgotten ({ex.Message}): {PathFor(processId)}");
        }
    }

    /// <summary>
    /// The sessions still running, by the notes on disk; notes for processes
    /// that are gone are deleted on the way past.
    /// </summary>
    public IReadOnlyList<ClientSession> ReadLiveSessions(Func<int, (string Name, DateTime StartedUtc)?>? probe = null)
    {
        var live = new List<ClientSession>();
        if (!Directory.Exists(Root)) return live;

        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            ClientSession? session = null;
            try
            {
                session = JsonSerializer.Deserialize<ClientSession>(File.ReadAllText(file), JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }
            if (session is null || session.ProcessId <= 0 || !PackCatalogService.ValidFolder(session.PackRelativePath)) continue;
            (string Name, DateTime StartedUtc)? actual;
            if (probe is not null) actual = probe(session.ProcessId);
            else if (!TryProbeProcess(session.ProcessId, session.ExecutablePath is not null, out actual)) continue;
            if (IsStillRunning(session, actual))
            {
                live.Add(session);
                continue;
            }
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return live;
    }

    /// <summary>Returns null when an active game's component references cannot be established.</summary>
    internal IReadOnlySet<string>? ReadComponentReferences(Func<int, (string Name, DateTime StartedUtc)?>? probe = null)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(Root)) return hashes;
            if (!MaintenancePath.TryRead(paths, Root, out var folder) || !folder.HasFlag(FileAttributes.Directory)) return null;
            foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
            {
                if (!MaintenancePath.TryRead(paths, file, out var attributes) || attributes.HasFlag(FileAttributes.Directory))
                    return null;
                var session = JsonSerializer.Deserialize<ClientSession>(File.ReadAllText(file), JsonOptions);
                if (session is null || session.ProcessId <= 0 || !PackCatalogService.ValidFolder(session.PackRelativePath)) return null;
                (string Name, DateTime StartedUtc)? actual;
                if (probe is not null) actual = probe(session.ProcessId);
                else if (!TryProbeProcess(session.ProcessId, session.ExecutablePath is not null, out actual)) return null;
                if (!IsStillRunning(session, actual)) continue;
                if (session.ComponentHashes is null)
                {
                    // An older Java process can hold extracted agents from before the shared cache.
                    if (session.ExecutablePath is null) return null;
                    continue;
                }
                if (session.ComponentHashes.Any(hash => !IsComponentHash(hash))) return null;
                hashes.UnionWith(session.ComponentHashes);
            }
            return hashes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsComponentHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);

    /// <summary>
    /// Whether the note still describes the process that is running under that
    /// id. A different name, or a different moment of starting, means the id
    /// was handed to someone else after the game ended.
    /// </summary>
    public static bool IsStillRunning(ClientSession session, (string Name, DateTime StartedUtc)? actual)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (actual is null) return false;
        if (session.ExecutablePath is { } executable
                ? !Path.IsPathFullyQualified(executable) || !executable.Equals(actual.Value.Name, StringComparison.OrdinalIgnoreCase)
                : !actual.Value.Name.StartsWith("java", StringComparison.OrdinalIgnoreCase)) return false;
        // Process start times come back with more precision than a round trip
        // through JSON keeps, so a second of slack is the honest comparison.
        return Math.Abs((actual.Value.StartedUtc - session.StartedUtc).TotalSeconds) < 1.5;
    }

    private static bool TryProbeProcess(int processId, bool native, out (string Name, DateTime StartedUtc)? actual)
    {
        actual = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = native ? process.MainModule?.FileName : process.ProcessName;
            if (name is null) return false;
            actual = (name, process.StartTime.ToUniversalTime());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true;
        }
        catch (SystemException)
        {
            return false;
        }
    }

    private string PathFor(int processId) =>
        Path.Combine(Root, processId.ToString(CultureInfo.InvariantCulture) + ".json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
