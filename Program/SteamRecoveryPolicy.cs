namespace Minecraft;

/// <summary>
/// What the window does when the Steam session changes state on its own.
/// </summary>
/// <remarks>
/// While Steam is away from its servers, nothing that needs other players
/// runs: the presence friends find this launcher by is not published and the
/// peer directory stops asking. The session itself survives that (see
/// <see cref="SteamClientService"/>), but those do not restart by themselves,
/// so the return from Reconnecting to Ready is where the window brings them
/// back. A first start is not that return - the Steam handshake starts the
/// network itself - and Ready staying Ready needs nothing.
/// </remarks>
public static class SteamRecoveryPolicy
{
    public static bool HasMatchingIdentity(SteamClientStatus? status, SteamIdentityBinding? binding) =>
        status is { IsReady: true } && binding is not null && binding.SteamId64.Value == status.SteamId64;

    // The live callback pump owns a temporary server disconnect. Every ended
    // session needs a fresh handshake, including Steam starting after us.
    public static bool ShouldConnect(SteamClientStatus status, bool identityBound) =>
        status.Availability != SteamAvailability.Starting &&
        status.Availability != SteamAvailability.Reconnecting &&
        (!status.IsReady || !identityBound);

    public static bool ShouldRestartNetworking(SteamAvailability previous, SteamAvailability current) =>
        previous == SteamAvailability.Reconnecting && current == SteamAvailability.Ready;
}
