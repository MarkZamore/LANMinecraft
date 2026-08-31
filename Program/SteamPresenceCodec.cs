using System.Globalization;
using System.Text;

namespace Minecraft;

/// <summary>
/// What one launcher tells the others about itself. This is the successor of
/// the UDP announcement: the same facts, carried by Steam rich presence.
/// </summary>
public sealed record SteamPeerPresence
{
    public required SteamId64 SteamId { get; init; }
    public string PersonaName { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public string PlayerName { get; init; } = "";
    public string MinecraftUuid { get; init; } = "";
    public string PackHash { get; init; } = "";
    /// <summary>The build they are in, by the name of its folder.</summary>
    public string PackName { get; init; } = "";
    public string State { get; init; } = SteamPresenceCodec.StateIdle;
    public bool IsSkinAvailable { get; init; }
    public string SkinSha256 { get; init; } = "";
    public string SkinModel { get; init; } = "classic";
    public string HostedWorldId { get; init; } = "";
    public int WaypointProtocolVersion { get; init; }
    public IReadOnlyList<WaypointProviderAnnouncement> WaypointProviders { get; init; } = [];
    public int DiagnosticProtocolVersion { get; init; }

    public bool IsMinecraftRunning => State is SteamPresenceCodec.StateInGame or SteamPresenceCodec.StateHosting;
    public bool IsMinecraftPreparing => State == SteamPresenceCodec.StatePreparing;

    /// <summary>
    /// They said they were leaving. Steam keeps the last keys a launcher wrote
    /// long after the launcher is gone, and a peer whose keys merely stop being
    /// readable is given three minutes of grace because Steam drops them for no
    /// reason all the time. A goodbye is the difference between the two: it is
    /// a reading, not an absence, and it means the list can drop them at once.
    /// </summary>
    public bool HasLeft => State == SteamPresenceCodec.StateOffline;

    /// <summary>
    /// Seen in the app this launcher shares with e4steam, but publishing none
    /// of its keys - so they are in Minecraft, and not through here.
    /// </summary>
    public bool IsOutsideLauncher { get; init; }
}

/// <summary>
/// Packs and unpacks the launcher's rich-presence keys. Steam allows 20 keys of
/// 64 characters with 256-character values per user, and e4steam already uses
/// some of them, so this stays at ten short keys and truncates rather than
/// overflowing.
/// </summary>
public static class SteamPresenceCodec
{
    /// <summary>The launcher's one protocol version; see <see cref="PortableFormat"/>.</summary>
    public const int ProtocolVersion = PortableFormat.ProtocolVersion;

    public const int MaxValueLength = 256;
    public const string StateIdle = "idle";
    public const string StatePreparing = "preparing";
    public const string StateInGame = "ingame";
    public const string StateHosting = "hosting";

    /// <summary>
    /// Written once, on the way out, so friends can take this launcher off
    /// their list the moment they read it instead of waiting out the grace
    /// period for keys that Steam will happily keep serving.
    /// </summary>
    public const string StateOffline = "offline";

    internal const string MarkerKey = "lanmc";
    internal const string VersionKey = "lanmc_v";
    internal const string NameKey = "lanmc_name";
    internal const string UuidKey = "lanmc_uuid";
    internal const string PackKey = "lanmc_pack";
    internal const string StateKey = "lanmc_state";
    internal const string SkinKey = "lanmc_skin";
    internal const string WorldKey = "lanmc_world";
    internal const string WaypointsKey = "lanmc_wp";
    internal const string DiagnosticsKey = "lanmc_diag";
    internal const string BuildKey = "lanmc_build";

    /// <summary>Keys the launcher owns; nothing else is touched, e4steam's least of all.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        MarkerKey, VersionKey, NameKey, UuidKey, PackKey,
        StateKey, SkinKey, WorldKey, WaypointsKey, DiagnosticsKey, BuildKey
    ];

    public static IReadOnlyDictionary<string, string> Encode(SteamPeerPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MarkerKey] = "1",
            [VersionKey] = ProtocolVersion.ToString(CultureInfo.InvariantCulture),
            [NameKey] = Clamp(presence.PlayerName),
            [UuidKey] = Clamp(presence.MinecraftUuid),
            [PackKey] = Clamp(presence.PackHash),
            [StateKey] = Clamp(presence.State),
            [SkinKey] = presence.IsSkinAvailable
                ? Clamp($"{presence.SkinSha256}|{presence.SkinModel}")
                : "",
            [WorldKey] = Clamp(presence.HostedWorldId),
            [WaypointsKey] = EncodeWaypointProviders(
                presence.WaypointProtocolVersion,
                presence.WaypointProviders),
            [DiagnosticsKey] = presence.DiagnosticProtocolVersion.ToString(CultureInfo.InvariantCulture),
            // Added after the others, and read as absent rather than as wrong
            // where a friend's launcher is older than it: a build nobody named
            // is shown as "in the launcher", which is what it was before.
            [BuildKey] = Clamp(presence.PackName)
        };
        return values;
    }

    /// <summary>
    /// Rebuilds a peer from the keys Steam gives us. Returns null for friends
    /// who are not running this launcher, or who speak another version.
    /// </summary>
    public static SteamPeerPresence? TryDecode(
        SteamId64 peer,
        string personaName,
        Func<string, string> readKey)
    {
        ArgumentNullException.ThrowIfNull(readKey);
        if (!peer.IsValid) return null;
        if (readKey(MarkerKey) != "1") return null;
        _ = int.TryParse(readKey(VersionKey), NumberStyles.None, CultureInfo.InvariantCulture, out var version);

        // A friend running another version is still a friend running this
        // launcher. Hiding them was the worst possible answer: both players saw
        // an empty list and concluded the other was offline. They are listed,
        // marked incompatible, and told which side has to update.
        if (!PortableFormat.CanSpeak(version))
        {
            return new SteamPeerPresence
            {
                SteamId = peer,
                PersonaName = personaName,
                ProtocolVersion = version,
                PlayerName = readKey(NameKey)
            };
        }

        var (skinSha, skinModel) = DecodeSkin(readKey(SkinKey));
        var (waypointVersion, providers) = DecodeWaypointProviders(readKey(WaypointsKey));
        _ = int.TryParse(
            readKey(DiagnosticsKey),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var diagnosticsVersion);

        return new SteamPeerPresence
        {
            SteamId = peer,
            PersonaName = personaName,
            ProtocolVersion = version,
            PlayerName = readKey(NameKey),
            MinecraftUuid = readKey(UuidKey),
            PackHash = readKey(PackKey),
            PackName = readKey(BuildKey),
            State = NormalizeState(readKey(StateKey)),
            IsSkinAvailable = skinSha.Length > 0,
            SkinSha256 = skinSha,
            SkinModel = skinModel,
            HostedWorldId = readKey(WorldKey),
            WaypointProtocolVersion = waypointVersion,
            WaypointProviders = providers,
            DiagnosticProtocolVersion = diagnosticsVersion
        };
    }

    private static string NormalizeState(string value) => value switch
    {
        StatePreparing or StateInGame or StateHosting or StateOffline => value,
        _ => StateIdle
    };

    private static (string Sha256, string Model) DecodeSkin(string value)
    {
        var separator = value.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0) return ("", "classic");
        var sha = value[..separator];
        var model = value[(separator + 1)..];
        return sha.Length != 64 ? ("", "classic") : (sha, model == "slim" ? "slim" : "classic");
    }

    /// <summary>"&lt;version&gt;;&lt;id&gt;:&lt;modVersion&gt;:&lt;worldContext&gt;;…", truncated whole entries only.</summary>
    private static string EncodeWaypointProviders(
        int protocolVersion,
        IReadOnlyList<WaypointProviderAnnouncement> providers)
    {
        if (protocolVersion <= 0 || providers.Count == 0) return "";
        var builder = new StringBuilder(protocolVersion.ToString(CultureInfo.InvariantCulture));
        foreach (var provider in providers)
        {
            var entry = $";{provider.ProviderId}:{provider.ModVersion}:{provider.WorldContextId}";
            if (builder.Length + entry.Length > MaxValueLength) break;
            builder.Append(entry);
        }
        return builder.ToString();
    }

    private static (int Version, IReadOnlyList<WaypointProviderAnnouncement> Providers) DecodeWaypointProviders(
        string value)
    {
        if (value.Length == 0) return (0, []);
        var parts = value.Split(';');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return (0, []);
        }

        var providers = new List<WaypointProviderAnnouncement>(parts.Length - 1);
        foreach (var part in parts.Skip(1))
        {
            var fields = part.Split(':');
            if (fields.Length != 3 || fields[0].Length == 0) continue;
            providers.Add(new WaypointProviderAnnouncement
            {
                ProviderId = fields[0],
                ModVersion = fields[1],
                WorldContextId = fields[2]
            });
        }
        return (version, providers);
    }

    private static string Clamp(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var trimmed = value.Trim();
        return trimmed.Length <= MaxValueLength ? trimmed : trimmed[..MaxValueLength];
    }
}
