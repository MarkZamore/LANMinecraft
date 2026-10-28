using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackRenameMigrationTests : IDisposable
{
    private const string Old = "Minecraft 26.2";
    private const string New = "Minecraft 26.3";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "minecraft-pack-rename-" + Guid.NewGuid().ToString("N"));
    private static readonly CatalogSource Entry = new("minecraft-26-2", New,
        new PackSyncSource("MarkZamore", "Minecraft-26.3", "pack-latest"))
    {
        LegacyRelativePaths = [Old],
        LegacySources = [new PackSyncSource("MarkZamore", "Minecraft-26.2", "pack-latest")]
    };

    public void Dispose()
    {
        // Never let recursive fixture cleanup follow a junction into its target.
        foreach (var name in new[] {Old, New})
        {
            var link = Path.Combine(_root, "Minecraft", "Personal", "Instances", name, "saves");
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
        }
        TempTree.Delete(_root);
    }

    [Fact]
    public void ExistingPack_RenamesItsOwnedLocationsAndReferencesWithoutReplacingGameData()
    {
        var (paths, settings) = Fixture();
        var oldJar = Path.Combine(paths.Packs, Old, "mods", "same.jar");
        var jarBytes = File.ReadAllBytes(oldJar);
        var jarTime = File.GetLastWriteTimeUtc(oldJar);
        SavesFolderService.CreateJunction(Path.Combine(paths.Instances, Old, "saves"), Path.Combine(paths.Worlds, Old));
        var saved = 0;
        new PackRenameMigration(paths).Apply(settings, [Entry], _ => saved++);

        foreach (var root in new[] {paths.Packs, paths.Instances, paths.Worlds, paths.Runtimes})
        {
            Assert.False(Directory.Exists(Path.Combine(root, Old)));
            Assert.True(Directory.Exists(Path.Combine(root, New)));
        }
        var newJar = Path.Combine(paths.Packs, New, "mods", "same.jar");
        Assert.Equal(jarBytes, File.ReadAllBytes(newJar));
        Assert.Equal(jarTime, File.GetLastWriteTimeUtc(newJar));
        Assert.Equal("owned-sync-state", File.ReadAllText(Path.Combine(paths.Packs, New, ".portable-pack-sync.json")));
        Assert.Equal("personal-options", File.ReadAllText(Path.Combine(paths.Instances, New, "options.txt")));
        Assert.Equal("unmanaged-mod", File.ReadAllText(Path.Combine(paths.Instances, New, "mods", "my-personal-mod.jar")));
        Assert.Equal("personal-world", File.ReadAllText(Path.Combine(paths.Worlds, New, "World", "level.dat")));
        Assert.Equal("unknown-world-file", File.ReadAllText(Path.Combine(paths.Worlds, New, "World", "future-format.bin")));
        Assert.Equal(Path.Combine(paths.Worlds, New), new DirectoryInfo(Path.Combine(paths.Instances, New, "saves")).LinkTarget);
        Assert.Equal(New, settings.ClientRelativePath);
        Assert.Equal([New, "Other"], settings.AddedPackRelativePaths);
        Assert.Equal(7, settings.MemoryByPack[New]);
        Assert.False(settings.MemoryByPack.ContainsKey(Old));
        Assert.Equal(New + "/World", settings.SelectedWorldRelativePath);
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(paths.Worlds, New, "World", WorldMetadataService.MetadataFileName)))!;
        Assert.Equal(New, metadata["buildRelativePath"]!.GetValue<string>());
        Assert.Equal("keep-uuid", metadata["ownerIdentityId"]!.GetValue<string>());
        Assert.Equal("future-data", metadata["unknownField"]!.GetValue<string>());
        var instance = JsonNode.Parse(File.ReadAllText(Path.Combine(paths.Instances, New, ".portable-instance.json")))!;
        Assert.Equal(New, instance["packRelativePath"]!.GetValue<string>());
        Assert.Equal("keep-files", instance["files"]!.GetValue<string>());
        var sync = new PortablePackSyncService(paths, new Logger(paths.LogFile));
        Assert.Equal(Entry.Source, sync.TryResolveSource(New));
        new PackRenameMigration(paths).Apply(settings, [Entry], _ => saved++);
        Assert.Equal(1, saved);
    }

    [Fact]
    public void InterruptedMoves_AreFinishedOnRetryAndDoNotRecreateAnOldPack()
    {
        var (paths, settings) = Fixture();
        SavesFolderService.CreateJunction(Path.Combine(paths.Instances, Old, "saves"), Path.Combine(paths.Worlds, Old));
        Assert.Throws<IOException>(() => new PackRenameMigration(paths, afterMove: step =>
        {
            if (step == 2) throw new IOException("simulated interruption");
        }).Apply(settings, [Entry], _ => throw new Exception("must not save early")));
        Assert.Equal(Old, settings.ClientRelativePath);
        Assert.True(Directory.Exists(Path.Combine(paths.Packs, New)));
        Assert.True(Directory.Exists(Path.Combine(paths.Worlds, Old)));
        new PackRenameMigration(paths).Apply(settings, [Entry], _ => { });
        Assert.Equal(New, settings.ClientRelativePath);
        Assert.Equal("personal-world", File.ReadAllText(Path.Combine(paths.Worlds, New, "World", "level.dat")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(paths.Personal, "PackRenames"), "*.json"));
    }

    [Theory]
    [InlineData("collision")]
    [InlineData("live")]
    [InlineData("locked-world")]
    [InlineData("foreign-link")]
    [InlineData("memory-conflict")]
    public void UnsafeRename_RefusesBeforeMovingOrChangingSettings(string reason)
    {
        var (paths, settings) = Fixture();
        FileStream? held = null;
        try
        {
            if (reason == "collision") Directory.CreateDirectory(Path.Combine(paths.Packs, New));
            if (reason == "memory-conflict") settings.MemoryByPack[New] = 9;
            if (reason == "foreign-link")
                SavesFolderService.CreateJunction(Path.Combine(paths.Instances, Old, "saves"), paths.Packs);
            if (reason == "locked-world")
                held = new FileStream(Path.Combine(paths.Worlds, Old, "World", "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var sessions = reason == "live"
                ? new[] {new ClientPresenceService.ClientSession(123, DateTime.UtcNow, Old)} : [];
            Assert.Throws<IOException>(() => new PackRenameMigration(paths, liveSessions: () => sessions)
                .Apply(settings, [Entry], _ => throw new Exception("must not save")));
            Assert.True(Directory.Exists(Path.Combine(paths.Packs, Old)));
            Assert.True(Directory.Exists(Path.Combine(paths.Worlds, Old)));
            Assert.Equal(Old, settings.ClientRelativePath);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(paths.Personal, "PackRenames"), "*.json"));
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public void LegacyOrdinarySaves_ArePreservedWhenAnInterruptedRenameResumes()
    {
        var (paths, settings) = Fixture();
        var local = Path.Combine(paths.Instances, Old, "saves", "UnadoptedWorld");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "level.dat"), "unadopted-personal-world");
        File.WriteAllText(Path.Combine(local, WorldMetadataService.MetadataFileName),
            """{"buildRelativePath":"Minecraft 26.2","ownerIdentityId":"same-player"}""");
        Assert.Throws<IOException>(() => new PackRenameMigration(paths, afterMove: step =>
        {
            if (step == 4) throw new IOException("simulated interruption after all moves");
        }).Apply(settings, [Entry], _ => { }));
        new PackRenameMigration(paths).Apply(settings, [Entry], _ => { });
        var renamed = Path.Combine(paths.Instances, New, "saves", "UnadoptedWorld");
        Assert.Null(new DirectoryInfo(Path.Combine(paths.Instances, New, "saves")).LinkTarget);
        Assert.Equal("unadopted-personal-world", File.ReadAllText(Path.Combine(renamed, "level.dat")));
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(renamed, WorldMetadataService.MetadataFileName)))!;
        Assert.Equal(New, metadata["buildRelativePath"]!.GetValue<string>());
        Assert.Equal("same-player", metadata["ownerIdentityId"]!.GetValue<string>());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(paths.Personal, "PackRenames"), "*.json"));
    }

    [Fact]
    public async Task RenamedSource_ReusesVerifiedLocalFilesAndFetchesOnlyTheNewRepositoryManifest()
    {
        var (paths, settings) = Fixture();
        var pack = Path.Combine(paths.Packs, Old);
        File.WriteAllText(Path.Combine(pack, "portable-pack.json"), """{"schemaVersion":1,"minecraftVersion":"26.3","loader":{"type":"neoforge","version":"26.3.0.99"}}""");
        var files = new[] { "mods/same.jar", "portable-pack.json" }.Select(relative =>
        {
            var file = new FileInfo(Path.Combine(pack, relative.Replace('/', Path.DirectorySeparatorChar)));
            return new { path = relative, sizeBytes = file.Length, lastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))).ToLowerInvariant() };
        }).ToArray();
        File.WriteAllText(Path.Combine(pack, PortablePackSyncService.SyncStateFileName), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, revision = "same-revision", files = files.ToDictionary(file => file.path, file => new
            { file.sizeBytes, file.lastWriteUtcTicks, file.sha256 })
        }));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, packId = New, revision = "same-revision",
            files = files.Select(file => new { file.path, file.sizeBytes, file.sha256 }),
            assets = files.Select((file, index) => new { name = "asset-" + index + ".jar", kind = "jar", file.sha256,
                file.sizeBytes, covers = new[] {file.path} }) });
        new PackRenameMigration(paths).Apply(settings, [Entry], _ => { });
        using var handler = new ManifestOnlyHandler(manifest);
        using var http = new HttpClient(handler);
        var result = await new PortablePackSyncService(paths, new Logger(paths.LogFile), http)
            .SyncAsync(New, null, CancellationToken.None);
        Assert.Equal(PackSyncOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, result.BytesDownloaded);
        Assert.Equal(0, result.FilesChanged);
        Assert.Contains("/MarkZamore/Minecraft-26.3/", Assert.Single(handler.Requests).AbsolutePath, StringComparison.Ordinal);
        Assert.All(files, file => Assert.Equal(file.lastWriteUtcTicks,
            File.GetLastWriteTimeUtc(Path.Combine(paths.Packs, New, file.path.Replace('/', Path.DirectorySeparatorChar))).Ticks));
    }

    [Fact]
    public void CachedLegacyCatalog_KeepsOneCurrentCardWithTheStableIdentity()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var library = Path.Combine(paths.Launcher, "Library");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "catalog.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, packs = new[] { new { id = Entry.Id, relativePath = Old,
                source = new {owner = "MarkZamore", repo = "Minecraft-26.2", tag = "pack-latest"} } }
        }));
        var catalog = new PackCatalogService(paths, new Logger(paths.LogFile));
        var source = Assert.Single(catalog.Sources, source => source.Id == Entry.Id);
        Assert.Equal(New, source.RelativePath);
        Assert.Equal(Entry.Source, source.Source);
        Assert.DoesNotContain(catalog.Sources, source => source.RelativePath == Old);
        Assert.DoesNotContain(catalog.Packs, pack => pack.Entry.RelativePath == Old);
    }

    [Fact]
    public void CatalogLegacyPath_DoesNotReplaceAnExplicitChangedRepository()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var library = Path.Combine(paths.Launcher, "Library");
        Directory.CreateDirectory(library);
        var changed = new PackSyncSource("SomeoneElse", "Minecraft-Fork", "pack-latest");
        var previous = new PackSyncSource("SomeoneElse", "Minecraft-Fork-Old", "pack-latest");
        File.WriteAllText(Path.Combine(library, "catalog.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packs = new[]
            {
                new CatalogSource(Entry.Id, Old, changed)
                {
                    LegacyRelativePaths = ["Earlier Minecraft"],
                    LegacySources = [previous]
                }
            }
        }));
        var catalog = new PackCatalogService(paths, new Logger(paths.LogFile));
        var source = Assert.Single(catalog.Sources, source => source.Id == Entry.Id);
        Assert.Equal(New, source.RelativePath);
        Assert.Equal(changed, source.Source);
        Assert.Contains(Old, source.LegacyRelativePaths);
        Assert.Contains("Earlier Minecraft", source.LegacyRelativePaths);
        Assert.Contains(previous, source.LegacySources);
        Assert.Contains(Entry.LegacySources[0], source.LegacySources);
    }

    [Fact]
    public void CatalogSourceAliases_KeepValueEqualityAcrossJsonRoundTrip()
    {
        var source = Entry with
        {
            LegacyRelativePaths = [Old, "Earlier Minecraft"],
            LegacySources = [Entry.LegacySources[0], new PackSyncSource("MarkZamore", "Earlier-Minecraft", "pack-latest")]
        };
        var restored = JsonSerializer.Deserialize<CatalogSource>(JsonSerializer.Serialize(source))!;
        Assert.NotSame(source.LegacyRelativePaths, restored.LegacyRelativePaths);
        Assert.NotSame(source.LegacySources, restored.LegacySources);
        Assert.Equal(source, restored);
        Assert.Equal(source.GetHashCode(), restored.GetHashCode());
        Assert.NotEqual(source, restored with { LegacyRelativePaths = restored.LegacyRelativePaths.AsEnumerable().Reverse().ToList() });
        Assert.NotEqual(source, restored with { LegacySources = restored.LegacySources.AsEnumerable().Reverse().ToList() });
        Assert.NotEqual(source, restored with { LegacyRelativePaths = [Old.ToLowerInvariant(), "Earlier Minecraft"] });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacySettings_LoadBeforeRename_PreserveImplicitMembershipButRespectAnExplicitEmptyList(bool explicitEmpty)
    {
        var (paths, _) = Fixture();
        var json = new JsonObject
        {
            ["schemaVersion"] = SettingsService.CurrentSchemaVersion,
            ["playerName"] = "Player",
            ["clientRelativePath"] = PortablePackSyncService.DefaultPackRelativePath,
            ["memorySettingIsTheHeap"] = true,
            ["maxMemoryGb"] = 6
        };
        if (explicitEmpty) json["addedPackRelativePaths"] = new JsonArray();
        File.WriteAllText(paths.SettingsFile, json.ToJsonString());
        var service = new SettingsService(paths);
        var loaded = service.Load();
        new PackRenameMigration(paths).Apply(loaded, [Entry], service.Save);
        var reloaded = service.Load();
        Assert.Equal(!explicitEmpty, PackMembership.Contains(reloaded, New));
        Assert.DoesNotContain(Old, reloaded.AddedPackRelativePaths!);
        Assert.Equal(PortablePackSyncService.DefaultPackRelativePath, reloaded.ClientRelativePath);
    }

    [Fact]
    public void CatalogAliases_CannotCreateDuplicateTargetsAfterCanonicalization()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var library = Path.Combine(paths.Launcher, "Library");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "catalog.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, packs = new[]
            {
                new { id = Entry.Id, relativePath = Old, source = new {owner = "MarkZamore", repo = "Minecraft-26.2", tag = "pack-latest"} },
                new { id = "different-pack", relativePath = New, source = new {owner = "SomeoneElse", repo = "Other", tag = "pack-latest"} }
            }
        }));
        var catalog = new PackCatalogService(paths, new Logger(paths.LogFile));
        Assert.DoesNotContain(catalog.Sources, source => source.Id == "different-pack");
        Assert.Equal(New, Assert.Single(catalog.Sources, source => source.Id == Entry.Id).RelativePath);
        Assert.Contains("Invalid/duplicate library rename targets", File.ReadAllText(paths.LogFile), StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenMembershipAndLegacySourceRedirect_KeepTheExistingChoice()
    {
        var (paths, settings) = Fixture();
        settings.HiddenPackRelativePaths = [Old];
        settings.AddedPackRelativePaths = [];
        new PackRenameMigration(paths).Apply(settings, [Entry], _ => { });
        Assert.Equal([New], settings.HiddenPackRelativePaths);
        Assert.Empty(settings.AddedPackRelativePaths!);
        var marker = Path.Combine(paths.Packs, New, PortablePackSyncService.SourceMarkerFileName);
        File.WriteAllText(marker, """{"schemaVersion":1,"owner":"MarkZamore","repo":"Minecraft-26.2","tag":"pack-latest"}""");
        var sync = new PortablePackSyncService(paths, new Logger(paths.LogFile));
        Assert.Equal(Entry.Source, sync.TryResolveSource(New));
        File.WriteAllText(marker, """{"schemaVersion":1,"owner":"SomeoneElse","repo":"Custom","tag":"pack-latest"}""");
        Assert.Equal("SomeoneElse", sync.TryResolveSource(New)!.Owner);
    }

    private sealed class ManifestOnlyHandler(byte[] manifest) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.EndsWith("/pack-manifest.json", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) });
        }
    }

    private (AppPaths Paths, AppSettings Settings) Fixture()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        foreach (var root in new[] {paths.Packs, paths.Instances, paths.Worlds, paths.Runtimes}) Directory.CreateDirectory(Path.Combine(root, Old));
        Directory.CreateDirectory(Path.Combine(paths.Packs, Old, "mods"));
        File.WriteAllBytes(Path.Combine(paths.Packs, Old, "mods", "same.jar"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(paths.Packs, Old, ".portable-pack-sync.json"), "owned-sync-state");
        File.WriteAllText(Path.Combine(paths.Packs, Old, PortablePackSyncService.SourceMarkerFileName),
            """{"schemaVersion":1,"owner":"MarkZamore","repo":"Minecraft-26.2","tag":"pack-latest"}""");
        File.WriteAllText(Path.Combine(paths.Instances, Old, "options.txt"), "personal-options");
        Directory.CreateDirectory(Path.Combine(paths.Instances, Old, "mods"));
        File.WriteAllText(Path.Combine(paths.Instances, Old, "mods", "my-personal-mod.jar"), "unmanaged-mod");
        File.WriteAllText(Path.Combine(paths.Instances, Old, ".portable-instance.json"),
            """{"schemaVersion":1,"packRelativePath":"Minecraft 26.2","files":"keep-files"}""");
        var world = Path.Combine(paths.Worlds, Old, "World");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "level.dat"), "personal-world");
        File.WriteAllText(Path.Combine(world, "future-format.bin"), "unknown-world-file");
        File.WriteAllText(Path.Combine(world, "session.lock"), "lock");
        File.WriteAllText(Path.Combine(world, WorldMetadataService.MetadataFileName),
            """{"schemaVersion":1,"buildRelativePath":"Minecraft 26.2","buildName":"Minecraft 26.2","ownerIdentityId":"keep-uuid","unknownField":"future-data"}""");
        return (paths, new AppSettings {ClientRelativePath = Old, AddedPackRelativePaths = [Old, "Other"],
            MemoryByPack = new(StringComparer.OrdinalIgnoreCase) {{Old, 7}}, SelectedWorldRelativePath = Old + "/World"});
    }
}
