using System.IO;

namespace Minecraft;

/// <summary>Serializes shared game files across launcher processes for one portable install.</summary>
internal static class SharedRuntimeGate
{
    internal static FileStream? TryAcquire(AppPaths paths)
    {
        foreach (var parent in new[] { paths.Root, paths.Service, paths.Launcher })
        {
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The shared runtime directory is redirected.");
        }
        var path = Path.Combine(paths.Launcher, ".shared-runtime.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The shared runtime lock is redirected.");

        Directory.CreateDirectory(paths.Launcher);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }

    internal static async Task<FileStream> AcquireAsync(AppPaths paths, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryAcquire(paths) is { } lease) return lease;
            await Task.Delay(75, token).ConfigureAwait(false);
        }
    }
}
