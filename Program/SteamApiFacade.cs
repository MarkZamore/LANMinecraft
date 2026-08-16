using Steamworks;

namespace Minecraft;

/// <summary>
/// Steam friend as the launcher sees it: the account, its display name, and
/// whether that account is currently running something under the shared
/// App ID the launcher and e4steam use.
/// </summary>
public sealed record SteamFriendInfo(
    ulong SteamId64,
    string PersonaName,
    bool IsInSharedApp,
    ulong LobbyId);

/// <summary>
/// Every Steamworks call the launcher makes, behind one interface so the rest
/// of the code (and the tests) never touch the static Steamworks.NET API.
/// </summary>
public interface ISteamApiFacade
{
    bool Initialize(out string failureReason);
    void Shutdown();
    void RunCallbacks();
    bool IsSteamRunning();
    bool IsLoggedOn();
    ulong GetLocalSteamId();
    string GetPersonaName();
    void InitRelayNetworkAccess();
    IReadOnlyList<SteamFriendInfo> GetFriends();
    bool SetRichPresence(string key, string? value);
    string GetFriendRichPresence(ulong steamId64, string key);
    bool RequestFriendRichPresence(ulong steamId64);

    /// <summary>Presence keys Steam holds for a friend, whatever their origin.</summary>
    int GetFriendRichPresenceKeyCount(ulong steamId64);
}

/// <summary>The production implementation; the only place that links Steamworks.NET.</summary>
public sealed class SteamworksApiFacade : ISteamApiFacade
{
    /// <summary>
    /// Spacewar. Valve keeps it open for exactly this purpose, and it is what
    /// e4steam initialises inside the game, so both processes agree.
    /// </summary>
    public const uint SharedAppId = 480;

    private bool _initialized;

    public bool Initialize(out string failureReason)
    {
        failureReason = string.Empty;
        // Must be set before the first Steamworks call: without it the API
        // refuses to start outside a Steam-launched process.
        Environment.SetEnvironmentVariable("SteamAppId", SharedAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("SteamGameId", SharedAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // The launcher draws its own UI; the overlay has nothing to hook here.
        Environment.SetEnvironmentVariable("SteamNoOverlayUIDrawing", "1");

        try
        {
            var result = SteamAPI.InitEx(out var message);
            _initialized = result == ESteamAPIInitResult.k_ESteamAPIInitResult_OK;
            if (!_initialized)
            {
                failureReason = string.IsNullOrWhiteSpace(message) ? result.ToString() : message;
            }
            return _initialized;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    public void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;
        SteamAPI.Shutdown();
    }

    public void RunCallbacks()
    {
        if (!_initialized) return;
        SteamAPI.RunCallbacks();
    }

    public bool IsSteamRunning() => SteamAPI.IsSteamRunning();

    public bool IsLoggedOn() => _initialized && SteamUser.BLoggedOn();

    public ulong GetLocalSteamId() => _initialized ? SteamUser.GetSteamID().m_SteamID : 0UL;

    public string GetPersonaName() => _initialized ? SteamFriends.GetPersonaName() : string.Empty;

    public void InitRelayNetworkAccess()
    {
        if (!_initialized) return;
        SteamNetworkingUtils.InitRelayNetworkAccess();
    }

    public IReadOnlyList<SteamFriendInfo> GetFriends()
    {
        if (!_initialized) return [];
        var count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        if (count <= 0) return [];

        var friends = new List<SteamFriendInfo>(count);
        for (var index = 0; index < count; index++)
        {
            var id = SteamFriends.GetFriendByIndex(index, EFriendFlags.k_EFriendFlagImmediate);
            var inApp = SteamFriends.GetFriendGamePlayed(id, out var game) &&
                        game.m_gameID.AppID().m_AppId == SharedAppId;
            friends.Add(new SteamFriendInfo(
                id.m_SteamID,
                SteamFriends.GetFriendPersonaName(id),
                inApp,
                inApp ? game.m_steamIDLobby.m_SteamID : 0UL));
        }
        return friends;
    }

    public bool SetRichPresence(string key, string? value) =>
        _initialized && SteamFriends.SetRichPresence(key, value);

    public string GetFriendRichPresence(ulong steamId64, string key) =>
        _initialized ? SteamFriends.GetFriendRichPresence(new CSteamID(steamId64), key) : string.Empty;

    public bool RequestFriendRichPresence(ulong steamId64)
    {
        if (!_initialized) return false;
        SteamFriends.RequestFriendRichPresence(new CSteamID(steamId64));
        return true;
    }

    /// <summary>
    /// How many presence keys Steam is holding for a friend, of any origin.
    /// Valve labels this the debugging path and it answers the question that
    /// reading one key cannot: zero means Steam has no record of them at all,
    /// while a non-zero count without ours means they are publishing something
    /// else. Those are different problems and the launcher could not tell them
    /// apart while two players stared at each other's empty lists.
    /// </summary>
    public int GetFriendRichPresenceKeyCount(ulong steamId64) =>
        _initialized ? SteamFriends.GetFriendRichPresenceKeyCount(new CSteamID(steamId64)) : 0;
}
