using System.Globalization;
using System.IO;
using System.Text;

namespace Minecraft;

/// <summary>
/// Finds the folder Xaero's minimap keeps a guest's waypoints in.
///
/// Xaero names a multiplayer folder after the server address, and e4steam's
/// address carries a token that is regenerated every time the host opens their
/// world: <c>s-&lt;host id in base36&gt;-&lt;random token&gt;.steam</c>. So a guest who
/// joins the same friend three times ends up with three folders, each holding
/// the waypoints of one session, and none of them under a name the launcher
/// could predict.
///
/// What is predictable is the host: their SteamID64 in base36 is the second
/// segment of every one of those names. That is enough to collect a guest's
/// waypoints for a host across sessions, and to write them back into the
/// session the guest is in now.
/// </summary>
public static class XaeroMultiplayerFolderResolver
{
    private const string MultiplayerPrefix = "Multiplayer_";

    /// <summary>The minimap's own root, where every world and server folder lives.</summary>
    public static string GetMinimapRoot(string gameDirectory) =>
        Path.Combine(gameDirectory, "xaero", "minimap");

    /// <summary>
    /// The host's SteamID64 as e4steam writes it into an address: base36,
    /// lower case. 76561198256236531 becomes "kxuogxe7bhv".
    /// </summary>
    public static string ToBase36(ulong value)
    {
        if (value == 0) return "0";
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var builder = new StringBuilder(13);
        while (value > 0)
        {
            builder.Insert(0, digits[(int)(value % 36)]);
            value /= 36;
        }
        return builder.ToString();
    }

    /// <summary>
    /// Every folder that belongs to this host, newest first. The list is empty
    /// until the guest has actually joined them once - there is nothing to
    /// import from a host you have never visited.
    /// </summary>
    public static string[] FindHostFolders(string gameDirectory, SteamId64 host)
    {
        if (!host.IsValid) return [];
        var root = GetMinimapRoot(gameDirectory);
        if (!Directory.Exists(root)) return [];

        var marker = "-" + ToBase36(host.Value) + "-";
        try
        {
            return Directory.EnumerateDirectories(root, MultiplayerPrefix + "*")
                .Where(directory => BelongsToHost(Path.GetFileName(directory), marker))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The folder the guest's current session writes to - the most recently
    /// touched one - or null when they have not joined this host yet.
    /// </summary>
    public static string? FindCurrentHostFolder(string gameDirectory, SteamId64 host)
    {
        var folders = FindHostFolders(gameDirectory, host);
        return folders.Length == 0 ? null : folders[0];
    }

    /// <summary>
    /// The address Xaero derived a folder name from, which is what the
    /// launcher hands the provider as <see cref="WaypointNativeContext.RemoteAddress"/>
    /// so exports and imports land where the game will read them.
    /// </summary>
    public static string? FindCurrentHostAddress(string gameDirectory, SteamId64 host)
    {
        var folder = FindCurrentHostFolder(gameDirectory, host);
        return folder is null ? null : ToAddress(folder);
    }

    /// <summary>
    /// The address behind a folder path. The provider escapes an address to
    /// build the folder name, so this has to undo exactly that escaping or the
    /// provider would write to a second, double-escaped folder.
    /// </summary>
    public static string ToAddress(string folderPath) =>
        DecodeNode(Path.GetFileName(folderPath)[MultiplayerPrefix.Length..]);

    /// <summary>
    /// True when a folder name is one of this host's sessions. e4steam's
    /// address has exactly three parts - "s", the host in base36, and the
    /// session token - so the host segment is matched whole rather than as a
    /// substring, which would also match a token that happens to contain it.
    /// </summary>
    private static bool BelongsToHost(string folderName, string marker) =>
        folderName.StartsWith(MultiplayerPrefix + "s" + marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>Undoes the escaping Xaero applies to a folder segment.</summary>
    private static string DecodeNode(string value) => value
        .Replace("%rb%", "]", StringComparison.Ordinal)
        .Replace("%lb%", "[", StringComparison.Ordinal)
        .Replace("%bs%", "\\", StringComparison.Ordinal)
        .Replace("%fs%", "/", StringComparison.Ordinal)
        .Replace("%us%", "_", StringComparison.Ordinal);

    /// <summary>
    /// A stable name for a host that the guest has never joined in game. It is
    /// only ever used as a place to keep waypoints until a real session folder
    /// exists, so it must not look like one of e4steam's addresses.
    /// </summary>
    public static string PendingAddress(SteamId64 host) =>
        host.IsValid ? "lanmc-" + host.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
}
