using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>Owns world transfer staging, journals and filesystem promotion.</summary>
internal sealed partial class WorldTransferStorage(AppPaths paths, Logger logger, WorldMetadataService? metadata = null)
{
    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly WorldMetadataService _metadata = metadata ?? new WorldMetadataService();

    internal string TransfersRoot => Path.Combine(paths.Personal, "Transfers");

    internal string CreateTransactionDirectory(string transferId)
    {
        if (!Guid.TryParseExact(transferId, "N", out _)) throw new InvalidDataException("Transfer ID is invalid.");
        var path = Path.Combine(TransfersRoot, transferId);
        ValidateTransaction(path);
        if (GetAttributes(path) is not null) throw new IOException("Transfer transaction already exists.");
        Directory.CreateDirectory(path);
        return path;
    }

    internal string CreateStagingDirectory(string transactionRoot)
    {
        var path = RequireNewTransactionFile(transactionRoot, "staging-world");
        Directory.CreateDirectory(path);
        return path;
    }

    internal string RequireNewTransactionFile(string transactionRoot, string name)
    {
        ValidateTransaction(transactionRoot);
        if (!Path.GetFileName(name).Equals(name, StringComparison.Ordinal) || name is "." or "..")
            throw new ArgumentException("Invalid transfer filename.", nameof(name));
        var path = Path.Combine(transactionRoot, name);
        if (GetAttributes(path) is not null) throw new IOException("Transfer path already exists.");
        return path;
    }

    internal WorldTransferJournal? ReadJournal(string transactionRoot)
    {
        ValidateTransaction(transactionRoot);
        var file = Path.Combine(transactionRoot, "transaction.json");
        if (GetAttributes(file) is not { } attributes) return null;
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("Transfer journal is not an ordinary file.");
        var journal = JsonSerializer.Deserialize<WorldTransferJournal>(File.ReadAllText(file), JournalJson)
            ?? throw new InvalidDataException("Transfer journal is empty.");
        ValidateJournal(transactionRoot, journal);
        return journal;
    }

    internal void WriteJournal(string transactionRoot, WorldTransferJournal journal)
    {
        ValidateJournal(transactionRoot, journal);
        var file = Path.Combine(transactionRoot, "transaction.json");
        if (GetAttributes(file) is { } attributes &&
            (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("Transfer journal is not an ordinary file.");
        journal.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(journal, JournalJson));
    }

    internal string ResolveWorldToSend(string worldPath)
    {
        if (string.IsNullOrWhiteSpace(worldPath))
            throw new DirectoryNotFoundException("Choose a world to transfer.");
        var world = Path.GetFullPath(worldPath);
        EnsureWorldPath(world);
        WorldProfilePathGuard.EnsureUnlinked(world, paths.Worlds);
        if (!WorldLocations.IsWorld(world))
            throw new DirectoryNotFoundException("Selected folder is not a Minecraft world.");
        logger.Info($"Selected world for transfer: {Path.GetFileName(world)}.");
        return world;
    }

    internal WorldMetadata? ReadWorldMetadata(string worldPath) => _metadata.Read(worldPath);

    internal (string sourceManifestSha, string waypointManifestSha, string installedManifestSha) PrepareReceivedWorld(
        string stagedWorldPath, WorldPlayerProfileService profiles, WaypointStoreService waypoints,
        LocalIdentityContext identity, (string id, string name, string steamId) owner,
        string expectedPlayerSha, string expectedWaypointSha)
    {
        profiles.ValidatePlayerManifest(stagedWorldPath);
        var sourceManifestSha = profiles.GetPlayerManifestHash(stagedWorldPath);
        if (!string.Equals(sourceManifestSha, expectedPlayerSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Player manifest SHA256 mismatch after extraction.");
        waypoints.ValidateManifest(stagedWorldPath);
        var waypointManifestSha = waypoints.GetManifestHash(stagedWorldPath);
        if (!string.Equals(waypointManifestSha, expectedWaypointSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Waypoint manifest SHA256 mismatch after extraction.");

        profiles.PrepareReceivedWorldForIdentity(stagedWorldPath, identity);
        profiles.ValidatePlayerManifest(stagedWorldPath);
        var installedManifestSha = profiles.GetPlayerManifestHash(stagedWorldPath);
        if (!_metadata.TryWriteOwnerMetadata(stagedWorldPath, owner.id, owner.name,
                overwriteExistingOwner: false, ownerSteamId64: owner.steamId))
            throw new InvalidOperationException("Could not preserve world creator metadata.");
        if (!_metadata.TryWriteCurrentHolderMetadata(stagedWorldPath, identity.MinecraftUuid, identity.IdentityName,
                transferred: true, holderSteamId64: identity.SteamId64.ToString()))
            throw new InvalidOperationException("Could not update current world holder metadata.");
        return (sourceManifestSha, waypointManifestSha, installedManifestSha);
    }

    internal string? RebindTransferBuild(string worldPath, Func<string, string>? resolve)
    {
        var currentMetadata = _metadata.Read(worldPath);
        if (currentMetadata is null || resolve is null || string.IsNullOrEmpty(currentMetadata.BuildRelativePath))
            return currentMetadata?.BuildRelativePath;
        var old = currentMetadata.BuildRelativePath;
        var current = resolve(old);
        if (string.Equals(old, current, StringComparison.OrdinalIgnoreCase)) return old;
        if (!_metadata.TryRebindBuild(worldPath, old, currentMetadata.BuildName, current))
            throw new IOException("Could not update the transferred world's installation path.");
        return current;
    }

    internal void PlanReceivedInstall(string transactionRoot, WorldTransferJournal journal, string? worldName,
        Func<string, string>? installationPathFor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateJournal(transactionRoot, journal);
        if (journal is not { Role: "Receiver", State: "Ready" })
            throw new InvalidDataException("The receiver has not prepared the world.");
        var staging = GetStagingPath(transactionRoot);
        WorldProfilePathGuard.EnsureUnlinked(staging, transactionRoot);
        var build = RebindTransferBuild(staging, installationPathFor);
        var parent = Path.GetFullPath(WorldLocations.ForBuild(paths.Worlds, build ?? ""));
        EnsureWorldPath(parent, allowRoot: true);
        var name = GetSafeWorldName(worldName);
        var existing = Directory.Exists(parent)
            ? Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var suffix = 2; existing.Contains(candidate); suffix++)
            candidate = $"{name} ({suffix})";

        journal.InstalledWorldPath = Path.Combine(parent, candidate);
        journal.State = "CommitReceived";
        WriteJournal(transactionRoot, journal);
    }

    internal string ApplyReceivedInstall(string transactionRoot, WorldTransferJournal journal, CancellationToken token)
    {
        if (journal is not { Role: "Receiver", State: "CommitReceived", InstalledWorldPath.Length: > 0 })
            throw new InvalidDataException("The receiver has no planned installation.");
        ValidateJournal(transactionRoot, journal);
        var staged = GetStagingPath(transactionRoot);
        var destination = journal.InstalledWorldPath;
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureWorldPath(destination);
        EnsureOrdinaryDirectoryPath(staged);
        token.ThrowIfCancellationRequested();
        // Directory.Move does not replace a destination that appeared after planning.
        Directory.Move(staged, destination);
        journal.State = "Installed";
        WriteJournal(transactionRoot, journal);
        logger.Info($"Received world installed: {Path.GetFileName(destination)}.");
        return destination;
    }

    internal void EscrowSourceWorld(string transactionRoot, WorldTransferJournal journal, CancellationToken token)
    {
        ValidateJournal(transactionRoot, journal);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(journal.EscrowPath)!);
        ValidateJournal(transactionRoot, journal);
        WorldAccessGuard.EnsureClosed(journal.SourceWorldPath);
        token.ThrowIfCancellationRequested();
        Directory.Move(journal.SourceWorldPath, journal.EscrowPath);
    }

    internal void RestoreEscrowWorld(string transactionRoot, WorldTransferJournal journal)
    {
        ValidateJournal(transactionRoot, journal);
        if (!Directory.Exists(journal.EscrowPath)) return;
        if (GetAttributes(journal.SourceWorldPath) is not null)
            throw new IOException("Both source and escrow worlds exist before commit; automatic recovery was stopped.");
        Directory.CreateDirectory(Path.GetDirectoryName(journal.SourceWorldPath)!);
        ValidateJournal(transactionRoot, journal);
        Directory.Move(journal.EscrowPath, journal.SourceWorldPath);
    }

    internal bool HasEscrowWorld(string transactionRoot, WorldTransferJournal journal)
    {
        ValidateJournal(transactionRoot, journal);
        return GetAttributes(journal.EscrowPath) is not null;
    }

    internal void DeleteTransaction(string transactionRoot)
    {
        ValidateTransaction(transactionRoot);
        if (!Directory.Exists(transactionRoot)) return;
        // Walk ordinary directories only. A redirected tree is left for inspection.
        var directories = new Stack<string>();
        var pending = new Stack<string>();
        var files = new List<string>();
        pending.Push(transactionRoot);
        while (pending.TryPop(out var path))
        {
            EnsureOrdinaryDirectoryPath(path);
            directories.Push(path);
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(child);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Transfer transaction contains a filesystem link.");
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(child);
                else files.Add(child);
            }
        }
        foreach (var file in files) File.Delete(file);
        while (directories.TryPop(out var directory)) Directory.Delete(directory);
    }

    internal string GetStagingPath(string transactionRoot)
    {
        ValidateTransaction(transactionRoot);
        var path = Path.Combine(transactionRoot, "staging-world");
        EnsureOrdinaryDirectoryPath(path);
        return path;
    }

    internal void EnsureWorldPath(string path, bool allowRoot = false)
    {
        if (!IsWithin(path, paths.Worlds, allowRoot))
            throw new InvalidDataException("World transfer destination must be under Minecraft/Worlds.");
        EnsureOrdinaryDirectoryPath(path);
    }

    internal static void EnsureOrdinaryDirectoryPath(string path)
    {
        var components = new Stack<string>();
        for (string? part = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); part is not null;
             part = Path.GetDirectoryName(part))
            components.Push(part);

        while (components.TryPop(out var part))
        {
            if (GetAttributes(part) is not { } attributes) break;
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("World transfer requires an ordinary directory: " + part);
        }
    }

    private void ValidateTransaction(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(Path.GetDirectoryName(full), TransfersRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new InvalidDataException("Transfer transaction is outside its staging root.");
        EnsureOrdinaryDirectoryPath(full);
    }

    private void ValidateJournal(string transactionRoot, WorldTransferJournal journal)
    {
        ValidateTransaction(transactionRoot);
        if (journal.SchemaVersion != 1 ||
            !string.Equals(Path.GetFileName(transactionRoot), journal.TransferId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Transfer journal identity or schema is invalid.");

        if (journal.Role == "Sender")
        {
            EnsureWorldPath(journal.SourceWorldPath);
            var escrowRoot = Path.Combine(transactionRoot, "escrow");
            if (!IsWithin(journal.EscrowPath, escrowRoot))
                throw new InvalidDataException("Transfer escrow must be inside its own transaction.");
            EnsureOrdinaryDirectoryPath(journal.EscrowPath);
        }
        else if (journal.Role != "Receiver")
        {
            throw new InvalidDataException("Unknown transfer journal role.");
        }

        if (!string.IsNullOrWhiteSpace(journal.InstalledWorldPath))
            EnsureWorldPath(journal.InstalledWorldPath);
    }

    private static bool IsWithin(string path, string root, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return (allowRoot && full.Equals(parent, StringComparison.OrdinalIgnoreCase)) ||
               full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string GetSafeWorldName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "World";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).TrimEnd(' ', '.');
        return safe.Length == 0 ? "World" : safe;
    }
}
