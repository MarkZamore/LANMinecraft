namespace Minecraft;

/// <summary>
/// The players who were here before Steam, and the Minecraft UUIDs their
/// progress already lives under. Everyone else's UUID is derived from their
/// Steam account (<see cref="SteamIdentityDerivation"/>), which is the same on
/// every machine; for these three the UUID predates that rule, so the launcher
/// keeps playing them as the profiles that hold their quests, FTB teams, homes
/// and inventories in the shared world.
///
/// The list is not meant to grow: a new player never needs an entry, because
/// derivation gives them a stable UUID from day one.
/// </summary>
public static class KnownSteamPlayers
{
    /// <summary>A Steam account and the profile it plays as.</summary>
    public sealed record KnownPlayer(ulong SteamId64, Guid PlayerUuid, string Name);

    public static IReadOnlyList<KnownPlayer> All { get; } =
    [
        new(76561198256236531, new Guid("06c83c9e-980b-47d5-b7be-23d2bb649068"), "MarkZamore"),
        new(76561198050776152, new Guid("f0f5ec1a-14f5-47b6-9e27-b860f62c14e5"), "anuvenn"),
        new(76561198088743612, new Guid("a4c56fa5-a630-42a6-9223-d6abfe63b130"), "ASSin"),
    ];

    private static readonly Dictionary<ulong, KnownPlayer> BySteamId =
        All.ToDictionary(player => player.SteamId64);

    private static readonly Dictionary<Guid, KnownPlayer> ByUuid =
        All.ToDictionary(player => player.PlayerUuid);

    /// <summary>The profile a Steam account plays as, when it is one of the three.</summary>
    public static bool TryGetPlayer(SteamId64 steamId, out KnownPlayer player)
    {
        if (steamId.IsValid && BySteamId.TryGetValue(steamId.Value, out var known))
        {
            player = known;
            return true;
        }
        player = null!;
        return false;
    }

    /// <summary>The Steam account behind a profile, when the profile is one of the three.</summary>
    public static bool TryGetSteamId(Guid playerUuid, out SteamId64 steamId)
    {
        if (ByUuid.TryGetValue(playerUuid, out var known) && SteamId64.TryFrom(known.SteamId64, out steamId))
        {
            return true;
        }
        steamId = SteamId64.None;
        return false;
    }

    /// <summary>Same as <see cref="TryGetSteamId(Guid, out SteamId64)"/> for a UUID in text form.</summary>
    public static bool TryGetSteamId(string? playerUuid, out SteamId64 steamId)
    {
        if (Guid.TryParse(playerUuid, out var uuid)) return TryGetSteamId(uuid, out steamId);
        steamId = SteamId64.None;
        return false;
    }
}
