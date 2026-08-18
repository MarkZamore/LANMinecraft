using System.Text;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The pack ships a key layout; the launcher writes it into a player's
/// options.txt and reports whether it is still there. These pin the format
/// the pack writes, the merge that leaves every other option alone, and the
/// three states the button is built on.
/// </summary>
public sealed class ControlsPresetServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "controls-preset-" + Guid.NewGuid().ToString("N"));
    private readonly string _pack;
    private readonly string _instance;
    private readonly ControlsPresetService _service = new();

    public ControlsPresetServiceTests()
    {
        _pack = Path.Combine(_root, "Packs", "LL8");
        _instance = Path.Combine(_root, "Instances", "LL8");
        Directory.CreateDirectory(_pack);
        Directory.CreateDirectory(_instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Parse_ReadsMappingsAndSkipsComments()
    {
        const string text = """
            # LL8 controls preset
            # --- Minecraft ---
            key_key.forward:key.keyboard.w
            key_key.sneak:key.keyboard.left.shift  # held

            key_gui.xaero_open_map:key.keyboard.m  # Open World Map
            key_key.sfm.manager.text_editor:key.keyboard.e:CONTROL
            key_key.crawl:key.keyboard.unknown  # was c: rare
            key_Speed Module:key.keyboard.unknown
            """;

        var entries = ControlsPresetService.Parse(text);

        Assert.Equal(
            [
                ("key.forward", "key.keyboard.w"),
                ("key.sneak", "key.keyboard.left.shift"),
                ("gui.xaero_open_map", "key.keyboard.m"),
                ("key.sfm.manager.text_editor", "key.keyboard.e:CONTROL"),
                ("key.crawl", "key.keyboard.unknown"),
                ("Speed Module", "key.keyboard.unknown"),
            ],
            entries.Select(entry => (entry.Name, entry.Value)).ToArray());
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("key_key.forward")]
    [InlineData("key_key.forward:")]
    [InlineData("key_:key.keyboard.w")]
    [InlineData("key_key.forward:key.keyboard.w\nkey_key.forward:key.keyboard.a")]
    public void Parse_RejectsWhatIsNotAMapping(string text)
    {
        Assert.Throws<FormatException>(() => ControlsPresetService.Parse(text));
    }

    /// <summary>
    /// The options file is the player's; only the key lines the preset names
    /// change, in place, and the file keeps its own line endings.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Merge_ReplacesOnlyTheNamedKeysAndKeepsTheLineEndings(string newline)
    {
        var options = string.Join(newline,
            "version:3955", "fov:0.0", "key_key.attack:key.mouse.left", "key_key.forward:key.keyboard.w",
            "key_key.crawl:key.keyboard.c", "soundCategory_master:1.0") + newline;

        var (text, changed) = ControlsPresetService.Merge(options,
        [
            new ControlsPresetEntry("key.crawl", "key.keyboard.unknown"),
            new ControlsPresetEntry("key.forward", "key.keyboard.w"),
            new ControlsPresetEntry("gui.xaero_open_map", "key.keyboard.m"),
        ]);

        Assert.Equal(2, changed);
        Assert.Equal(string.Join(newline,
            "version:3955", "fov:0.0", "key_key.attack:key.mouse.left", "key_key.forward:key.keyboard.w",
            "key_key.crawl:key.keyboard.unknown", "key_gui.xaero_open_map:key.keyboard.m",
            "soundCategory_master:1.0") + newline, text);
    }

    [Fact]
    public void Merge_OnNothing_WritesJustTheKeys()
    {
        var (text, changed) = ControlsPresetService.Merge(string.Empty,
            [new ControlsPresetEntry("key.crawl", "key.keyboard.c")]);

        Assert.Equal(1, changed);
        Assert.Equal("key_key.crawl:key.keyboard.c\n", text);
    }

    [Fact]
    public void Evaluate_WithoutAPreset_HasNothingToOffer()
    {
        var status = _service.Evaluate(_pack, _instance);

        Assert.False(status.HasPreset);
        Assert.False(status.IsApplied);
    }

    [Fact]
    public void Evaluate_WithAPresetAndNoOptionsFile_IsNotApplied()
    {
        WritePreset("key_key.crawl:key.keyboard.c\n");

        var status = _service.Evaluate(_pack, _instance);

        Assert.True(status.HasPreset);
        Assert.False(status.IsApplied);
    }

    /// <summary>
    /// Applied means every preset line stands in the options file. One key
    /// changed in game, or one line changed in the pack's preset, and it is
    /// an offer again.
    /// </summary>
    [Fact]
    public void Apply_ThenEvaluate_TracksTheOptionsAndThePreset()
    {
        WritePreset("key_key.crawl:key.keyboard.c\nkey_gui.xaero_open_map:key.keyboard.m\n");
        WriteOptions("fov:0.0\nkey_key.crawl:key.keyboard.x\nkey_key.forward:key.keyboard.w\n");

        var changed = _service.Apply(_pack, _instance);
        Assert.Equal(2, changed);
        Assert.True(_service.Evaluate(_pack, _instance).IsApplied);
        Assert.Equal(
            "fov:0.0\nkey_key.crawl:key.keyboard.c\nkey_key.forward:key.keyboard.w\nkey_gui.xaero_open_map:key.keyboard.m\n",
            ReadOptions());

        // The player rebinds crawl in game.
        WriteOptions(ReadOptions().Replace("key_key.crawl:key.keyboard.c", "key_key.crawl:key.keyboard.v"));
        Assert.False(_service.Evaluate(_pack, _instance).IsApplied);

        // Applying again is not destructive and puts it back.
        Assert.Equal(1, _service.Apply(_pack, _instance));
        Assert.True(_service.Evaluate(_pack, _instance).IsApplied);

        // The pack ships a new layout.
        WritePreset("key_key.crawl:key.keyboard.c\nkey_gui.xaero_open_map:key.keyboard.j\n");
        Assert.False(_service.Evaluate(_pack, _instance).IsApplied);
    }

    /// <summary>
    /// The game writes down every mapping it registers whenever it saves its
    /// options, so a preset line that file never mentions belongs to no mod in
    /// the build - a mod update dropped the mapping, or it only existed in a
    /// development build. Such a line cannot be applied by anyone: the launcher
    /// writes it, the game drops it again on exit. It must not light the button,
    /// or the button is lit after every session for ever.
    /// </summary>
    [Fact]
    public void Evaluate_AMappingThisBuildDoesNotHave_IsNotHeldAgainstThePreset()
    {
        WritePreset(
            "key_key.crawl:key.keyboard.c\n" +
            "key_key.cobblemon.printmodelsettings:key.keyboard.unknown\n");
        WriteOptions("fov:0.0\nkey_key.crawl:key.keyboard.c\nkey_key.forward:key.keyboard.w\n");

        var status = _service.Evaluate(_pack, _instance);

        Assert.True(status.IsApplied);
        Assert.Null(status.FirstDifference);

        // A mapping the build does have is still compared, absence and all.
        WriteOptions("fov:0.0\nkey_key.crawl:key.keyboard.v\nkey_key.forward:key.keyboard.w\n");
        Assert.Equal("key.crawl", _service.Evaluate(_pack, _instance).FirstDifference);
    }

    /// <summary>
    /// An options file the game has never written says nothing about which
    /// mappings exist, so there every line of the preset still counts.
    /// </summary>
    [Fact]
    public void Evaluate_AnOptionsFileWithoutAnyMappings_StillOffersThePreset()
    {
        WritePreset("key_key.crawl:key.keyboard.c\n");
        WriteOptions("fov:0.0\nguiScale:3\n");

        var status = _service.Evaluate(_pack, _instance);

        Assert.False(status.IsApplied);
        Assert.Equal("key.crawl", status.FirstDifference);
    }

    /// <summary>
    /// A never-launched instance has no options.txt. The preset must not become
    /// the whole file: the pack's first-launch options come first, exactly as
    /// the ConfiguredDefaults mod would have copied them, then the keys.
    /// </summary>
    [Fact]
    public void Apply_BeforeTheFirstLaunch_StartsFromThePackDefaults()
    {
        WritePreset("key_key.crawl:key.keyboard.c\n");
        var defaults = ControlsPresetService.GetPackDefaultOptionsPath(_pack);
        Directory.CreateDirectory(Path.GetDirectoryName(defaults)!);
        File.WriteAllText(defaults, "fov:0.5\nguiScale:3\nkey_key.crawl:key.keyboard.x\nkey_key.jump:key.keyboard.space\n");

        _service.Apply(_pack, _instance);

        Assert.Equal("fov:0.5\nguiScale:3\nkey_key.crawl:key.keyboard.c\nkey_key.jump:key.keyboard.space\n", ReadOptions());
        Assert.True(_service.Evaluate(_pack, _instance).IsApplied);
    }

    [Fact]
    public void Apply_WritesUtf8WithoutAByteOrderMark()
    {
        WritePreset("key_key.crawl:key.keyboard.c\n");
        WriteOptions("lang:ru_ru\nkey_key.crawl:key.keyboard.x\n");

        _service.Apply(_pack, _instance);

        var bytes = File.ReadAllBytes(Path.Combine(_instance, ControlsPresetService.OptionsFileName));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "a BOM would confuse the game");
    }

    /// <summary>A preset the parser cannot read is, for the button, no preset.</summary>
    [Fact]
    public void ABrokenPreset_CountsAsNone()
    {
        WritePreset("key_key.crawl:key.keyboard.c\nthis is not a mapping\n");

        Assert.Null(_service.TryLoad(_pack));
        Assert.False(_service.Evaluate(_pack, _instance).HasPreset);
    }

    /// <summary>
    /// The real preset the pack ships, if the pack repository is checked out
    /// beside this one: it parses, and it is what check_preset.py approved.
    /// </summary>
    [Fact]
    public void ThePackPreset_ParsesWhenTheRepositoryIsAvailable()
    {
        var packRepo = Path.GetFullPath(Path.Combine(FindRepositoryRoot(), "..", "LL8"));
        var preset = Path.Combine(packRepo, "launcher", "controls-preset.txt");
        if (!File.Exists(preset)) return;

        var entries = ControlsPresetService.Parse(File.ReadAllText(preset, Encoding.UTF8));

        Assert.True(entries.Count >= 600, $"only {entries.Count} mappings");
        Assert.Contains(entries, entry => entry.Name == "key.forward" && entry.Value == "key.keyboard.w");
        Assert.All(entries, entry => Assert.DoesNotContain(':', entry.Name));
    }

    private void WritePreset(string text)
    {
        var path = ControlsPresetService.GetPresetPath(_pack);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private void WriteOptions(string text) =>
        File.WriteAllText(Path.Combine(_instance, ControlsPresetService.OptionsFileName), text, new UTF8Encoding(false));

    private string ReadOptions() =>
        File.ReadAllText(Path.Combine(_instance, ControlsPresetService.OptionsFileName), Encoding.UTF8);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Program", "Minecraft.csproj"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
