using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft;

/// <summary>Moves this renamed pack together with its personal instance and worlds.</summary>
public sealed class TerraFirmaPackMigrationService
{
    private const string OldName = PortablePackSyncService.LegacyTerraFirmaRelativePath;
    private const string NewName = PortablePackSyncService.TerraFirmaRelativePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly Logger? _logger;
    private readonly Func<bool> _isPlaying;

    public TerraFirmaPackMigrationService(AppPaths paths, Logger? logger = null)
        : this(paths, logger, () => new ClientPresenceService(paths).ReadLiveSessions().Any(session =>
            IsName(session.PackRelativePath, OldName) || IsName(session.PackRelativePath, NewName))) { }

    internal TerraFirmaPackMigrationService(AppPaths paths, Logger? logger, Func<bool> isPlaying)
    {
        _paths = paths;
        _logger = logger;
        _isPlaying = isPlaying;
    }

    /// <summary>
    /// Runs before settings are loaded. Existing destinations are never merged.
    /// Each move and JSON replacement is repeatable after an interrupted run;
    /// settings are changed last, after the worlds and their links are ready.
    /// </summary>
    public bool Migrate()
    {
        var directories = new[] { _paths.Packs, _paths.Instances, _paths.Worlds, _paths.Runtimes }
            .Select(root => new Move(Path.Combine(root, OldName), Path.Combine(root, NewName))).ToArray();
        EnsureRealPath(_paths.Personal);
        Directory.CreateDirectory(_paths.Personal);
        using var migrationLock = new FileStream(Path.Combine(_paths.Personal, ".terrafirma-rename.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pendingPath = Path.Combine(_paths.Personal, ".terrafirma-rename.pending");
        EnsureRealPath(pendingPath);
        var hasLegacyDirectory = directories.Any(move => Directory.Exists(move.Before) || File.Exists(move.Before));
        var isPending = File.Exists(pendingPath);
        var edits = new List<Edit>();
        try { PlanJson(_paths.SettingsFile, edits, RenameSettings); }
        catch (JsonException) when (!hasLegacyDirectory && !isPending)
        {
            // Ordinary settings recovery remains responsible for an unrelated
            // damaged file. A pending rename must instead finish or stop.
            return false;
        }
        if (!hasLegacyDirectory && !isPending && edits.Count == 0) return false;
        foreach (var move in directories)
        {
            EnsureRealPath(move.Before);
            EnsureRealPath(move.After);
            if (File.Exists(move.Before) || File.Exists(move.After) ||
                Directory.Exists(move.Before) && Directory.Exists(move.After))
                throw new IOException("Не удалось переименовать TerraFirma Rebirth: папки со старым и новым именем конфликтуют. Файлы сохранены.");
        }

        var worlds = new List<string>();
        foreach (var build in new[] { OldName, NewName })
        {
            var worldsRoot = Path.Combine(_paths.Worlds, build);
            CollectWorlds(worldsRoot, worlds);
            var instance = _paths.CombineUnderInstances(build);
            var saves = Path.Combine(instance, "saves");
            if (Directory.Exists(saves) && new DirectoryInfo(saves).LinkTarget is null)
                CollectWorlds(saves, worlds);
            PlanJson(Path.Combine(instance, ".portable-instance.json"), edits, doc =>
                RenameValue(doc, "packRelativePath"));
            PlanJson(Path.Combine(_paths.Packs, build, PortablePackSyncService.SourceMarkerFileName), edits, doc =>
            {
                if (!string.Equals(Read(doc, "owner"), "MarkZamore", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Read(doc, "repo"), "TerraFirma-Rebirth", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Read(doc, "tag"), "pack-latest", StringComparison.OrdinalIgnoreCase)) return false;
                doc["repo"] = PortablePackSyncService.TerraFirmaSource.Repo;
                return true;
            });
            PlanJson(Path.Combine(_paths.Runtimes, build, ".portable-runtime.json"), edits, RenameRuntimePaths);
        }
        // A world from the old flat layout also keeps its recorded pack.
        if (Directory.Exists(_paths.Worlds))
            foreach (var world in Directory.EnumerateDirectories(_paths.Worlds))
                if (WorldLocations.IsWorld(world) && new DirectoryInfo(world).LinkTarget is null && HasLegacyWorldMetadata(world))
                    AddWorld(world, worlds);
        foreach (var world in worlds.Distinct(StringComparer.OrdinalIgnoreCase))
            PlanJson(Path.Combine(world, WorldMetadataService.MetadataFileName), edits, doc =>
            {
                if (!RenameValue(doc, "buildRelativePath")) return false;
                doc["buildName"] = NewName;
                return true;
            });
        var links = PlanSavesLinks();
        var moves = directories.Where(move => Directory.Exists(move.Before)).ToArray();
        if (moves.Length == 0 && edits.Count == 0 && links.Count == 0)
        {
            if (isPending) File.Delete(pendingPath);
            return false;
        }
        if (_isPlaying() || worlds.Any(SavesFolderService.IsWorldOpen))
            throw new IOException("Для переименования TerraFirma Rebirth завершите игру и перезапустите лаунчер. Файлы сохранены.");

        // Only small launcher metadata is backed up; worlds themselves are moved intact.
        foreach (var edit in edits)
        {
            var relative = Path.GetRelativePath(_paths.Service, edit.Path);
            var backup = Path.Combine(_paths.Personal, "Backups", "PackRenames", NewName, relative);
            EnsureRealPath(backup);
            if (!File.Exists(backup)) AtomicFile.WriteAllBytes(backup, edit.Original);
        }
        // This small journal distinguishes an interrupted all-directory move
        // from a completed installation whose new-name cache may be damaged.
        AtomicFile.WriteAllText(pendingPath, "TerraFirma Rebirth 2");
        foreach (var move in moves)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(move.After)!);
            Directory.Move(move.Before, move.After);
        }
        foreach (var edit in edits.Where(edit => edit.Path != _paths.SettingsFile))
            AtomicFile.WriteAllText(Destination(edit.Path, directories), edit.Content);
        foreach (var link in links)
        {
            var path = Destination(link.Path, directories);
            // Delete only the verified junction, never its target or a real saves folder.
            if (new DirectoryInfo(path).LinkTarget is not null) Directory.Delete(path);
            Directory.CreateDirectory(link.Target);
            SavesFolderService.CreateJunction(path, link.Target);
        }
        foreach (var edit in edits.Where(edit => edit.Path == _paths.SettingsFile))
            AtomicFile.WriteAllText(edit.Path, edit.Content);
        File.Delete(pendingPath);
        _logger?.Info("TerraFirma Rebirth переименована в TerraFirma Rebirth 2 вместе с экземпляром и сохранёнными мирами.");
        return true;
    }

    private List<Link> PlanSavesLinks()
    {
        var result = new List<Link>();
        var oldWorlds = Path.Combine(_paths.Worlds, OldName);
        var newWorlds = Path.Combine(_paths.Worlds, NewName);
        foreach (var name in new[] { OldName, NewName })
        {
            var instance = _paths.CombineUnderInstances(name);
            if (!Directory.Exists(instance)) continue;
            var saves = Path.Combine(instance, "saves");
            var target = new DirectoryInfo(saves).LinkTarget;
            if (target is not null)
            {
                var fullTarget = Path.GetFullPath(target, instance);
                if (IsPath(fullTarget, oldWorlds)) result.Add(new Link(saves, newWorlds));
                else if (!IsPath(fullTarget, newWorlds))
                    throw new IOException("Папка сохранений TerraFirma Rebirth ссылается на другой путь. Файлы сохранены.");
                continue;
            }
            if (!Directory.Exists(saves))
            {
                if (File.Exists(saves)) throw new IOException("Путь saves занят файлом. Файлы сохранены.");
                if (Directory.Exists(oldWorlds) || Directory.Exists(newWorlds)) result.Add(new Link(saves, newWorlds));
                continue;
            }
            foreach (var entry in Directory.EnumerateDirectories(saves))
            {
                var childTarget = new DirectoryInfo(entry).LinkTarget;
                if (childTarget is null) continue;
                var fullTarget = Path.GetFullPath(childTarget, saves);
                if (IsPath(fullTarget, oldWorlds) || fullTarget.StartsWith(oldWorlds + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    result.Add(new Link(entry, newWorlds + fullTarget[oldWorlds.Length..]));
            }
        }
        return result;
    }

    private void CollectWorlds(string root, List<string> worlds)
    {
        if (!Directory.Exists(root)) return;
        EnsureRealPath(root);
        foreach (var world in Directory.EnumerateDirectories(root))
            if (new DirectoryInfo(world).LinkTarget is null && WorldLocations.IsWorld(world)) AddWorld(world, worlds);
    }

    private static bool HasLegacyWorldMetadata(string world)
    {
        try
        {
            var path = Path.Combine(world, WorldMetadataService.MetadataFileName);
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject doc &&
                IsName(Read(doc, "buildRelativePath"), OldName);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A flat world cannot be attributed from unreadable metadata.
            return false;
        }
    }

    private void AddWorld(string world, List<string> worlds)
    {
        EnsureRealPath(world);
        worlds.Add(world);
    }

    private void PlanJson(string path, List<Edit> edits, Func<JsonObject, bool> transform)
    {
        if (!File.Exists(path)) return;
        EnsureRealPath(path);
        var original = File.ReadAllBytes(path);
        var doc = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("Не удалось прочитать данные переименования: " + Path.GetFileName(path));
        if (transform(doc)) edits.Add(new Edit(path, original, doc.ToJsonString(JsonOptions) + Environment.NewLine));
    }

    private static bool RenameSettings(JsonObject doc)
    {
        var changed = RenameValue(doc, "clientRelativePath");
        foreach (var key in new[] { "addedPackRelativePaths", "hiddenPackRelativePaths" })
            if (doc[key] is JsonArray names)
                for (var index = 0; index < names.Count; index++)
                    if (names[index] is JsonValue value && value.TryGetValue<string>(out var name) && IsName(name, OldName))
                    {
                        names[index] = NewName;
                        changed = true;
                    }
        if (doc["memoryByPack"] is JsonObject memory)
            foreach (var pair in memory.ToArray())
                if (IsName(pair.Key, OldName))
                {
                    if (!memory.Any(next => IsName(next.Key, NewName))) memory[NewName] = pair.Value?.DeepClone();
                    memory.Remove(pair.Key);
                    changed = true;
                }
        var selected = Read(doc, "selectedWorldRelativePath");
        if (selected is not null && (selected.StartsWith(OldName + "\\", StringComparison.OrdinalIgnoreCase) ||
                                     selected.StartsWith(OldName + "/", StringComparison.OrdinalIgnoreCase)))
        {
            doc["selectedWorldRelativePath"] = NewName + selected[OldName.Length..];
            changed = true;
        }
        return changed;
    }

    private static bool RenameRuntimePaths(JsonObject doc)
    {
        var changed = false;
        foreach (var key in new[] { "javaPathRelativePath", "clientJarRelativePath" })
        {
            var value = Read(doc, key);
            var renamed = RuntimePath(value);
            if (value == renamed) continue;
            doc[key] = renamed;
            changed = true;
        }
        if (doc["files"] is JsonObject files)
            foreach (var pair in files.ToArray())
            {
                var renamed = RuntimePath(pair.Key)!;
                if (renamed == pair.Key) continue;
                if (files.ContainsKey(renamed)) throw new IOException("Конфликт путей runtime при переименовании сборки.");
                files[renamed] = pair.Value?.DeepClone();
                files.Remove(pair.Key);
                changed = true;
            }
        return changed;
    }

    private static string? RuntimePath(string? value)
    {
        if (value is null) return null;
        foreach (var separator in new[] { '/', '\\' })
        {
            var prefix = "Runtimes" + separator + OldName + separator;
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return "Runtimes" + separator + NewName + separator + value[prefix.Length..];
        }
        return value;
    }

    private void EnsureRealPath(string path)
    {
        _paths.EnsureUnderRoot(path);
        for (var current = Path.GetFullPath(path); !IsPath(current, _paths.Root); current = Path.GetDirectoryName(current)!)
        {
            if (new FileInfo(current).LinkTarget is not null ||
                (File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Переименование через ссылку на другую папку запрещено. Файлы сохранены.");
        }
    }

    private static string Destination(string path, IEnumerable<Move> moves)
    {
        foreach (var move in moves)
            if (path.StartsWith(move.Before + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return move.After + path[move.Before.Length..];
        return path;
    }

    private static bool RenameValue(JsonObject doc, string key)
    {
        if (!IsName(Read(doc, key), OldName)) return false;
        doc[key] = NewName;
        return true;
    }

    private static string? Read(JsonObject doc, string key) =>
        doc[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool IsName(string? value, string name) => string.Equals(value?.Trim().Trim('\\', '/'), name, StringComparison.OrdinalIgnoreCase);
    private static bool IsPath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private sealed record Move(string Before, string After);
    private sealed record Edit(string Path, byte[] Original, string Content);
    private sealed record Link(string Path, string Target);
}
