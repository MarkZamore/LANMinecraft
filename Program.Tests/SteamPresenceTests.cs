namespace Minecraft.Tests;

/// <summary>
/// Rich presence is the whole discovery mechanism now, and Steam caps what fits
/// in it, so the codec has to survive long pack hashes, many waypoint providers
/// and peers running another version of the launcher.
/// </summary>
public sealed class SteamPresenceTests
{
    private const ulong PeerId = 76561198000000002;

    [Fact]
    public void APresenceRoundTrips()
    {
        var presence = SamplePresence();

        var encoded = SteamPresenceCodec.Encode(presence);
        var decoded = SteamPresenceCodec.TryDecode(
            presence.SteamId,
            "anuvenn",
            key => encoded.TryGetValue(key, out var value) ? value : "");

        Assert.NotNull(decoded);
        Assert.Equal(presence.PlayerName, decoded.PlayerName);
        Assert.Equal(presence.MinecraftUuid, decoded.MinecraftUuid);
        Assert.Equal(presence.PackHash, decoded.PackHash);
        Assert.Equal(presence.PackName, decoded.PackName);
        Assert.Equal(presence.Release, decoded.Release);
        Assert.Equal(presence.SkinSha256, decoded.SkinSha256);
        Assert.Equal("slim", decoded.SkinModel);
        Assert.True(decoded.IsSkinAvailable);
        Assert.Equal(presence.HostedWorldId, decoded.HostedWorldId);
        Assert.Equal(presence.WaypointProtocolVersion, decoded.WaypointProtocolVersion);
        Assert.Equal(
            presence.WaypointProviders.Select(provider => provider.ProviderId),
            decoded.WaypointProviders.Select(provider => provider.ProviderId));
        Assert.Equal(presence.DiagnosticProtocolVersion, decoded.DiagnosticProtocolVersion);
        Assert.True(decoded.IsMinecraftRunning);
    }

    [Fact]
    public void EveryValueFitsInSteamsLimits()
    {
        var presence = SamplePresence() with
        {
            PlayerName = new string('n', 400),
            PackHash = new string('a', 400),
            WaypointProviders = Enumerable.Range(0, 40)
                .Select(index => new WaypointProviderAnnouncement
                {
                    ProviderId = $"provider-with-a-long-name-{index}",
                    ModVersion = "1.2.3",
                    WorldContextId = Guid.NewGuid().ToString("N")
                })
                .ToArray()
        };

        var encoded = SteamPresenceCodec.Encode(presence);

        Assert.Equal(SteamPresenceCodec.Keys.Count, encoded.Count);
        Assert.All(encoded, pair =>
        {
            Assert.True(pair.Key.Length <= 64, pair.Key);
            Assert.True(
                pair.Value.Length <= SteamPresenceCodec.MaxValueLength,
                $"{pair.Key} is {pair.Value.Length} characters");
        });

        // A truncated provider list still parses; entries are dropped whole.
        var decoded = SteamPresenceCodec.TryDecode(
            presence.SteamId, "anuvenn", key => encoded[key]);
        Assert.NotNull(decoded);
        Assert.NotEmpty(decoded.WaypointProviders);
        Assert.All(decoded.WaypointProviders, provider => Assert.NotEmpty(provider.ProviderId));
    }

    [Fact]
    public void AFriendWithoutOurKeys_IsNotAPeer()
    {
        Assert.True(SteamId64.TryFrom(PeerId, out var peer));
        Assert.Null(SteamPresenceCodec.TryDecode(peer, "someone", _ => ""));
    }

    [Fact]
    /// <summary>
    /// A friend on another version used to disappear from the list, so both
    /// players concluded the other was offline. They are listed, marked
    /// incompatible, and the name says which side has to act.
    /// </summary>
    public void AFriendOnAnotherProtocolVersion_IsListedAsNeedingAnUpdate()
    {
        var encoded = SteamPresenceCodec.Encode(SamplePresence())
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        encoded["lanmc_v"] = "999";

        Assert.True(SteamId64.TryFrom(PeerId, out var peer));
        var decoded = SteamPresenceCodec.TryDecode(peer, "anuvenn", key => encoded[key]);

        Assert.NotNull(decoded);
        Assert.Equal(999, decoded.ProtocolVersion);
        var viewModel = new PeerViewModel { SteamId = peer };
        viewModel.Apply(decoded, "pack-hash");
        Assert.False(viewModel.IsCompatible);
        Assert.False(viewModel.SupportsDiagnosticLogs);
        Assert.Contains("обновить", viewModel.DisplayName, StringComparison.Ordinal);
    }

    /// <summary>A friend who is not running this launcher at all is not a peer.</summary>
    [Fact]
    public void AFriendWithoutTheMarker_IsNotAPeerAtAll()
    {
        Assert.True(SteamId64.TryFrom(PeerId, out var peer));
        Assert.Null(SteamPresenceCodec.TryDecode(peer, "anuvenn", _ => ""));
    }

    [Fact]
    public void AMalformedSkinOrStateFallsBackInsteadOfThrowing()
    {
        var encoded = SteamPresenceCodec.Encode(SamplePresence())
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        encoded["lanmc_skin"] = "not-a-hash|weird";
        encoded["lanmc_state"] = "dancing";

        Assert.True(SteamId64.TryFrom(PeerId, out var peer));
        var decoded = SteamPresenceCodec.TryDecode(peer, "anuvenn", key => encoded[key]);

        Assert.NotNull(decoded);
        Assert.False(decoded.IsSkinAvailable);
        Assert.Equal("classic", decoded.SkinModel);
        Assert.Equal(SteamPresenceCodec.StateIdle, decoded.State);
        Assert.False(decoded.IsMinecraftRunning);
    }

    private static SteamPeerPresence SamplePresence()
    {
        Assert.True(SteamId64.TryFrom(PeerId, out var peer));
        return new SteamPeerPresence
        {
            SteamId = peer,
            PersonaName = "anuvenn",
            ProtocolVersion = SteamPresenceCodec.ProtocolVersion,
            PlayerName = "anuvenn",
            MinecraftUuid = "f0f5ec1a-14f5-47b6-9e27-b860f62c14e5",
            PackHash = new string('b', 64),
            PackName = "All The Fabric 3",
            Release = 312,
            State = SteamPresenceCodec.StateInGame,
            IsSkinAvailable = true,
            SkinSha256 = new string('C', 64),
            SkinModel = "slim",
            HostedWorldId = Guid.NewGuid().ToString("D"),
            WaypointProtocolVersion = 1,
            WaypointProviders =
            [
                new WaypointProviderAnnouncement
                {
                    ProviderId = "ftbchunks",
                    ModVersion = "2101.1.20",
                    WorldContextId = "team-uuid"
                }
            ],
            DiagnosticProtocolVersion = 1
        };
    }
}
