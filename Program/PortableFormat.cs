namespace Minecraft;

/// <summary>
/// One version for everything the launcher writes, and one for everything it
/// says to another launcher.
///
/// Every document and every protocol used to carry its own number, bumped
/// whenever that particular file changed - the world metadata reached 6 while
/// the player manifest was on 3 and the transfer protocol on 5. Those numbers
/// recorded the history of the code, not anything a player or another launcher
/// needs to know: all three players update together, and a build either
/// understands its own formats or it does not.
///
/// So there is one rule now. A build writes <see cref="SchemaVersion"/> into
/// every file it owns and <see cref="ProtocolVersion"/> into everything it
/// sends. It reads anything at or below its own number, because it knows every
/// shape it has ever written, and refuses anything above it, because that came
/// from a build that knows more. Releasing a change means incrementing one
/// constant here; nothing else in the codebase decides a version.
/// </summary>
public static class PortableFormat
{
    /// <summary>
    /// The shape of the files the launcher writes: world metadata, the player
    /// manifest, waypoint manifests, transaction journals, settings.
    ///
    /// It starts at 10 rather than 1 so that it is above every per-file number
    /// that came before it - a document written by an older build is simply an
    /// older version of the same sequence, with no special cases to remember.
    /// </summary>
    public const int SchemaVersion = 10;

    /// <summary>
    /// What one launcher says to another: world transfer, waypoint sync, skins,
    /// rich presence, bug reports. Also above every number that came before.
    /// </summary>
    public const int ProtocolVersion = 10;

    /// <summary>True when this build knows the shape a document claims to be.</summary>
    public static bool CanRead(int schemaVersion) => schemaVersion is > 0 and <= SchemaVersion;

    /// <summary>True when the peer speaks a protocol this build understands.</summary>
    public static bool CanSpeak(int protocolVersion) => protocolVersion is > 0 and <= ProtocolVersion;

    /// <summary>The message a player sees when a file came from a newer build.</summary>
    public static string DescribeUnreadable(string what, int schemaVersion) =>
        $"{what} записан более новой версией лаунчера (формат {schemaVersion}, " +
        $"эта версия понимает до {SchemaVersion}). Обновите лаунчер.";
}
