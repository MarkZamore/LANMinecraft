using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>
/// Derives the Minecraft player UUID a Steam account gets when it has no
/// history to inherit. The value is a name-based (version 5) UUID over a fixed
/// namespace, so the same Steam account produces the same UUID on any machine
/// and no state has to travel with the player.
///
/// The three players who predate Steam keep the UUIDs their progress already
/// lives under instead (<see cref="KnownSteamPlayers"/>): quests, teams, homes
/// and inventories are written by mods the launcher does not track, so
/// re-keying them would quietly lose progress.
/// </summary>
public static class SteamIdentityDerivation
{
    /// <summary>Frozen: changing it changes every derived UUID.</summary>
    public static Guid Namespace { get; } = new("3b26fe30-fc0f-44d9-8a9e-08c3bde54df1");

    public static string NameFor(ulong steamId64) =>
        $"steam:{steamId64.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static Guid DeriveMinecraftUuid(SteamId64 steamId)
    {
        if (!steamId.IsValid)
        {
            throw new ArgumentException("A valid SteamID64 is required.", nameof(steamId));
        }
        return UuidV5(Namespace, NameFor(steamId.Value));
    }

    /// <summary>RFC 4122 §4.3 name-based UUID with SHA-1.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is what RFC 4122 defines for version 5 UUIDs; " +
            "this is an identifier derivation, not a security primitive.")]
    internal static Guid UuidV5(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        SwapEndianness(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(input);
        var uuid = new byte[16];
        Buffer.BlockCopy(hash, 0, uuid, 0, 16);
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50); // version 5
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // RFC 4122 variant

        SwapEndianness(uuid);
        return new Guid(uuid);
    }

    /// <summary>
    /// System.Guid stores its first three fields little-endian while RFC 4122
    /// defines them big-endian, so both directions need the same swap.
    /// </summary>
    private static void SwapEndianness(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
