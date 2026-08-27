using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The UUID e4steam gives a player it carried over Steam.
/// </summary>
/// <remarks>
/// The launcher does not choose this value, it recognises it: e4steam 0.3.0
/// replaces the login profile with one of its own inside
/// startClientVerification, which is the method the launcher's own login hook
/// calls, so the portable UUID it had just accepted was gone a frame later.
/// Reproducing the derivation is what lets the adapter undo the swap.
/// </remarks>
public sealed class E4steamIdentityTests
{
    private static SteamId64 Account(ulong steamId64) =>
        SteamId64.TryFrom(steamId64, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(steamId64));

    /// <summary>
    /// The number out of the world where this was found. anuvenn's server
    /// printed "Accepted portable UUID 06c83c9e-980b-47d5-b7be-23d2bb649068"
    /// and then let MarkZamore in as eedf749f-0e25-39a2-8a84-60146b6343a0,
    /// standing at (33.5, 66.0, -11.5) with an empty inventory.
    /// </summary>
    [Fact]
    public void TheUuidASteamTunnelStamps_IsTheOneTheGuestLostHisThingsUnder()
    {
        Assert.Equal(
            new Guid("eedf749f-0e25-39a2-8a84-60146b6343a0"),
            E4steamIdentity.ProfileUuid(Account(76561198256236531)));
    }

    /// <summary>
    /// One account, one stamp, on every machine that tunnel ever runs on -
    /// which is the only reason the launcher can write it down in advance.
    /// </summary>
    [Fact]
    public void OneAccount_AlwaysGetsTheSameStamp()
    {
        Assert.Equal(
            E4steamIdentity.ProfileUuid(Account(76561198050776152)),
            E4steamIdentity.ProfileUuid(Account(76561198050776152)));
        Assert.NotEqual(
            E4steamIdentity.ProfileUuid(Account(76561198050776152)),
            E4steamIdentity.ProfileUuid(Account(76561198088743612)));
    }

    /// <summary>
    /// The tunnel's UUIDs are version 3 and the launcher's are version 5, so
    /// the adapter can never read one of its own remapped profiles as a profile
    /// still waiting to be remapped.
    /// </summary>
    [Fact]
    public void ATunnelStamp_IsNeverMistakenForAPortableUuid()
    {
        var account = Account(76561198256236531);

        var stamp = E4steamIdentity.ProfileUuid(account);
        var portable = SteamIdentityDerivation.DeriveMinecraftUuid(account);

        // Character 14 of the canonical form is the version digit.
        Assert.Equal('3', stamp.ToString("D")[14]);
        Assert.Equal('5', portable.ToString("D")[14]);
        Assert.NotEqual(portable, stamp);
    }

    [Fact]
    public void AnAccountSteamCouldNotName_HasNoStampToRecognise()
    {
        Assert.Throws<ArgumentException>(() => E4steamIdentity.ProfileUuid(SteamId64.None));
    }
}
