using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>
/// Settings are re-saved on every load, so the first launch of a new build
/// rewrites the file in its own shape. What is pinned here is that this never
/// costs the player anything they configured, whatever else the file contains.
/// </summary>
public sealed class SettingsSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-settings-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    /// <summary>
    /// The file is not a way around the window. The box beside "RAM" has always
    /// refused more than the machine can spare, but the file was held only to
    /// what a number may be at all - so a value written into settings.json by
    /// hand went straight through to -Xmx, and the two disagreed about the very
    /// same setting.
    /// </summary>
    [Fact]
    public void AMemoryNumberBiggerThanTheMachine_IsCutToWhatTheMachineAllows()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        File.WriteAllText(paths.SettingsFile, """
        {
          "playerName": "MarkZamore",
          "maxMemoryGb": 128,
          "memorySettingIsWholeGame": true,
          "memoryChosenByPlayer": true,
          "clientRelativePath": "E10"
        }
        """);

        var settings = new SettingsService(paths).Load();

        // 128 is the largest number the type allows and no machine's share of
        // itself, so whatever this one is, the answer is that share.
        Assert.Equal(MemorySizingService.GetAllowedMaxMemoryGb(), settings.MaxMemoryGb);
        Assert.Equal(settings.MaxMemoryGb, MemorySizingService.ClampMemoryGb(settings.MaxMemoryGb));
    }

    /// <summary>
    /// The keys of the VPN and voice era are gone from the model; a file that
    /// still has them loads, keeps what matters, and drops the rest.
    /// </summary>
    [Fact]
    public void AFileWithKeysThisBuildNoLongerHas_LoadsAndKeepsWhatMatters()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        File.WriteAllText(paths.SettingsFile, """
        {
          "playerName": "MarkZamore",
          "maxMemoryGb": 12,
          "clientRelativePath": "Infinity",
          "selectedNetworkInterfaceId": "{144D92B7-CEA3-4EE3-87C9-C8D14EDAD1AB}",
          "selectedNetworkAddress": "10.147.18.145",
          "voicePttMode": "Hold",
          "voiceInputVolume": 2
        }
        """);

        var settings = new SettingsService(paths).Load();

        Assert.Equal("MarkZamore", settings.PlayerName);
        // 12 was written when the number meant the Java heap alone. It becomes
        // the smallest budget that still leaves that heap, so the game keeps
        // exactly the memory it had. There is no pack under this root, so the
        // split is the one the launcher uses for a pack it cannot see.
        var unseen = PackMemoryProfile.Unknown;
        Assert.Equal(MemorySizingService.GetBudgetForHeapGb(unseen, 12), settings.MaxMemoryGb);
        Assert.Equal(12, MemorySizingService.GetHeapGb(unseen, settings.MaxMemoryGb));
        Assert.True(settings.MemorySettingIsWholeGame);
        Assert.Equal("Infinity", settings.ClientRelativePath);
        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);

        var saved = File.ReadAllText(paths.SettingsFile);
        Assert.DoesNotContain("selectedNetworkAddress", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("voicePttMode", saved, StringComparison.Ordinal);
        Assert.Equal(
            SettingsService.CurrentSchemaVersion,
            JsonDocument.Parse(saved).RootElement.GetProperty("schemaVersion").GetInt32());

        // And the conversion happens once: a file that has already been carried
        // across is read as it stands, not converted again.
        var reloaded = new SettingsService(paths).Load();
        Assert.Equal("MarkZamore", reloaded.PlayerName);
        Assert.Equal(settings.MaxMemoryGb, reloaded.MaxMemoryGb);
    }

    [Fact]
    public void AFreshInstall_StartsAtTheCurrentSchema()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();

        var settings = new SettingsService(paths).Load();

        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);
    }

    /// <summary>
    /// A number the launcher suggested follows the pack it is for: put a
    /// vanilla pack under a file nobody has edited by hand, and the suggestion
    /// comes down to what vanilla wants rather than staying at a modpack's.
    /// </summary>
    [Fact]
    public void ANumberTheLauncherChose_FollowsThePack()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        WriteVanillaPack(paths, "Vanilla");
        WriteSettings(paths, memoryGb: 20, chosenByPlayer: false, pack: "Vanilla");

        var settings = new SettingsService(paths).Load();

        var vanilla = PackMemoryProfile.Measure(Path.Combine(paths.Packs, "Vanilla"));
        Assert.True(vanilla.IsKnown);
        Assert.Equal(
            MemorySizingService.GetRecommendedDefaultMemoryGb(vanilla, VideoMemoryProfile.Measure()),
            settings.MaxMemoryGb);
        Assert.True(settings.MaxMemoryGb < 20, "vanilla must not keep a modpack's number");
    }

    /// <summary>And a number the player typed is left where they put it.</summary>
    [Fact]
    public void ANumberThePlayerChose_IsLeftAlone()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        WriteVanillaPack(paths, "Vanilla");
        WriteSettings(paths, memoryGb: 20, chosenByPlayer: true, pack: "Vanilla");

        var settings = new SettingsService(paths).Load();

        Assert.Equal(20, settings.MaxMemoryGb);
        Assert.True(settings.MemoryChosenByPlayer);
    }

    private static void WriteSettings(AppPaths paths, int memoryGb, bool chosenByPlayer, string pack)
    {
        File.WriteAllText(paths.SettingsFile, $$"""
        {
          "schemaVersion": {{SettingsService.CurrentSchemaVersion}},
          "maxMemoryGb": {{memoryGb}},
          "memorySettingIsWholeGame": true,
          "memoryChosenByPlayer": {{(chosenByPlayer ? "true" : "false")}},
          "clientRelativePath": "{{pack}}"
        }
        """);
    }

    private static void WriteVanillaPack(AppPaths paths, string name)
    {
        var pack = Path.Combine(paths.Packs, name);
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, PackManifestService.ManifestFileName), $$"""
        {
          "schemaVersion": {{PackManifestService.CurrentSchemaVersion}},
          "minecraftVersion": "1.21.1",
          "loader": {"type": "vanilla"},
          "clientJar": "client.jar"
        }
        """);
    }

    [Fact]
    public void UnknownKeysFromAnyEra_AreIgnoredRatherThanFatal()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        File.WriteAllText(paths.SettingsFile, """
        {
          "schemaVersion": 2,
          "playerName": "anuvenn",
          "somethingFromAFutureBuild": {"nested": true}
        }
        """);

        var settings = new SettingsService(paths).Load();

        Assert.Equal("anuvenn", settings.PlayerName);
        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);
    }
}
