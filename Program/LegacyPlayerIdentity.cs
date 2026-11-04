using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Minecraft;

/// <summary>Stable native names for Minecraft versions whose saves are keyed by name.</summary>
public static class LegacyPlayerIdentity
{
    public static string NativeName(SteamId64 steamId)
    {
        if (!steamId.IsValid) throw new ArgumentException("A valid Steam account is required.", nameof(steamId));
        Span<char> name = stackalloc char[14];
        name[0] = 's';
        var value = steamId.Value;
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        for (var index = 13; index > 0; index--)
        {
            name[index] = alphabet[(int)(value % 36)];
            value /= 36;
        }
        return new string(name);
    }

    public static bool TrySteamId(string? nativeName, out SteamId64 steamId)
    {
        steamId = default;
        if (nativeName is not { Length: 14 } || nativeName[0] != 's') return false;
        ulong value = 0;
        foreach (var character in nativeName.AsSpan(1))
        {
            var digit = character is >= '0' and <= '9' ? character - '0'
                : character is >= 'a' and <= 'z' ? character - 'a' + 10 : -1;
            if (digit < 0 || value > (ulong.MaxValue - (ulong)digit) / 36) return false;
            value = value * 36 + (ulong)digit;
        }
        return SteamId64.TryFrom(value, out steamId) && NativeName(steamId) == nativeName;
    }

    public static Guid PortableUuid(SteamId64 steamId) => KnownSteamPlayers.TryGetPlayer(steamId, out var known)
        ? known.PlayerUuid : SteamIdentityDerivation.DeriveMinecraftUuid(steamId);
}

/// <summary>Presentation data only. The native Steam bridge authenticates every connection.</summary>
public sealed class LegacyIdentityRegistryService(AppPaths paths, Logger logger)
{
    private readonly ConcurrentDictionary<SteamId64, string> _players = new();
    public string RegistryPath => Path.Combine(paths.Personal, "legacy-identity-profiles.properties");

    public void ObservePeer(PeerViewModel peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        Remember(peer.SteamId, peer.PlayerName);
    }

    public string Prepare(LocalIdentityContext identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Remember(identity.SteamId64, identity.IdentityName);
        Write();
        return RegistryPath;
    }

    private void Remember(SteamId64 steamId, string? displayName)
    {
        if (!steamId.IsValid || !LocalIdentityService.TryNormalizeNickname(displayName, out var normalized, out _)) return;
        if (_players.TryGetValue(steamId, out var known) && known == normalized) return;
        _players[steamId] = normalized;
        Write();
    }

    private void Write()
    {
        try
        {
            var contents = string.Join('\n', _players.OrderBy(pair => pair.Key.Value).Select(pair =>
                $"{pair.Key}|{LegacyPlayerIdentity.NativeName(pair.Key)}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value))}|{LegacyPlayerIdentity.PortableUuid(pair.Key):D}"));
            if (File.Exists(RegistryPath) && File.ReadAllText(RegistryPath) == contents) return;
            AtomicFile.WriteAllText(RegistryPath, contents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"Legacy presentation registry could not be written: {ex.Message}");
        }
    }
}
