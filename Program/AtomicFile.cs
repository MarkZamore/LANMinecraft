using System.IO;
using System.Text;

namespace Minecraft;

internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
    {
        WriteAllBytes(path, (encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).GetBytes(contents));
    }

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"File has no parent directory: {path}");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            var verified = File.ReadAllBytes(temporaryPath);
            if (!verified.AsSpan().SequenceEqual(contents))
            {
                throw new IOException($"Temporary file verification failed: {Path.GetFileName(path)}");
            }
            Publish(temporaryPath, fullPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Puts the finished file in place. Anything that opens a file the moment
    /// it changes - a virus scanner, a folder that syncs to the cloud - holds
    /// it for a breath, and a single attempt would report that breath as a
    /// failed save. The last attempt deletes first, which needs no handle of
    /// its own.
    /// </summary>
    private static void Publish(string temporaryPath, string fullPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, fullPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException &&
                                       attempt < PublishAttempts && !Directory.Exists(fullPath))
            {
                Thread.Sleep(PublishRetryDelay * attempt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException &&
                                       File.Exists(fullPath))
            {
                File.SetAttributes(fullPath, FileAttributes.Normal);
                File.Delete(fullPath);
                File.Move(temporaryPath, fullPath);
                return;
            }
        }
    }

    private const int PublishAttempts = 5;
    private const int PublishRetryDelay = 120;
}
