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
    Failed
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
    private static readonly TimeSpan LivenessCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long shutdown waits for the callback thread to leave Steam.</summary>
    private static readonly TimeSpan PumpStopTimeout = TimeSpan.FromSeconds(5);

    internal const string SteamLostMessage =
        "Steam закрылся или выполнен выход из аккаунта. Запустите Steam и нажмите «Повторить».";

    private static readonly TimeSpan FriendsRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly ISteamApiFacade _api;
    private readonly SteamNativeLibraryService? _native;
    private readonly Logger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _stateGate = new();

    private Thread? _pump;
    private IReadOnlyList<SteamFriendInfo> _friends = [];
    private SteamClientStatus _status = SteamClientStatus.NotStarted;
    private bool _disposed;

    public SteamClientService(
        ISteamApiFacade api,
        SteamNativeLibraryService? native = null,
        Logger? logger = null)
    {
        _api = api;
        _native = native;
        _logger = logger;
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
            // A status of Ready is only trustworthy while Steam is still
            // there; if it went away, this call is the recovery path.
            if (Status.IsReady && IsSteamAlive()) return Status;
            if (Status.IsReady) TearDownApi("Steam закрылся; переподключение.");
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
            StartPump();
            _logger?.Info($"Steam connected as {persona} ({steamId}).");
            return Publish(new SteamClientStatus(
                SteamAvailability.Ready,
                steamId,
                persona,
                $"Steam: {persona} ({steamId.ToString(CultureInfo.InvariantCulture)})"));
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

    private void StartPump()
    {
        // A pump that died on a Steam failure is replaced when the player
        // retries; the old thread has already returned.
        if (_pump is { IsAlive: true }) return;
        _pump = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "steam-callbacks"
        };
        _pump.Start();
    }

    private void PumpLoop()
    {
        var nextFriendsRefresh = DateTimeOffset.MinValue;
        var nextLivenessCheck = DateTimeOffset.UtcNow + LivenessCheckInterval;
        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                _api.RunCallbacks();
                var now = DateTimeOffset.UtcNow;
                if (now >= nextLivenessCheck)
                {
                    nextLivenessCheck = now + LivenessCheckInterval;
                    // Steam closing does not raise anything: RunCallbacks simply
                    // stops doing work. Without this the window would go on
                    // claiming a connection nobody has.
                    if (!IsSteamAlive())
                    {
                        _logger?.Warn("Steam is no longer running or signed in; the session ended.");
                        Publish(new SteamClientStatus(
                            SteamAvailability.SteamNotRunning, 0, string.Empty, SteamLostMessage));
                        return;
                    }
                }
                if (now >= nextFriendsRefresh)
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
                _shutdownCts.Token.WaitHandle.WaitOne(IdlePumpInterval);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
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
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        StopPumpAndApi();
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
