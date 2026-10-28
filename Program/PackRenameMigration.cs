using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft;

/// <summary>A journaled, resumable rename of an existing catalog identity.</summary>
internal sealed class PackRenameMigration(AppPaths paths, Logger? logger = null,
    Func<IReadOnlyList<ClientPresenceService.ClientSession>>? liveSessions = null,
    Action<int>? afterMove = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string[] _roots = [paths.Packs, paths.Instances, paths.Worlds, paths.Runtimes];

    public void Apply(AppSettings settings, IEnumerable<CatalogSource> catalog, Action<AppSettings> save)
    {
        var aliases = catalog.Where(entry => entry.LegacyRelativePaths.Count > 0).ToArray();
        if (aliases.Length == 0) return;
        var journalRoot = Path.Combine(paths.Personal, "PackRenames");
        EnsureOrdinaryParents(journalRoot);
        Directory.CreateDirectory(journalRoot);
        using var gate = new FileStream(Path.Combine(journalRoot, "migration.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        foreach (var entry in aliases)
        foreach (var old in entry.LegacyRelativePaths)
        {
            if (!PackCatalogService.ValidFolder(old) || !PackCatalogService.ValidFolder(entry.RelativePath) ||
                old.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid catalog rename.");
            var journalPath = Path.Combine(journalRoot, Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.Id + "|" + old))) + ".json");
            EnsureOrdinaryParents(journalPath);
            var pending = File.Exists(journalPath);
            var oldExists = _roots.Any(root => Exists(Path.Combine(root, old)));
            if (!pending && !oldExists && !RefersTo(settings, old)) continue;
            var oldMemory = settings.MemoryByPack.FirstOrDefault(pair => Same(pair.Key, old));
            var newMemory = settings.MemoryByPack.FirstOrDefault(pair => Same(pair.Key, entry.RelativePath));
            if (oldMemory.Key is not null && newMemory.Key is not null && oldMemory.Value != newMemory.Value)
                throw new IOException("Переименование сборки остановлено: для старого и нового имени сохранены разные настройки памяти.");
            var sessions = liveSessions?.Invoke() ?? new ClientPresenceService(paths, logger).ReadLiveSessions();
            if (sessions.Any(session => Same(session.PackRelativePath, old) || Same(session.PackRelativePath, entry.RelativePath)))
                throw new IOException("Закройте Minecraft перед переименованием сборки " + old + ".");
            foreach (var world in FlatWorldsFor(old))
            {
                EnsureOrdinaryParents(world);
                var sessionLock = Path.Combine(world, "session.lock");
                if (File.Exists(sessionLock))
                { using var held = new FileStream(sessionLock, FileMode.Open, FileAccess.Read, FileShare.None); }
            }
            Journal journal;
            if (pending)
            {
                journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath), Json)
                    ?? throw new InvalidDataException("Invalid pack rename journal.");
                if (journal.SchemaVersion != 1 || journal.Id != entry.Id || journal.Old != old || journal.New != entry.RelativePath ||
                    journal.Roots.Any(index => index < 0 || index >= _roots.Length) || journal.Roots.Distinct().Count() != journal.Roots.Count)
                    throw new InvalidDataException("Pack rename journal does not match the catalog.");
            }
            else
            {
                var oldSaves = Path.Combine(paths.Instances, old, "saves");
                journal = new Journal { Id = entry.Id, Old = old, New = entry.RelativePath,
                    SavesJunction = Exists(oldSaves) && (File.GetAttributes(oldSaves) & FileAttributes.ReparsePoint) != 0 };
                for (var index = 0; index < _roots.Length; index++)
                {
                    var source = Path.Combine(_roots[index], old);
                    var target = Path.Combine(_roots[index], entry.RelativePath);
                    EnsureOrdinaryParents(source);
                    EnsureOrdinaryParents(target);
                    if (Exists(source) && Exists(target))
                        throw new IOException("Переименование сборки остановлено: уже существуют обе папки " + old + " и " + entry.RelativePath + ".");
                    if (Exists(source)) journal.Roots.Add(index);
                }
                // Validate all trees and locks before the first move or journal.
                ValidateTrees(old, entry.RelativePath);
                AtomicFile.WriteAllText(journalPath, JsonSerializer.Serialize(journal, Json));
            }
            ValidateTrees(old, entry.RelativePath);
            for (var step = 0; step < journal.Roots.Count; step++)
            {
                var root = _roots[journal.Roots[step]];
                var source = Path.Combine(root, old);
                var target = Path.Combine(root, entry.RelativePath);
                EnsureOrdinaryParents(source);
                EnsureOrdinaryParents(target);
                if (Exists(source) && Exists(target)) throw new IOException("Pack rename destination is occupied: " + target);
                if (Directory.Exists(source)) Directory.Move(source, target);
                else if (!Directory.Exists(target)) throw new IOException("Pack rename data is missing: " + source);
                afterMove?.Invoke(step + 1);
            }
            RetargetSaves(old, entry.RelativePath, journal.SavesJunction);
            RewriteOwnedJson(old, entry);
            RewriteSettings(settings, old, entry.RelativePath);
            save(settings);
            File.Delete(journalPath);
            logger?.Info("Catalog pack renamed: " + old + " -> " + entry.RelativePath + ".");
        }
    }

    private void ValidateTrees(string old, string current)
    {
        foreach (var root in _roots)
        foreach (var name in new[] { old, current })
        {
            var directory = Path.Combine(root, name);
            EnsureOrdinaryParents(directory);
            if (!Directory.Exists(directory)) continue;
            var todo = new Stack<string>();
            todo.Push(directory);
            while (todo.TryPop(out var next))
            foreach (var item in Directory.EnumerateFileSystemEntries(next))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (root == paths.Instances && Same(Path.GetRelativePath(directory, item), "saves") &&
                        ExpectedSaves(item, old, current)) continue;
                    throw new IOException("Pack rename refuses an unexpected link: " + item);
                }
                if ((attributes & FileAttributes.Directory) != 0) todo.Push(item);
                else if (Same(Path.GetFileName(item), "session.lock"))
                {
                    using var worldLock = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.None);
                }
            }
        }
    }

    private bool ExpectedSaves(string link, string old, string current)
    {
        var target = new DirectoryInfo(link).LinkTarget;
        if (target is null) return false;
        var resolved = Path.GetFullPath(target, Path.GetDirectoryName(link)!);
        return Same(resolved, Path.Combine(paths.Worlds, old)) || Same(resolved, Path.Combine(paths.Worlds, current));
    }

    private void RetargetSaves(string old, string current, bool required)
    {
        var link = Path.Combine(paths.Instances, current, "saves");
        if (!Exists(link) && !required) return;
        if (Exists(link) && !required && (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) return;
        if (Exists(link) && ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0 || !ExpectedSaves(link, old, current)))
            throw new IOException("Unexpected saves junction: " + link);
        var target = Path.Combine(paths.Worlds, current);
        EnsureOrdinaryParents(target);
        Directory.CreateDirectory(target);
        // Remove only the verified link itself; never recurse into its target.
        if (Exists(link)) Directory.Delete(link);
        SavesFolderService.CreateJunction(link, target);
    }

    private void RewriteOwnedJson(string old, CatalogSource entry)
    {
        var instance = Path.Combine(paths.Instances, entry.RelativePath);
        Rewrite(Path.Combine(instance, ".portable-instance.json"), node => RenameValue(node, "packRelativePath", old, entry.RelativePath));
        var worlds = Path.Combine(paths.Worlds, entry.RelativePath);
        var locations = new List<string>();
        if (Directory.Exists(worlds)) locations.AddRange(Directory.EnumerateDirectories(worlds));
        var saves = Path.Combine(instance, "saves");
        if (Directory.Exists(saves) && (File.GetAttributes(saves) & FileAttributes.ReparsePoint) == 0)
            locations.AddRange(Directory.EnumerateDirectories(saves));
        locations.AddRange(FlatWorldsFor(old));
        foreach (var world in locations.Distinct(StringComparer.OrdinalIgnoreCase))
            Rewrite(Path.Combine(world, WorldMetadataService.MetadataFileName), node =>
            {
                if (!Same(node["buildRelativePath"]?.GetValue<string>(), old)) return;
                node["buildRelativePath"] = entry.RelativePath;
                if (Same(node["buildName"]?.GetValue<string>(), old)) node["buildName"] = entry.RelativePath;
            });
        var marker = Path.Combine(paths.Packs, entry.RelativePath, PortablePackSyncService.SourceMarkerFileName);
        Rewrite(marker, node =>
        {
            var source = new PackSyncSource(node["owner"]?.GetValue<string>() ?? "", node["repo"]?.GetValue<string>() ?? "", node["tag"]?.GetValue<string>() ?? "");
            if (!entry.LegacySources.Any(legacy => SourceEquals(legacy, source))) return;
            node["owner"] = entry.Source.Owner;
            node["repo"] = entry.Source.Repo;
            node["tag"] = entry.Source.Tag;
        });
    }

    private IEnumerable<string> FlatWorldsFor(string old)
    {
        if (!Directory.Exists(paths.Worlds)) yield break;
        foreach (var world in Directory.EnumerateDirectories(paths.Worlds))
        {
            if ((File.GetAttributes(world) & FileAttributes.ReparsePoint) != 0 || !File.Exists(Path.Combine(world, "level.dat"))) continue;
            var metadata = Path.Combine(world, WorldMetadataService.MetadataFileName);
            if (!File.Exists(metadata)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(File.ReadAllText(metadata)); }
            catch (JsonException) { continue; }
            if (node is JsonObject obj && obj["buildRelativePath"] is JsonValue value && value.TryGetValue<string>(out var build) && Same(build, old))
                yield return world;
        }
    }

    private void Rewrite(string file, Action<JsonObject> update)
    {
        EnsureOrdinaryParents(file);
        if (!File.Exists(file)) return;
        var before = File.ReadAllText(file);
        var node = JsonNode.Parse(before) as JsonObject ?? throw new InvalidDataException("Invalid rename metadata: " + file);
        var initial = node.ToJsonString();
        update(node);
        if (initial != node.ToJsonString()) AtomicFile.WriteAllText(file, node.ToJsonString(Json));
    }

    private void EnsureOrdinaryParents(string path)
    {
        paths.EnsureUnderRoot(path);
        for (var item = Path.GetFullPath(path); !Same(item, paths.Root); item = Path.GetDirectoryName(item)!)
            if (Exists(item) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Pack rename refuses a linked path: " + item);
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    internal static bool SourceEquals(PackSyncSource left, PackSyncSource right) =>
        Same(left.Owner, right.Owner) && Same(left.Repo, right.Repo) && Same(left.Tag, right.Tag);
    private static void RenameValue(JsonObject node, string key, string old, string current)
    { if (Same(node[key]?.GetValue<string>(), old)) node[key] = current; }
    private static bool RefersTo(AppSettings settings, string old) => Same(settings.ClientRelativePath, old) ||
        settings.AddedPackRelativePaths?.Any(path => Same(path, old)) == true || settings.HiddenPackRelativePaths.Any(path => Same(path, old)) ||
        settings.MemoryByPack.Keys.Any(path => Same(path, old)) || settings.SelectedWorldRelativePath.StartsWith(old + "/", StringComparison.OrdinalIgnoreCase) ||
        settings.SelectedWorldRelativePath.StartsWith(old + "\\", StringComparison.OrdinalIgnoreCase);

    private static void RewriteSettings(AppSettings settings, string old, string current)
    {
        var selected = Same(settings.ClientRelativePath, old);
        if (selected) settings.ClientRelativePath = current;
        if (settings.AddedPackRelativePaths is { } added)
        {
            settings.AddedPackRelativePaths = added.Select(path => Same(path, old) ? current : path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (selected && !settings.HiddenPackRelativePaths.Any(path => Same(path, old) || Same(path, current)) &&
                !settings.AddedPackRelativePaths.Contains(current, StringComparer.OrdinalIgnoreCase)) settings.AddedPackRelativePaths.Add(current);
        }
        settings.HiddenPackRelativePaths = settings.HiddenPackRelativePaths.Select(path => Same(path, old) ? current : path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var memoryKey = settings.MemoryByPack.Keys.FirstOrDefault(path => Same(path, old));
        if (memoryKey is not null)
        {
            if (!settings.MemoryByPack.ContainsKey(current)) settings.MemoryByPack[current] = settings.MemoryByPack[memoryKey];
            settings.MemoryByPack.Remove(memoryKey);
        }
        if (settings.SelectedWorldRelativePath.StartsWith(old + "/", StringComparison.OrdinalIgnoreCase) ||
            settings.SelectedWorldRelativePath.StartsWith(old + "\\", StringComparison.OrdinalIgnoreCase))
            settings.SelectedWorldRelativePath = current + settings.SelectedWorldRelativePath[old.Length..];
    }

    private sealed class Journal
    {
        public int SchemaVersion { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Old { get; set; } = "";
        public string New { get; set; } = "";
        public bool SavesJunction { get; set; }
        public List<int> Roots { get; set; } = [];
    }
}
