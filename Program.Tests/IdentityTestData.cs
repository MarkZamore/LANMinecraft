namespace Minecraft.Tests;

/// <summary>
/// Builds a bound identity for tests that used to call
/// <c>new LocalIdentityService(paths).ResolveContext(...)</c>.
/// </summary>
internal static class TestIdentity
{
    public const ulong DefaultSteamId = 76561198000000001;

    public static SteamIdentityService CreateBound(
        AppPaths paths,
        ulong steamId64 = DefaultSteamId,
        string personaName = "TestPlayer")
    {
        var service = new SteamIdentityService(paths, new FakeSteamUserSource(steamId64, personaName));
        var result = service.EnsureBoundAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.Bound, "The test identity could not be bound.");
        return service;
    }

    public static LocalIdentityContext CreateContext(
        AppPaths paths,
        string playerName,
        ulong steamId64 = DefaultSteamId) =>
        CreateBound(paths, steamId64, playerName).ResolveContext(new AppSettings { PlayerName = playerName });
}

internal sealed class FakeSteamUserSource(ulong steamId64, string personaName = "TestPlayer") : ISteamUserSource
{
    public bool TryGetLocalUser(out ulong id, out string persona)
    {
        id = steamId64;
        persona = personaName;
        return steamId64 != 0;
    }
}
