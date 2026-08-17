using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// What the game inherits from the launcher. The launcher's own Steam settings
/// are not all meant for it.
/// </summary>
public sealed class GameProcessEnvironmentTests
{
    [Fact]
    public void TheOverlayIsLeftOnForTheGame()
    {
        // What the launcher's own process looks like once Steam is up.
        var environment = new Dictionary<string, string?>
        {
            [SteamworksApiFacade.NoOverlayVariable] = "1",
            ["SteamAppId"] = "480",
            ["TEMP"] = @"C:\Windows\Temp"
        };

        MinecraftProcessService.ConfigureChildEnvironment(environment, @"C:\game\temp");

        // Shift+Tab is how a player invites a friend into a Steam session.
        Assert.False(environment.ContainsKey(SteamworksApiFacade.NoOverlayVariable));
        // e4steam initialises the same app id inside the game; that one stays.
        Assert.Equal("480", environment["SteamAppId"]);
        Assert.Equal(@"C:\game\temp", environment["TEMP"]);
        Assert.Equal(@"C:\game\temp", environment["TMP"]);
    }

    [Fact]
    public void RunsWithoutSteamPresentToo()
    {
        var environment = new Dictionary<string, string?>();

        MinecraftProcessService.ConfigureChildEnvironment(environment, @"C:\game\temp");

        Assert.Equal(@"C:\game\temp", environment["TMP"]);
    }
}
