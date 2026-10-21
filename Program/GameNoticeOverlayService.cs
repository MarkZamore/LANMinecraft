using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

/// <summary>Delivers notices to the launcher-owned renderer inside the game's swap chain.</summary>
public sealed class GameNoticeOverlayService
{
    private const string ResourceName = "Minecraft.NoticeOverlayAgent.jar";
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan HeartbeatLifetime = TimeSpan.FromSeconds(2);
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, OverlaySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<int> _foregroundProcess;
    private readonly Func<int, long?> _processStartTicks;
    private readonly Func<Stream> _agentResource;
    private IReadOnlyDictionary<string, string> _style = new Dictionary<string, string>();

    public GameNoticeOverlayService(AppPaths paths, Logger logger)
        : this(paths, logger, () => DateTimeOffset.UtcNow, ForegroundProcessId, ProcessStartTicks) { }

    internal GameNoticeOverlayService(AppPaths paths, Logger logger, Func<DateTimeOffset> now,
        Func<int> foregroundProcess, Func<int, long?> processStartTicks, Func<Stream>? agentResource = null)
    {
        _paths = paths;
        _logger = logger;
        _now = now;
        _foregroundProcess = foregroundProcess;
        _processStartTicks = processStartTicks;
        _agentResource = agentResource ?? OpenAgentResource;
        _paths.EnsureUnderRoot(Root);
        AdoptSessions();
    }

    private string Root => Path.Combine(_paths.Launcher, "NoticeOverlay");
    private string SessionsRoot => Path.Combine(Root, "Sessions");

    /// <summary>Styles are copied from the same resources as the desktop notice.</summary>
    internal void SetStyle(IReadOnlyDictionary<string, string> style)
    {
        lock (_gate)
        {
            _style = new Dictionary<string, string>(style, StringComparer.Ordinal);
            foreach (var session in _sessions.Values)
            {
                try { WriteProperties(Path.Combine(session.Directory, "style.properties"), _style); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn($"Could not refresh the in-game notice style: {ex.Message}");
                }
            }
        }
    }

    internal PreparedOverlay PrepareSession()
    {
        var agent = ExtractAgent();
        var directory = Path.Combine(SessionsRoot, Guid.NewGuid().ToString("N"));
        EnsureOrdinaryDirectory(directory);
        var session = new OverlaySession(directory, 0, 0);
        lock (_gate)
        {
            WriteProperties(Path.Combine(directory, "style.properties"), _style);
            WriteSnapshot(session);
            _sessions.Add(directory, session);
        }
        return new PreparedOverlay(this, session,
            [$"-javaagent:{agent}", $"-Dlanminecraft.notice.directory={directory}"]);
    }

    /// <summary>False leaves presentation to the desktop toast. No window or input is activated here.</summary>
    public bool TryShow(PeerNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        lock (_gate)
        {
            var processId = _foregroundProcess();
            var now = _now();
            foreach (var session in _sessions.Values)
            {
                if (session.ProcessId != processId || processId <= 0 ||
                    _processStartTicks(processId) != session.StartedUtcTicks || !IsRendererReady(session, now)) continue;
                session.Notices.RemoveAll(item => now - item.Created >= Lifetime);
                while (session.Notices.Count >= 3) session.Notices.RemoveAt(0);
                session.Notices.Add(new QueuedNotice(notice, now));
                try
                {
                    WriteSnapshot(session);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    session.Notices.RemoveAt(session.Notices.Count - 1);
                    _logger.Warn($"Could not publish an in-game notice: {ex.Message}");
                    return false;
                }
            }
            return false;
        }
    }

    private static bool IsRendererReady(OverlaySession session, DateTimeOffset now)
    {
        var heartbeat = ReadProperties(Path.Combine(session.Directory, "heartbeat.properties"));
        return string.IsNullOrEmpty(heartbeat.GetValueOrDefault("error")) &&
               int.TryParse(heartbeat.GetValueOrDefault("pid"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
               pid == session.ProcessId &&
               long.TryParse(heartbeat.GetValueOrDefault("timestampUnixMs"), NumberStyles.None, CultureInfo.InvariantCulture, out var stamp) &&
               stamp <= now.ToUnixTimeMilliseconds() && now.ToUnixTimeMilliseconds() - stamp <= HeartbeatLifetime.TotalMilliseconds &&
               heartbeat.GetValueOrDefault("backend") is "lwjgl2" or "lwjgl3";
    }

    private void WriteSnapshot(OverlaySession session)
    {
        var values = new Dictionary<string, string> { ["count"] = session.Notices.Count.ToString(CultureInfo.InvariantCulture) };
        for (var index = 0; index < session.Notices.Count; index++)
        {
            var item = session.Notices[index];
            var prefix = index.ToString(CultureInfo.InvariantCulture) + ".";
            values[prefix + "title"] = item.Notice.Title;
            values[prefix + "body"] = item.Notice.Body;
            values[prefix + "createdUnixMs"] = item.Created.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }
        WriteProperties(Path.Combine(session.Directory, "notices.properties"), values);
    }

    private string ExtractAgent()
    {
        using var resource = _agentResource();
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var directory = Path.Combine(Root, "Agent", hash);
        EnsureOrdinaryDirectory(directory);
        var target = Path.Combine(directory, "launcher-notice-agent.jar");
        if (File.Exists(target))
        {
            EnsureOrdinaryFile(target);
            using var existing = File.OpenRead(target);
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes))) return target;
        }
        AtomicWrite(target, bytes);
        return target;
    }

    private static Stream OpenAgentResource() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("The launcher has no fullscreen notice renderer. Rebuild with its Java tools enabled.");

    private void Bind(OverlaySession session, int processId, long startedUtcTicks)
    {
        lock (_gate)
        {
            session.ProcessId = processId;
            session.StartedUtcTicks = startedUtcTicks;
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new SessionMetadata(processId, startedUtcTicks));
            try { AtomicWrite(Path.Combine(session.Directory, "session.json"), metadata); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Process.Start already succeeded. A failed restart note must not
                // prevent the normal game monitor from taking ownership of it.
                _logger.Warn($"Could not persist the in-game notice session: {ex.Message}");
            }
        }
        _logger.Info($"Fullscreen notice renderer attached to game process {processId}.");
    }

    private void Forget(OverlaySession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session.Directory);
            session.Notices.Clear();
            try
            {
                WriteSnapshot(session);
                File.Delete(Path.Combine(session.Directory, "session.json"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"Could not clear a finished notice session: {ex.Message}");
            }
        }
    }

    internal void ProcessExited(int processId)
    {
        lock (_gate)
            foreach (var session in _sessions.Values.Where(item => item.ProcessId == processId).ToArray()) Forget(session);
    }

    private void AdoptSessions()
    {
        if (!Directory.Exists(SessionsRoot)) return;
        EnsureOrdinaryDirectory(SessionsRoot);
        foreach (var directory in Directory.EnumerateDirectories(SessionsRoot))
        {
            try
            {
                EnsureOrdinaryDirectory(directory);
                var metadataPath = Path.Combine(directory, "session.json");
                if (!File.Exists(metadataPath)) continue;
                EnsureOrdinaryFile(metadataPath);
                var metadata = JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllBytes(metadataPath));
                if (metadata is null || metadata.ProcessId <= 0 || _processStartTicks(metadata.ProcessId) != metadata.StartedUtcTicks) continue;
                var session = new OverlaySession(directory, metadata.ProcessId, metadata.StartedUtcTicks);
                // The previous launcher's messages must not reappear after a restart.
                WriteSnapshot(session);
                _sessions.Add(directory, session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.Warn($"Could not adopt an in-game notice session: {ex.Message}");
            }
        }
    }

    internal static void WriteProperties(string path, IReadOnlyDictionary<string, string> values)
    {
        static string Escape(string text)
        {
            var result = new StringBuilder(text.Length);
            foreach (var character in text)
            {
                result.Append(character switch
                {
                    '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                    '=' => "\\=", ':' => "\\:", '#' => "\\#", '!' => "\\!", ' ' => "\\ ",
                    _ => character.ToString()
                });
            }
            return result.ToString();
        }
        var text = string.Join("\n", values.Select(pair => Escape(pair.Key) + "=" + Escape(pair.Value))) + "\n";
        AtomicWrite(path, new UTF8Encoding(false).GetBytes(text));
    }

    private static Dictionary<string, string> ReadProperties(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            EnsureOrdinaryFile(path);
            if (new FileInfo(path).Length > 4096) return [];
            return File.ReadAllLines(path, Encoding.UTF8)
                .Where(line => line.Length > 0 && line[0] is not '#' and not '!')
                .Select(line => line.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        EnsureOrdinaryDirectory(directory);
        if (File.Exists(path)) EnsureOrdinaryFile(path);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void EnsureOrdinaryDirectory(string directory)
    {
        // Walk all existing parents before writing; a session may not redirect outside the portable tree.
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Notice overlay directory cannot be a reparse point.");
        Directory.CreateDirectory(directory);
    }

    private static void EnsureOrdinaryFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Notice overlay file cannot be a reparse point.");
    }

    private static long? ProcessStartTicks(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static int ForegroundProcessId()
    {
        _ = GetWindowThreadProcessId(PeerNoticeNative.GetForegroundWindow(), out var processId);
        return unchecked((int)processId);
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    internal sealed record QueuedNotice(PeerNotice Notice, DateTimeOffset Created);
    private sealed record SessionMetadata(int ProcessId, long StartedUtcTicks);

    internal sealed class OverlaySession(string directory, int processId, long startedUtcTicks)
    {
        internal string Directory { get; } = directory;
        internal int ProcessId { get; set; } = processId;
        internal long StartedUtcTicks { get; set; } = startedUtcTicks;
        internal List<QueuedNotice> Notices { get; } = [];
    }

    internal sealed class PreparedOverlay(GameNoticeOverlayService service, OverlaySession session, IReadOnlyList<string> jvmArguments) : IDisposable
    {
        private bool _bound;
        internal string Directory => session.Directory;
        internal IReadOnlyList<string> JvmArguments { get; } = jvmArguments;
        internal void BindProcess(Process process)
        {
            try { BindProcess(process.Id, process.StartTime.ToUniversalTime().Ticks); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // A JVM may exit before StartTime can be read. Its ordinary exit
                // monitor still has to own and report that failed startup.
                service._logger.Warn($"Game exited before the notice session was attached: {ex.Message}");
            }
        }
        internal void BindProcess(int processId, long startedUtcTicks)
        {
            service.Bind(session, processId, startedUtcTicks);
            _bound = true;
        }
        internal void ProcessExited() { service.Forget(session); _bound = false; }
        public void Dispose() { if (!_bound) service.Forget(session); }
    }
}
