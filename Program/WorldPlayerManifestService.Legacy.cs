using System.IO;
using System.Text.Json;

namespace Minecraft;

public sealed partial class WorldPlayerManifestService
{
    private static readonly string[] LegacyDirectories = ["players", "stats"];

    private WorldPlayersManifest WriteLegacy(string worldPath, LocalIdentityContext? holder)
    {
        var previous = Read(worldPath);
        var files = EnumerateLegacyFiles(worldPath).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var names = files.Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "players")
            .Select(Path.GetFileNameWithoutExtension).Where(name => LegacyPlayerIdentity.TrySteamId(name, out _))
            .Distinct(StringComparer.Ordinal).ToArray();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var players = new List<WorldPlayerManifestEntry>();
        foreach (var name in names)
        {
            _ = LegacyPlayerIdentity.TrySteamId(name, out var steam);
            var uuid = LegacyPlayerIdentity.PortableUuid(steam).ToString("D");
            var ownFiles = files.Where(path => IsLegacyPathForName(NormalizeRelativePath(Path.GetRelativePath(worldPath, path)), name!))
                .Select(path => CreateFileEntry(worldPath, path)).ToList();
            foreach (var file in ownFiles) assigned.Add(file.Path);
            players.Add(new WorldPlayerManifestEntry
            {
                StorageName = name, SteamId64 = steam.ToString(), PortableUuid = uuid, MinecraftUuid = uuid,
                LastKnownName = holder?.SteamId64 == steam ? holder.IdentityName
                    : previous?.Players.FirstOrDefault(player => player.StorageName == name)?.LastKnownName ?? string.Empty,
                Files = ownFiles
            });
        }
        var manifest = new WorldPlayersManifest
        {
            SchemaVersion = PortableFormat.SchemaVersion, PlayerStorage = WorldPlayerStorage.Legacy,
            CurrentHolderUuid = holder is null ? previous?.CurrentHolderUuid ?? string.Empty : ResolveEffectiveUuid(holder)?.ToString("D") ?? string.Empty,
            CurrentHolderSteamId64 = holder is null ? previous?.CurrentHolderSteamId64 : NormalizeSteamId(holder.SteamId64.ToString()),
            Players = players, UpdatedAtUtc = DateTimeOffset.UtcNow,
            UnassignedFiles = files.Select(path => CreateFileEntry(worldPath, path)).Where(file => !assigned.Contains(file.Path)).ToList()
        };
        AtomicFile.WriteAllText(GetManifestPath(worldPath), JsonSerializer.Serialize(manifest, _jsonOptions));
        return manifest;
    }

    private WorldPlayersManifest ValidateLegacy(string worldPath, WorldPlayersManifest manifest)
    {
        WorldProfilePathGuard.EnsureUnlinked(worldPath);
        if (WorldPlayerStorage.Resolve(worldPath) != WorldPlayerStorage.Legacy)
            throw new InvalidDataException("Legacy player manifest does not match the recorded world platform.");
        ValidateLegacyIdentityFields(manifest);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var player in manifest.Players)
        {
            if (!LegacyPlayerIdentity.TrySteamId(player.StorageName, out var steam) || !names.Add(player.StorageName!) ||
                !SteamId64.TryParse(player.SteamId64, out var declaredSteam) || declaredSteam != steam ||
                !Guid.TryParse(player.PortableUuid, out var uuid) || uuid != LegacyPlayerIdentity.PortableUuid(steam) ||
                !Guid.TryParse(player.MinecraftUuid, out var minecraftUuid) || uuid != minecraftUuid)
                throw new InvalidDataException("Legacy player manifest assigns a native name to the wrong portable account.");
            foreach (var file in player.Files)
            {
                if (!IsLegacyPathForName(file.Path, player.StorageName!))
                    throw new InvalidDataException("Legacy player file is assigned to the wrong native name.");
                ValidateLegacyFile(worldPath, file, files);
                if (file.Path == $"players/{player.StorageName}.dat") _ = NbtFile.Read(ResolveManifestPath(worldPath, file.Path));
            }
        }
        foreach (var file in manifest.UnassignedFiles)
        {
            var relative = NormalizeRelativePath(file.Path);
            if (manifest.Players.Any(player => IsLegacyPathForName(relative, player.StorageName!)))
                throw new InvalidDataException("A mapped legacy profile cannot be hidden in the unassigned list.");
            ValidateLegacyFile(worldPath, file, files);
        }
        var actual = EnumerateLegacyFiles(worldPath).Select(path => NormalizeRelativePath(Path.GetRelativePath(worldPath, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(files)) throw new InvalidDataException("Legacy manifest does not exactly cover all preserved player files.");
        return manifest;
    }

    private static void ValidateLegacyIdentityFields(WorldPlayersManifest manifest)
    {
        if (manifest.CurrentHolderSteamId64 is not null &&
            (!SteamId64.TryParse(manifest.CurrentHolderSteamId64, out var holder) ||
                !Guid.TryParse(manifest.CurrentHolderUuid, out var holderUuid) || LegacyPlayerIdentity.PortableUuid(holder) != holderUuid))
            throw new InvalidDataException("Legacy player manifest has an inconsistent current holder.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var player in manifest.Players)
        {
            if (player is null || player.Files is null ||
                !LegacyPlayerIdentity.TrySteamId(player.StorageName, out var steam) || !names.Add(player.StorageName!) ||
                !SteamId64.TryParse(player.SteamId64, out var declaredSteam) || declaredSteam != steam ||
                !Guid.TryParse(player.PortableUuid, out var uuid) || uuid != LegacyPlayerIdentity.PortableUuid(steam) ||
                !Guid.TryParse(player.MinecraftUuid, out var minecraftUuid) || uuid != minecraftUuid ||
                player.Files.Any(file => file is null || !IsLegacyPathForName(file.Path, player.StorageName!)))
                throw new InvalidDataException("Legacy player manifest assigns a native name to the wrong portable account.");
        }
    }

    private static void ValidateLegacyFile(string worldPath, WorldPlayerFileManifestEntry file, HashSet<string> files)
    {
        if (!IsCanonicalLegacyPath(file.Path))
            throw new InvalidDataException("Legacy player manifest requires a canonical relative file path.");
        var path = ResolveManifestPath(worldPath, file.Path);
        var relative = NormalizeRelativePath(Path.GetRelativePath(worldPath, path));
        var parts = relative.Split('/');
        if (parts.Length != 2 || !LegacyDirectories.Contains(parts[0], StringComparer.Ordinal) || !files.Add(relative))
            throw new InvalidDataException("Invalid or duplicate legacy profile file.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.Size || !string.Equals(HashFile(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Legacy player file failed SHA-256 validation.");
    }

    private static IEnumerable<string> EnumerateLegacyFiles(string worldPath) => LegacyDirectories
        .Select(name => Path.Combine(worldPath, name)).Where(Directory.Exists)
        .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly));

    private static bool IsLegacyPathForName(string relative, string name) => IsCanonicalLegacyPath(relative) &&
        (relative == $"players/{name}.dat" || relative == $"players/{name}.dat_old" ||
         relative.StartsWith($"stats/{name}.", StringComparison.Ordinal) ||
         relative.StartsWith($"stats/stats_{name}_", StringComparison.Ordinal));

    private static bool IsCanonicalLegacyPath(string? relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\')) return false;
        var parts = relative.Split('/');
        return parts.Length == 2 && LegacyDirectories.Contains(parts[0], StringComparer.Ordinal) &&
            parts[1].Length > 0 && parts[1] is not ("." or "..") &&
            !parts[1].EndsWith('.') && !parts[1].EndsWith(' ') &&
            parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}
