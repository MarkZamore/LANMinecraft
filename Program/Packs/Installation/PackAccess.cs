using System.IO;

namespace Minecraft;

/// <summary>One writer for each installed pack within the launcher process.</summary>
internal static class PackAccess
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, TaskCompletionSource> Owners = new(StringComparer.OrdinalIgnoreCase);

    internal static async Task<Lease> AcquireAsync(AppPaths paths, string pack, CancellationToken token)
    {
        var path = Key(paths, pack);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task wait;
            lock (Gate)
            {
                if (!Owners.TryGetValue(path, out var owner)) return Reserve([path]);
                wait = owner.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    internal static Lease? TryAcquire(AppPaths paths, string pack) => TryAcquire(paths, [pack]);

    // Check and reserve the whole set atomically: no operation holds one alias or
    // batch member while waiting for another, and callers need not block the UI.
    internal static Lease? TryAcquire(AppPaths paths, IEnumerable<string> packs)
    {
        var keys = packs.Select(pack => Key(paths, pack))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (Gate)
            return keys.Any(Owners.ContainsKey) ? null : Reserve(keys);
    }

    internal static Lease RequireAvailable(AppPaths paths, IEnumerable<string> packs) =>
        TryAcquire(paths, packs) ?? throw new IOException("Другая операция уже использует сборку. Повторите попытку.");

    internal static bool IsRunning(AppPaths paths, IEnumerable<string> packs, Logger? logger)
    {
        var names = packs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ClientPresenceService(paths, logger).ReadLiveSessions()
            .Any(session => names.Contains(session.PackRelativePath.Replace('\\', '/').Trim('/')));
    }

    private static Lease Reserve(string[] keys)
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var key in keys) Owners.Add(key, released);
        return new Lease(keys, released);
    }

    private static string Key(AppPaths paths, string pack)
    {
        if (!PackCatalogIndex.ValidFolder(pack))
            throw new InvalidDataException("Недопустимое имя папки сборки.");
        var full = paths.CombineUnderPacks(pack);
        for (string? entry = full; entry is not null; entry = Path.GetDirectoryName(entry))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(entry); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
                throw new IOException("Папка сборки или её родительский каталог перенаправлены: " + entry);
        }
        return full;
    }

    internal sealed class Lease(string[] keys, TaskCompletionSource released) : IDisposable
    {
        private bool _disposed;

        internal void RequireOwner(AppPaths paths, string pack)
        {
            var key = Key(paths, pack);
            lock (Gate)
                if (_disposed || !ReferenceEquals(Owners.GetValueOrDefault(key), released))
                    throw new InvalidOperationException("Pack access was not acquired for the requested directory.");
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var key in keys) Owners.Remove(key);
            }
            released.SetResult();
        }
    }
}
