using System.IO;
using System.IO.Compression;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A pack built for a weak machine arriving already set for one.
///
/// Mods and their configs a pack can ship; render distance it cannot, because
/// that lives in options.txt, which belongs to the launcher and to the player.
/// So a pack meant for eight gigabytes of laptop used to arrive at the vanilla
/// twelve chunks and fancy graphics, and the player found out by watching it
/// stutter.
/// </summary>
public sealed class OptionsDefaultsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-options-defaults-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    private (string Pack, string Instance) Make(string? list)
    {
        var pack = Path.Combine(_root, "pack");
        var instance = Path.Combine(_root, "instance");
        Directory.CreateDirectory(instance);
        Directory.CreateDirectory(Path.Combine(pack, "launcher"));
        if (list is not null)
        {
            File.WriteAllText(Path.Combine(pack, "launcher", "options-default.txt"), list);
        }
        return (pack, instance);
    }

    private static Dictionary<string, string> ReadOptions(string instance)
    {
        var path = Path.Combine(instance, "options.txt");
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return read;
        foreach (var line in File.ReadAllLines(path))
        {
            var at = line.IndexOf(':');
            if (at > 0) read[line[..at]] = line[(at + 1)..];
        }
        return read;
    }

    /// <summary>An instance that has never run gets the pack's settings.</summary>
    [Fact]
    public void AFreshInstance_StartsWithWhatThePackAsksFor()
    {
        var (pack, instance) = Make("""
        # What a laptop of eight gigabytes can draw.
        renderDistance:6
        simulationDistance:5
        graphicsMode:0
        ao:false
        """);

        var added = new OptionsDefaultsService().Apply(pack, instance, "1.21.1");

        Assert.Equal(5, added);
        var options = ReadOptions(instance);
        Assert.Equal("ru_ru", options["lang"]);
        Assert.Equal("6", options["renderDistance"]);
        Assert.Equal("5", options["simulationDistance"]);
        Assert.Equal("0", options["graphicsMode"]);
        Assert.Equal("false", options["ao"]);
    }

    /// <summary>
    /// And what somebody set is never touched - not on the next launch, not
    /// ever. This is the whole safety of the thing: the game writes back every
    /// option it knows the first time it saves, so from then on every key is
    /// present and nothing here can change one.
    /// </summary>
    [Fact]
    public void WhatThePlayerSet_IsNeverOverwritten()
    {
        var (pack, instance) = Make("renderDistance:6\nsimulationDistance:5\n");
        File.WriteAllText(
            Path.Combine(instance, "options.txt"),
            "lang:de_de\nrenderDistance:16\nfov:0.75\n");

        var added = new OptionsDefaultsService().Apply(pack, instance, "1.21.1");

        Assert.Equal(1, added);
        var options = ReadOptions(instance);
        Assert.Equal("de_de", options["lang"]);
        Assert.Equal("16", options["renderDistance"]);
        Assert.Equal("5", options["simulationDistance"]);
        Assert.Equal("0.75", options["fov"]);

        // A second launch changes nothing at all.
        Assert.Equal(0, new OptionsDefaultsService().Apply(pack, instance, "1.21.1"));
        Assert.Equal("16", ReadOptions(instance)["renderDistance"]);
    }

    [Theory]
    [InlineData("1.6.4", "ru_RU")]
    [InlineData("1.10.2", "ru_RU")]
    [InlineData("1.12.2", "ru_ru")]
    [InlineData("26.3", "ru_ru")]
    [InlineData("27.1-pre1", "ru_ru")]
    public void ANewPackWithoutADefaultsList_StartsInRussian(string version, string language)
    {
        var (pack, instance) = Make(null);
        Assert.Equal(1, new OptionsDefaultsService().Apply(pack, instance, version));
        Assert.Equal(["lang:" + language], File.ReadAllLines(Path.Combine(instance, "options.txt")));
    }

    [Theory]
    [InlineData("config/defaultoptions", "1.12.2", "1343", false)]
    [InlineData("config/defaultoptions", "1.21.1", "3955", false)]
    [InlineData("config/yosbr", "1.21.1", "3955", false)]
    [InlineData("configureddefaults", "1.20.1", "3465", false)]
    [InlineData("configureddefaults", "26.3", "5023", true)]
    public void AnAuthoredSeedKeepsItsSettingsAndThePlayerKeepsTheirLaterLanguage(
        string folder, string minecraftVersion, string optionsVersion, bool native)
    {
        var (pack, instance) = Make("!lang:en_us\n");
        var optionsFolder = Path.Combine(instance, folder);
        Directory.CreateDirectory(optionsFolder);
        const string key = "key_key.sneak:42";
        var seed = $"version:{optionsVersion}\nlang:en_us\nrenderDistance:19\n{key}\n";
        var seedPath = Path.Combine(optionsFolder, "options.txt");
        var personal = Path.Combine(instance, "options.txt");
        File.WriteAllText(seedPath, seed);

        var service = new OptionsDefaultsService();
        Assert.Equal(1, service.Apply(pack, instance, minecraftVersion, native));
        Assert.Equal("ru_ru", ReadOptions(instance)["lang"]);
        Assert.Equal(optionsVersion, ReadOptions(instance)["version"]);
        Assert.Equal("19", ReadOptions(instance)["renderDistance"]);
        Assert.Equal(folder != "config/defaultoptions", File.ReadAllLines(personal).Contains(key));
        Assert.Equal(seed, File.ReadAllText(seedPath));

        var changed = File.ReadAllText(personal).Replace("lang:ru_ru", "lang:fr_fr", StringComparison.Ordinal);
        File.WriteAllText(personal, changed);
        Assert.Equal(0, service.Apply(pack, instance, minecraftVersion, native));
        Assert.Equal(changed, File.ReadAllText(personal));
    }

    [Fact]
    public void APreparedClientSuppliesTheDataVersionForFreshOptions()
    {
        var (pack, instance) = Make(null);
        var jar = Path.Combine(_root, "client.jar");
        using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("version.json").Open()))
            writer.Write("""{"id":"1.21.1","world_version":3955}""");

        var service = new OptionsDefaultsService();
        Assert.Equal(1, service.Apply(pack, instance, "1.21.1", clientJar: jar));
        var options = Path.Combine(instance, "options.txt");
        Assert.Equal(["version:3955", "lang:ru_ru"], File.ReadAllLines(options));

        const string migrated = "version:3700\r\nlang:uk_ua\r\nrenderDistance:24\r\n";
        File.WriteAllText(options, migrated);
        Assert.Equal(0, service.Apply(pack, instance, "1.21.1", clientJar: jar));
        Assert.Equal(migrated, File.ReadAllText(options));
    }

    [Theory]
    [InlineData("configureddefaults")]
    [InlineData("config/yosbr")]
    public void ModernPackKeepsItsResourcePackDefaultsWhenRussianIsSeeded(string folder)
    {
        var (pack, instance) = Make(null);
        var defaults = Directory.CreateDirectory(Path.Combine(instance, folder)).FullName;
        File.WriteAllText(Path.Combine(defaults, "options.txt"), "resourcePacks:[\"vanilla\",\"file/Author.zip\"]\n");

        Assert.Equal(1, new OptionsDefaultsService().Apply(pack, instance, "1.21.1"));

        Assert.Equal("ru_ru", ReadOptions(instance)["lang"]);
        Assert.Equal("[\"vanilla\",\"file/Author.zip\"]", ReadOptions(instance)["resourcePacks"]);
    }

    /// <summary>
    /// Comments, blank lines and malformed entries are skipped rather than
    /// written, and a key named twice is a mistake in the pack rather than an
    /// instruction to write it twice.
    /// </summary>
    [Fact]
    public void OnlyRealSettingsAreWritten()
    {
        var wanted = OptionsDefaultsService.Parse("""

        # a comment
        renderDistance:6
        nonsense
        :novalue
        emptyvalue:
        renderDistance:12
        graphicsMode:0
        """);

        Assert.Equal(
            [
                new OptionsDefaultsService.OptionDefault("renderDistance", "6", Held: false),
                new OptionsDefaultsService.OptionDefault("graphicsMode", "0", Held: false)
            ],
            wanted);
    }

    /// <summary>
    /// A line marked with <c>!</c> is the pack's rather than the player's: it is
    /// put back at every launch, where an unmarked one is only ever a starting
    /// point. This is for the settings a build depends on looking the same for
    /// everybody, and it is why the mark exists at all.
    /// </summary>
    [Fact]
    public void ASettingThePackKeeps_IsPutBackAfterItIsChanged()
    {
        var pack = Path.Combine(_root, "pack");
        var instance = Path.Combine(_root, "instance");
        Directory.CreateDirectory(Path.Combine(pack, "launcher"));
        Directory.CreateDirectory(instance);
        File.WriteAllText(
            Path.Combine(pack, "launcher", OptionsDefaultsService.ListFileName),
            "!textBackgroundOpacity:0.0\nrenderDistance:8\n");
        // The player has been playing: every key exists, and they moved both.
        File.WriteAllText(
            Path.Combine(instance, "options.txt"),
            "fov:0.5\ntextBackgroundOpacity:0.7\nrenderDistance:16\nguiScale:2\nlang:en_us\n");

        var changed = new OptionsDefaultsService().Apply(pack, instance, "1.21.1");

        Assert.Equal(1, changed);
        var lines = File.ReadAllLines(Path.Combine(instance, "options.txt"));
        Assert.Equal(
            ["fov:0.5", "textBackgroundOpacity:0.0", "renderDistance:16", "guiScale:2", "lang:en_us"],
            lines);

        // And it stays put: a second launch has nothing left to do.
        Assert.Equal(0, new OptionsDefaultsService().Apply(pack, instance, "1.21.1"));
    }

    /// <summary>
    /// The mark says nothing about whether the key is there yet. A held setting
    /// an instance has never had is simply written, like any other.
    /// </summary>
    [Fact]
    public void ASettingThePackKeeps_IsWrittenWhenItIsMissing()
    {
        var pack = Path.Combine(_root, "pack");
        var instance = Path.Combine(_root, "instance");
        Directory.CreateDirectory(Path.Combine(pack, "launcher"));
        Directory.CreateDirectory(instance);
        File.WriteAllText(
            Path.Combine(pack, "launcher", OptionsDefaultsService.ListFileName),
            "!backgroundForChatOnly:false\n");

        Assert.Equal(2, new OptionsDefaultsService().Apply(pack, instance, "1.21.1"));
        Assert.Equal(
            ["lang:ru_ru", "backgroundForChatOnly:false"],
            File.ReadAllLines(Path.Combine(instance, "options.txt")));
    }
}
