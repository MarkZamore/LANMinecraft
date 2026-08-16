using System.Globalization;

namespace Minecraft;

/// <summary>
/// What is left of the pre-Steam identity: the nickname rules the UI enforces.
/// Choosing the player's UUID now belongs to <see cref="SteamIdentityService"/>,
/// which reads Minecraft/Personal/UUID.json but never creates one behind the
/// player's back.
/// </summary>
public static class LocalIdentityService
{
    public const int MaxNicknameLength = 16;

    public static string NormalizeNickname(string? value, string? fallback = null)
    {
        if (TryNormalizeNickname(value, out var normalized, out _))
        {
            return normalized;
        }

        return TryNormalizeNickname(fallback, out normalized, out _) ? normalized : "Player";
    }

    public static bool IsNicknameDraftValid(string? value)
    {
        if (value is null || value.Length > MaxNicknameLength)
        {
            return false;
        }

        return HasOnlyAllowedUnicode(value);
    }

    public static bool TryNormalizeNickname(string? value, out string normalized, out string error)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            error = "Ник не может быть пустым.";
            return false;
        }
        if (normalized.Length > MaxNicknameLength)
        {
            error = $"Ник не может быть длиннее {MaxNicknameLength} символов UTF-16.";
            return false;
        }
        if (!HasOnlyAllowedUnicode(normalized))
        {
            error = "Ник содержит управляющий символ или перенос строки.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool HasOnlyAllowedUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }
                index++;
                continue;
            }
            if (char.IsLowSurrogate(character) || char.IsControl(character))
            {
                return false;
            }

            var category = char.GetUnicodeCategory(character);
            if (category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
            if (category == UnicodeCategory.Format && character is not ('\u200C' or '\u200D'))
            {
                return false;
            }
        }

        return true;
    }

}

public sealed class PortableIdentity
{
    public int SchemaVersion { get; set; } = 1;
    public Guid PlayerUuid { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class LocalIdentityContext
{
    public string IdentityId { get; set; } = "";
    public string IdentityName { get; set; } = "";
    public string MinecraftUuid { get; set; } = "";
    public string SessionAccessToken { get; set; } = "";

    /// <summary>The Steam account this machine is signed in to; None before the migration.</summary>
    public SteamId64 SteamId64 { get; set; }

    /// <summary>Same value as <see cref="MinecraftUuid"/>, typed.</summary>
    public Guid PlayerUuid { get; set; }

    public IdentityBindingSource Source { get; set; } = IdentityBindingSource.MigratedUuidJson;
}
