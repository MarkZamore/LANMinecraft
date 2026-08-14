using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

// Minecraft exposes a peer's world as a LAN server on a loopback port, and mods
// key their per-world data by the address they joined - JEI stores bookmarks
// under config/jei/world/server/<motd>_<hash of ip:port>. A port that moves
// between sessions therefore orphans those bookmarks, so each peer gets a
// deterministic port derived from its identity, remembered across restarts.
public sealed class LanRelayPortStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxEntries = 64;
    // Below the Windows dynamic range (49152-65535): a port the OS hands out to
    // any process on demand cannot be relied on to still be free next session.
    private const int FirstDerivedPort = 30000;
    private const int DerivedPortCount = 10000;
    private const int DerivedCandidateCount = 8;

    private readonly string _path;
    private readonly Logger _logger;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public LanRelayPortStore(AppPaths paths, Logger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.LanRelayPortsFile;
        _logger = logger;
    }

    // Stable for a given peer without any stored state, so bookmarks survive
    // even when the history file is lost. Consecutive candidates let two peers
    // that collide on the first one still get stable ports of their own.
    public static IReadOnlyList<int> DerivePortCandidates(string? peerId)
    {
        var key = NormalizeKey(peerId);
        if (key.Length == 0) return [];
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var offset = (int)(BitConverter.ToUInt32(digest, 0) % DerivedPortCount);
        var candidates = new int[DerivedCandidateCount];
        for (var index = 0; index < candidates.Length; index++)
        {
            candidates[index] = FirstDerivedPort + ((offset + index) % DerivedPortCount);
        }
        return candidates;
    }

    public int GetRememberedPort(string? peerId)
    {
        var key = NormalizeKey(peerId);
        if (key.Length == 0) return 0;
        lock (_gate)
        {
            var entries = Load();
            return entries.TryGetValue(key, out var entry) && IsUsablePort(entry.Port) ? entry.Port : 0;
        }
    }

    public void RememberPort(string? peerId, int port)
    {
        var key = NormalizeKey(peerId);
        if (key.Length == 0 || !IsUsablePort(port)) return;
        lock (_gate)
        {
            // Reload before writing so a second launcher process cannot have its
            // entries dropped by a stale snapshot.
            var entries = Load();
            entries[key] = new PortEntry { Port = port, UsedAtUtc = DateTimeOffset.UtcNow };
            Prune(entries);
            Save(entries);
        }
    }

    private static void Prune(Dictionary<string, PortEntry> entries)
    {
        if (entries.Count <= MaxEntries) return;
        foreach (var stale in entries
                     .OrderByDescending(pair => pair.Value.UsedAtUtc)
                     .Skip(MaxEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            entries.Remove(stale);
        }
    }

    private Dictionary<string, PortEntry> Load()
    {
        var entries = new Dictionary<string, PortEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_path)) return entries;
            var state = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(_path), _jsonOptions);
            if (state?.SchemaVersion != CurrentSchemaVersion || state.Ports is null) return entries;
            foreach (var (peerId, entry) in state.Ports)
            {
                var key = NormalizeKey(peerId);
                if (key.Length == 0 || entry is null || !IsUsablePort(entry.Port)) continue;
                entries[key] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warn($"LAN relay port history could not be read and will be rebuilt: {ex.Message}");
            entries.Clear();
        }

        return entries;
    }

    private void Save(Dictionary<string, PortEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(
                _path,
                JsonSerializer.Serialize(
                    new StoredState { SchemaVersion = CurrentSchemaVersion, Ports = entries },
                    _jsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The derived candidate still keeps the port stable next session.
            _logger.Warn($"LAN relay port history could not be saved: {ex.Message}");
        }
    }

    private static bool IsUsablePort(int port) => port is > 1024 and <= 65535;

    private static string NormalizeKey(string? peerId) =>
        Guid.TryParse(peerId, out var value) ? value.ToString("D") : "";

    private sealed class StoredState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public Dictionary<string, PortEntry> Ports { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record PortEntry
    {
        public int Port { get; init; }
        public DateTimeOffset UsedAtUtc { get; init; }
    }
}
