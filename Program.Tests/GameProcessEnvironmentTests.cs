using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// What the game inherits from the launcher. The launcher's own Steam settings
/// are not all meant for it, and the profile a mod writes into belongs inside
/// the installation rather than in the player's own.
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

        MinecraftProcessService.ConfigureChildEnvironment(environment, @"C:\game\temp", @"C:\game\home");

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

        MinecraftProcessService.ConfigureChildEnvironment(environment, @"C:\game\temp", @"C:\game\home");

        Assert.Equal(@"C:\game\temp", environment["TMP"]);
    }

    /// <summary>
    /// Every way of asking Windows where this player keeps their things,
    /// answered from inside the installation. -Duser.home already covers the
    /// same question asked of Java; these are what a mod reads when it asks the
    /// system instead, and what used to leave folders in the real profile.
    /// </summary>
    [Fact]
    public void TheProfileTheGameWritesInto_IsInsideTheInstallation()
    {
        var environment = new Dictionary<string, string?>
        {
            ["APPDATA"] = @"C:\Users\Player\AppData\Roaming",
            ["LOCALAPPDATA"] = @"C:\Users\Player\AppData\Local",
            ["USERPROFILE"] = @"C:\Users\Player"
        };

        MinecraftProcessService.ConfigureChildEnvironment(environment, @"C:\game\temp", @"C:\game\home");

        Assert.Equal(@"C:\game\home", environment["USERPROFILE"]);
        Assert.Equal(@"C:\game\home", environment["HOME"]);
        // The shape Windows uses, so a mod that builds %APPDATA%\.minecraft out
        // of it finds what it expects, one folder deeper.
        Assert.Equal(@"C:\game\home\AppData\Roaming", environment["APPDATA"]);
        Assert.Equal(@"C:\game\home\AppData\Local", environment["LOCALAPPDATA"]);
    }
}
