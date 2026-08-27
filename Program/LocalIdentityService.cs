using System.Globalization;

namespace Minecraft;

/// <summary>
/// What is left of the pre-Steam identity: the nickname rules the UI enforces.
/// Choosing the player's UUID belongs to <see cref="SteamIdentityService"/>.
/// </summary>
public static class LocalIdentityService
{
    public const int MaxNicknameLength = 16;

    /// <summary>The shortest name Minecraft's own rule allows.</summary>
    public const int MinNicknameLength = 3;

    /// <summary>
    /// Whether a name is one Minecraft itself would accept: Latin letters,
    /// digits and the underscore, three to sixteen of them.
    /// </summary>
    /// <remarks>
    /// Not a taste in names - a rule of the game. A command reads a player's
    /// name as an unquoted string, and the parser there accepts only
    /// [A-Za-z0-9_.+-]; the first Cyrillic letter ends the parse. So a Russian
    /// nickname is not merely unusual, it is a name nothing can address:
    /// "/tp @s Женя" does not run, and neither does a message, a team, a
    /// scoreboard entry, or any mod command built out of a name. It was found
    /// the way such things are found - two players could not teleport to each
    /// other and nobody could say why.
    ///
    /// A name already saved is left alone: it is what the player is called on
    /// every world they have played, and taking it away here would cost them
    /// more than the commands are worth. Only new ones are held to the rule.
    /// </remarks>
    public static bool IsNameMinecraftAccepts(string? value)
    {
        if (value is null || value.Length < MinNicknameLength || value.Length > MaxNicknameLength)
        {
            return false;
        }
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_') return false;
        }
        return true;
    }

    /// <summary>
    /// A name on its way in: held to what Minecraft accepts, with the reason
    /// said in the words a player would use.
    /// </summary>
    public static bool TryNormalizeNewNickname(string? value, out string normalized, out string error)
    {
        if (!TryNormalizeNickname(value, out normalized, out error)) return false;
        if (IsNameMinecraftAccepts(normalized))
        {
            error = string.Empty;
            return true;
        }

        error = normalized.Length < MinNicknameLength
            ? $"Ник короче {MinNicknameLength} символов Minecraft не принимает."
            : "В нике только латиница, цифры и подчёркивание: команды игры - телепорт, " +
              "сообщения, команды модов - читают имя латиницей и на первой же русской букве " +
              "останавливаются.";
        return false;
    }

    public static string NormalizeNickname(string? value, string? fallback = null)
    {
        if (TryNormalizeNickname(value, out var normalized, out _))
        {
            return normalized;
        }

        return TryNormalizeNickname(fallback, out normalized, out _) ? normalized : "Player";
    }

    /// <summary>
    /// Whether what is being typed may stand in the field. Shorter than the
    /// minimum is allowed here - a name is typed one letter at a time - and
    /// the rest is what Minecraft accepts, so a letter the game could never
    /// address never reaches the box.
    /// </summary>
    public static bool IsNicknameDraftValid(string? value)
    {
        if (value is null || value.Length > MaxNicknameLength)
        {
            return false;
        }
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_') return false;
        }

        return true;
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

/// <summary>
/// Who this machine is, in both currencies: the Steam account other launchers
/// address, and the Minecraft UUID that names progress inside worlds. There is
/// deliberately no single "identity id" any more - the two are not the same
/// value, and the compiler should say which one a call site means.
/// </summary>
public sealed class LocalIdentityContext
{
    public string IdentityName { get; set; } = "";
    public string MinecraftUuid { get; set; } = "";
    public string SessionAccessToken { get; set; } = "";

    /// <summary>The Steam account this machine is signed in to.</summary>
    public SteamId64 SteamId64 { get; set; }

    /// <summary>Same value as <see cref="MinecraftUuid"/>, typed.</summary>
    public Guid PlayerUuid { get; set; }

    public IdentityBindingSource Source { get; set; } = IdentityBindingSource.Derived;
}
