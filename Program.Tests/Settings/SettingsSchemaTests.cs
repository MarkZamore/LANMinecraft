using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>
/// Settings are normalised on every load, so the first launch of a new build
/// can rewrite the file in its own shape. What is pinned here is that this never
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
          "clientRelativePath": "Some Build"
        }
        """);

        var settings = new SettingsService(paths).Load();

        // 128 is the largest number the type allows and no machine's share of
        // itself, so whatever this one is, the answer is that share.
        Assert.Equal(MemorySizingService.GetAllowedHeapGb(), settings.MaxHeapGb);
        Assert.Equal(settings.MaxHeapGb, MemorySizingService.ClampHeapGb(settings.MaxHeapGb));
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
          "clientRelativePath": "Some Build",
          "selectedNetworkInterfaceId": "{144D92B7-CEA3-4EE3-87C9-C8D14EDAD1AB}",
          "selectedNetworkAddress": "10.147.18.145",
          "voicePttMode": "Hold",
          "voiceInputVolume": 2
        }
        """);

        var settings = new SettingsService(paths).Load();

        Assert.Equal("MarkZamore", settings.PlayerName);
        // 12 was written before either flag existed, which means it was already
        // a heap: the number is taken as it stands and only held to what this
        // machine will leave a pack it cannot see. A sixteen gigabyte runner
        // cannot offer what a thirty-two gigabyte desktop can, and the number
        // has to be right on both.
        var unseen = PackMemoryProfile.Unknown;
        var carried = MemorySizingService.ClampHeapGb(12);
        Assert.Equal(carried, settings.MaxHeapGb);
        Assert.True(settings.MemorySettingIsTheHeap);
        Assert.Equal("Some Build", settings.ClientRelativePath);
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
        Assert.Equal(settings.MaxHeapGb, reloaded.MaxHeapGb);
    }

    [Fact]
    public void AFreshInstall_StartsAtTheCurrentSchema()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();

        var settings = new SettingsService(paths).Load();

        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.MemoryIsGlobal);
        Assert.Equal(MemorySizingService.GetDefaultHeapGb(), settings.MaxHeapGb);
    }

    /// <summary>
    /// Existing automatic values are replaced when switching to the shared setting.
    /// </summary>
    [Fact]
    public void AnOldAutomaticValue_MigratesToTheHardwareDefault()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        WriteVanillaPack(paths, "Vanilla");
        WriteSettings(paths, memoryGb: 20, chosenByPlayer: false, pack: "Vanilla");

        var settings = new SettingsService(paths).Load();

        Assert.Equal(MemorySizingService.GetDefaultHeapGb(), settings.MaxHeapGb);
        Assert.True(settings.MemoryIsGlobal);
    }

    /// <summary>
    /// And a number the player typed is left where they put it - as far as the
    /// machine reading it can go.
    /// </summary>
    /// <remarks>
    /// The ceiling is why this is not simply "left alone": the file is held to
    /// the same limit as the box in the window, so a number larger than the
    /// machine's share of itself comes back smaller. Twenty survives on a
    /// thirty-two gigabyte machine and becomes twelve on a sixteen gigabyte
    /// one, and this test used to assert the first of those - which passed
    /// where it was written and failed on every build runner.
    /// </remarks>
    [Fact]
    public void ANumberThePlayerChose_IsLeftAloneWithinWhatTheMachineAllows()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        WriteVanillaPack(paths, "Vanilla");
        WriteSettings(paths, memoryGb: 20, chosenByPlayer: true, pack: "Vanilla");

        var settings = new SettingsService(paths).Load();

        // 20 was written while the number meant the whole of the game, so what
        // survives is the heap those 20 were already leaving this pack - one
        // gigabyte less for a vanilla client - and then only as much of it as
        // the machine reading the file will allow.
        var vanillaPack = PackMemoryProfile.Measure(Path.Combine(paths.Packs, "Vanilla"));
        var video = VideoMemoryProfile.Measure();
        var kept = MemorySizingService.ClampHeapGb(
            MemorySizingService.GetHeapForBudgetGb(vanillaPack, 20, video));
        Assert.Equal(kept, settings.MaxHeapGb);
        Assert.True(settings.MemoryIsGlobal);
        Assert.Equal(kept, new SettingsService(paths).Load().MaxHeapGb);
    }

    /// <summary>
    /// The first load prefers the selected pack's old edit.
    /// Future pack selections keep the new shared value.
    /// </summary>
    [Fact]
    public void SelectedManualHeapMigratesAndThenAppliesAcrossPacks()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        WriteVanillaPack(paths, "Vanilla");
        WriteVanillaPack(paths, "Heavy");
        File.WriteAllText(paths.SettingsFile, """
        {
          "maxMemoryGb": 12,
          "memorySettingIsTheHeap": true,
          "clientRelativePath": "Vanilla",
          "memoryByPack": { "vanilla": 3, "Heavy": 12 }
        }
        """);

        var service = new SettingsService(paths);
        var settings = service.Load();

        var migrated = MemorySizingService.ClampHeapGb(3);
        Assert.Equal(migrated, settings.MaxHeapGb);
        settings.ClientRelativePath = "Heavy";
        service.MeasurePack(settings.ClientRelativePath);
        service.Save(settings);
        var reloaded = new SettingsService(paths).Load();

        Assert.Equal("Heavy", reloaded.ClientRelativePath);
        Assert.Equal(migrated, reloaded.MaxHeapGb);
        Assert.Equal(12, reloaded.MemoryByPack["Heavy"]);
    }

    /// <summary>
    /// The number is written down on the edit, not on the launch: a pack chosen
    /// for, thought better of, and never started still answers with it.
    /// </summary>
    [Fact]
    public void ANumberChosenWithoutEverPlaying_IsStillThereNextTime()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        WriteVanillaPack(paths, "Vanilla");
        WriteSettings(paths, memoryGb: 20, chosenByPlayer: false, pack: "Vanilla");

        var service = new SettingsService(paths);
        var settings = service.Load();
        const int chosen = 3;
        settings.MaxHeapGb = chosen;
        service.Save(settings);

        Assert.Equal(MemorySizingService.ClampHeapGb(chosen), new SettingsService(paths).Load().MaxHeapGb);
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
