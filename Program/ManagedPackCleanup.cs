using System.IO;
using System.Text;
using System.Text.Json;

namespace Minecraft;

internal static class ManagedPackCleanup
{
    internal static bool IsManagedPack(AppPaths paths, PortablePackSyncService sync, string packRelativePath,
        Action<CancellationToken>? checkpoint = null, CancellationToken token = default) =>
        sync.ReadCleanupProof(paths, packRelativePath, checkpoint ?? (static _ => { }), token) is not null;

    internal static int Run(AppPaths paths, PortablePackSyncService sync, string packRelativePath,
        Action<CancellationToken> checkpoint, CancellationToken token) =>
        Run(paths, sync, packRelativePath, checkpoint, token, out _);

    internal static int Run(AppPaths paths, PortablePackSyncService sync, string packRelativePath,
        Action<CancellationToken> checkpoint, CancellationToken token, out bool managed)
    {
        var proof = sync.ReadCleanupProof(paths, packRelativePath, checkpoint, token);
        managed = false;
        if (proof is null) return 0;
        var removed = 0;
        Walk(proof.Directory, "", depth: 0);
        if (proof.IsCurrent(paths))
        {
            removed += PortablePackSyncService.CleanupAbandonedPlans(paths, proof.Directory,
                DateTime.UtcNow.AddDays(-1), checkpoint, token);
            managed = proof.IsCurrent(paths);
        }
        return removed;

        bool Walk(string directory, string relativeDirectory, int depth)
        {
            if (depth > 128) return false;
            var changed = false;
            try
            {
                if (!MaintenancePath.TryRead(paths, directory, out var folderAttributes) ||
                    !folderAttributes.HasFlag(FileAttributes.Directory)) return false;
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    checkpoint(token);
                    var name = Path.GetFileName(entry);
                    if (MaintenancePath.IsProtectedAssetName(name) ||
                        depth == 0 && PortablePackSyncService.IsProtectedCleanupRoot(name)) continue;
                    var relative = depth == 0 ? name : relativeDirectory + "/" + name;
                    if (!proof.CanCleanLauncherDirectory && depth == 0 && name.Equals("launcher", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!MaintenancePath.TryRead(paths, entry, out var attributes)) continue;
                    var directoryEntry = attributes.HasFlag(FileAttributes.Directory);
                    var changedBelow = directoryEntry && Walk(entry, relative, depth + 1);
                    var keep = directoryEntry
                        ? !changedBelow || proof.Directories.Contains(relative) || proof.Files.Contains(relative)
                        : proof.Files.Contains(relative) || proof.Directories.Contains(relative);
                    if (!keep)
                    {
                        checkpoint(token);
                        // A source, sync state or partial metadata publication changed during the walk.
                        if (!proof.IsCurrent(paths)) return changed;
                        token.ThrowIfCancellationRequested();
                        if (!MaintenancePath.TryDelete(paths, entry, directoryEntry,
                            () => !token.IsCancellationRequested && proof.IsCurrent(paths))) continue;
                        removed++;
                        changed = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return changed;
        }
    }
}

public sealed partial class PortablePackSyncService
{
    private const string LauncherCleanupStateFileName = ".portable-pack-launcher-state.json";

    internal static bool IsProtectedCleanupRoot(string name) => IsProtectedRootName(name) ||
        IsPackManifestPath(name) || IsStagingEntryName(name) ||
        name.Equals(PackLaunchContextService.RuntimeDirectory, StringComparison.OrdinalIgnoreCase);

    internal sealed record CleanupStamp(long CreationTimeTicks, long WriteTimeTicks, long Length);

    internal sealed record CleanupProof(string Directory, IReadOnlySet<string> Files, IReadOnlySet<string> Directories,
        bool CanCleanLauncherDirectory, CleanupStamp Source, CleanupStamp State,
        bool LauncherMetadataExists, CleanupStamp? LauncherMetadata)
    {
        internal bool IsCurrent(AppPaths paths)
        {
            try
            {
                return !ExistsForCleanup(Path.Combine(Directory, ApplyPendingFileName)) &&
                    ReadCleanupStamp(paths, Path.Combine(Directory, SourceMarkerFileName)) == Source &&
                    ReadCleanupStamp(paths, Path.Combine(Directory, SyncStateFileName)) == State &&
                    ExistsForCleanup(Path.Combine(Directory, LauncherCleanupStateFileName)) == LauncherMetadataExists &&
                    ReadCleanupStamp(paths, Path.Combine(Directory, LauncherCleanupStateFileName)) == LauncherMetadata;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    internal CleanupProof? ReadCleanupProof(AppPaths paths, string packRelativePath,
        Action<CancellationToken> checkpoint, CancellationToken token)
    {
        if (!PackCatalogService.ValidFolder(packRelativePath)) return null;
        try
        {
            checkpoint(token);
            var directory = paths.CombineUnderPacks(packRelativePath);
            if (ExistsForCleanup(Path.Combine(directory, ApplyPendingFileName)) ||
                !MaintenancePath.TryRead(paths, Path.Combine(directory, PackManifestService.ManifestFileName), out var manifest) ||
                manifest.HasFlag(FileAttributes.Directory)) return null;

            var sourcePath = Path.Combine(directory, SourceMarkerFileName);
            var statePath = Path.Combine(directory, SyncStateFileName);
            if (ReadCleanupStamp(paths, sourcePath) is not { Length: <= 4096 } sourceStamp ||
                TryReadSourceMarker(sourcePath) is not { } source ||
                !MatchesCleanupSource(packRelativePath, source) ||
                ReadCleanupStamp(paths, statePath) is not { Length: <= 32 * 1024 * 1024 } stateStamp) return null;

            var state = JsonSerializer.Deserialize<SyncStateDto>(File.ReadAllText(statePath), JsonOptions);
            if (state?.SchemaVersion != SyncStateCacheGeneration || state.Files is not { Count: > 0 } ||
                string.IsNullOrWhiteSpace(state.Revision)) return null;

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, file) in state.Files)
            {
                checkpoint(token);
                var relative = ValidateManifestPath(path, directory, checkAncestors: false);
                if (file is null || file.SizeBytes < 0 || !files.Add(relative)) return null;
                _ = ValidateSha256(file.Sha256, relative);
                AddParents(relative, parents);
            }
            if (!files.Contains(PackManifestService.ManifestFileName)) return null;

            var metadataPath = Path.Combine(directory, LauncherCleanupStateFileName);
            var metadataExists = ExistsForCleanup(metadataPath);
            var metadataStamp = ReadCleanupStamp(paths, metadataPath);
            var complete = state.HasCompleteLauncherPaths;
            if (metadataExists)
            {
                // An unreadable current overlay may describe files newer than the full snapshot.
                // Defer launcher/ unless it is an ordinary overlay tied to the current source.
                complete = false;
                if (metadataStamp is { Length: <= MaximumManifestBytes })
                {
                    try
                    {
                        var overlay = JsonSerializer.Deserialize<LauncherCleanupState>(File.ReadAllText(metadataPath), JsonOptions);
                        if (overlay is { SchemaVersion: 1, Source: not null, SyncState: not null, Files: not null } &&
                            MatchesCleanupSource(packRelativePath, overlay.Source))
                        {
                            if (overlay.SyncState == stateStamp)
                            {
                                var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var path in overlay.Files)
                                {
                                    checkpoint(token);
                                    var relative = ValidateManifestPath(path, directory, checkAncestors: false);
                                    if (!relative.StartsWith(LauncherDataPrefix, StringComparison.OrdinalIgnoreCase) || !accepted.Add(relative))
                                        throw new InvalidDataException("Invalid launcher metadata inventory.");
                                }
                                foreach (var relative in accepted)
                                {
                                    files.Add(relative);
                                    AddParents(relative, parents);
                                }
                                complete = true;
                            }
                            // A newer full sync supersedes an older subset publication.
                            else complete = state.HasCompleteLauncherPaths;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
                }
            }

            var proof = new CleanupProof(directory, files, parents, complete, sourceStamp, stateStamp, metadataExists, metadataStamp);
            return proof.IsCurrent(paths) ? proof : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                                   ArgumentException or NotSupportedException) { return null; }
    }

    private bool MatchesCleanupSource(string relativePath, PackSyncSource marker)
    {
        var catalog = Catalog;
        var sources = catalog is null ? PackCatalogService.SeedSources() : catalog.Sources;
        var entry = sources.FirstOrDefault(candidate =>
            candidate.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase) ||
            candidate.LegacyRelativePaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase));
        if (entry is null) return false;
        static bool Same(PackSyncSource left, PackSyncSource right) => PackRenameMigration.SourceEquals(left, right);
        return Same(entry.Source, marker) || entry.LegacySources.Any(legacy => Same(legacy, marker)) ||
            catalog is not null && Same(entry.Source, catalog.ResolveHistoricalSourceMarker(relativePath, marker));
    }

    private void WriteLauncherCleanupState(string directory, PackSyncSource source, ValidatedManifest manifest)
    {
        if (ReadCleanupStamp(_paths, Path.Combine(directory, SyncStateFileName)) is not { } stateStamp) return;
        var markerPath = Path.Combine(directory, SourceMarkerFileName);
        if (ReadCleanupStamp(_paths, markerPath) is not { Length: <= 4096 } ||
            TryReadSourceMarker(markerPath) is not { } marker ||
            !MatchesCleanupSource(Path.GetFileName(directory), marker) ||
            !MatchesCleanupSource(Path.GetFileName(directory), source)) return;
        var path = Path.Combine(directory, LauncherCleanupStateFileName);
        if (ExistsForCleanup(path) && ReadCleanupStamp(_paths, path) is null)
            throw new IOException("The launcher metadata inventory path is redirected.");
        var files = manifest.Files.Select(file => file.Path)
            .Where(relative => relative.StartsWith(LauncherDataPrefix, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var snapshot = new LauncherCleanupState(1, source, stateStamp, files);
        AtomicFile.WriteAllTextIfChanged(path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private static CleanupStamp? ReadCleanupStamp(AppPaths paths, string path)
    {
        if (!MaintenancePath.TryRead(paths, path, out var attributes) || attributes.HasFlag(FileAttributes.Directory)) return null;
        var info = new FileInfo(path);
        return new CleanupStamp(info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, info.Length);
    }

    private static bool ExistsForCleanup(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static void AddParents(string path, HashSet<string> parents)
    {
        for (var separator = path.LastIndexOf('/'); separator >= 0; separator = path.LastIndexOf('/'))
        {
            path = path[..separator];
            if (!parents.Add(path)) break;
        }
    }

    private static FileStream CreateDownloadPlanLease(string stage)
    {
        Directory.CreateDirectory(stage);
        var owner = new FileStream(Path.Combine(stage, DownloadPlanOwnershipFile), FileMode.CreateNew,
            FileAccess.Write, FileShare.Delete);
        try
        {
            owner.Write(Encoding.UTF8.GetBytes(DownloadPlanOwnership));
            owner.Flush();
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    internal static int CleanupAbandonedPlans(AppPaths paths, string rootDirectory, DateTime cutoffUtc,
        Action<CancellationToken> checkpoint, CancellationToken token)
    {
        var removed = 0;
        if (!MaintenancePath.TryRead(paths, rootDirectory, out var rootAttributes) ||
            !rootAttributes.HasFlag(FileAttributes.Directory)) return removed;
        var expected = Encoding.UTF8.GetBytes(DownloadPlanOwnership);
        try
        {
            foreach (var stage in Directory.EnumerateDirectories(rootDirectory, DownloadPlanDirectoryPrefix + "*"))
            {
                checkpoint(token);
                var name = Path.GetFileName(stage);
                if (!name.StartsWith(DownloadPlanDirectoryPrefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name[DownloadPlanDirectoryPrefix.Length..], "N", out _) ||
                    !MaintenancePath.TryRead(paths, stage, out var attributes) || !attributes.HasFlag(FileAttributes.Directory) ||
                    Directory.GetLastWriteTimeUtc(stage) >= cutoffUtc) continue;
                var marker = Path.Combine(stage, DownloadPlanOwnershipFile);
                if (!MaintenancePath.TryRead(paths, marker, out var markerAttributes) || markerAttributes.HasFlag(FileAttributes.Directory))
                    continue;
                try
                {
                    using (var lease = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Delete))
                    {
                        if (lease.Length != expected.Length) continue;
                        var actual = new byte[expected.Length];
                        lease.ReadExactly(actual);
                        if (!actual.AsSpan().SequenceEqual(expected)) continue;
                        removed += DeleteStageContents(paths, stage, marker, checkpoint, token, depth: 0);
                        if (Directory.EnumerateFileSystemEntries(stage).Any(path => !path.Equals(marker, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }
                    checkpoint(token);
                    token.ThrowIfCancellationRequested();
                    if (!MaintenancePath.TryDelete(paths, marker, directory: false)) continue;
                    removed++;
                    if (MaintenancePath.TryDelete(paths, stage, directory: true)) removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    private void TryDeleteLegacyApplyStage(string packDir)
    {
        var stage = Path.Combine(packDir, StagingDirectoryName);
        if (!Directory.Exists(stage)) return;
        // This is the reserved pre-plan apply directory. Current installs use
        // an independently leased plan area and can reconcile old staged files.
        try
        {
            _ = DeleteStageContents(_paths, stage, null, static _ => { }, CancellationToken.None, depth: 0);
            _ = MaintenancePath.TryDelete(_paths, stage, directory: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static int DeleteStageContents(AppPaths paths, string directory, string? keep,
        Action<CancellationToken> checkpoint, CancellationToken token, int depth)
    {
        if (depth > 128 || !MaintenancePath.TryRead(paths, directory, out var attributes) ||
            !attributes.HasFlag(FileAttributes.Directory)) return 0;
        var removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            checkpoint(token);
            if (entry.Equals(keep, StringComparison.OrdinalIgnoreCase) || MaintenancePath.IsProtectedAssetName(Path.GetFileName(entry)) ||
                !MaintenancePath.TryRead(paths, entry, out var entryAttributes)) continue;
            var isDirectory = entryAttributes.HasFlag(FileAttributes.Directory);
            if (isDirectory) removed += DeleteStageContents(paths, entry, keep, checkpoint, token, depth + 1);
            checkpoint(token);
            token.ThrowIfCancellationRequested();
            if (MaintenancePath.TryDelete(paths, entry, isDirectory)) removed++;
        }
        return removed;
    }

    private sealed record LauncherCleanupState(int SchemaVersion, PackSyncSource Source, CleanupStamp SyncState, string[] Files);
}
