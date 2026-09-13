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

    /// <summary>
    /// Steam's own account of its connection to its servers: lost, failed to
    /// come back, or back. Raised on the thread that runs callbacks.
    /// </summary>
    event Action<SteamServerConnectionChange>? ServerConnectionChanged;
}

/// <summary>One change in the Steam client's connection to Steam's servers.</summary>
/// <param name="Connected">True when the servers are back.</param>
/// <param name="Result">Steam's EResult for a loss; 1 (OK) when connected.</param>
/// <param name="StillRetrying">Whether Steam says it keeps trying by itself.</param>
public readonly record struct SteamServerConnectionChange(bool Connected, int Result, bool StillRetrying)
{
    /// <summary>
    /// The account signed in on another computer. Steam does not sign back in
    /// by itself after this one; the player has to.
    /// </summary>
    public bool SignedInElsewhere =>
        !Connected &&
        Result is (int)EResult.k_EResultLoggedInElsewhere
            or (int)EResult.k_EResultLogonSessionReplaced
            or (int)EResult.k_EResultAlreadyLoggedInElsewhere;
}

/// <summary>The production implementation; the only place that links Steamworks.NET.</summary>
public sealed class SteamworksApiFacade : ISteamApiFacade
{
    /// <summary>
    /// Spacewar. Valve keeps it open for exactly this purpose, and it is what
    /// e4steam initialises inside the game, so both processes agree.
    /// </summary>
    public const uint SharedAppId = 480;

    /// <summary>
    /// Steam reads this to decide whether to draw its overlay in a process.
    /// The launcher sets it for itself and clears it for the game, so keep the
    /// name in one place.
    /// </summary>
    public const string NoOverlayVariable = "SteamNoOverlayUIDrawing";

    private bool _initialized;
    private Callback<SteamServersConnected_t>? _serversConnected;
    private Callback<SteamServersDisconnected_t>? _serversDisconnected;
    private Callback<SteamServerConnectFailure_t>? _serverConnectFailure;

    public event Action<SteamServerConnectionChange>? ServerConnectionChanged;

    public bool Initialize(out string failureReason)
    {
        failureReason = string.Empty;
        // Must be set before the first Steamworks call: without it the API
        // refuses to start outside a Steam-launched process.
        Environment.SetEnvironmentVariable("SteamAppId", SharedAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("SteamGameId", SharedAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // The launcher draws its own UI; the overlay has nothing to hook here.
        // The game must not inherit this - see MinecraftProcessService.
        Environment.SetEnvironmentVariable(NoOverlayVariable, "1");

        try
        {
            var result = SteamAPI.InitEx(out var message);
            _initialized = result == ESteamAPIInitResult.k_ESteamAPIInitResult_OK;
            if (!_initialized)
            {
                failureReason = string.IsNullOrWhiteSpace(message) ? result.ToString() : message;
                return false;
            }

            // Registered once per facade: a retry initialises Steam again
            // without shutting it down first, and a second registration would
            // deliver every change twice. Shutdown releases them.
            _serversConnected ??= Callback<SteamServersConnected_t>.Create(_ =>
                ServerConnectionChanged?.Invoke(new SteamServerConnectionChange(true, (int)EResult.k_EResultOK, false)));
            _serversDisconnected ??= Callback<SteamServersDisconnected_t>.Create(lost =>
                ServerConnectionChanged?.Invoke(new SteamServerConnectionChange(false, (int)lost.m_eResult, true)));
            _serverConnectFailure ??= Callback<SteamServerConnectFailure_t>.Create(failed =>
                ServerConnectionChanged?.Invoke(new SteamServerConnectionChange(false, (int)failed.m_eResult, failed.m_bStillRetrying)));
            return true;
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
        _serversConnected?.Dispose();
        _serversDisconnected?.Dispose();
        _serverConnectFailure?.Dispose();
        _serversConnected = null;
        _serversDisconnected = null;
        _serverConnectFailure = null;
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
