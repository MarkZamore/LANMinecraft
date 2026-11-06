using System.IO;

namespace Minecraft;

public static class WorldAccessGuard
{
    public static bool IsOpen(string worldPath)
    {
        try
        {
            EnsureClosed(worldPath);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public static void EnsureClosed(string worldPath)
    {
        using var locked = LockClosed(worldPath);
    }

    internal static FileStream? LockClosed(string worldPath)
    {
        var lockPath = Path.Combine(worldPath, "session.lock");
        if (!File.Exists(lockPath)) return null;
        FileStream? stream = null;
        try
        {
            stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Lock(0, Math.Max(1, stream.Length));
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            throw new InvalidOperationException(
                $"World {Path.GetFileName(worldPath)} is open in Minecraft. Close the world before continuing.",
                ex);
        }
    }
}
