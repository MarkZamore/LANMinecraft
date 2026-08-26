using System.IO;
using System.Text.Json;

namespace Minecraft;

public sealed class WorldMetadataService
{
    public const string MetadataFileName = ".minecraft-portable-world.json";
    /// <summary>The launcher's one format version; see <see cref="PortableFormat"/>.</summary>
    public const int CurrentSchemaVersion = PortableFormat.SchemaVersion;
    private const string UnknownBuildName = "\u043D\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043D\u043E";
    private const string UnknownOwnerName = "\u043D\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043D\u043E";
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public WorldMetadata? Read(string worldPath)
    {
        var metadataPath = GetMetadataPath(worldPath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllText(metadataPath), _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <param name="claimBuild">
    /// Whether a world that has no build recorded may be given the one in
    /// <paramref name="context"/>. False for anything that merely looks at a
    /// world: listing the worlds used to write the selected build into every
    /// unlabelled one, so the filter that hides another build's worlds then
    /// compared that fresh label against the build that had just written it and
    /// always matched. A world was claimed by whichever build opened its list
    /// first, and an LL8 world showed up under ATM10 for exactly that reason.
    /// The build is only decided by playing the world - see
    /// <see cref="StampPlayedWorlds"/>.
    /// </param>
    public WorldMetadata? EnsureMetadata(
        string worldPath, WorldMetadataContext? context, bool claimBuild = true)
    {
        lock (_gate) return EnsureMetadataCore(worldPath, context, claimBuild);
    }

    private WorldMetadata? EnsureMetadataCore(
        string worldPath, WorldMetadataContext? context, bool claimBuild = true)
    {
        var metadataPath = GetMetadataPath(worldPath);
        var existing = Read(worldPath);
        if (existing is not null)
        {
            if (EnsureWorldId(existing))
            {
                try
                {
                    AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(existing, _jsonOptions));
                }
                catch
                {
                    return null;
                }
            }
            return existing;
        }
        if (File.Exists(metadataPath) || context is null)
        {
            return null;
        }

        var metadata = new WorldMetadata
        {
            WorldId = Guid.NewGuid().ToString("D"),
            BuildName = claimBuild && !string.IsNullOrWhiteSpace(context.BuildName)
                ? context.BuildName
                : UnknownBuildName,
            BuildRelativePath = claimBuild ? context.BuildRelativePath : string.Empty,
            PackHash = claimBuild ? context.PackHash : string.Empty,
            OwnerIdentityId = string.IsNullOrWhiteSpace(context.OwnerIdentityId) ? "" : context.OwnerIdentityId.Trim(),
            OwnerIdentityName = string.IsNullOrWhiteSpace(context.OwnerIdentityName) ? UnknownOwnerName : context.OwnerIdentityName.Trim(),
            CurrentHolderIdentityId = string.IsNullOrWhiteSpace(context.OwnerIdentityId) ? "" : context.OwnerIdentityId.Trim(),
            CurrentHolderIdentityName = string.IsNullOrWhiteSpace(context.OwnerIdentityName) ? UnknownOwnerName : context.OwnerIdentityName.Trim(),
            SchemaVersion = CurrentSchemaVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MarkedBy = "LANMinecraft.exe"
        };

        try
        {
            AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            return metadata;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renames the build a world belongs to, and only for a world still naming
    /// <paramref name="legacyBuildRelativePath"/>. The world id, its pack hash
    /// and everyone recorded around it stay exactly as they are.
    /// </summary>
    public bool TryRebindBuild(
        string worldPath,
        string legacyBuildRelativePath,
        string buildName,
        string buildRelativePath)
    {
        lock (_gate)
        {
            var metadata = Read(worldPath);
            if (metadata is null ||
                !IsSameBuildPath(metadata.BuildRelativePath, legacyBuildRelativePath))
            {
                return false;
            }

            metadata.BuildName = buildName;
            metadata.BuildRelativePath = buildRelativePath;
            try
            {
                AtomicFile.WriteAllText(
                    GetMetadataPath(worldPath),
                    JsonSerializer.Serialize(metadata, _jsonOptions));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool IsSameBuildPath(string? left, string? right) =>
        string.Equals(
            left?.Trim().Trim('\\', '/'),
            right?.Trim().Trim('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a world belongs to the build a player has selected, and so should
    /// be offered to them. A world made on one pack has no business being opened
    /// on another: its blocks and entities belong to mods the other pack may not
    /// have, and opening it there is how a world loses content.
    ///
    /// Two kinds of world are shown anyway. One whose build was never recorded
    /// cannot be attributed to anyone, and hiding it would be the launcher
    /// losing somebody's world rather than protecting it. One recorded under a
    /// name the built-in pack used to have belongs to the pack that was renamed,
    /// and it is offered under the new name until the migration rewrites it.
    /// </summary>
    public static bool BelongsToBuild(string? recordedBuildRelativePath, string? selectedBuildRelativePath)
    {
        var recorded = recordedBuildRelativePath?.Trim().Trim('\\', '/');
        if (string.IsNullOrEmpty(recorded)) return true;

        var selected = selectedBuildRelativePath?.Trim().Trim('\\', '/');
        if (string.IsNullOrEmpty(selected)) return true;

        // A name is a name. A pack that is renamed leaves the worlds of its
        // former name behind, which is the price of not carrying a table of
        // every name anything has ever had.
        return string.Equals(recorded, selected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gives a build to the worlds that were played in a session and had none.
    ///
    /// A world nobody stamped is offered in every build, because hiding a world
    /// the launcher cannot attribute would be worse than showing it in the
    /// wrong place. That leaves worlds from before this file existed, and ones
    /// dropped into the folder by hand, permanently ambiguous - and a world
    /// opened under the wrong build loses the blocks of every mod that build
    /// does not have.
    ///
    /// The launcher cannot know which world the game will open: it prepares
    /// them all and the player chooses inside the game. It can know which one
    /// was opened, afterwards, because the game writes level.dat when it does.
    /// So the answer is written when the session ends, from what happened
    /// rather than from a guess, and only for worlds that had no build at all.
    /// </summary>
    /// <param name="worldsRoot">The portable Worlds folder.</param>
    /// <param name="context">The build that just ran, and who is playing it.</param>
    /// <param name="sessionStartedUtc">When the game was started.</param>
    /// <returns>The names of the worlds that were given a build.</returns>
    public IReadOnlyList<string> StampPlayedWorlds(
        string worldsRoot,
        WorldMetadataContext context,
        DateTimeOffset sessionStartedUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.BuildRelativePath)) return [];
        if (string.IsNullOrWhiteSpace(worldsRoot) || !Directory.Exists(worldsRoot)) return [];

        var stamped = new List<string>();
        foreach (var worldPath in Directory.EnumerateDirectories(worldsRoot))
        {
            if (!File.Exists(Path.Combine(worldPath, "level.dat"))) continue;

            // session.lock, and not level.dat: the game writes the lock the
            // moment it opens a world and nothing else ever touches it, while
            // level.dat is rewritten in every world before every launch when
            // the launcher moves the player into it.
            var lockFile = Path.Combine(worldPath, "session.lock");
            if (!File.Exists(lockFile)) continue;
            try
            {
                if (new FileInfo(lockFile).LastWriteTimeUtc < sessionStartedUtc.UtcDateTime) continue;
            }
            catch (IOException)
            {
                continue;
            }

            var existing = Read(worldPath);
            if (existing is not null && !string.IsNullOrWhiteSpace(existing.BuildRelativePath))
            {
                continue;
            }

            lock (_gate)
            {
                // Re-read inside the lock; the listing writes here too.
                var metadata = Read(worldPath);
                if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.BuildRelativePath))
                {
                    continue;
                }

                metadata ??= EnsureMetadataCore(worldPath, context);
                if (metadata is null) continue;
                if (!string.IsNullOrWhiteSpace(metadata.BuildRelativePath))
                {
                    // EnsureMetadataCore wrote a whole record, build included.
                    stamped.Add(Path.GetFileName(worldPath));
                    continue;
                }

                metadata.BuildRelativePath = context.BuildRelativePath;
                metadata.BuildName = string.IsNullOrWhiteSpace(context.BuildName)
                    ? UnknownBuildName
                    : context.BuildName;
                if (string.IsNullOrWhiteSpace(metadata.PackHash))
                {
                    metadata.PackHash = context.PackHash;
                }

                try
                {
                    AtomicFile.WriteAllText(
                        GetMetadataPath(worldPath),
                        JsonSerializer.Serialize(metadata, _jsonOptions));
                    stamped.Add(Path.GetFileName(worldPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The world is still playable; it simply stays unattributed.
                }
            }
        }

        return stamped;
    }

    public string GetBuildName(string worldPath)
    {
        var metadata = Read(worldPath);
        return string.IsNullOrWhiteSpace(metadata?.BuildName) ? UnknownBuildName : metadata.BuildName;
    }

    /// <summary>
    /// A Steam id only ever reaches the document as a plain SteamID64; anything
    /// else (including the empty string a machine without Steam produces) is
    /// treated as "not known".
    /// </summary>
    private static string NormalizeSteamId(string? value) =>
        SteamId64.TryNormalize(value, out var canonical) ? canonical : string.Empty;

    public bool TryWriteOwnerMetadata(
        string worldPath,
        string? ownerId,
        string? ownerName,
        bool overwriteExistingOwner = false,
        string? ownerSteamId64 = null)
    {
        lock (_gate)
        {
            return TryWriteOwnerMetadataCore(
                worldPath, ownerId, ownerName, overwriteExistingOwner, ownerSteamId64);
        }
    }

    private bool TryWriteOwnerMetadataCore(
        string worldPath,
        string? ownerId,
        string? ownerName,
        bool overwriteExistingOwner,
        string? ownerSteamId64)
    {
        var metadataPath = GetMetadataPath(worldPath);
        WorldMetadata metadata;
        if (File.Exists(metadataPath))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllText(metadataPath), _jsonOptions) ?? new WorldMetadata();
            }
            catch
            {
                return false;
            }
        }
        else
        {
            metadata = new WorldMetadata
            {
                BuildName = UnknownBuildName,
                BuildRelativePath = string.Empty,
                PackHash = string.Empty,
                SchemaVersion = CurrentSchemaVersion,
                MarkedBy = "LANMinecraft.exe",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var normalizedOwnerId = string.IsNullOrWhiteSpace(ownerId) ? string.Empty : ownerId.Trim();
        var normalizedOwnerName = string.IsNullOrWhiteSpace(ownerName) ? UnknownOwnerName : ownerName.Trim();
        var normalizedOwnerSteamId = NormalizeSteamId(ownerSteamId64);

        if (!overwriteExistingOwner &&
            !string.IsNullOrWhiteSpace(metadata.OwnerIdentityId))
        {
            var ownerIsLocalPlayer = string.Equals(
                metadata.OwnerIdentityId, normalizedOwnerId, StringComparison.OrdinalIgnoreCase);
            var changed = false;

            // The name is refreshed only from the owner's own machine.
            if (ownerIsLocalPlayer &&
                !string.IsNullOrWhiteSpace(normalizedOwnerName) &&
                !string.Equals(metadata.OwnerIdentityName, normalizedOwnerName, StringComparison.Ordinal))
            {
                metadata.OwnerIdentityName = normalizedOwnerName;
                changed = true;
            }

            // The owner UUID stays exactly as it is; only the Steam account
            // behind it is learned - from the owner's own machine, or from the
            // table of players who predate Steam, which any machine knows.
            if (string.IsNullOrWhiteSpace(metadata.OwnerSteamId64))
            {
                var learnedSteamId = ownerIsLocalPlayer && normalizedOwnerSteamId.Length != 0
                    ? normalizedOwnerSteamId
                    : KnownSteamPlayers.TryGetSteamId(metadata.OwnerIdentityId, out var knownOwner)
                        ? knownOwner.ToString()
                        : string.Empty;
                if (learnedSteamId.Length != 0)
                {
                    metadata.OwnerSteamId64 = learnedSteamId;
                    if (metadata.SchemaVersion < CurrentSchemaVersion)
                    {
                        metadata.SchemaVersion = CurrentSchemaVersion;
                    }
                    changed = true;
                }
            }

            if (changed)
            {
                try
                {
                    AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        if (string.Equals(metadata.OwnerIdentityId, normalizedOwnerId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(metadata.OwnerIdentityName, normalizedOwnerName, StringComparison.Ordinal) &&
            string.Equals(metadata.OwnerSteamId64, normalizedOwnerSteamId, StringComparison.Ordinal))
        {
            return true;
        }

        metadata.OwnerIdentityId = normalizedOwnerId;
        metadata.OwnerIdentityName = normalizedOwnerName;
        if (normalizedOwnerSteamId.Length != 0) metadata.OwnerSteamId64 = normalizedOwnerSteamId;
        EnsureWorldId(metadata);
        if (metadata.SchemaVersion < CurrentSchemaVersion)
        {
            metadata.SchemaVersion = CurrentSchemaVersion;
        }

        try
        {
            AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryWriteCurrentHolderMetadata(
        string worldPath,
        string? holderId,
        string? holderName,
        bool transferred,
        string? holderSteamId64 = null)
    {
        lock (_gate)
        {
            return TryWriteCurrentHolderMetadataCore(
                worldPath, holderId, holderName, transferred, holderSteamId64);
        }
    }

    private bool TryWriteCurrentHolderMetadataCore(
        string worldPath,
        string? holderId,
        string? holderName,
        bool transferred,
        string? holderSteamId64)
    {
        var metadataPath = GetMetadataPath(worldPath);
        var metadata = Read(worldPath);
        if (metadata is null)
        {
            if (File.Exists(metadataPath)) return false;
            metadata = new WorldMetadata
            {
                BuildName = UnknownBuildName,
                MarkedBy = "LANMinecraft.exe",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        // Never downgrade: a document written by a newer build keeps its version.
        if (metadata.SchemaVersion < CurrentSchemaVersion)
        {
            metadata.SchemaVersion = CurrentSchemaVersion;
        }
        EnsureWorldId(metadata);
        var normalizedHolderId = string.IsNullOrWhiteSpace(holderId) ? string.Empty : holderId.Trim();
        var normalizedHolderName = string.IsNullOrWhiteSpace(holderName) ? UnknownOwnerName : holderName.Trim();
        var normalizedHolderSteamId = NormalizeSteamId(holderSteamId64) is { Length: > 0 } holderSteamId
            ? holderSteamId
            : KnownSteamPlayers.TryGetSteamId(normalizedHolderId, out var knownHolder)
                ? knownHolder.ToString()
                : string.Empty;

        // The world list refreshes every two seconds; without this, each refresh
        // rewrote a JSON file per world for nothing.
        if (!transferred &&
            metadata.SchemaVersion == CurrentSchemaVersion &&
            string.Equals(metadata.CurrentHolderIdentityId, normalizedHolderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(metadata.CurrentHolderIdentityName, normalizedHolderName, StringComparison.Ordinal) &&
            string.Equals(metadata.CurrentHolderSteamId64, normalizedHolderSteamId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(metadata.WorldId))
        {
            return true;
        }

        metadata.CurrentHolderIdentityId = normalizedHolderId;
        metadata.CurrentHolderIdentityName = normalizedHolderName;
        metadata.CurrentHolderSteamId64 = normalizedHolderSteamId;
        if (transferred) metadata.LastSuccessfulTransferUtc = DateTimeOffset.UtcNow;
        try
        {
            AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetMetadataPath(string worldPath)
    {
        return Path.Combine(worldPath, MetadataFileName);
    }

    public string EnsureWorldId(string worldPath, WorldMetadataContext? context = null)
    {
        lock (_gate)
        {
            var metadata = EnsureMetadataCore(worldPath, context)
                ?? throw new InvalidDataException($"World metadata is missing or damaged: {Path.GetFileName(worldPath)}");
            if (!Guid.TryParse(metadata.WorldId, out var worldId) || worldId == Guid.Empty)
            {
                throw new InvalidDataException($"World metadata has an invalid WorldId: {Path.GetFileName(worldPath)}");
            }
            return worldId.ToString("D");
        }
    }

    private static bool EnsureWorldId(WorldMetadata metadata)
    {
        var changed = false;
        if (!Guid.TryParse(metadata.WorldId, out var worldId) || worldId == Guid.Empty)
        {
            metadata.WorldId = Guid.NewGuid().ToString("D");
            changed = true;
        }
        else
        {
            var normalized = worldId.ToString("D");
            if (!string.Equals(metadata.WorldId, normalized, StringComparison.Ordinal))
            {
                metadata.WorldId = normalized;
                changed = true;
            }
        }
        if (metadata.SchemaVersion < CurrentSchemaVersion)
        {
            metadata.SchemaVersion = CurrentSchemaVersion;
            changed = true;
        }
        return changed;
    }
}
