using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A Steam session that waited out a blip is Ready again, but the presence
/// friends find this launcher by stopped while it waited. Only that return
/// brings network play back; nothing else should restart it.
/// </summary>
public sealed class SteamRecoveryPolicyTests
{
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
