namespace Minecraft;

/// <summary>Where a player's Minecraft UUID came from.</summary>
public enum IdentityBindingSource
{
    /// <summary>Inherited from Minecraft/Personal/UUID.json, so existing progress keeps working.</summary>
    MigratedUuidJson,

    /// <summary>Derived from the Steam account, so the same account gets the same UUID anywhere.</summary>
    Derived
}

/// <summary>
/// Raised when the launcher cannot tell who the player is - Steam is closed,
/// nobody is signed in, or the identity file is damaged. The message is shown
/// to the player as is.
/// </summary>
public sealed class IdentityUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// The rare case where both histories exist: this machine has a legacy
/// UUID.json that was never bound, and some world already holds progress under
/// the UUID this Steam account would derive. Choosing wrongly hides a player's
/// quests, teams and homes, so the player decides.
/// </summary>
public sealed record IdentityConflict(
    ulong SteamId64,
    string PersonaName,
    Guid LegacyUuid,
    Guid DerivedUuid,
    IReadOnlyList<string> ConflictingWorlds);

public enum IdentityConflictDecision
{
    /// <summary>Keep playing as the legacy UUID (recommended: it holds the existing progress).</summary>
    KeepLegacy,

    /// <summary>Switch to the UUID derived from the Steam account.</summary>
    UseDerived,

    /// <summary>Decide later; nothing is written.</summary>
    Cancel
}

/// <summary>The Steam account the launcher is running as.</summary>
public interface ISteamUserSource
{
    bool TryGetLocalUser(out ulong steamId64, out string personaName);
}

/// <summary>Asks the player to resolve an <see cref="IdentityConflict"/>.</summary>
public interface IIdentityConflictResolver
{
    Task<IdentityConflictDecision> ResolveAsync(IdentityConflict conflict, CancellationToken token);
}

/// <summary>What the launcher knows about the local player.</summary>
public interface IIdentityService
{
    bool IsBound { get; }

    LocalIdentityContext ResolveContext(AppSettings settings);
}

/// <summary>Outcome of the one-time binding at startup.</summary>
public sealed record IdentityBindingResult(
    bool Bound,
    IdentityBindingSource? Source,
    bool Migrated,
    bool ConflictResolved,
    string? BackupPath,
    string Message);
