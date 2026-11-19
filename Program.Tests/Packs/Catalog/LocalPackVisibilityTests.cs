using System.Text;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class LocalPackVisibilityTests : IDisposable
{
    private const string Ll8 = "LL8 Extended";
    private const string SelectedPack = "Minecraft 26.2";
    private const string OtherHidden = "Another Local Pack";
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "local-pack-visibility-" + Guid.NewGuid().ToString("N")));

    public void Dispose() => TempTree.Delete(_paths.Root);

    [Fact]
    public void Release408AutoHiddenInstalledLl8_IsRestoredOnceWithoutTouchingLocalData()
    {
        _paths.Ensure();
        var preservedFiles = InstallLocalLl8();
        WriteRelease408Settings();

        var restored = new SettingsService(_paths).Load();

        Assert.True(restored.Ll8LocalVisibilityMigrated);
        Assert.False(PackMembership.IsHidden(restored, Ll8));
        Assert.True(PackMembership.ShouldListInstalled(restored, Ll8, isCatalogPack: false));
        Assert.True(PackMembership.IsHidden(restored, OtherHidden));
        Assert.Equal(new[] { SelectedPack }, restored.AddedPackRelativePaths);
        Assert.Equal(SelectedPack, restored.ClientRelativePath);
        Assert.Equal("Player", restored.PlayerName);
        Assert.Equal("PreviousPlayer", restored.PreviousPlayerName);
        Assert.Equal("skins/chosen.png", restored.SkinPath);
        Assert.Equal($"{Ll8}/Дом", restored.SelectedWorldRelativePath);
        Assert.Equal(6, restored.MemoryByPack[Ll8]);
        Assert.Equal(MemorySizingService.ClampHeapGb(6), restored.MemoryByPack[SelectedPack]);
        AssertLocalDataUnchanged(preservedFiles);
        AssertRetiredSourceStaysUnavailable();

        var savedBytes = File.ReadAllBytes(_paths.SettingsFile);
        File.SetLastWriteTimeUtc(_paths.SettingsFile, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var savedTime = File.GetLastWriteTimeUtc(_paths.SettingsFile);
        var repeated = new SettingsService(_paths).Load();

        Assert.True(PackMembership.ShouldListInstalled(repeated, Ll8, isCatalogPack: false));
        Assert.True(repeated.Ll8LocalVisibilityMigrated);
        Assert.Equal(savedBytes, File.ReadAllBytes(_paths.SettingsFile));
        Assert.Equal(savedTime, File.GetLastWriteTimeUtc(_paths.SettingsFile));
        AssertLocalDataUnchanged(preservedFiles);
    }

    [Fact]
    public void RemovingRestoredLl8FromList_RemainsHiddenAcrossSubsequentLoads()
    {
        _paths.Ensure();
        var preservedFiles = InstallLocalLl8();
        WriteRelease408Settings();
        var service = new SettingsService(_paths);
        var settings = service.Load();
        Assert.True(PackMembership.ShouldListInstalled(settings, Ll8, isCatalogPack: false));

        Assert.True(MainWindow.TryRemoveBuildFromList(settings, Ll8, BuildRemovalAnswer.ListOnly));
        service.Save(settings);
        for (var load = 0; load < 2; load++)
        {
            settings = new SettingsService(_paths).Load();
            Assert.True(settings.Ll8LocalVisibilityMigrated);
            Assert.True(PackMembership.IsHidden(settings, Ll8));
            Assert.False(PackMembership.ShouldListInstalled(settings, Ll8, isCatalogPack: false));
            Assert.True(PackMembership.IsHidden(settings, OtherHidden));
            Assert.Equal(new[] { SelectedPack }, settings.AddedPackRelativePaths);
        }
        AssertLocalDataUnchanged(preservedFiles);
    }

    [Fact]
    public void MissingLl8IsNotOffered_AndLaterInstallationDoesNotRepeatMigration()
    {
        _paths.Ensure();
        WriteRelease408Settings();
        var service = new SettingsService(_paths);
        var settings = service.Load();

        Assert.True(settings.Ll8LocalVisibilityMigrated);
        Assert.False(PackMembership.Contains(settings, Ll8));
        Assert.False(Directory.Exists(_paths.CombineUnderPacks(Ll8)));
        AssertRetiredSourceStaysUnavailable();

        // Closing this migration is independent of installation. A later
        // intentional list removal must survive even when local files appear.
        PackMembership.Remove(settings, Ll8);
        service.Save(settings);
        var preservedFiles = InstallLocalLl8();
        settings = new SettingsService(_paths).Load();

        Assert.True(PackMembership.IsHidden(settings, Ll8));
        Assert.False(PackMembership.ShouldListInstalled(settings, Ll8, isCatalogPack: false));
        Assert.False(PackMembership.Contains(settings, Ll8));
        AssertLocalDataUnchanged(preservedFiles);
    }

    [Fact]
    public void FreshDefaultsAndNewlySavedSettings_PreserveIntentionalLocalListRemoval()
    {
        _paths.Ensure();
        var service = new SettingsService(_paths);
        var fresh = service.Load();
        Assert.True(fresh.Ll8LocalVisibilityMigrated);
        Assert.False(PackMembership.IsHidden(fresh, Ll8));
        Assert.DoesNotContain(Ll8, fresh.AddedPackRelativePaths!);
        InstallLocalLl8();
        Assert.True(MainWindow.TryRemoveBuildFromList(fresh, Ll8, BuildRemovalAnswer.ListOnly));
        service.Save(fresh);
        Assert.True(PackMembership.IsHidden(service.Load(), Ll8));

        // Save is also used with a newly created settings object. It must
        // record this choice without treating it as a settings-file upgrade.
        var newlyCreated = new AppSettings
        {
            ClientRelativePath = SelectedPack,
            AddedPackRelativePaths = [],
            MemorySettingIsTheHeap = true
        };
        Assert.True(MainWindow.TryRemoveBuildFromList(newlyCreated, Ll8, BuildRemovalAnswer.ListOnly));
        service.Save(newlyCreated);
        var reloaded = service.Load();

        Assert.True(reloaded.Ll8LocalVisibilityMigrated);
        Assert.True(PackMembership.IsHidden(reloaded, Ll8));
        Assert.False(PackMembership.ShouldListInstalled(reloaded, Ll8, isCatalogPack: false));
        Assert.Empty(reloaded.AddedPackRelativePaths!);
    }

    private void WriteRelease408Settings()
    {
        var heap = MemorySizingService.ClampHeapGb(6);
        File.WriteAllBytes(_paths.SettingsFile, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SettingsService.CurrentSchemaVersion,
            playerName = "Player",
            previousPlayerName = "PreviousPlayer",
            maxMemoryGb = heap,
            memorySettingIsTheHeap = true,
            memoryByPack = new Dictionary<string, int> { [SelectedPack] = heap, [Ll8] = 6 },
            clientRelativePath = SelectedPack,
            addedPackRelativePaths = new[] { SelectedPack },
            hiddenPackRelativePaths = new[] { "ll8 extended", OtherHidden },
            skinPath = "skins/chosen.png",
            selectedWorldRelativePath = $"{Ll8}/Дом"
        }));
    }

    private Dictionary<string, byte[]> InstallLocalLl8()
    {
        var pack = _paths.CombineUnderPacks(Ll8);
        var files = new Dictionary<string, byte[]>
        {
            [Path.Combine(pack, PackManifestService.ManifestFileName)] = Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"minecraftVersion\":\"1.21.1\",\"loader\":{\"type\":\"neoforge\",\"version\":\"21.1.248\"}}"),
            [Path.Combine(pack, "config", "keep.toml")] = Encoding.UTF8.GetBytes("personal = true\r\n"),
            [Path.Combine(pack, "mods", "keep.jar")] = [1, 2, 3, 4],
            [Path.Combine(_paths.Worlds, Ll8, "Дом", "level.dat")] = [10, 20, 30, 40],
            [Path.Combine(_paths.Instances, Ll8, "options.txt")] = Encoding.UTF8.GetBytes("key_key.forward:key.keyboard.up\n")
        };
        foreach (var (path, bytes) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        return files;
    }

    private static void AssertLocalDataUnchanged(Dictionary<string, byte[]> files)
    {
        foreach (var (path, bytes) in files)
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private void AssertRetiredSourceStaysUnavailable()
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor(Ll8));
        Assert.DoesNotContain(PackCatalogService.SeedSources(), entry => entry.RelativePath.Equals(Ll8, StringComparison.OrdinalIgnoreCase));
        Assert.Null(new PortablePackSyncService(_paths, new Logger(_paths.LogFile)).TryResolveSource(Ll8));
    }
}
