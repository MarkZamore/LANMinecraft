using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// Decides which Minecraft UUID the launcher plays as, given the Steam account
/// it is signed in to.
///
/// A player who already has Minecraft/Personal/UUID.json keeps that UUID: the
/// worlds hold their quests, FTB teams, homes and inventories under it, and
/// most of that is written by mods the launcher does not track, so re-keying
/// would look successful and quietly lose progress. Everyone else gets a UUID
/// derived from their Steam account, which is identical on every machine.
///
/// UUID.json itself is only read and backed up - never rewritten, never
/// deleted - so the previous launcher still works if this one is rolled back.
/// </summary>
public sealed class SteamIdentityService : IIdentityService, IDisposable
{
    private static readonly JsonSerializerOptions LegacyJsonOptions = new(JsonSerializerDefaults.Web);
    private const string SessionTokenNamespace = "MinecraftPortableLocalSession:v2";
    internal const string SteamUnavailableMessage =
        "Steam не запущен или вход в аккаунт не выполнен. " +
        "Запустите Steam, войдите в аккаунт и нажмите «Повторить».";

    private readonly AppPaths _paths;
    private readonly ISteamUserSource _steamUser;
    private readonly SteamIdentityStore _store;
    private readonly IIdentityConflictResolver? _conflictResolver;
    private readonly Logger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();

    private SteamIdentityBinding? _binding;

    public SteamIdentityService(
        AppPaths paths,
        ISteamUserSource steamUser,
        Logger? logger = null,
        IIdentityConflictResolver? conflictResolver = null,
        SteamIdentityStore? store = null)
    {
        _paths = paths;
        _steamUser = steamUser;
        _logger = logger;
        _conflictResolver = conflictResolver;
        _store = store ?? new SteamIdentityStore(paths, logger);
    }

    public bool IsBound
    {
        get { lock (_stateGate) return _binding is not null; }
    }

    /// <summary>The bound account, or null while unbound.</summary>
    public SteamIdentityBinding? Binding
    {
        get { lock (_stateGate) return _binding; }
    }

    /// <summary>
    /// Binds this machine's Steam account to a Minecraft UUID, once. Safe to
    /// call again: an existing binding is returned untouched, and nothing is
    /// written when Steam is unavailable or the player cancels.
    /// </summary>
    public async Task<IdentityBindingResult> EnsureBoundAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = Binding;
            if (current is not null)
            {
                return new IdentityBindingResult(true, current.Source, false, false, current.LegacyBackupPath, "");
            }

            if (!_steamUser.TryGetLocalUser(out var steamId64, out var personaName) ||
                !SteamId64.TryFrom(steamId64, out var steamId))
            {
                return new IdentityBindingResult(false, null, false, false, null, SteamUnavailableMessage);
            }

            var document = _store.TryLoad() ?? new SteamIdentityDocument();
            var canonical = steamId.ToString();
            var existing = document.Bindings.FirstOrDefault(binding =>
                string.Equals(binding.SteamId64, canonical, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.PersonaName = personaName;
                _store.Save(document);
                Publish(existing);
                return new IdentityBindingResult(true, existing.Source, false, false, existing.LegacyBackupPath, "");
            }

            var derivedUuid = SteamIdentityDerivation.DeriveMinecraftUuid(steamId);
            var legacyUuid = TryReadLegacyUuid();
            var legacyIsFree = legacyUuid is not null &&
                               !document.Bindings.Any(binding => binding.PlayerUuid == legacyUuid.Value);

            var source = IdentityBindingSource.Derived;
            var playerUuid = derivedUuid;
            var conflictResolved = false;
            string? backupPath = null;

            if (legacyIsFree)
            {
                var conflictingWorlds = FindWorldsHoldingProfile(derivedUuid);
                var decision = IdentityConflictDecision.KeepLegacy;
                if (conflictingWorlds.Count > 0)
                {
                    conflictResolved = true;
                    decision = _conflictResolver is null
                        ? IdentityConflictDecision.KeepLegacy
                        : await _conflictResolver.ResolveAsync(
                            new IdentityConflict(
                                steamId.Value,
                                personaName,
                                legacyUuid!.Value,
                                derivedUuid,
                                conflictingWorlds),
                            token).ConfigureAwait(false);
                    if (decision == IdentityConflictDecision.Cancel)
                    {
                        return new IdentityBindingResult(
                            false,
                            null,
                            false,
                            true,
                            null,
                            "Привязка прогресса к Steam отменена. Профиль не изменён.");
                    }
                }

                if (decision == IdentityConflictDecision.KeepLegacy)
                {
                    source = IdentityBindingSource.MigratedUuidJson;
                    playerUuid = legacyUuid!.Value;
                }

                backupPath = _store.BackUpLegacyIdentityFile();
            }

            var created = new SteamIdentityBinding
            {
                SteamId64 = canonical,
                PersonaName = personaName,
                PlayerUuid = playerUuid,
                Source = source,
                BoundAtUtc = DateTimeOffset.UtcNow,
                LegacyPlayerUuid = legacyUuid,
                LegacyBackupPath = backupPath,
                ConflictDecision = conflictResolved
                    ? (source == IdentityBindingSource.MigratedUuidJson
                        ? IdentityConflictDecision.KeepLegacy
                        : IdentityConflictDecision.UseDerived)
                    : null
            };
            document.Bindings.Add(created);
            _store.Save(document);
            Publish(created);

            var migrated = source == IdentityBindingSource.MigratedUuidJson;
            _logger?.Info(migrated
                ? $"Progress from UUID.json ({playerUuid}) is now bound to Steam account {personaName} ({canonical})."
                : $"Steam account {personaName} ({canonical}) plays as the derived profile {playerUuid}.");
            return new IdentityBindingResult(
                true,
                source,
                migrated,
                conflictResolved,
                backupPath,
                migrated
                    ? $"Прогресс из UUID.json привязан к аккаунту Steam {personaName}."
                    : "");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The identity everything downstream uses. IdentityId stays the Minecraft
    /// UUID for now - the peer layer switches to the SteamID64 in a later step.
    /// </summary>
    public LocalIdentityContext ResolveContext(AppSettings settings)
    {
        var binding = Binding ?? throw new IdentityUnavailableException(SteamUnavailableMessage);
        var nickname = LocalIdentityService.NormalizeNickname(settings.PlayerName, Environment.UserName);
        var uuid = binding.PlayerUuid.ToString("D");
        return new LocalIdentityContext
        {
            IdentityId = uuid,
            IdentityName = nickname,
            MinecraftUuid = uuid,
            SteamId64 = SteamId64.Parse(binding.SteamId64),
            PlayerUuid = binding.PlayerUuid,
            Source = binding.Source,
            SessionAccessToken = CreateSessionToken(uuid, binding.SteamId64, nickname)
        };
    }

    private void Publish(SteamIdentityBinding binding)
    {
        lock (_stateGate) _binding = binding;
    }

    private Guid? TryReadLegacyUuid()
    {
        if (!File.Exists(_paths.IdentityFile)) return null;
        try
        {
            var identity = JsonSerializer.Deserialize<PortableIdentity>(
                File.ReadAllText(_paths.IdentityFile),
                LegacyJsonOptions);
            return identity is null || identity.PlayerUuid == Guid.Empty ? null : identity.PlayerUuid;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new IdentityUnavailableException(
                "Файл Minecraft\\Personal\\UUID.json повреждён или недоступен. " +
                "Восстановите его перед запуском игры.",
                ex);
        }
    }

    /// <summary>
    /// Worlds that already hold player data for a UUID. Used to spot the case
    /// where both the legacy profile and the derived one have a history here.
    /// </summary>
    private List<string> FindWorldsHoldingProfile(Guid playerUuid)
    {
        if (!Directory.Exists(_paths.Worlds)) return [];

        var fileName = playerUuid.ToString("D") + ".dat";
        var worlds = new List<string>();
        foreach (var world in Directory.EnumerateDirectories(_paths.Worlds))
        {
            var profile = Path.Combine(world, "playerdata", fileName);
            if (File.Exists(profile)) worlds.Add(Path.GetFileName(world));
        }
        return worlds;
    }

    private static string CreateSessionToken(string uuid, string steamId64, string nickname)
    {
        var seed = $"{SessionTokenNamespace}|{uuid}|{steamId64}|{nickname}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..40].ToLowerInvariant();
    }

    public void Dispose() => _gate.Dispose();
}
