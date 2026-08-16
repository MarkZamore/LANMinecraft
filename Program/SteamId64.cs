using System.Globalization;

namespace Minecraft;

/// <summary>
/// A Steam account id, the value that identifies a player to every other
/// launcher once the peer layer speaks Steam. It replaces the random GUID the
/// VPN era used as a peer id, so it also has to be safe as a file-name segment
/// - which a validated SteamID64 is, being decimal digits only.
/// </summary>
public readonly record struct SteamId64
{
    /// <summary>Individual accounts: universe 1 (public), account type 1.</summary>
    private const ulong IndividualAccountBase = 76_561_197_960_265_728UL;
    private const ulong MaximumAccountId = uint.MaxValue;

    private SteamId64(ulong value) => Value = value;

    public ulong Value { get; }

    public static SteamId64 None => default;

    public bool IsValid => IsValidValue(Value);

    public static bool IsValidValue(ulong value) =>
        value > IndividualAccountBase &&
        value - IndividualAccountBase <= MaximumAccountId;

    public static bool TryParse(string? text, out SteamId64 steamId)
    {
        steamId = None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (!ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (!IsValidValue(value)) return false;
        steamId = new SteamId64(value);
        return true;
    }

    public static bool TryFrom(ulong value, out SteamId64 steamId)
    {
        steamId = None;
        if (!IsValidValue(value)) return false;
        steamId = new SteamId64(value);
        return true;
    }

    /// <summary>Parses or throws; use for values the launcher itself produced.</summary>
    public static SteamId64 Parse(string? text) =>
        TryParse(text, out var steamId)
            ? steamId
            : throw new FormatException($"'{text}' is not a valid SteamID64.");

    /// <summary>The canonical decimal form used on the wire and on disk.</summary>
    public override string ToString() =>
        IsValid ? Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>
    /// Canonicalises a peer-supplied string, rejecting anything that is not a
    /// plain individual SteamID64 (including GUIDs from the VPN era).
    /// </summary>
    public static bool TryNormalize(string? text, out string canonical)
    {
        canonical = string.Empty;
        if (!TryParse(text, out var steamId)) return false;
        canonical = steamId.ToString();
        return true;
    }
}
