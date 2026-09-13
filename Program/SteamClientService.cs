using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Minecraft;

public enum SteamAvailability
{
    NotStarted,
    Starting,
    Ready,
    SteamNotRunning,
    NotLoggedIn,
    Failed,
    Reconnecting
}

/// <summary>What the UI needs to render the Steam line, in one immutable value.</summary>
public sealed record SteamClientStatus(
    SteamAvailability Availability,
    ulong SteamId64,
    string PersonaName,
    string Message)
{
    public bool IsReady => Availability == SteamAvailability.Ready && SteamId64 != 0;

    public static SteamClientStatus NotStarted { get; } =
        new(SteamAvailability.NotStarted, 0, string.Empty, "Steam ещё не подключён.");
}

/// <summary>
/// Owns the launcher's Steam session: initialisation, the callback pump, the
/// local account and the friend list. Failures never propagate to the caller -
/// they become a status the window can show with a "Повторить" button, so a
/// missing or signed-out Steam can never keep the launcher from opening.
/// </summary>
public sealed class SteamClientService : IAsyncDisposable
{
    private const string SteamNotRunningMessage =
        "Steam не запущен. Запустите Steam, войдите в аккаунт и нажмите «Повторить».";
    private const string NotLoggedInMessage =
        "Steam запущен, но вход в аккаунт не выполнен. Войдите в Steam и нажмите «Повторить».";
    private const string FailedMessagePrefix = "Не удалось подключиться к Steam: ";

    private static readonly TimeSpan IdlePumpInterval = TimeSpan.FromMilliseconds(50);
    /// <summary>How often the pump asks whether Steam is still there.</summary>
    private static readonly TimeSpan DefaultLivenessCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a session waits for Steam to sign back in. A lost connection
    /// comes back within seconds to a minute; past this it is not coming back
    /// by itself, and the player gets the button again.
    /// </summary>
    private static readonly TimeSpan DefaultReconnectLimit = TimeSpan.FromMinutes(2);

    /// <summary>How long shutdown waits for the callback thread to leave Steam.</summary>
    private static readonly TimeSpan PumpStopTimeout = TimeSpan.FromSeconds(5);

    internal const string SteamLostMessage =
        "Steam закрылся или выполнен выход из аккаунта. Запустите Steam и нажмите «Повторить».";

    internal const string ReconnectingMessage =
        "Нет связи с серверами Steam. Лаунчер ждёт, пока Steam подключится снова, и вернётся сам.";

    internal const string LoggedInElsewhereMessage =
        "В этот аккаунт Steam вошли на другом компьютере. Войдите в Steam снова, и лаунчер вернётся сам.";

    internal const string ReconnectTimedOutMessage =
        "Steam так и не подключился снова к своим серверам. Проверьте Steam и нажмите «Повторить».";

    internal const string AccountChangedMessage =
        "Steam вошёл в другой аккаунт. Нажмите «Повторить», чтобы играть под ним.";

    private static readonly TimeSpan FriendsRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly ISteamApiFacade _api;
    private readonly SteamNativeLibraryService? _native;
    private readonly Logger? _logger;
    private readonly TimeSpan _livenessCheckInterval;
    private readonly TimeSpan _reconnectLimit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _stateGate = new();

    private Thread? _pump;
    private CancellationTokenSource? _pumpCts;
    private IReadOnlyList<SteamFriendInfo> _friends = [];
    private SteamClientStatus _status = SteamClientStatus.NotStarted;
    private int _serversLost;
    private int _signedInElsewhere;
    private DateTimeOffset? _reconnectingSince;
    private bool _disposed;

    public SteamClientService(
        ISteamApiFacade api,
        SteamNativeLibraryService? native = null,
        Logger? logger = null,
        TimeSpan? livenessCheckInterval = null,
        TimeSpan? reconnectLimit = null)
    {
        _api = api;
        _native = native;
        _logger = logger;
        _livenessCheckInterval = livenessCheckInterval ?? DefaultLivenessCheckInterval;
        _reconnectLimit = reconnectLimit ?? DefaultReconnectLimit;
        _api.ServerConnectionChanged += OnServerConnectionChanged;
    }

    public event EventHandler<SteamClientStatus>? StatusChanged;
    public event EventHandler<IReadOnlyList<SteamFriendInfo>>? FriendsChanged;

    /// <summary>
    /// The one initialised Steamworks facade. Anything that reads Steam
    /// state must go through this instance - a second facade is never
    /// initialised and answers every question with "nothing".
    /// </summary>
    public ISteamApiFacade Api => _api;

    public SteamClientStatus Status
    {
        get { lock (_stateGate) return _status; }
    }

    public IReadOnlyList<SteamFriendInfo> Friends
    {
        get { lock (_stateGate) return _friends; }
    }

    /// <summary>
    /// Connects to the running Steam client. Returns the resulting status; it
    /// is also published through <see cref="StatusChanged"/>. Safe to call
    /// again to retry after the player starts Steam.
    /// </summary>
    public async Task<SteamClientStatus> StartAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // One reading of the status for every decision here: the pump
            // publishes on its own thread and does not wait for this gate.
            var current = Status;
            var pumpAlive = _pump is { IsAlive: true };
            // A session waiting for Steam's servers is still a session: the
            // pump brings it back by itself, or gives up and says so.
            if (current.Availability == SteamAvailability.Reconnecting && pumpAlive) return current;
            // A status of Ready is only trustworthy while Steam is still
            // there; if it went away, this call is the recovery path.
            if (current.IsReady && IsSteamAlive()) return current;
            if (current.IsReady || pumpAlive) TearDownApi("Steam закрылся; переподключение.");
            Publish(new SteamClientStatus(SteamAvailability.Starting, 0, string.Empty, "Подключение к Steam…"));

            try
            {
                _native?.Prepare();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                _logger?.Warn($"Steam native could not be prepared: {ex.Message}");
                return Publish(new SteamClientStatus(
                    SteamAvailability.Failed, 0, string.Empty, FailedMessagePrefix + ex.Message));
            }

            if (!_api.IsSteamRunning())
            {
                return Publish(new SteamClientStatus(
                    SteamAvailability.SteamNotRunning, 0, string.Empty, SteamNotRunningMessage));
            }

            if (!_api.Initialize(out var failureReason))
            {
                var availability = _api.IsSteamRunning()
                    ? SteamAvailability.NotLoggedIn
                    : SteamAvailability.SteamNotRunning;
                var message = availability == SteamAvailability.SteamNotRunning
                    ? SteamNotRunningMessage
                    : NotLoggedInMessage;
                _logger?.Warn($"Steam initialization failed: {failureReason}");
                return Publish(new SteamClientStatus(availability, 0, string.Empty, message));
            }

            if (!_api.IsLoggedOn())
            {
                _api.Shutdown();
                return Publish(new SteamClientStatus(
                    SteamAvailability.NotLoggedIn, 0, string.Empty, NotLoggedInMessage));
            }

            _api.InitRelayNetworkAccess();
            var steamId = _api.GetLocalSteamId();
            var persona = _api.GetPersonaName();
            Volatile.Write(ref _serversLost, 0);
            Volatile.Write(ref _signedInElsewhere, 0);
            // Read the friend list before anyone is told the session is ready:
            // it is what decides who may connect, so an empty one for the first
            // seconds means refusing friends and showing nobody online.
            RefreshFriends();
            StartPump();
            _logger?.Info($"Steam connected as {persona} ({steamId}).");
            return Publish(ReadyStatus(steamId, persona));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Publishes one of the launcher's own rich-presence keys.</summary>
    public bool SetPresence(string key, string? value) => Status.IsReady && _api.SetRichPresence(key, value);

    /// <summary>Reads a rich-presence key another launcher published.</summary>
    public string GetFriendPresence(ulong steamId64, string key) =>
        Status.IsReady ? _api.GetFriendRichPresence(steamId64, key) : string.Empty;

    private static SteamClientStatus ReadyStatus(ulong steamId, string persona) =>
        new(SteamAvailability.Ready,
            steamId,
            persona,
            $"Steam: {persona} ({steamId.ToString(CultureInfo.InvariantCulture)})");

    private void StartPump()
    {
        // A pump that died on a Steam failure is replaced when the player
        // retries; the old thread has already returned.
        if (_pump is { IsAlive: true }) return;
        _pumpCts?.Dispose();
        _pumpCts = new CancellationTokenSource();
        var session = _pumpCts.Token;
        _reconnectingSince = null;
        _pump = new Thread(() => PumpLoop(session))
        {
            IsBackground = true,
            Name = "steam-callbacks"
        };
        _pump.Start();
    }

    private void PumpLoop(CancellationToken session)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, session);
        var nextFriendsRefresh = DateTimeOffset.MinValue;
        var nextLivenessCheck = DateTimeOffset.UtcNow + _livenessCheckInterval;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                _api.RunCallbacks();
                var now = DateTimeOffset.UtcNow;
                if (now >= nextLivenessCheck)
                {
                    nextLivenessCheck = now + _livenessCheckInterval;
                    // Steam closing does not raise anything: RunCallbacks simply
                    // stops doing work. Without this the window would go on
                    // claiming a connection nobody has.
                    if (!CheckLiveness(now)) return;
                }
                // While Steam is away from its servers the friend list comes
                // back empty, and an empty list is read as "nobody may connect".
                if (Status.Availability == SteamAvailability.Ready && now >= nextFriendsRefresh)
                {
                    nextFriendsRefresh = now + FriendsRefreshInterval;
                    RefreshFriends();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
            {
                _logger?.Warn($"Steam callback pump stopped: {ex.Message}");
                Publish(new SteamClientStatus(
                    SteamAvailability.Failed, 0, string.Empty, FailedMessagePrefix + ex.Message));
                return;
            }

            try
            {
                stop.Token.WaitHandle.WaitOne(IdlePumpInterval);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Asks whether the session is still there, and what kind of gone it is.
    /// False when the session has ended and the pump must stop.
    /// </summary>
    /// <remarks>
    /// A closed Steam and a Steam that lost its servers both answer "not signed
    /// in", and they are nothing alike. A Steam that lost its connection - a
    /// network blip, a laptop waking up - is still running, says so through
    /// SteamServersDisconnected, retries by itself and is usually back within
    /// seconds. The launcher used to end its session on the first such blip and
    /// stayed without Steam until somebody pressed "Повторить", which guests
    /// found out about by being unable to play. That session is kept now, but
    /// only when Steam itself reported losing its servers, and only for a while:
    /// a Steam that restarted between two checks also answers "running, not
    /// signed in", and the session this launcher holds is not the one it has.
    /// </remarks>
    private bool CheckLiveness(DateTimeOffset now)
    {
        bool running;
        bool signedIn;
        try
        {
            running = _api.IsSteamRunning();
            signedIn = running && _api.IsLoggedOn();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            running = false;
            signedIn = false;
        }

        var current = Status;
        if (!running)
        {
            _logger?.Warn("Steam is no longer running; the session ended.");
            Publish(new SteamClientStatus(
                SteamAvailability.SteamNotRunning, 0, string.Empty, SteamLostMessage));
            return false;
        }

        if (!signedIn)
        {
            if (current.Availability != SteamAvailability.Reconnecting && Volatile.Read(ref _serversLost) == 0)
            {
                // Not signed in, and Steam never said it lost its servers: this
                // is a sign-out or a restarted Steam, not a blip to wait out.
                _logger?.Warn("Steam is not signed in and did not report losing its servers; the session ended.");
                Publish(new SteamClientStatus(
                    SteamAvailability.SteamNotRunning, 0, string.Empty, SteamLostMessage));
                return false;
            }

            _reconnectingSince ??= now;
            if (now - _reconnectingSince.Value >= _reconnectLimit)
            {
                _logger?.Warn($"Steam did not sign back in within {_reconnectLimit.TotalSeconds:0} s; the session ended.");
                Publish(new SteamClientStatus(
                    SteamAvailability.SteamNotRunning, 0, string.Empty, ReconnectTimedOutMessage));
                return false;
            }

            var message = Volatile.Read(ref _signedInElsewhere) == 1
                ? LoggedInElsewhereMessage
                : ReconnectingMessage;
            if (current.Availability != SteamAvailability.Reconnecting)
            {
                _logger?.Warn("Steam is running but lost its servers; waiting for it to sign back in.");
            }
            if (current.Availability != SteamAvailability.Reconnecting || current.Message != message)
            {
                Publish(new SteamClientStatus(
                    SteamAvailability.Reconnecting, current.SteamId64, current.PersonaName, message));
            }
            return true;
        }

        _reconnectingSince = null;
        if (current.Availability != SteamAvailability.Reconnecting) return true;

        var steamId = _api.GetLocalSteamId();
        var persona = _api.GetPersonaName();
        if (steamId != current.SteamId64)
        {
            // Everything bound to the old account - the profile played as, who
            // may connect - would be wrong under the new one.
            _logger?.Warn($"Steam signed back in as another account ({steamId}); the session ended.");
            Publish(new SteamClientStatus(SteamAvailability.NotLoggedIn, 0, string.Empty, AccountChangedMessage));
            return false;
        }

        _logger?.Info($"Steam is signed in to its servers again as {persona} ({steamId}); the session carries on.");
        RefreshFriends();
        Publish(ReadyStatus(steamId, persona));
        return true;
    }

    /// <summary>
    /// Writes down what Steam said about its servers. The pump decides what the
    /// launcher does; this only remembers whether Steam reported losing them,
    /// and whether it was because the account signed in somewhere else.
    /// </summary>
    private void OnServerConnectionChanged(SteamServerConnectionChange change)
    {
        if (change.Connected)
        {
            Volatile.Write(ref _serversLost, 0);
            Volatile.Write(ref _signedInElsewhere, 0);
            _logger?.Info("Steam servers connected.");
            return;
        }

        Volatile.Write(ref _serversLost, 1);
        Volatile.Write(ref _signedInElsewhere, change.SignedInElsewhere ? 1 : 0);
        _logger?.Warn(
            $"Steam servers disconnected (EResult {change.Result}" +
            (change.StillRetrying ? ", Steam keeps retrying)." : ")."));
    }

    private void RefreshFriends()
    {
        var friends = _api.GetFriends();
        bool changed;
        lock (_stateGate)
        {
            changed = !_friends.SequenceEqual(friends);
            if (changed) _friends = friends;
        }
        if (changed) FriendsChanged?.Invoke(this, friends);
    }

    private SteamClientStatus Publish(SteamClientStatus status)
    {
        lock (_stateGate) _status = status;
        StatusChanged?.Invoke(this, status);
        return status;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _api.ServerConnectionChanged -= OnServerConnectionChanged;
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        StopPumpAndApi();
        _pumpCts?.Dispose();
        _shutdownCts.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// True while the Steam client this session belongs to is still there.
    /// </summary>
    private bool IsSteamAlive()
    {
        try
        {
            return _api.IsSteamRunning() && _api.IsLoggedOn();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ends the session so the next <see cref="StartAsync"/> starts a fresh one.
    /// </summary>
    private void TearDownApi(string reason)
    {
        _logger?.Info(reason);
        StopPumpAndApi();
        lock (_stateGate) _friends = [];
    }

    /// <summary>
    /// Stops the callback thread before the API goes away. Steamworks is not
    /// safe to shut down while another thread is inside it, so a pump that
    /// refuses to stop means the API is left alone - the process is ending
    /// anyway, and a crash on exit would look like a crash to the player.
    /// </summary>
    private void StopPumpAndApi()
    {
        var pump = _pump;
        _pump = null;
        // A pump waiting out a lost connection never ends by itself.
        _pumpCts?.Cancel();
        if (pump is { IsAlive: true } && !pump.Join(PumpStopTimeout))
        {
            _logger?.Warn("The Steam callback thread did not stop; leaving the API to the process exit.");
            return;
        }

        try
        {
            _api.Shutdown();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            _logger?.Warn($"Steam shutdown failed: {ex.Message}");
        }
    }
}
