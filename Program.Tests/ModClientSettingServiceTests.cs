using System.IO;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The pack can ask for one key of a mod's client config, and the launcher
/// writes it into the instance once. These pin the two things that make it
/// safe: only that line changes, and it happens exactly once.
/// </summary>
public sealed class ModClientSettingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mod-client-setting-" + Guid.NewGuid().ToString("N"));

    private readonly string _pack;
    private readonly string _instance;
    private readonly ModClientSetting _setting = ModClientSettingService.YsmLoadingBanner;

    public ModClientSettingServiceTests()
    {
        _pack = Path.Combine(_root, "pack");
        _instance = Path.Combine(_root, "instance");
        Directory.CreateDirectory(Path.Combine(_pack, PackInstanceService.LauncherDataRoot));
        Directory.CreateDirectory(Path.Combine(_instance, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void WriteToken(string text) => File.WriteAllText(
        Path.Combine(_pack, PackInstanceService.LauncherDataRoot, _setting.TokenFileName),
        text,
        new UTF8Encoding(false));

    private string ConfigPath => Path.Combine(
        _instance,
        _setting.ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteConfig(string text) =>
        File.WriteAllText(ConfigPath, text, new UTF8Encoding(false));

    private const string Config =
        "[general]\r\n\t#Whether to display disclaimer GUI\r\n\tDisclaimerShow = true\r\n\r\n" +
        "[loading_state_screen]\r\n\t#Whether to disable loading state screen\r\n" +
        "\tDisableLoadingStateScreen = false\r\n\tLoadingStatePosition = \"TOP_CENTER\"\r\n";

    [Fact]
    public void TheAskedKey_IsSet_AndNothingElseMoves()
    {
        WriteToken("2026-08-23: off");
        WriteConfig(Config);

        Assert.True(new ModClientSettingService().Apply(_pack, _instance, _setting));

        var written = File.ReadAllText(ConfigPath, new UTF8Encoding(false));
        Assert.Contains("\tDisableLoadingStateScreen = true", written, StringComparison.Ordinal);
        Assert.DoesNotContain("DisableLoadingStateScreen = false", written, StringComparison.Ordinal);
        // Every other line, its indentation and the line endings survive.
        Assert.Contains("\tDisclaimerShow = true", written, StringComparison.Ordinal);
        Assert.Contains("\tLoadingStatePosition = \"TOP_CENTER\"", written, StringComparison.Ordinal);
        Assert.Contains("\r\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ItRunsOnce_AndThenTheValueIsThePlayersAgain()
    {
        WriteToken("2026-08-23: off");
        WriteConfig(Config);
        Assert.True(new ModClientSettingService().Apply(_pack, _instance, _setting));

        // The player turns it back on; the launcher does not argue.
        WriteConfig(Config);
        Assert.False(new ModClientSettingService().Apply(_pack, _instance, _setting));
        Assert.Contains(
            "DisableLoadingStateScreen = false",
            File.ReadAllText(ConfigPath, new UTF8Encoding(false)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ANewAsk_RunsAgain()
    {
        WriteToken("2026-08-23: off");
        WriteConfig(Config);
        Assert.True(new ModClientSettingService().Apply(_pack, _instance, _setting));
        WriteConfig(Config);

        WriteToken("2026-09-01: off, once more");
        Assert.True(new ModClientSettingService().Apply(_pack, _instance, _setting));
    }

    [Fact]
    public void NoConfigYet_LeavesTheAskOpen()
    {
        WriteToken("2026-08-23: off");

        Assert.False(new ModClientSettingService().Apply(_pack, _instance, _setting));
        Assert.True(ModClientSettingService.NeedsApplying(_pack, _instance, _setting));

        // The mod writes its config on the first launch; the next one lands.
        WriteConfig(Config);
        Assert.True(new ModClientSettingService().Apply(_pack, _instance, _setting));
    }

    [Fact]
    public void NoToken_NothingHappens()
    {
        WriteConfig(Config);
        Assert.False(new ModClientSettingService().Apply(_pack, _instance, _setting));
        Assert.Contains(
            "DisableLoadingStateScreen = false",
            File.ReadAllText(ConfigPath, new UTF8Encoding(false)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyTheModHasRenamed_IsLeftAlone()
    {
        WriteToken("2026-08-23: off");
        WriteConfig("[loading_state_screen]\r\n\tSomethingElse = false\r\n");

        Assert.False(new ModClientSettingService().Apply(_pack, _instance, _setting));
        // No marker either: a later build of the mod may bring the key back.
        Assert.True(ModClientSettingService.NeedsApplying(_pack, _instance, _setting));
    }

    [Theory]
    [InlineData("\tKey = false\n", "\tKey = true\n")]
    [InlineData("Key=false", "Key = true")]
    [InlineData("  Key   =   false  ", "  Key = true")]
    public void SetKey_KeepsTheIndentAndTheLineEndings(string line, string expected)
    {
        var (text, found) = ModClientSettingService.SetKey(line, "Key", "true");
        Assert.True(found);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void SetKey_SkipsACommentedLine()
    {
        var (text, found) = ModClientSettingService.SetKey(
            "\t#Key = false\n\tKey = false\n",
            "Key",
            "true");
        Assert.True(found);
        Assert.Equal("\t#Key = false\n\tKey = true\n", text);
    }
}
