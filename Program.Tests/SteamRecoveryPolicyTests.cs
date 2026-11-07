using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A Steam session that waited out a blip is Ready again, but the presence
/// friends find this launcher by stopped while it waited. Only that return
/// brings network play back; nothing else should restart it.
/// </summary>
public sealed class SteamRecoveryPolicyTests
{
    [Theory]
    [InlineData(SteamAvailability.NotStarted, true)]
    [InlineData(SteamAvailability.SteamNotRunning, true)]
    [InlineData(SteamAvailability.NotLoggedIn, true)]
    [InlineData(SteamAvailability.Failed, true)]
    [InlineData(SteamAvailability.Starting, false)]
    [InlineData(SteamAvailability.Reconnecting, false)]
    public void AutomaticChecks_RestartEndedSessionsAndLeaveTheLivePumpAlone(SteamAvailability availability, bool expected)
    {
        var status = new SteamClientStatus(availability, 0, "", "");
        Assert.Equal(expected, SteamRecoveryPolicy.ShouldConnect(status, identityBound: false));
    }

    [Fact]
    public async Task AutomaticChecks_FollowLateStartSignOutAndAnotherAccount()
    {
        var api = new FakeSteamApi { InitializeSucceeds = false };
        await using var client = new SteamClientService(api, livenessCheckInterval: TimeSpan.FromMilliseconds(20));
        var identity = new SteamIdentityService(new SteamClientUserSource(client));
        async Task CheckAsync()
        {
            if (SteamRecoveryPolicy.ShouldConnect(client.Status,
                SteamRecoveryPolicy.HasMatchingIdentity(client.Status, identity.Binding)))
            {
                if ((await client.StartAsync()).IsReady) Assert.True(identity.Bind().Bound);
            }
        }

        await CheckAsync();
        Assert.Equal(SteamAvailability.SteamNotRunning, client.Status.Availability);
        api.SteamRunning = true;
        api.InitializeSucceeds = true;
        await CheckAsync();
        Assert.Equal(SteamAvailability.NotLoggedIn, client.Status.Availability);

        api.LoggedOn = true;
        api.SteamId = 76561198000000001;
        await CheckAsync();
        var first = identity.Binding;
        Assert.NotNull(first);
        var initializations = api.InitializeCount;
        await CheckAsync();
        Assert.Equal(initializations, api.InitializeCount);

        var signedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, status) =>
        {
            if (status.Availability == SteamAvailability.SteamNotRunning) signedOut.TrySetResult();
        };
        api.LoggedOn = false;
        await signedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(SteamRecoveryPolicy.HasMatchingIdentity(client.Status, first));
        await CheckAsync();
        Assert.False(client.Status.IsReady);

        api.SteamId = 76561198000000002;
        api.LoggedOn = true;
        await CheckAsync();
        Assert.True(SteamRecoveryPolicy.HasMatchingIdentity(client.Status, identity.Binding));
        Assert.False(SteamRecoveryPolicy.HasMatchingIdentity(client.Status, first));
        Assert.NotEqual(first.PlayerUuid, identity.Binding!.PlayerUuid);
    }

    [Fact]
    public void BackFromWaitingForSteamServers_RestartsNetworkPlay()
    {
        Assert.True(SteamRecoveryPolicy.ShouldRestartNetworking(SteamAvailability.Reconnecting, SteamAvailability.Ready));
    }

    [Theory]
    [InlineData(SteamAvailability.Ready, SteamAvailability.Ready)]
    [InlineData(SteamAvailability.Starting, SteamAvailability.Ready)]
    [InlineData(SteamAvailability.NotStarted, SteamAvailability.Ready)]
    [InlineData(SteamAvailability.Ready, SteamAvailability.Reconnecting)]
    [InlineData(SteamAvailability.Reconnecting, SteamAvailability.SteamNotRunning)]
    [InlineData(SteamAvailability.Reconnecting, SteamAvailability.NotLoggedIn)]
    public void AnythingElse_LeavesItAlone(SteamAvailability previous, SteamAvailability current)
    {
        // A first start brings the network up through the Steam handshake itself.
        Assert.False(SteamRecoveryPolicy.ShouldRestartNetworking(previous, current));
    }
}
