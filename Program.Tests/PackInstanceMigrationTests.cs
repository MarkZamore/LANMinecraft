using System.Security.Cryptography;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackInstanceMigrationTests : IDisposable
{
    private const string PackName = "MigrationPack";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-instance-migration-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task DroppedOwnedFile_RemainsOwnedWhenTheNewPackAlsoDropsItsClaim()
    {
        var (paths, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "config/removed.toml", "requiresRemovedMod = true\n");
        Write(pack, "launcher/pack-owned.txt", "config/removed.toml\n");
        await service.PrepareAsync(PackName);
        var game = service.GetInstanceDirectory(PackName);
        const string rewrittenByGame = "requiresRemovedMod = true\nnormalized = true\n";
        Write(game, "config/removed.toml", rewrittenByGame);

        File.Delete(Path.Combine(pack, "config", "removed.toml"));
        File.Delete(Path.Combine(pack, "launcher", "pack-owned.txt"));
        await service.PrepareAsync(PackName);

        Assert.False(File.Exists(Path.Combine(game, "config", "removed.toml")));
        AssertPreserved(paths, "removed.toml.user-file", rewrittenByGame);
        await service.PrepareAsync(PackName);
        Assert.False(File.Exists(Path.Combine(game, "config", "removed.toml")));
        AssertPreserved(paths, "removed.toml.user-file", rewrittenByGame);
    }

    [Fact]
    public async Task ModMirror_PreservesUnknownAndEditedJarsBeforeReplacingTheActiveSet()
    {
        var (paths, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "mods/current.jar", "author-v1");
        Write(pack, "mods/removed.jar", "removed-author");
        Write(pack, "mods/untouched-old.jar", "old-author");
        await service.PrepareAsync(PackName);
        var game = service.GetInstanceDirectory(PackName);
        // Unlink before writing so this fixture edits only the instance even
        // when preparation used hard links on Windows.
        Replace(game, "mods/current.jar", "player-edited-current");
        Replace(game, "mods/removed.jar", "player-edited-removed");
        Write(game, "mods/player-extra.jar", "player-extra");
        Replace(pack, "mods/current.jar", "author-v2");
        File.Delete(Path.Combine(pack, "mods", "removed.jar"));
        File.Delete(Path.Combine(pack, "mods", "untouched-old.jar"));

        await service.PrepareAsync(PackName);

        Assert.Equal("author-v2", Read(game, "mods/current.jar"));
        Assert.Equal(new[] { "current.jar" }, Directory.GetFiles(Path.Combine(game, "mods"))
            .Select(path => Path.GetFileName(path)!).OrderBy(name => name).ToArray());
        AssertPreserved(paths, "current.jar.user-file", "player-edited-current");
        AssertPreserved(paths, "removed.jar.user-file", "player-edited-removed");
        AssertPreserved(paths, "player-extra.jar.user-file", "player-extra");
        Assert.Empty(FindPreserved(paths, "untouched-old.jar.user-file"));

        var conflicts = Directory.GetFiles(paths.PackConflicts, "*", SearchOption.AllDirectories).Length;
        await service.PrepareAsync(PackName);
        Assert.Equal(conflicts, Directory.GetFiles(paths.PackConflicts, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("author-v2", Read(game, "mods/current.jar"));
    }

    [Fact]
    public async Task RemovingTheEntireModsDirectory_AlsoPreservesUntrackedInstanceJars()
    {
        var (paths, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "mods/old.jar", "old-author");
        await service.PrepareAsync(PackName);
        var game = service.GetInstanceDirectory(PackName);
        Replace(game, "mods/old.jar", "player-edited-old");
        Write(game, "mods/player-extra.jar", "player-extra");
        Directory.Delete(Path.Combine(pack, "mods"), recursive: true);

        await service.PrepareAsync(PackName);

        Assert.Empty(Directory.GetFiles(Path.Combine(game, "mods"), "*", SearchOption.AllDirectories));
        AssertPreserved(paths, "old.jar.user-file", "player-edited-old");
        AssertPreserved(paths, "player-extra.jar.user-file", "player-extra");
    }

    [Fact]
    public async Task OneUpdateWithManyPersonalFiles_UsesOneRetainedConflictSnapshot()
    {
        var (paths, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "mods/current.jar", "author-current");
        Write(pack, "config/owned.toml", "author-config-v1");
        Write(pack, "launcher/pack-owned.txt", "config/owned.toml\n");
        await service.PrepareAsync(PackName);
        var game = service.GetInstanceDirectory(PackName);
        var personalJars = Enumerable.Range(0, PackInstanceService.KeptConflictSnapshots + 3)
            .ToDictionary(index => $"personal-{index}.jar", index => $"personal-bytes-{index}");
        foreach (var (name, bytes) in personalJars) Write(game, "mods/" + name, bytes);
        Replace(game, "config/owned.toml", "personal-config");
        Replace(pack, "config/owned.toml", "author-config-v2");

        await service.PrepareAsync(PackName);
        PackInstanceService.PruneOldConflictSnapshots(paths.PackConflicts);

        Assert.Single(Directory.EnumerateDirectories(Path.Combine(paths.PackConflicts, PackName)));
        foreach (var (name, bytes) in personalJars) AssertPreserved(paths, name + ".user-file", bytes);
        AssertPreserved(paths, "owned.toml.user-file", "personal-config");
        Assert.Equal("author-config-v2", Read(game, "config/owned.toml"));
        await service.PrepareAsync(PackName);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(paths.PackConflicts, PackName)));
    }

    [Fact]
    public async Task FailedUnknownJarPreservation_LeavesTheOriginalAndPreviousInstanceState()
    {
        var (paths, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "mods/current.jar", "author-current");
        await service.PrepareAsync(PackName);
        var game = service.GetInstanceDirectory(PackName);
        Write(game, "mods/personal.jar", "irreplaceable-personal-bytes");
        var statePath = Path.Combine(game, ".portable-instance.json");
        var previousState = File.ReadAllBytes(statePath);
        File.WriteAllText(paths.PackConflicts, "fixture prevents creating the backup directory");

        await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync(PackName));

        Assert.Equal("irreplaceable-personal-bytes", Read(game, "mods/personal.jar"));
        Assert.Equal(previousState, File.ReadAllBytes(statePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceConfigChanges_AreHashedEvenWithTheSameRevisionLengthAndTimestamp(bool owned)
    {
        var (_, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "config/setting.toml", "delay = 3\n");
        Write(pack, "launcher/pack-owned.txt", owned ? "config/setting.toml\n" : "# no claims\n");
        WriteSyncState(pack, "same-revision", "config/setting.toml");
        await service.PrepareAsync(PackName);
        var game = service.GetInstanceDirectory(PackName);
        var source = Path.Combine(pack, "config", "setting.toml");
        var originalTimestamp = File.GetLastWriteTimeUtc(source);
        Write(pack, "config/setting.toml", "delay = 0\n");
        File.SetLastWriteTimeUtc(source, originalTimestamp);

        await service.PrepareAsync(PackName);

        Assert.Equal("delay = 0\n", Read(game, "config/setting.toml"));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, ".portable-instance.json")));
        Assert.Equal(Sha256(source), state.RootElement.GetProperty("files")
            .GetProperty("config/setting.toml").GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task SourceJarChanges_AreHashedEvenWithTheSameRevisionLengthAndTimestamp()
    {
        var (_, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "mods/current.jar", "author-v1");
        WriteSyncState(pack, "same-revision", "mods/current.jar");
        await service.PrepareAsync(PackName);
        var source = Path.Combine(pack, "mods", "current.jar");
        var originalTimestamp = File.GetLastWriteTimeUtc(source);
        Replace(pack, "mods/current.jar", "author-v2");
        File.SetLastWriteTimeUtc(source, originalTimestamp);

        await service.PrepareAsync(PackName);

        var game = service.GetInstanceDirectory(PackName);
        Assert.Equal("author-v2", Read(game, "mods/current.jar"));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, ".portable-instance.json")));
        Assert.Equal(Sha256(source), state.RootElement.GetProperty("modFiles")
            .GetProperty("current.jar").GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task PendingPackApply_StopsPreparationWithoutChangingTheUsableInstance()
    {
        var (_, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "config/setting.toml", "delay = 3\n");
        await service.PrepareAsync(PackName);
        var game = service.GetInstanceDirectory(PackName);
        var statePath = Path.Combine(game, ".portable-instance.json");
        var previousState = File.ReadAllBytes(statePath);
        Write(pack, "config/setting.toml", "delay = 0\n");
        Write(pack, PortablePackSyncService.ApplyPendingFileName, "{\"revision\":\"new\"}");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(PackName));

        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("delay = 3\n", Read(game, "config/setting.toml"));
        Assert.Equal(previousState, File.ReadAllBytes(statePath));
        File.Delete(Path.Combine(pack, PortablePackSyncService.ApplyPendingFileName));
        await service.PrepareAsync(PackName);
        Assert.Equal("delay = 0\n", Read(game, "config/setting.toml"));
    }

    [Fact]
    public async Task ManagedUpdate_KeepsPersonalOptionsWorldProgressAndScreenshots()
    {
        var (paths, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "config/owned.toml", "requiredMod = old\n");
        Write(pack, "launcher/pack-owned.txt", "config/owned.toml\n");
        Write(pack, "options.txt", "music:1.0\nrenderDistance:12\n");
        await service.PrepareAsync(PackName);
        var game = service.GetInstanceDirectory(PackName);
        var personalFiles = new Dictionary<string, string>
        {
            ["options.txt"] = "music:0.25\nrenderDistance:8\nsimulationDistance:8\n",
            ["saves/PersonalWorld/level.dat"] = "personal-level",
            ["saves/PersonalWorld/playerdata/player.dat"] = "personal-inventory",
            ["screenshots/kept.png"] = "personal-screenshot",
            ["config/my-preferences.toml"] = "volume = 0.25\n"
        };
        foreach (var (relative, content) in personalFiles) Write(game, relative, content);
        var externalWorld = Path.Combine(paths.Worlds, PackName, "ExternalWorld");
        Write(externalWorld, "level.dat", "external-personal-level");
        Write(pack, "config/owned.toml", "requiredMod = new\n");
        Write(pack, "options.txt", "music:0.0\nrenderDistance:4\n");

        await service.PrepareAsync(PackName);
        await service.PrepareAsync(PackName);

        Assert.Equal("requiredMod = new\n", Read(game, "config/owned.toml"));
        foreach (var (relative, content) in personalFiles) Assert.Equal(content, Read(game, relative));
        Assert.Equal("external-personal-level", Read(externalWorld, "level.dat"));
    }

    [Fact]
    public async Task ValidManagedFileList_ExcludesUnknownSourceJarsWithoutDeletingTheirBytes()
    {
        var (_, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "mods/current.jar", "author-current");
        Write(pack, "mods/unknown-old-helper.jar", "unclaimed-source-bytes");
        WriteSyncState(pack, "fresh-history-revision", "mods/current.jar");

        await service.PrepareAsync(PackName);

        var game = service.GetInstanceDirectory(PackName);
        Assert.Equal(new[] { "current.jar" }, Directory.GetFiles(Path.Combine(game, "mods"))
            .Select(path => Path.GetFileName(path)!).OrderBy(name => name).ToArray());
        Assert.Equal("author-current", Read(game, "mods/current.jar"));
        Assert.Equal("unclaimed-source-bytes", Read(pack, "mods/unknown-old-helper.jar"));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, ".portable-instance.json")));
        Assert.False(state.RootElement.GetProperty("modFiles").TryGetProperty("unknown-old-helper.jar", out _));
    }

    [Fact]
    public async Task ValidManagedFileList_ExcludesUnclaimedSourceConfigsAndScripts()
    {
        var (_, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "config/current.toml", "current = true\n");
        Write(pack, "kubejs/server_scripts/current.js", "// current author script\n");
        Write(pack, "config/unknown-removed-mod.toml", "removedMod = true\n");
        Write(pack, "kubejs/server_scripts/unknown-removed-mod.js", "// stale removed mod reference\n");
        Write(pack, "config/personal-slot/stale.toml", "unclaimed = true\n");
        WriteSyncState(pack, "new-authoritative-revision",
            "config/current.toml", "kubejs/server_scripts/current.js");
        var game = service.GetInstanceDirectory(PackName);
        Write(game, "config/personal-slot", "personal-file-at-unclaimed-directory-path");

        await service.PrepareAsync(PackName);

        Assert.Equal("current = true\n", Read(game, "config/current.toml"));
        Assert.Equal("// current author script\n", Read(game, "kubejs/server_scripts/current.js"));
        Assert.False(File.Exists(Path.Combine(game, "config", "unknown-removed-mod.toml")));
        Assert.False(File.Exists(Path.Combine(game, "kubejs", "server_scripts", "unknown-removed-mod.js")));
        Assert.Equal("removedMod = true\n", Read(pack, "config/unknown-removed-mod.toml"));
        Assert.Equal("// stale removed mod reference\n", Read(pack, "kubejs/server_scripts/unknown-removed-mod.js"));
        Assert.Equal("personal-file-at-unclaimed-directory-path", Read(game, "config/personal-slot"));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, ".portable-instance.json")));
        Assert.False(state.RootElement.GetProperty("files").TryGetProperty("config/unknown-removed-mod.toml", out _));
        Assert.False(state.RootElement.GetProperty("files").TryGetProperty("kubejs/server_scripts/unknown-removed-mod.js", out _));
    }

    [Fact]
    public async Task LegacyInstanceWithoutState_RemovesOnlyExactRetiredConfigAndScriptHashes()
    {
        var (_, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "config/current.toml", "current = true\n");
        WriteSyncState(pack, "rebuilt-from-new-history", "config/current.toml");
        var game = service.GetInstanceDirectory(PackName);
        Write(game, "config/retired.toml", "removedDependency = true\n");
        Write(game, "kubejs/server_scripts/retired.js", "// removed dependency hook\n");
        Write(game, "config/edited-retired.toml", "player-kept = true\n");
        Write(game, "config/my-preferences.toml", "personal = true\n");
        var retired = new[]
        {
            new { path = "config/retired.toml", content = "removedDependency = true\n" },
            new { path = "kubejs/server_scripts/retired.js", content = "// removed dependency hook\n" },
            new { path = "config/edited-retired.toml", content = "author-original = true\n" }
        }.Select(file =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(file.content);
            return new { file.path, sizeBytes = bytes.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        }).ToArray();
        var syncPath = Path.Combine(pack, PortablePackSyncService.SyncStateFileName);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(syncPath))!;
        state["retiredFiles"] = JsonSerializer.SerializeToNode(retired);
        File.WriteAllText(syncPath, state.ToJsonString());
        Assert.False(File.Exists(Path.Combine(game, ".portable-instance.json")));

        await service.PrepareAsync(PackName);

        Assert.False(File.Exists(Path.Combine(game, "config", "retired.toml")));
        Assert.False(File.Exists(Path.Combine(game, "kubejs", "server_scripts", "retired.js")));
        Assert.Equal("player-kept = true\n", Read(game, "config/edited-retired.toml"));
        Assert.Equal("personal = true\n", Read(game, "config/my-preferences.toml"));
        Assert.Equal("current = true\n", Read(game, "config/current.toml"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigJunction_CannotRedirectCopyOrRetiredFileDeletionIntoAnotherDirectory(bool retired)
    {
        var (_, pack, service) = CreatePack();
        using var scope = service;
        Write(pack, "config/current.toml", "author-current\n");
        WriteSyncState(pack, "verified-revision", "config/current.toml");
        var game = service.GetInstanceDirectory(PackName);
        Directory.CreateDirectory(game);
        var external = Path.Combine(_root, "ExternalPrivateConfig");
        Write(external, "current.toml", "external-personal-current\n");
        Write(external, "retired.toml", "exact-retired-author-bytes\n");
        if (retired)
        {
            var retiredPath = Path.Combine(external, "retired.toml");
            var syncPath = Path.Combine(pack, PortablePackSyncService.SyncStateFileName);
            var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(syncPath))!;
            state["retiredFiles"] = JsonSerializer.SerializeToNode(new[]
            {
                new { path = "config/retired.toml", sizeBytes = new FileInfo(retiredPath).Length, sha256 = Sha256(retiredPath) }
            });
            File.WriteAllText(syncPath, state.ToJsonString());
        }
        var configJunction = Path.Combine(game, "config");
        SavesFolderService.CreateJunction(configJunction, external);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(PackName));

            Assert.Equal("external-personal-current\n", Read(external, "current.toml"));
            Assert.Equal("exact-retired-author-bytes\n", Read(external, "retired.toml"));
            Assert.False(File.Exists(Path.Combine(game, ".portable-instance.json")));
        }
        finally
        {
            // Unlink only the fixture junction; its target is independently
            // owned test data and must never be recursively deleted here.
            if (new DirectoryInfo(configJunction).LinkTarget is not null) Directory.Delete(configJunction);
        }
    }

    private (AppPaths Paths, string Pack, PackInstanceService Service) CreatePack()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = Path.Combine(paths.Packs, PackName);
        Write(pack, PackManifestService.ManifestFileName,
            """
            {
              "schemaVersion": 1,
              "minecraftVersion": "1.20.1",
              "loader": { "type": "vanilla" },
              "clientJar": "client.jar"
            }
            """);
        Write(pack, "client.jar", "client-fixture");
        return (paths, pack, new PackInstanceService(paths, new Logger(paths.LogFile)));
    }

    private static void WriteSyncState(string pack, string revision, params string[] managedFiles)
    {
        var entries = managedFiles.Concat([PackManifestService.ManifestFileName, "client.jar"])
            .Distinct(StringComparer.Ordinal).ToDictionary(relative => relative, relative =>
            {
                var path = Path.Combine(pack, relative.Replace('/', Path.DirectorySeparatorChar));
                var file = new FileInfo(path);
                return new { sizeBytes = file.Length, lastWriteUtcTicks = file.LastWriteTimeUtc.Ticks, sha256 = Sha256(path) };
            });
        Write(pack, PortablePackSyncService.SyncStateFileName,
            JsonSerializer.Serialize(new { schemaVersion = 1, revision, files = entries }));
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Replace(string root, string relative, string content)
    {
        File.Delete(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Write(root, relative, content);
    }

    private static string Read(string root, string relative) =>
        File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] FindPreserved(AppPaths paths, string name) => Directory.Exists(paths.PackConflicts)
        ? Directory.GetFiles(paths.PackConflicts, name, SearchOption.AllDirectories) : [];

    private static void AssertPreserved(AppPaths paths, string name, string content)
    {
        var preserved = Assert.Single(FindPreserved(paths, name));
        Assert.Equal(content, File.ReadAllText(preserved).Replace("\r\n", "\n"));
    }
}
