using System.Security.Cryptography;

namespace Minecraft.Tests;

/// <summary>
/// The Steam bootstrap has two halves that must not drift: the vendored
/// steam_api64.dll the launcher embeds, and the service that turns a missing
/// or signed-out Steam into a status instead of a crash.
/// </summary>
public sealed class SteamBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-steam-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    [Fact]
    public void VendoredSteamNative_MatchesThePinnedBytes()
    {
        var path = Path.Combine(
            FindRepositoryDirectory("Program", "Native"),
            SteamNativeLibraryService.NativeFileName);
        Assert.True(File.Exists(path), "The vendored Steamworks native is missing.");
        Assert.Equal(SteamNativeLibraryService.NativeSizeBytes, new FileInfo(path).Length);
        using var stream = File.OpenRead(path);
        Assert.Equal(
            SteamNativeLibraryService.NativeSha256,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    [Fact]
    public void EmbeddedSteamNative_IsTheVendoredFile()
    {
        var bytes = SteamNativeLibraryService.ReadEmbeddedNative();
        Assert.Equal(SteamNativeLibraryService.NativeSizeBytes, bytes.LongLength);
        Assert.Equal(
            SteamNativeLibraryService.NativeSha256,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void Extract_IsIdempotent_AndRepairsACorruptedCopy()
    {
        var paths = new AppPaths(_root);
        var service = new SteamNativeLibraryService(paths);

        var first = service.Extract();
        Assert.True(File.Exists(first));
        var firstWrite = File.GetLastWriteTimeUtc(first);

        var second = service.Extract();
        Assert.Equal(first, second);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(second));

        File.WriteAllBytes(first, [1, 2, 3]);
        var repaired = service.Extract();
        Assert.Equal(first, repaired);
        Assert.Equal(SteamNativeLibraryService.NativeSizeBytes, new FileInfo(repaired).Length);
    }

    [Fact]
    public async Task StartAsync_WithoutSteamRunning_ReportsItAndKeepsWorking()
    {
        var api = new FakeSteamApi { SteamRunning = false };
        await using var service = new SteamClientService(api);

        var status = await service.StartAsync(CancellationToken.None);

        Assert.Equal(SteamAvailability.SteamNotRunning, status.Availability);
        Assert.False(status.IsReady);
        Assert.Contains("Steam не запущен", status.Message, StringComparison.Ordinal);
        Assert.False(api.Initialized);
    }

    [Fact]
    public async Task StartAsync_WhenSignedOut_ShutsTheApiDownAgain()
    {
        var api = new FakeSteamApi { SteamRunning = true, LoggedOn = false };
        await using var service = new SteamClientService(api);

        var status = await service.StartAsync(CancellationToken.None);

        Assert.Equal(SteamAvailability.NotLoggedIn, status.Availability);
        Assert.False(api.Initialized);
        Assert.Equal(1, api.ShutdownCount);
    }

    [Fact]
    public async Task StartAsync_WhenReady_PublishesTheAccountAndRetryIsANoop()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        await using var service = new SteamClientService(api);
        var published = new List<SteamClientStatus>();
        service.StatusChanged += (_, status) => published.Add(status);

        var status = await service.StartAsync(CancellationToken.None);

        Assert.True(status.IsReady);
        Assert.Equal(76561198000000001UL, status.SteamId64);
        Assert.Contains("MarkZamore", status.Message, StringComparison.Ordinal);
        Assert.Equal(1, api.InitializeCount);
        Assert.True(api.RelayInitialized);
        Assert.Contains(published, entry => entry.Availability == SteamAvailability.Starting);

        var again = await service.StartAsync(CancellationToken.None);
        Assert.Equal(status, again);
        Assert.Equal(1, api.InitializeCount);
    }

    [Fact]
    public async Task StartAsync_AfterSteamAppears_SucceedsOnRetry()
    {
        var api = new FakeSteamApi { SteamRunning = false };
        await using var service = new SteamClientService(api);

        var blocked = await service.StartAsync(CancellationToken.None);
        Assert.Equal(SteamAvailability.SteamNotRunning, blocked.Availability);

        api.SteamRunning = true;
        api.LoggedOn = true;
        api.SteamId = 76561198000000002;
        api.Persona = "anuvenn";

        var retried = await service.StartAsync(CancellationToken.None);
        Assert.True(retried.IsReady);
        Assert.Equal(76561198000000002UL, retried.SteamId64);
    }

    /// <summary>
    /// Steam closing does not raise anything - RunCallbacks simply stops doing
    /// work - so the launcher has to notice by asking. Until it did, the window
    /// went on showing a connection nobody had, and Retry returned instantly
    /// because the status still said Ready.
    /// </summary>
    [Fact]
    public async Task WhenSteamDisappears_TheSessionEndsAndRetryReconnects()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        await using var service = new SteamClientService(api);
        Assert.True((await service.StartAsync(CancellationToken.None)).IsReady);

        // The player closes Steam.
        api.SteamRunning = false;
        api.LoggedOn = false;

        var afterLoss = await service.StartAsync(CancellationToken.None);
        Assert.False(afterLoss.IsReady);
        Assert.Equal(SteamAvailability.SteamNotRunning, afterLoss.Availability);

        // ...starts it again, and the same button now reconnects.
        api.SteamRunning = true;
        api.LoggedOn = true;
        var recovered = await service.StartAsync(CancellationToken.None);
        Assert.True(recovered.IsReady);
        Assert.Equal(76561198000000001UL, recovered.SteamId64);
        Assert.Equal(2, api.InitializeCount);
    }

    /// <summary>
    /// A network blip takes Steam off its servers while Steam itself keeps
    /// running, and Steam is back within seconds. The launcher used to end its
    /// session on the first such blip and stayed without Steam until somebody
    /// pressed Retry - which a guest found out about by being unable to play.
    /// </summary>
    [Fact]
    public async Task WhenSteamServersDrop_WhileSteamRuns_TheSessionWaitsAndComesBackByItself()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        await using var service = new SteamClientService(api, livenessCheckInterval: TimeSpan.FromMilliseconds(20));
        Assert.True((await service.StartAsync(CancellationToken.None)).IsReady);

        var reconnecting = WaitFor(service, SteamAvailability.Reconnecting);
        api.RaiseServerConnectionChanged(new SteamServerConnectionChange(false, 3, true));
        api.LoggedOn = false;
        var waiting = await reconnecting;
        Assert.False(waiting.IsReady);
        Assert.Equal("MarkZamore", waiting.PersonaName);
        Assert.Equal(SteamClientService.ReconnectingMessage, waiting.Message);

        // Retry while it waits must not start a second session over the first.
        var retried = await service.StartAsync(CancellationToken.None);
        Assert.Equal(SteamAvailability.Reconnecting, retried.Availability);

        var ready = WaitFor(service, SteamAvailability.Ready);
        api.LoggedOn = true;
        var back = await ready;
        Assert.True(back.IsReady);
        Assert.Equal(76561198000000001UL, back.SteamId64);
        Assert.Equal(1, api.InitializeCount);
        Assert.Equal(0, api.ShutdownCount);
    }

    [Fact]
    public async Task WhenTheSteamProcessGoes_TheSessionStillEnds()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        await using var service = new SteamClientService(api, livenessCheckInterval: TimeSpan.FromMilliseconds(20));
        Assert.True((await service.StartAsync(CancellationToken.None)).IsReady);

        var ended = WaitFor(service, SteamAvailability.SteamNotRunning);
        api.SteamRunning = false;
        api.LoggedOn = false;

        Assert.Equal(SteamClientService.SteamLostMessage, (await ended).Message);
    }

    [Fact]
    public async Task SignedInOnAnotherComputer_TheWaitSaysSo()
    {
        // Steam does not come back from this by itself; the player has to.
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        await using var service = new SteamClientService(api, livenessCheckInterval: TimeSpan.FromMilliseconds(20));
        Assert.True((await service.StartAsync(CancellationToken.None)).IsReady);

        var reconnecting = WaitFor(service, SteamAvailability.Reconnecting);
        api.RaiseServerConnectionChanged(new SteamServerConnectionChange(false, 6, false));
        api.LoggedOn = false;

        Assert.Equal(SteamClientService.LoggedInElsewhereMessage, (await reconnecting).Message);
    }

    [Fact]
    public async Task SignedBackInAsAnotherAccount_TheSessionEnds()
    {
        // The profile played as and who may connect belong to the old account.
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        await using var service = new SteamClientService(api, livenessCheckInterval: TimeSpan.FromMilliseconds(20));
        Assert.True((await service.StartAsync(CancellationToken.None)).IsReady);

        var reconnecting = WaitFor(service, SteamAvailability.Reconnecting);
        api.RaiseServerConnectionChanged(new SteamServerConnectionChange(false, 3, true));
        api.LoggedOn = false;
        await reconnecting;

        var ended = WaitFor(service, SteamAvailability.NotLoggedIn);
        api.SteamId = 76561198000000002;
        api.LoggedOn = true;

        Assert.Equal(SteamClientService.AccountChangedMessage, (await ended).Message);
    }

    /// <summary>
    /// A Steam that restarted between two checks also answers "running, not
    /// signed in", but it never said it lost its servers, and the session the
    /// launcher holds belongs to the old process. Waiting for it would wait
    /// forever with nothing to press.
    /// </summary>
    [Fact]
    public async Task NotSignedIn_WithoutSteamReportingLostServers_EndsTheSessionAsBefore()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        await using var service = new SteamClientService(api, livenessCheckInterval: TimeSpan.FromMilliseconds(20));
        Assert.True((await service.StartAsync(CancellationToken.None)).IsReady);

        var ended = WaitFor(service, SteamAvailability.SteamNotRunning);
        api.LoggedOn = false;

        Assert.Equal(SteamClientService.SteamLostMessage, (await ended).Message);
    }

    [Fact]
    public async Task WaitingTooLong_EndsTheSession_SoRetryStartsAgain()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        await using var service = new SteamClientService(
            api, livenessCheckInterval: TimeSpan.FromMilliseconds(20), reconnectLimit: TimeSpan.FromMilliseconds(200));
        Assert.True((await service.StartAsync(CancellationToken.None)).IsReady);

        var reconnecting = WaitFor(service, SteamAvailability.Reconnecting);
        var ended = WaitFor(service, SteamAvailability.SteamNotRunning);
        api.RaiseServerConnectionChanged(new SteamServerConnectionChange(false, 3, true));
        api.LoggedOn = false;
        await reconnecting;

        Assert.Equal(SteamClientService.ReconnectTimedOutMessage, (await ended).Message);

        // The button that is back now does start a new session.
        api.LoggedOn = true;
        var retried = await service.StartAsync(CancellationToken.None);
        Assert.True(retried.IsReady);
    }

    [Fact]
    public async Task WhileWaiting_TheFriendListIsKept()
    {
        // The friend list decides who may connect; a Steam away from its
        // servers knows no friends, and emptying the list would refuse them all.
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        api.FriendList.Add(new SteamFriendInfo(76561198000000002, "anuvenn", true, 0));
        await using var service = new SteamClientService(api, livenessCheckInterval: TimeSpan.FromMilliseconds(20));
        Assert.True((await service.StartAsync(CancellationToken.None)).IsReady);

        var reconnecting = WaitFor(service, SteamAvailability.Reconnecting);
        api.RaiseServerConnectionChanged(new SteamServerConnectionChange(false, 3, true));
        api.LoggedOn = false;
        await reconnecting;
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Single(service.Friends);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(34, true)]
    [InlineData(50, true)]
    [InlineData(3, false)]
    public void WhichLossesMeanTheAccountIsInUseElsewhere(int result, bool elsewhere)
    {
        Assert.Equal(elsewhere, new SteamServerConnectionChange(false, result, false).SignedInElsewhere);
    }

    private static Task<SteamClientStatus> WaitFor(SteamClientService service, SteamAvailability availability)
    {
        var seen = new TaskCompletionSource<SteamClientStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status.Availability == availability) seen.TrySetResult(status);
        };
        return seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Shutting the Steam API down while the callback thread is inside it is a
    /// native crash on exit, which a player reads as "the launcher crashed".
    /// </summary>
    [Fact]
    public async Task Disposing_StopsTheCallbackThreadBeforeTheApi()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = 76561198000000001,
            Persona = "MarkZamore"
        };
        var service = new SteamClientService(api);
        Assert.True((await service.StartAsync(CancellationToken.None)).IsReady);

        await service.DisposeAsync();

        Assert.False(api.Initialized);
        Assert.Equal(1, api.ShutdownCount);
        Assert.False(api.CallbacksRanAfterShutdown, "The pump was still calling Steam after shutdown.");
    }

    [Fact]
    public async Task PresenceCalls_AreIgnoredUntilSteamIsReady()
    {
        var api = new FakeSteamApi { SteamRunning = false };
        await using var service = new SteamClientService(api);

        Assert.False(service.SetPresence("lanmc", "1"));
        Assert.Equal(string.Empty, service.GetFriendPresence(76561198000000003, "lanmc"));

        api.SteamRunning = true;
        api.LoggedOn = true;
        api.SteamId = 76561198000000001;
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.SetPresence("lanmc", "1"));
        Assert.Equal("1", api.Presence["lanmc"]);
    }

    private static string FindRepositoryDirectory(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Repository directory was not found: {Path.Combine(relativeParts)}");
    }
}

/// <summary>In-memory Steam client for tests: no Steamworks, no native library.</summary>
internal sealed class FakeSteamApi : ISteamApiFacade
{
    public bool SteamRunning { get; set; }
    public bool LoggedOn { get; set; }
    public ulong SteamId { get; set; }
    public string Persona { get; set; } = string.Empty;
    public List<SteamFriendInfo> FriendList { get; } = [];
    public Dictionary<string, string?> Presence { get; } = new(StringComparer.Ordinal);
    public bool Initialized { get; private set; }
    public bool RelayInitialized { get; private set; }
    public int InitializeCount { get; private set; }
    public int ShutdownCount { get; private set; }

    public bool Initialize(out string failureReason)
    {
        InitializeCount++;
        if (!SteamRunning)
        {
            failureReason = "Steam is not running";
            return false;
        }

        failureReason = string.Empty;
        Initialized = true;
        return true;
    }

    public bool CallbacksRanAfterShutdown { get; private set; }

    public void Shutdown()
    {
        if (!Initialized) return;
        Initialized = false;
        ShutdownCount++;
    }

    public void RunCallbacks()
    {
        if (ShutdownCount > 0) CallbacksRanAfterShutdown = true;
    }

    public bool IsSteamRunning() => SteamRunning;

    public bool IsLoggedOn() => Initialized && LoggedOn;

    public ulong GetLocalSteamId() => Initialized ? SteamId : 0UL;

    public string GetPersonaName() => Initialized ? Persona : string.Empty;

    public void InitRelayNetworkAccess() => RelayInitialized = Initialized;

    /// <summary>Like Steam's own: a client away from its servers knows no friends.</summary>
    public IReadOnlyList<SteamFriendInfo> GetFriends() => Initialized && LoggedOn ? FriendList : [];

    public bool SetRichPresence(string key, string? value)
    {
        if (!Initialized) return false;
        Presence[key] = value;
        return true;
    }

    /// <summary>What each friend publishes, keyed by account and presence key.</summary>
    public Dictionary<(ulong SteamId64, string Key), string> FriendPresence { get; } = [];

    public string GetFriendRichPresence(ulong steamId64, string key) =>
        Initialized && FriendPresence.TryGetValue((steamId64, key), out var value) ? value : string.Empty;

    public bool RequestFriendRichPresence(ulong steamId64) => Initialized;

    public int GetFriendRichPresenceKeyCount(ulong steamId64) =>
        Initialized ? FriendPresence.Keys.Count(entry => entry.SteamId64 == steamId64) : 0;

    public event Action<SteamServerConnectionChange>? ServerConnectionChanged;

    /// <summary>What Steam's own callback would say about its servers.</summary>
    public void RaiseServerConnectionChanged(SteamServerConnectionChange change) =>
        ServerConnectionChanged?.Invoke(change);
}
