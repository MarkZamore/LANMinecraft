namespace Minecraft.Tests;

/// <summary>
/// Which Minecraft UUID a Steam account plays as. There is no state behind
/// this any more - the answer is a function of the account - so what is pinned
/// is the function: the three players who predate Steam keep the profiles that
/// hold their progress in the shared world, everyone else derives, and without
/// Steam there is no identity at all.
/// </summary>
public sealed class SteamIdentityServiceTests
{
    [Theory]
    [InlineData(76561198256236531UL, "06c83c9e-980b-47d5-b7be-23d2bb649068", "MarkZamore")]
    [InlineData(76561198050776152UL, "f0f5ec1a-14f5-47b6-9e27-b860f62c14e5", "anuvenn")]
    [InlineData(76561198088743612UL, "a4c56fa5-a630-42a6-9223-d6abfe63b130", "ASSin")]
    public void APlayerFromBeforeSteam_KeepsTheProfileHoldingTheirProgress(
        ulong steamId64,
        string expectedUuid,
        string name)
    {
        var service = new SteamIdentityService(new FakeSteamUserSource(steamId64, name));

        var result = service.Bind();

        Assert.True(result.Bound);
        Assert.Equal(IdentityBindingSource.KnownPlayer, result.Source);
        Assert.Equal(new Guid(expectedUuid), service.Binding!.PlayerUuid);
        // Their UUID is not what derivation would give - that is the point.
        Assert.NotEqual(
            SteamIdentityDerivation.DeriveMinecraftUuid(service.Binding.SteamId64),
            service.Binding.PlayerUuid);
    }

    [Fact]
    public void AnyoneElse_PlaysAsTheProfileDerivedFromTheirAccount()
    {
        const ulong newcomer = 76561198000000001;
        var service = new SteamIdentityService(new FakeSteamUserSource(newcomer, "Newcomer"));

        var result = service.Bind();

        Assert.True(result.Bound);
        Assert.Equal(IdentityBindingSource.Derived, result.Source);
        Assert.True(SteamId64.TryFrom(newcomer, out var steamId));
        Assert.Equal(SteamIdentityDerivation.DeriveMinecraftUuid(steamId), service.Binding!.PlayerUuid);
    }

    [Fact]
    public void WithoutSteam_ThereIsNoIdentityAndThePlayerIsTold()
    {
        var service = new SteamIdentityService(new FakeSteamUserSource(0));

        var result = service.Bind();

        Assert.False(result.Bound);
        Assert.False(service.IsBound);
        Assert.Contains("Steam не запущен", result.Message, StringComparison.Ordinal);
        Assert.Throws<IdentityUnavailableException>(() => service.ResolveContext(new AppSettings()));
    }

    [Fact]
    public void TheResolvedContext_CarriesBothIdentities()
    {
        var service = new SteamIdentityService(new FakeSteamUserSource(76561198256236531, "MarkZamore"));
        service.Bind();

        var context = service.ResolveContext(new AppSettings { PlayerName = "MarkZamore" });

        Assert.Equal("06c83c9e-980b-47d5-b7be-23d2bb649068", context.MinecraftUuid);
        Assert.Equal(new Guid("06c83c9e-980b-47d5-b7be-23d2bb649068"), context.PlayerUuid);
        Assert.Equal(76561198256236531UL, context.SteamId64.Value);
        Assert.Equal("MarkZamore", context.IdentityName);
        Assert.Equal(40, context.SessionAccessToken.Length);
        Assert.Equal(IdentityBindingSource.KnownPlayer, context.Source);
    }

    /// <summary>The same account resolves the same way every time, on any machine.</summary>
    [Fact]
    public void BindingIsDeterministic_AndFollowsTheSignedInAccount()
    {
        var source = new SwitchableSteamUser(76561198050776152, "anuvenn");
        var service = new SteamIdentityService(source);
        service.Bind();
        var first = service.Binding!.PlayerUuid;

        service.Bind();
        Assert.Equal(first, service.Binding!.PlayerUuid);

        source.SteamId64 = 76561198088743612;
        service.Bind();
        Assert.Equal(new Guid("a4c56fa5-a630-42a6-9223-d6abfe63b130"), service.Binding!.PlayerUuid);
    }

    [Fact]
    public void TheKnownPlayerTable_IsConsistentBothWays()
    {
        foreach (var player in KnownSteamPlayers.All)
        {
            Assert.True(SteamId64.TryFrom(player.SteamId64, out var steamId));
            Assert.True(KnownSteamPlayers.TryGetPlayer(steamId, out var byId));
            Assert.Equal(player.PlayerUuid, byId.PlayerUuid);
            Assert.True(KnownSteamPlayers.TryGetSteamId(player.PlayerUuid, out var byUuid));
            Assert.Equal(steamId, byUuid);
        }
        Assert.Equal(KnownSteamPlayers.All.Count, KnownSteamPlayers.All.Select(p => p.PlayerUuid).Distinct().Count());
        Assert.False(KnownSteamPlayers.TryGetSteamId(Guid.NewGuid(), out _));
    }

    private sealed class SwitchableSteamUser(ulong steamId64, string personaName) : ISteamUserSource
    {
        public ulong SteamId64 { get; set; } = steamId64;

        public bool TryGetLocalUser(out ulong id, out string persona)
        {
            id = SteamId64;
            persona = personaName;
            return SteamId64 != 0;
        }
    }
}
