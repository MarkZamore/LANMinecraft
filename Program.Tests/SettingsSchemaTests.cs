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
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
        Assert.Equal(12, settings.MaxMemoryGb);
        Assert.Equal("Infinity", settings.ClientRelativePath);
        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);

        var saved = File.ReadAllText(paths.SettingsFile);
        Assert.DoesNotContain("selectedNetworkAddress", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("voicePttMode", saved, StringComparison.Ordinal);
        Assert.Equal(
            SettingsService.CurrentSchemaVersion,
            JsonDocument.Parse(saved).RootElement.GetProperty("schemaVersion").GetInt32());

        var reloaded = new SettingsService(paths).Load();
        Assert.Equal("MarkZamore", reloaded.PlayerName);
        Assert.Equal(12, reloaded.MaxMemoryGb);
    }

    [Fact]
    public void AFreshInstall_StartsAtTheCurrentSchema()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();

        var settings = new SettingsService(paths).Load();

        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);
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
