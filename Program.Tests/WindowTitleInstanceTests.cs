using System.IO;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class WindowTitleInstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "window-title-instance-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreparationAndCleanup_PreserveAuthorAndManualKubeJsTitlesAndPlayerSettings()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = Path.Combine(paths.Packs, "Title fixture");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "client.jar"), "jar");
        File.WriteAllText(Path.Combine(pack, PackManifestService.ManifestFileName),
            """{"schemaVersion":1,"minecraftVersion":"1.21.1","loader":{"type":"vanilla"},"clientJar":"client.jar"}""");
        var authorPath = Path.Combine(pack, "kubejs", "config", "client.json");
        var authorBytes = Utf8Bom("{\r\n  \"window_title\" : \"Author title - Multiplayer\",\r\n  \"other_setting\" : true\r\n}\r\n");
        Write(authorPath, authorBytes);
        using var service = new PackInstanceService(paths, new Logger(paths.LogFile));
        var game = service.GetInstanceDirectory("Title fixture");
        var kubePath = Path.Combine(game, "kubejs", "config", "client.json");
        var optionsPath = Path.Combine(game, "options.txt");
        var optionsBytes = Encoding.UTF8.GetBytes("renderDistance:8\r\nsimulationDistance:6\r\nkey_key.forward:key.keyboard.w\r\n");
        var settingsBytes = Utf8Bom("{ \"playerName\" : \"Fixture\", \"maxMemoryGb\" : 8 }\r\n");
        Write(optionsPath, optionsBytes);
        Write(paths.SettingsFile, settingsBytes);
        var privateBeforePrepare = Path.Combine(game, "config", "private-before-prepare.toml");
        Write(privateBeforePrepare, PrivateToml());

        await service.PrepareAsync("Title fixture");

        // The ordinary initial source mirror is allowed; sanitizing must not
        // rewrite its title or normalize its BOM, whitespace or line endings.
        Assert.Equal(authorBytes, File.ReadAllBytes(authorPath));
        Assert.Equal(authorBytes, File.ReadAllBytes(kubePath));
        Assert.Equal(optionsBytes, File.ReadAllBytes(optionsPath));
        Assert.Equal(settingsBytes, File.ReadAllBytes(paths.SettingsFile));
        Assert.False(File.Exists(privateBeforePrepare));

        var manualBytes = Utf8Bom("{ \"window_title\": \"My manual title\", \"other_setting\": false }\r\n");
        Write(kubePath, manualBytes);
        var kubeModified = File.GetLastWriteTimeUtc(kubePath);
        var optionsModified = File.GetLastWriteTimeUtc(optionsPath);
        var settingsModified = File.GetLastWriteTimeUtc(paths.SettingsFile);
        var privateBeforeCleanup = Path.Combine(game, "config", "private-before-cleanup.toml");
        Write(privateBeforeCleanup, PrivateToml());

        await service.CleanupGeneratedLocalArtifactsAsync("Title fixture", removeSessionLogs: false);
        AssertPreserved();
        Assert.False(File.Exists(privateBeforeCleanup));
        await service.PrepareAsync("Title fixture");
        AssertPreserved();
        Assert.Equal(authorBytes, File.ReadAllBytes(authorPath));

        void AssertPreserved()
        {
            Assert.Equal(manualBytes, File.ReadAllBytes(kubePath));
            Assert.Equal(kubeModified, File.GetLastWriteTimeUtc(kubePath));
            Assert.Equal(optionsBytes, File.ReadAllBytes(optionsPath));
            Assert.Equal(optionsModified, File.GetLastWriteTimeUtc(optionsPath));
            Assert.Equal(settingsBytes, File.ReadAllBytes(paths.SettingsFile));
            Assert.Equal(settingsModified, File.GetLastWriteTimeUtc(paths.SettingsFile));
        }
    }

    private static byte[] PrivateToml() => Encoding.UTF8.GetBytes("serverIP = \"192.0.2.1\"\nserverProxyIP = \"192.0.2.2\"\n");
    private static byte[] Utf8Bom(string text) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
    public void Dispose() => TempTree.Delete(_root);
}
