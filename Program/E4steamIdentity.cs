using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>
/// The Minecraft UUID e4steam hands a player it carried over Steam.
/// </summary>
/// <remarks>
/// e4steam 0.3.0 began binding the login profile to the Steam account that
/// opened the tunnel: its mixin rewrites the argument of
/// <c>ServerLoginPacketListenerImpl.startClientVerification</c> into
/// <c>new GameProfile(nameUUIDFromBytes("e4steam:steam-identity:v1:" + steamId), name)</c>.
/// That happens inside the very method the launcher's login hook calls, so the
/// portable UUID the hook had just accepted was thrown away one frame later.
/// The host's log shows both halves: "Accepted portable UUID
/// 06c83c9e-980b-47d5-b7be-23d2bb649068", and then the guest joining as
/// eedf749f-0e25-39a2-8a84-60146b6343a0 - a stranger, standing at the world
/// spawn with none of his things.
///
/// The launcher cannot stop the mod, so it recognises what the mod makes: the
/// identity registry carries this UUID beside the portable one, and the adapter
/// turns the first into the second wherever a profile is built.
///
/// Version 3 over MD5, which is what <c>UUID.nameUUIDFromBytes</c> produces;
/// the launcher's own UUIDs are version 5 (<see cref="SteamIdentityDerivation"/>),
/// so no profile can ever be mistaken for the other kind.
/// </remarks>
public static class E4steamIdentity
{
    /// <summary>
    /// Read out of <c>link/e4steam/steam/SteamMinecraftIdentity</c> in
    /// e4steam-neoforge-mc1.20.2-26.2-v0.3.0, the build the launcher pins.
    /// </summary>
    public const string Namespace = "e4steam:steam-identity:v1:";

    /// <summary>What the tunnel would call this account inside the game.</summary>
    public static Guid ProfileUuid(SteamId64 steamId)
    {
        if (!steamId.IsValid)
        {
            throw new ArgumentException("A valid SteamID64 is required.", nameof(steamId));
        }
        return UuidV3(Namespace + steamId.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>RFC 4122 4.3 name-based UUID with MD5, over no namespace at all.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "MD5 is what java.util.UUID.nameUUIDFromBytes uses; this reproduces " +
            "another program's identifier, it is not a security primitive.")]
    private static Guid UuidV3(string name)
    {
        var uuid = MD5.HashData(Encoding.UTF8.GetBytes(name));
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x30); // version 3
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // RFC 4122 variant

        SteamIdentityDerivation.SwapEndianness(uuid);
        return new Guid(uuid);
    }
}
