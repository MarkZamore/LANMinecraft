using System.IO;

namespace Minecraft.Tests;

/// <summary>
/// Removing the temporary tree a test worked in, without letting the removal
/// decide whether the test passed.
///
/// Windows releases a file handle a moment after the writer is done with it,
/// and on a build agent something is usually watching the filesystem as well.
/// A recursive delete that lands in that moment throws "the process cannot
/// access the file because it is being used by another process" - which is what
/// turned a green world-transfer test red on CI while it passed locally.
///
/// So the delete waits a little and tries again, and if the tree still will not
/// go it is left behind. It is in the temporary folder; the operating system
/// clears it eventually, and a test that already made its assertions has
/// nothing left to say about it.
/// </summary>
internal static class TempTree
{
    private const int Attempts = 5;

    public static void Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= Attempts) return;
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
