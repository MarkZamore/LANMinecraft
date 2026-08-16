namespace Minecraft.Tests;

/// <summary>Builds a bound identity for tests, without a real Steam client.</summary>
internal static class TestIdentity
{
    public const ulong DefaultSteamId = 76561198000000001;

    public static SteamIdentityService CreateBound(
        ulong steamId64 = DefaultSteamId,
        string personaName = "TestPlayer")
    {
        var service = new SteamIdentityService(new FakeSteamUserSource(steamId64, personaName));
        var result = service.Bind();
        Assert.True(result.Bound, "The test identity could not be bound.");
        return service;
    }

    public static LocalIdentityContext CreateContext(
        string playerName,
        ulong steamId64 = DefaultSteamId) =>
        CreateBound(steamId64, playerName).ResolveContext(new AppSettings { PlayerName = playerName });
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
