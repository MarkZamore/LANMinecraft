using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>
/// Settings are re-saved on every load, so the first launch of a new build
/// rewrites the file in its own shape. These tests pin the migration contract:
/// nothing the player configured is lost, and the pre-Steam file survives as a
/// backup so an older launcher can be restored by hand.
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

    [Fact]
    public void LoadingAPreSteamFile_KeepsTheSettingsAndBacksTheFileUpOnce()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var legacy = """
        {
          "playerName": "MarkZamore",
          "maxMemoryGb": 12,
          "clientRelativePath": "Infinity",
          "selectedNetworkInterfaceId": "{144D92B7-CEA3-4EE3-87C9-C8D14EDAD1AB}",
          "selectedNetworkAddress": "10.147.18.145",
          "voicePttMode": "Hold",
          "voiceInputVolume": 2
        }
        """;
        File.WriteAllText(paths.SettingsFile, legacy);

        var service = new SettingsService(paths);
        var settings = service.Load();

        Assert.Equal("MarkZamore", settings.PlayerName);
        Assert.Equal(12, settings.MaxMemoryGb);
        Assert.Equal("Infinity", settings.ClientRelativePath);
        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);

        var backups = Directory.GetFiles(
            Path.Combine(paths.Personal, "Backups"),
            "settings-v1-*.json");
        var backup = Assert.Single(backups);
        Assert.Contains("selectedNetworkAddress", File.ReadAllText(backup), StringComparison.Ordinal);

        // The saved file carries the new version, and a second load neither
        // re-backs-up nor changes anything.
        var saved = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile));
        Assert.Equal(
            SettingsService.CurrentSchemaVersion,
            saved.RootElement.GetProperty("schemaVersion").GetInt32());

        var reloaded = new SettingsService(paths).Load();
        Assert.Equal("MarkZamore", reloaded.PlayerName);
        Assert.Single(Directory.GetFiles(Path.Combine(paths.Personal, "Backups"), "settings-v1-*.json"));
    }

    [Fact]
    public void AFreshInstall_StartsAtTheCurrentSchemaWithoutABackup()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();

        var settings = new SettingsService(paths).Load();

        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);
        var backupDirectory = Path.Combine(paths.Personal, "Backups");
        Assert.True(
            !Directory.Exists(backupDirectory) ||
            Directory.GetFiles(backupDirectory, "settings-v*.json").Length == 0);
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
