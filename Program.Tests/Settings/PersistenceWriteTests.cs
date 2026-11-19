using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PersistenceWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "minecraft-persistence-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Earlier = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void Saving_identical_text_keeps_the_file_and_its_timestamp()
    {
        var path = Path.Combine(_root, "settings.json");
        Assert.True(AtomicFile.WriteAllTextIfChanged(path, "{\"name\":\"Игрок\"}"));
        File.SetLastWriteTimeUtc(path, Earlier);
        var originalTime = File.GetLastWriteTimeUtc(path);

        Assert.False(AtomicFile.WriteAllTextIfChanged(path, "{\"name\":\"Игрок\"}"));

        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(new UTF8Encoding(false).GetBytes("{\"name\":\"Игрок\"}"), File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void An_explicit_encoding_keeps_the_atomic_writers_existing_byte_format()
    {
        var path = Path.Combine(_root, "unicode.txt");
        AtomicFile.WriteAllText(path, "Игрок", Encoding.Unicode);
        File.SetLastWriteTimeUtc(path, Earlier);
        var originalTime = File.GetLastWriteTimeUtc(path);

        Assert.False(AtomicFile.WriteAllTextIfChanged(path, "Игрок", Encoding.Unicode));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(path));
        Assert.True(AtomicFile.WriteAllTextIfChanged(path, "Другой игрок", Encoding.Unicode));
        Assert.Equal(Encoding.Unicode.GetBytes("Другой игрок"), File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Repeated_settings_loads_and_saves_do_not_rewrite_current_settings()
    {
        var paths = new AppPaths(_root);
        var service = new SettingsService(paths);
        var settings = service.Load();
        Assert.True(settings.MemorySettingIsTheHeap);
        service.Save(settings);
        using (var initial = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile)))
        {
            Assert.True(initial.RootElement.GetProperty("memorySettingIsTheHeap").GetBoolean());
        }
        File.SetLastWriteTimeUtc(paths.SettingsFile, Earlier);
        var originalTime = File.GetLastWriteTimeUtc(paths.SettingsFile);

        service.Save(settings);
        var loaded = service.Load();

        Assert.Equal(settings.PlayerName, loaded.PlayerName);
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(paths.SettingsFile));
        Assert.Empty(Directory.EnumerateFiles(paths.Personal, "*.tmp"));
    }

    [Fact]
    public void An_actual_settings_change_is_saved()
    {
        var paths = new AppPaths(_root);
        var service = new SettingsService(paths);
        var settings = service.Load();
        settings.PlayerName = "ChangedPlayer";

        service.Save(settings);

        using var document = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile));
        Assert.Equal("ChangedPlayer", document.RootElement.GetProperty("playerName").GetString());
        Assert.Empty(Directory.EnumerateFiles(paths.Personal, "*.tmp"));
    }

    [Fact]
    public async Task Recomputing_an_identical_pack_hash_keeps_the_cache_timestamp_but_changes_are_saved()
    {
        var paths = new AppPaths(Path.Combine(_root, "Local", "Portable"));
        var packDirectory = paths.CombineUnderPacks("TestPack");
        var configDirectory = Path.Combine(packDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, "settings.txt");
        File.WriteAllText(configPath, "initial");
        var localDirectory = Path.Combine(configDirectory, "local");
        Directory.CreateDirectory(localDirectory);
        var localPath = Path.Combine(localDirectory, "cache.txt");
        File.WriteAllText(localPath, "initial local cache");
        string originalHash;
        using (var initial = new PackHashService(paths))
            originalHash = await initial.CalculateAsync(packDirectory);
        var originalBytes = File.ReadAllBytes(paths.PackHashesFile);
        File.SetLastWriteTimeUtc(paths.PackHashesFile, Earlier);
        var originalTime = File.GetLastWriteTimeUtc(paths.PackHashesFile);

        // A fresh service also covers the next launch reading the existing cache.
        using (var repeated = new PackHashService(paths))
            Assert.Equal(originalHash, await repeated.CalculateAsync(packDirectory));

        Assert.Equal(originalBytes, File.ReadAllBytes(paths.PackHashesFile));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(paths.PackHashesFile));
        Assert.Empty(Directory.EnumerateFiles(paths.Personal, "*.tmp"));

        // Only a local directory inside the pack is excluded, not a parent of it.
        File.WriteAllText(localPath, "changed local cache");
        using (var ignoredChange = new PackHashService(paths))
            Assert.Equal(originalHash, await ignoredChange.CalculateAsync(packDirectory));
        Assert.Equal(originalBytes, File.ReadAllBytes(paths.PackHashesFile));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(paths.PackHashesFile));

        File.WriteAllText(configPath, "changed content");
        string changedHash;
        using (var changed = new PackHashService(paths))
            changedHash = await changed.CalculateAsync(packDirectory);

        Assert.NotEqual(originalHash, changedHash);
        Assert.NotEqual(originalTime, File.GetLastWriteTimeUtc(paths.PackHashesFile));
        using var document = JsonDocument.Parse(File.ReadAllText(paths.PackHashesFile));
        Assert.Equal(changedHash, document.RootElement.GetProperty("packs").GetProperty("testpack").GetProperty("hash").GetString());
        Assert.Empty(Directory.EnumerateFiles(paths.Personal, "*.tmp"));
    }
}
