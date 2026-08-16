namespace Minecraft;

/// <summary>
/// Bridges the running Steam session to the identity layer, so the identity
/// service never has to know about Steamworks or its lifetime.
/// </summary>
public sealed class SteamClientUserSource(SteamClientService client) : ISteamUserSource
{
    public bool TryGetLocalUser(out ulong steamId64, out string personaName)
    {
        var status = client.Status;
        steamId64 = status.SteamId64;
        personaName = status.PersonaName;
        return status.IsReady;
    }
}
