namespace Minecraft;

/// <summary>Where a player's Minecraft UUID came from.</summary>
public enum IdentityBindingSource
{
    /// <summary>One of the players who existed before Steam; see <see cref="KnownSteamPlayers"/>.</summary>
    KnownPlayer,

    /// <summary>Derived from the Steam account, so the same account gets the same UUID anywhere.</summary>
    Derived
}

/// <summary>
/// Raised when the launcher cannot tell who the player is - Steam is closed or
/// nobody is signed in. The message is shown to the player as is.
/// </summary>
public sealed class IdentityUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The Steam account the launcher is running as.</summary>
public interface ISteamUserSource
{
    bool TryGetLocalUser(out ulong steamId64, out string personaName);
}

/// <summary>What the launcher knows about the local player.</summary>
public interface IIdentityService
{
    bool IsBound { get; }

    LocalIdentityContext ResolveContext(AppSettings settings);
}

/// <summary>One Steam account and the Minecraft UUID its progress lives under.</summary>
public sealed record SteamIdentityBinding(
    SteamId64 SteamId64,
    string PersonaName,
    Guid PlayerUuid,
    IdentityBindingSource Source);

/// <summary>Outcome of binding at startup.</summary>
public sealed record IdentityBindingResult(
    bool Bound,
    IdentityBindingSource? Source,
    string Message);
