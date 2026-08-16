using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The rename of the built-in pack happens once, on somebody's real portable
/// folder, with their instance, runtime, worlds and settings in it. What is
/// pinned here is as much what the migration must leave alone - a custom source,
/// a world of another build, an open world, a locked instance - as what it moves.
/// </summary>
public sealed class LegacyPackMigrationServiceTests : IDisposable
{
    private const string Legacy = LegacyPackMigrationService.LegacyPackRelativePath;
    private const string Target = PortablePackSyncService.DefaultPackRelativePath;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-legacy-migration-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EveryLegacyArtifact_IsRenamedAndTheTeleportLayerIsForgotten()
    {
        var fixture = CreateFixture();

        Run(fixture);

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.Packs, Legacy)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.Instances, Legacy)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.Runtimes, Legacy)));

        var instance = Path.Combine(fixture.Paths.Instances, Target);
        Assert.Equal("options", File.ReadAllText(Path.Combine(instance, "options.txt")));
        Assert.Equal(
            "waypoints",
            File.ReadAllText(Path.Combine(instance, "xaero", "minimap", "waypoints.txt")));
        Assert.Equal(
            "pack config",
            File.ReadAllText(Path.Combine(instance, "config", "tracked.toml")));
        foreach (var relativePath in LegacyPackMigrationService.TeleportInstanceFiles)
        {
            Assert.False(
                File.Exists(Path.Combine(instance, relativePath.Replace('/', Path.DirectorySeparatorChar))),
                relativePath);
        }
        // The empty parents the deletions leave behind go with them.
        Assert.False(Directory.Exists(Path.Combine(instance, "defaultconfigs")));
        Assert.False(Directory.Exists(Path.Combine(instance, "kubejs")));

        var state = ReadJson(Path.Combine(instance, ".portable-instance.json"));
        Assert.Equal(Target, state.GetProperty("packRelativePath").GetString());
        var trackedFiles = state.GetProperty("files");
        Assert.True(trackedFiles.TryGetProperty("config/tracked.toml", out _));
        foreach (var relativePath in LegacyPackMigrationService.TeleportInstanceFiles)
        {
            Assert.False(trackedFiles.TryGetProperty(relativePath, out _), relativePath);
        }

        Assert.Equal(
            "runtime state",
            File.ReadAllText(Path.Combine(fixture.Paths.Runtimes, Target, ".portable-runtime.json")));

        var packDir = Path.Combine(fixture.Paths.Packs, Target);
        Assert.Equal("mod", File.ReadAllText(Path.Combine(packDir, "mods", "some-mod.jar")));
        Assert.Equal(
            PortablePackSyncService.DefaultPackSource,
            new PortablePackSyncService(fixture.Paths, fixture.Logger).TryResolveSource(Target));
        var marker = ReadJson(Path.Combine(packDir, PortablePackSyncService.SourceMarkerFileName));
        Assert.Equal("LL8", marker.GetProperty("repo").GetString());

        Assert.Equal(Target, fixture.Settings.ClientRelativePath);
        Assert.Equal(
            Target,
            JsonDocument.Parse(File.ReadAllText(fixture.Paths.SettingsFile))
                .RootElement.GetProperty("clientRelativePath").GetString());

        var migrated = ReadJson(WorldMetadataPath(fixture.Paths, "Chebupeli"));
        Assert.Equal(Target, migrated.GetProperty("buildName").GetString());
        Assert.Equal(Target, migrated.GetProperty("buildRelativePath").GetString());
        Assert.Equal("world-pack-hash", migrated.GetProperty("packHash").GetString());
        Assert.Equal("world-id", migrated.GetProperty("worldId").GetString());
        Assert.Equal("owner-id", migrated.GetProperty("ownerIdentityId").GetString());
        Assert.Equal("holder-id", migrated.GetProperty("currentHolderIdentityId").GetString());
        Assert.False(File.Exists(Path.Combine(
            fixture.Paths.Worlds,
            "Chebupeli",
            "serverconfig",
            "ftbessentials-server.snbt")));

        var untouched = ReadJson(WorldMetadataPath(fixture.Paths, "Vanilla"));
        Assert.Equal("Other", untouched.GetProperty("buildRelativePath").GetString());
        Assert.True(File.Exists(Path.Combine(
            fixture.Paths.Worlds,
            "Vanilla",
            "serverconfig",
            "ftbessentials-server.snbt")));

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.Personal, "Temp", "Java", Legacy)));
        Assert.False(Directory.Exists(
            Path.Combine(fixture.Paths.Personal, "Temp", "RuntimeDownloads", Legacy)));

        var packs = ReadJson(fixture.Paths.PackHashesFile).GetProperty("packs");
        Assert.False(packs.TryGetProperty("infinity", out _));
        Assert.True(packs.TryGetProperty("other", out _));
    }

    /// <summary>
    /// Every step is gated on its legacy artifact, so a second start must be a
    /// no-op - in particular it must not delete the pack's own copies of the
    /// teleport files, which the first sync after the rename puts back.
    /// </summary>
    [Fact]
    public void RunningTwice_LeavesTheRenamedFilesAlone()
    {
        var fixture = CreateFixture();
        Run(fixture);

        var instance = Path.Combine(fixture.Paths.Instances, Target);
        var restored = Path.Combine(instance, "config", "ftbessentials.snbt");
        Directory.CreateDirectory(Path.GetDirectoryName(restored)!);
        File.WriteAllText(restored, "shipped by the pack");

        Run(fixture);

        Assert.Equal("shipped by the pack", File.ReadAllText(restored));
        Assert.Equal(Target, fixture.Settings.ClientRelativePath);
    }

    [Fact]
    public void NothingLegacy_ChangesNothing()
    {
        var fixture = CreateFixture(withLegacy: false);
        fixture.Settings.ClientRelativePath = Target;
        var before = Snapshot(fixture.Paths.Root);

        Run(fixture);

        Assert.Equal(before, Snapshot(fixture.Paths.Root));
    }

    [Fact]
    public void DestinationAlreadyThere_LeavesTheLegacyFolderInPlace()
    {
        var fixture = CreateFixture();
        WriteFile(Path.Combine(fixture.Paths.Packs, Target, "mods", "already-here.jar"), "newer");
        WriteFile(Path.Combine(fixture.Paths.Instances, Target, "options.txt"), "newer");
        WriteFile(Path.Combine(fixture.Paths.Runtimes, Target, ".portable-runtime.json"), "newer");

        Run(fixture);

        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.Packs, Legacy)));
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.Instances, Legacy)));
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.Runtimes, Legacy)));
        Assert.Equal("newer", File.ReadAllText(Path.Combine(fixture.Paths.Instances, Target, "options.txt")));
    }

    /// <summary>
    /// A running game holds files in the instance. Renaming the rest around it
    /// would leave a portable folder half in each name, so nothing is touched.
    /// </summary>
    [Fact]
    public void InstanceInUse_PostponesEverything()
    {
        var fixture = CreateFixture();
        var held = Path.Combine(fixture.Paths.Instances, Legacy, "logs", "latest.log");
        Directory.CreateDirectory(Path.GetDirectoryName(held)!);
        using (var _ = new FileStream(held, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Run(fixture);
        }

        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.Instances, Legacy)));
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.Packs, Legacy)));
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.Runtimes, Legacy)));
        Assert.Equal(Legacy, fixture.Settings.ClientRelativePath);
        Assert.Equal(
            Legacy,
            ReadJson(WorldMetadataPath(fixture.Paths, "Chebupeli"))
                .GetProperty("buildRelativePath").GetString());
    }

    [Fact]
    public void OpenWorld_IsReboundOnceItIsClosed()
    {
        var fixture = CreateFixture();
        var world = Path.Combine(fixture.Paths.Worlds, "Chebupeli");
        var sessionLock = Path.Combine(world, "session.lock");
        File.WriteAllText(sessionLock, "☃☃");
        using (var stream = new FileStream(sessionLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Run(fixture);
            Assert.Equal(
                Legacy,
                ReadJson(WorldMetadataPath(fixture.Paths, "Chebupeli"))
                    .GetProperty("buildRelativePath").GetString());
            Assert.True(File.Exists(Path.Combine(world, "serverconfig", "ftbessentials-server.snbt")));
            stream.Close();
        }

        Run(fixture);

        Assert.Equal(
            Target,
            ReadJson(WorldMetadataPath(fixture.Paths, "Chebupeli"))
                .GetProperty("buildRelativePath").GetString());
        Assert.False(File.Exists(Path.Combine(world, "serverconfig", "ftbessentials-server.snbt")));
    }

    [Fact]
    public void CustomSourceMarker_SurvivesTheRename()
    {
        var fixture = CreateFixture();
        WriteFile(
            Path.Combine(
                fixture.Paths.Packs,
                Legacy,
                PortablePackSyncService.SourceMarkerFileName),
            """{"schemaVersion":1,"owner":"SomebodyElse","repo":"Fork","tag":"nightly"}""");

        Run(fixture);

        Assert.Equal(
            new PackSyncSource("SomebodyElse", "Fork", "nightly"),
            new PortablePackSyncService(fixture.Paths, fixture.Logger).TryResolveSource(Target));
    }

    private static void Run(Fixture fixture) => LegacyPackMigrationService.Run(
        fixture.Paths,
        fixture.Settings,
        fixture.SettingsService,
        fixture.Logger);

    private Fixture CreateFixture(bool withLegacy = true)
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var logger = new Logger(Path.Combine(paths.Personal, "migration.log"));
        var settingsService = new SettingsService(paths, logger);
        var settings = settingsService.Load();
        if (!withLegacy) return new Fixture(paths, settings, settingsService, logger);

        settings.ClientRelativePath = Legacy;
        settingsService.Save(settings);

        WriteFile(Path.Combine(paths.Packs, Legacy, "mods", "some-mod.jar"), "mod");
        WriteFile(
            Path.Combine(paths.Packs, Legacy, PortablePackSyncService.SourceMarkerFileName),
            """{"schemaVersion":1,"owner":"MarkZamore","repo":"Infinity","tag":"pack-latest"}""");
        WriteFile(Path.Combine(paths.Runtimes, Legacy, ".portable-runtime.json"), "runtime state");

        var instance = Path.Combine(paths.Instances, Legacy);
        WriteFile(Path.Combine(instance, "options.txt"), "options");
        WriteFile(Path.Combine(instance, "xaero", "minimap", "waypoints.txt"), "waypoints");
        WriteFile(Path.Combine(instance, "config", "tracked.toml"), "pack config");
        foreach (var relativePath in LegacyPackMigrationService.TeleportInstanceFiles)
        {
            WriteFile(
                Path.Combine(instance, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                "teleport layer");
        }
        WriteFile(
            Path.Combine(instance, ".portable-instance.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                packRelativePath = Legacy,
                modsMode = "copy",
                files = LegacyPackMigrationService.TeleportInstanceFiles
                    .Append("config/tracked.toml")
                    .ToDictionary(path => path, _ => TrackedFile),
                modFiles = new Dictionary<string, object>()
            }));

        WriteWorld(paths, "Chebupeli", Legacy);
        WriteWorld(paths, "Vanilla", "Other");

        WriteFile(Path.Combine(paths.Personal, "Temp", "Java", Legacy, "hs_err.log"), "temp");
        WriteFile(
            Path.Combine(paths.Personal, "Temp", "RuntimeDownloads", Legacy, "runtime.zip.part"),
            "temp");
        WriteFile(
            paths.PackHashesFile,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                packs = new Dictionary<string, object>
                {
                    ["infinity"] = new { hash = "legacy", files = new Dictionary<string, object>() },
                    ["other"] = new { hash = "kept", files = new Dictionary<string, object>() }
                }
            }));

        return new Fixture(paths, settings, settingsService, logger);
    }

    private static object TrackedFile => new { sizeBytes = 4, lastWriteUtcTicks = 1, sha256 = new string('a', 64) };

    private static void WriteWorld(AppPaths paths, string name, string buildRelativePath)
    {
        var world = Path.Combine(paths.Worlds, name);
        WriteFile(Path.Combine(world, "level.dat"), "level");
        WriteFile(Path.Combine(world, "serverconfig", "ftbessentials-server.snbt"), "teleport layer");
        WriteFile(
            Path.Combine(world, WorldMetadataService.MetadataFileName),
            JsonSerializer.Serialize(new
            {
                schemaVersion = WorldMetadataService.CurrentSchemaVersion,
                worldId = "world-id",
                buildName = buildRelativePath,
                buildRelativePath,
                packHash = "world-pack-hash",
                ownerIdentityId = "owner-id",
                ownerIdentityName = "owner",
                currentHolderIdentityId = "holder-id",
                currentHolderIdentityName = "holder"
            }));
    }

    private static string WorldMetadataPath(AppPaths paths, string world) =>
        Path.Combine(paths.Worlds, world, WorldMetadataService.MetadataFileName);

    private static JsonElement ReadJson(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Snapshot(string root) => string.Join(
        "\n",
        Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("migration.log", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{Path.GetRelativePath(root, path)}|{File.ReadAllText(path)}"));

    private sealed record Fixture(
        AppPaths Paths,
        AppSettings Settings,
        SettingsService SettingsService,
        Logger Logger);
}
