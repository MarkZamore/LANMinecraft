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
    public sealed record ClientSession(int ProcessId, DateTime StartedUtc, string PackRelativePath);

    private string Root => Path.Combine(paths.Personal, DirectoryName);

    /// <summary>Writes the note that says this process is playing.</summary>
    public void Remember(Process process, string packRelativePath)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            var session = new ClientSession(process.Id, process.StartTime.ToUniversalTime(), packRelativePath);
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
        probe ??= ProbeProcess;
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
                session = null;
            }

            if (session is not null && IsStillRunning(session, probe(session.ProcessId)))
            {
                live.Add(session);
                continue;
            }
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return live;
    }

    /// <summary>
    /// Whether the note still describes the process that is running under that
    /// id. A different name, or a different moment of starting, means the id
    /// was handed to someone else after the game ended.
    /// </summary>
    public static bool IsStillRunning(ClientSession session, (string Name, DateTime StartedUtc)? actual)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (actual is null) return false;
        if (!actual.Value.Name.StartsWith("java", StringComparison.OrdinalIgnoreCase)) return false;
        // Process start times come back with more precision than a round trip
        // through JSON keeps, so a second of slack is the honest comparison.
        return Math.Abs((actual.Value.StartedUtc - session.StartedUtc).TotalSeconds) < 1.5;
    }

    private static (string Name, DateTime StartedUtc)? ProbeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return (process.ProcessName, process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SystemException)
        {
            return null;
        }
    }

    private string PathFor(int processId) =>
        Path.Combine(Root, processId.ToString(CultureInfo.InvariantCulture) + ".json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
