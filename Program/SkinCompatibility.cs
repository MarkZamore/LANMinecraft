using System.IO;

namespace Minecraft;

/// <summary>
/// What a skin has to be, and what the pack that is selected can do with it.
/// </summary>
/// <remarks>
/// The file itself is checked in one place, by <see cref="SkinService"/>, and
/// the rule there is about the file alone: a PNG, sixty-four pixels wide or a
/// multiple of it, square or half as tall. That was the whole of it while every
/// pack was on a modern version.
///
/// It stopped being the whole of it when the launcher began carrying packs back
/// to Minecraft 1.7. The square layout - a second layer on every limb, a left
/// arm and a left leg of their own - arrived in 1.8, and so did the slim model.
/// Before that there is one layout, 64x32, and a square file handed to it is
/// read as though it were the old one: the character comes out wrong, and
/// nothing anywhere says why. So the file may be perfectly valid and still be
/// the wrong file for the pack in the box, which is a thing the player can only
/// be told.
/// </remarks>
public static class SkinCompatibility
{
    /// <summary>
    /// The version that learned the square layout, the slim model and skins
    /// larger than the model needs. Everything below reads 64x32 and nothing
    /// else.
    /// </summary>
    public const string FirstModernVersion = "1.8";

    /// <summary>
    /// True where the pack's Minecraft can read a square skin. An unknown
    /// version is taken as modern: not knowing is not a reason to warn about a
    /// skin that is almost certainly fine.
    /// </summary>
    public static bool ReadsModernSkins(string? minecraftVersion)
    {
        if (!SteamTransportCatalog.TryParseVersion(minecraftVersion, out var version)) return true;
        SteamTransportCatalog.TryParseVersion(FirstModernVersion, out var modern);
        return SteamTransportCatalog.Compare(version, modern) >= 0;
    }

    /// <summary>
    /// The shape of the chosen skin, or null where none is chosen or the file
    /// is gone. Read from the PNG header rather than decoded: the eight bytes
    /// that carry the size are all this needs.
    /// </summary>
    public static (int Width, int Height)? MeasureSkin(string? skinPath)
    {
        if (string.IsNullOrWhiteSpace(skinPath)) return null;
        try
        {
            using var file = File.OpenRead(skinPath);
            Span<byte> header = stackalloc byte[24];
            return file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length &&
                   SkinService.TryReadPngDimensions(header, out var width, out var height)
                ? (width, height)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the Skin button says on hover: the rule for the file, and what the
    /// pack in the box will make of the one that is chosen.
    /// </summary>
    public static string Describe(string? minecraftVersion, string? skinPath)
    {
        var rule =
            "Скин - PNG шириной ровно 64 пикселя: столько читают все версии. Более крупный старые " +
            "обрезают по левому верхнему углу, а с 1.17 игра его вовсе отбрасывает. " +
            "Размер 64x64 для современного формата или 64x32 для старого. " +
            $"Не больше {SkinService.MaxSkinBytes / (1024 * 1024)} МБ: файл уходит по Steam всем, кто играет рядом.";

        if (ReadsModernSkins(minecraftVersion)) return rule;

        var version = string.IsNullOrWhiteSpace(minecraftVersion) ? "этой версии" : $"Minecraft {minecraftVersion}";
        var warning =
            $" Выбранная сборка на {version}: квадратные скины и тонкая модель появились в {FirstModernVersion}, " +
            "и здесь подойдёт только 64x32.";

        var measured = MeasureSkin(skinPath);
        if (measured is { } size && size.Height == size.Width)
        {
            warning += $" Выбранный сейчас - {size.Width}x{size.Height}, и на этой сборке он покажется неправильно.";
        }

        return rule + warning;
    }
}
