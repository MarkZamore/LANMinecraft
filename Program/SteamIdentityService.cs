using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>
/// Decides which Minecraft UUID the launcher plays as, given the Steam account
/// it is signed in to. The answer is a pure function of the account: one of the
/// three players who predate Steam keeps the UUID that already holds their
/// progress (<see cref="KnownSteamPlayers"/>), everyone else plays as the UUID
/// derived from their SteamID64 - identical on every machine, so nothing needs
/// to be stored, migrated or backed up.
/// </summary>
public sealed class SteamIdentityService : IIdentityService
{
    private const string SessionTokenNamespace = "MinecraftPortableLocalSession:v2";
    internal const string SteamUnavailableMessage =
        "Steam не запущен или вход в аккаунт не выполнен. " +
        "Запустите Steam, войдите в аккаунт и нажмите «Повторить».";

    private readonly ISteamUserSource _steamUser;
    private readonly Logger? _logger;
    private readonly object _stateGate = new();

    private SteamIdentityBinding? _binding;

    public SteamIdentityService(ISteamUserSource steamUser, Logger? logger = null)
    {
        _steamUser = steamUser ?? throw new ArgumentNullException(nameof(steamUser));
        _logger = logger;
    }

    public bool IsBound
    {
        get { lock (_stateGate) return _binding is not null; }
    }

    /// <summary>The bound account, or null while Steam is unavailable.</summary>
    public SteamIdentityBinding? Binding
    {
        get { lock (_stateGate) return _binding; }
    }

    /// <summary>
    /// Reads the signed-in Steam account and resolves the profile it plays as.
    /// Safe to call again - after a retry, or when the account changes.
    /// </summary>
    public IdentityBindingResult Bind()
    {
        if (!_steamUser.TryGetLocalUser(out var steamId64, out var personaName) ||
            !SteamId64.TryFrom(steamId64, out var steamId))
        {
            lock (_stateGate) _binding = null;
            return new IdentityBindingResult(false, null, SteamUnavailableMessage);
        }

        var binding = KnownSteamPlayers.TryGetPlayer(steamId, out var known)
            ? new SteamIdentityBinding(steamId, personaName, known.PlayerUuid, IdentityBindingSource.KnownPlayer)
            : new SteamIdentityBinding(
                steamId,
                personaName,
                SteamIdentityDerivation.DeriveMinecraftUuid(steamId),
                IdentityBindingSource.Derived);

        var changed = Binding != binding;
        lock (_stateGate) _binding = binding;
        if (changed)
        {
            _logger?.Info(binding.Source == IdentityBindingSource.KnownPlayer
                ? $"Steam account {personaName} ({steamId}) plays as {known.Name}'s profile {binding.PlayerUuid}."
                : $"Steam account {personaName} ({steamId}) plays as the derived profile {binding.PlayerUuid}.");
        }
        return new IdentityBindingResult(true, binding.Source, "");
    }

    /// <summary>
    /// The identity everything downstream uses: the SteamID64 addresses the
    /// player on the network, the Minecraft UUID names their progress on disk.
    /// </summary>
    public LocalIdentityContext ResolveContext(AppSettings settings)
    {
        var binding = Binding ?? throw new IdentityUnavailableException(SteamUnavailableMessage);
        var nickname = LocalIdentityService.NormalizeNickname(settings.PlayerName, Environment.UserName);
        var uuid = binding.PlayerUuid.ToString("D");
        return new LocalIdentityContext
        {
            IdentityName = nickname,
            MinecraftUuid = uuid,
            SteamId64 = binding.SteamId64,
            PlayerUuid = binding.PlayerUuid,
            Source = binding.Source,
            SessionAccessToken = CreateSessionToken(uuid, binding.SteamId64.ToString(), nickname)
        };
    }

    private static string CreateSessionToken(string uuid, string steamId64, string nickname)
    {
        var seed = $"{SessionTokenNamespace}|{uuid}|{steamId64}|{nickname}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..40].ToLowerInvariant();
    }
}
