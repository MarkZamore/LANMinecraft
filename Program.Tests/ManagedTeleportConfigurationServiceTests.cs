using System.Text;
using System.Text.RegularExpressions;

namespace Minecraft.Tests;

public sealed class ManagedTeleportConfigurationServiceTests
{
    [Fact]
    public void FtbPolicy_EnablesOnlyRequestedPlayerTeleportFeatures()
    {
        var rewritten =
            ManagedTeleportConfigurationService.RewriteFtbEssentialsConfig(FtbConfig);

        Assert.Matches(
            @"register_alias_as_well_as_namespace:\s*false",
            rewritten);
        Assert.Matches(@"register_to_namespace:\s*false", rewritten);
        Assert.Matches(
            @"admin:\s*\{\s*extinguish:\s*\{\s*enabled:\s*false",
            rewritten);
        Assert.Matches(
            @"integration:\s*\{\s*team_bases_spawn_override:\s*false",
            rewritten);
        Assert.Matches(
            @"misc:\s*\{\s*anvil:\s*\{\s*enabled:\s*false",
            rewritten);
        Assert.Matches(
            @"home:\s*\{\s*cooldown:\s*10\s*enabled:\s*true\s*" +
            @"max:\s*1\s*warmup:\s*0",
            rewritten);
        Assert.Matches(
            @"spawn:\s*\{\s*cooldown:\s*10\s*enabled:\s*true\s*" +
            @"warmup:\s*0",
            rewritten);
        Assert.Matches(
            @"tpa:\s*\{\s*cooldown:\s*10\s*enabled:\s*true\s*" +
            @"warmup:\s*0",
            rewritten);
        Assert.Matches(@"back:\s*\{\s*enabled:\s*false", rewritten);
        Assert.Matches(@"jump:\s*\{\s*enabled:\s*false", rewritten);
        Assert.Matches(@"playerspawn:\s*\{\s*enabled:\s*false", rewritten);
        Assert.Matches(@"rtp:\s*\{\s*enabled:\s*false", rewritten);
        Assert.Matches(@"tpl:\s*\{\s*enabled:\s*false", rewritten);
        Assert.Matches(@"warp:\s*\{\s*enabled:\s*false", rewritten);
        Assert.Matches(@"tpx:\s*\{\s*enabled:\s*true", rewritten);
        Assert.Contains("custom_keep: 73", rewritten, StringComparison.Ordinal);
        Assert.Equal(
            rewritten,
            ManagedTeleportConfigurationService.RewriteFtbEssentialsConfig(
                rewritten));
    }

    [Fact]
    public void FtbPolicy_IncompatibleSchemaFailsClosed()
    {
        var incompatible = Regex.Replace(
            FtbConfig,
            @"\s*tpl:\s*\{\s*enabled:\s*true\s*}",
            string.Empty,
            RegexOptions.CultureInvariant);

        var error = Assert.Throws<InvalidDataException>(
            () => ManagedTeleportConfigurationService
                .RewriteFtbEssentialsConfig(incompatible));

        Assert.Contains(
            "teleportation.tpl.enabled",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void XaeroPolicy_PreservesUnrelatedValuesAndIsIdempotent()
    {
        const string profile =
            "profile_name = Custom\r\n" +
            "default_waypoint_teleport_format = /tp @s {x} {y} {z}\r\n" +
            "waypoint_teleport_cross_dimension = false\r\n";

        var rewritten =
            ManagedTeleportConfigurationService.RewriteXaeroProfile(profile);

        Assert.Contains("profile_name = Custom\r\n", rewritten, StringComparison.Ordinal);
        Assert.Contains(
            "default_waypoint_teleport_format = " +
            "/portable_waypoint_tp {x} {y} {z}\r\n",
            rewritten,
            StringComparison.Ordinal);
        Assert.Contains(
            "default_waypoint_teleport_rotation_format = " +
            "/portable_waypoint_tp {x} {y} {z} {yaw}\r\n",
            rewritten,
            StringComparison.Ordinal);
        Assert.Contains(
            "waypoint_teleport_cross_dimension = true\r\n",
            rewritten,
            StringComparison.Ordinal);
        Assert.Contains(
            "waypoint_partial_y_teleport = true\r\n",
            rewritten,
            StringComparison.Ordinal);
        Assert.Equal(
            rewritten,
            ManagedTeleportConfigurationService.RewriteXaeroProfile(rewritten));
    }

    [Fact]
    public void Apply_InstallsCommandsAndUpdatesInstanceAndWorldsIdempotently()
    {
        using var fixture = new TemporaryTeleportRoot();
        var globalConfig = Path.Combine(
            fixture.GameDirectory,
            "config",
            "ftbessentials.snbt");
        Write(globalConfig, FtbConfig.Replace("\n", "\r\n", StringComparison.Ordinal));

        var defaultConfig = Path.Combine(
            fixture.GameDirectory,
            "defaultconfigs",
            "ftbessentials-server.snbt");
        Write(
            defaultConfig,
            FtbConfig
                .Replace("custom_keep: 73", "custom_keep: 84", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal));

        var profile = Path.Combine(
            fixture.GameDirectory,
            "config",
            "xaero",
            "minimap",
            "profiles",
            "default.cfg");
        Write(
            profile,
            "profile_name = Kept\n" +
            "default_waypoint_teleport_format = /tp @s {x} {y} {z}\n");

        var xaeroWorld = Path.Combine(
            fixture.GameDirectory,
            "xaero",
            "minimap",
            "Multiplayer_127.0.0.1",
            "config.txt");
        Write(
            xaeroWorld,
            "teleportationEnabled:false\n" +
            "usingDefaultTeleportCommand:false\n" +
            "sortType:NONE\n");

        var existingWorld = fixture.CreateWorld("Existing", "Infinity");
        var existingWorldConfig = Path.Combine(
            existingWorld,
            "serverconfig",
            "ftbessentials-server.snbt");
        Write(
            existingWorldConfig,
            FtbConfig.Replace(
                "custom_keep: 73",
                "custom_keep: 95",
                StringComparison.Ordinal));
        var newWorld = fixture.CreateWorld("New", "Infinity");
        var otherPackWorld = fixture.CreateWorld("OtherPack", "OtherPack");
        var otherPackConfig = Path.Combine(
            otherPackWorld,
            "serverconfig",
            "ftbessentials-server.snbt");
        Write(otherPackConfig, FtbConfig);
        var legacyWorld = fixture.CreateWorld("Legacy");
        var legacyConfig = Path.Combine(
            legacyWorld,
            "serverconfig",
            "ftbessentials-server.snbt");
        Write(legacyConfig, FtbConfig);

        var service = new ManagedTeleportConfigurationService();
        var first = service.Apply(
            fixture.GameDirectory,
            fixture.Worlds,
            "Infinity");

        Assert.NotEmpty(first.ChangedPaths);
        var scriptPath = Path.Combine(
            fixture.GameDirectory,
            ManagedTeleportConfigurationService.CommandScriptRelativePath
                .Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(scriptPath));
        var script = File.ReadAllText(scriptPath);
        AssertCommandScriptSafety(script);

        Assert.Contains(
            "custom_keep: 73",
            File.ReadAllText(globalConfig),
            StringComparison.Ordinal);
        Assert.Contains(
            "custom_keep: 84",
            File.ReadAllText(defaultConfig),
            StringComparison.Ordinal);
        Assert.Contains(
            "custom_keep: 95",
            File.ReadAllText(existingWorldConfig),
            StringComparison.Ordinal);
        Assert.Contains(
            "custom_keep: 84",
            File.ReadAllText(Path.Combine(
                newWorld,
                "serverconfig",
                "ftbessentials-server.snbt")),
            StringComparison.Ordinal);
        Assert.Contains(
            "profile_name = Kept",
            File.ReadAllText(profile),
            StringComparison.Ordinal);
        Assert.Contains(
            "teleportationEnabled:true",
            File.ReadAllText(xaeroWorld),
            StringComparison.Ordinal);
        Assert.Contains(
            "usingDefaultTeleportCommand:true",
            File.ReadAllText(xaeroWorld),
            StringComparison.Ordinal);
        Assert.Contains(
            "sortType:NONE",
            File.ReadAllText(xaeroWorld),
            StringComparison.Ordinal);
        Assert.Equal(FtbConfig, File.ReadAllText(otherPackConfig));
        Assert.Equal(FtbConfig, File.ReadAllText(legacyConfig));

        var snapshot = Directory
            .EnumerateFiles(
                fixture.Root,
                "*",
                SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(fixture.Root, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

        var second = service.Apply(
            fixture.GameDirectory,
            fixture.Worlds,
            "Infinity");

        Assert.Empty(second.ChangedPaths);
        foreach (var entry in snapshot)
        {
            Assert.Equal(
                entry.Value,
                File.ReadAllBytes(Path.Combine(fixture.Root, entry.Key)));
        }
    }

    [Fact]
    public void EmbeddedCommandScript_IsSelfOnlyFiniteAndWorldBorderBounded()
    {
        using var fixture = new TemporaryTeleportRoot();
        Write(
            Path.Combine(
                fixture.GameDirectory,
                "config",
                "ftbessentials.snbt"),
            FtbConfig);

        new ManagedTeleportConfigurationService().Apply(
            fixture.GameDirectory,
            fixture.Worlds);
        var script = File.ReadAllText(Path.Combine(
            fixture.GameDirectory,
            ManagedTeleportConfigurationService.CommandScriptRelativePath
                .Replace('/', Path.DirectorySeparatorChar)));

        AssertCommandScriptSafety(script);
    }

    private static void AssertCommandScriptSafety(string script)
    {
        Assert.Contains(
            "Commands.literal(PORTABLE_WAYPOINT_COMMAND)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Commands.literal(PORTABLE_WAYPOINT_DIMENSION_COMMAND)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Commands.literal('nether')", script, StringComparison.Ordinal);
        Assert.Contains(
            ".requires(source => source.player != null)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Number.isFinite", script, StringComparison.Ordinal);
        Assert.Contains(
            "worldBorder.isWithinBounds(x, z)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Arguments.DIMENSION.getResult(context, 'dimension')",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "player.teleportTo(level.dimension, x, y, z, yaw, pitch)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "player.teleportTo(\n    level,",
            script.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "return portableTeleportSharedSpawn(context.source, level)",
            script,
            StringComparison.Ordinal);
        var sharedSpawnFunction = Regex.Match(
            script,
            @"function portableTeleportSharedSpawn\(.*?(?=\r?\n}\r?\n\r?\nServerEvents)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline).Value;
        Assert.NotEmpty(sharedSpawnFunction);
        Assert.Contains(
            "const spawn = level.sharedSpawnPos",
            sharedSpawnFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Number(level.sharedSpawnAngle)",
            sharedSpawnFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "portableValidDestination",
            sharedSpawnFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "level.dimension",
            sharedSpawnFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "player.x",
            sharedSpawnFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "player.z",
            sharedSpawnFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain("runCommand", script, StringComparison.Ordinal);
        Assert.DoesNotContain("performPrefixedCommand", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments.PLAYER", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments.ENTITY", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@a", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@p", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@s", script, StringComparison.Ordinal);
    }

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
    }

    private const string FtbConfig = """
        # Compatible FTB Essentials test configuration
        {
          register_alias_as_well_as_namespace: true
          register_to_namespace: true
          admin: {
            extinguish: {
              enabled: true
            }
            feed: {
              enabled: true
            }
            fly: {
              enabled: true
            }
            god: {
              enabled: true
            }
            heal: {
              enabled: true
            }
            invsee: {
              enabled: true
            }
            kit: {
              enabled: true
            }
            mute: {
              enabled: true
            }
            speed: {
              enabled: true
            }
            tp_offline: {
              enabled: true
            }
          }
          integration: {
            team_bases_spawn_override: true
          }
          misc: {
            anvil: {
              enabled: true
            }
            crafting: {
              enabled: true
            }
            enderchest: {
              enabled: true
            }
            hat: {
              enabled: true
            }
            kickme: {
              enabled: true
            }
            leaderboard: {
              enabled: true
            }
            near: {
              enabled: true
            }
            nick: {
              enabled: true
            }
            rec: {
              enabled: true
            }
            smithing: {
              enabled: true
            }
            stonecutter: {
              enabled: true
            }
            trashcan: {
              enabled: true
            }
          }
          teleportation: {
            back: {
              enabled: true
            }
            home: {
              cooldown: 99
              enabled: false
              max: 20
              warmup: 12
            }
            jump: {
              enabled: true
            }
            playerspawn: {
              enabled: true
            }
            rtp: {
              enabled: true
            }
            spawn: {
              cooldown: 99
              enabled: false
              warmup: 12
            }
            tpa: {
              cooldown: 99
              enabled: false
              warmup: 12
            }
            tpl: {
              enabled: true
            }
            tpx: {
              enabled: false
            }
            warp: {
              enabled: true
            }
          }
          custom_keep: 73
        }
        """;

    private sealed class TemporaryTeleportRoot : IDisposable
    {
        public TemporaryTeleportRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "MinecraftTeleportTests",
                Guid.NewGuid().ToString("N"));
            GameDirectory = Path.Combine(Root, "Instance");
            Worlds = Path.Combine(Root, "Worlds");
            Directory.CreateDirectory(GameDirectory);
            Directory.CreateDirectory(Worlds);
        }

        public string Root { get; }
        public string GameDirectory { get; }
        public string Worlds { get; }

        public string CreateWorld(
            string name,
            string? buildRelativePath = null)
        {
            var world = Path.Combine(Worlds, name);
            Directory.CreateDirectory(world);
            File.WriteAllBytes(Path.Combine(world, "level.dat"), [1, 2, 3]);
            if (!string.IsNullOrWhiteSpace(buildRelativePath))
            {
                Write(
                    Path.Combine(
                        world,
                        WorldMetadataService.MetadataFileName),
                    $$"""
                    {
                      "schemaVersion": 5,
                      "worldId": "{{Guid.NewGuid():D}}",
                      "buildRelativePath": "{{buildRelativePath}}"
                    }
                    """);
            }
            return world;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
