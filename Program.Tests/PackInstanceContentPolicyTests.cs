using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackInstanceContentPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pack-content-policy-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkKeys_DoNotExcludePackTomlOrDeletePersonalToml(bool managedSnapshot)
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = Path.Combine(paths.Packs, "Content fixture");
        Write(pack, "client.jar", Encoding.UTF8.GetBytes("jar"));
        Write(pack, PackManifestService.ManifestFileName, Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"minecraftVersion":"1.21.1","loader":{"type":"vanilla"},"clientJar":"client.jar"}"""));
        var author = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            "# Author-selected config\r\nserverIP = \"192.0.2.10\"\r\nserverProxyIP = \"192.0.2.11\"\r\notherSetting = true\r\n")).ToArray();
        Write(pack, "config/author.toml", author);
        if (managedSnapshot)
        {
            var entries = new[] { "config/author.toml", PackManifestService.ManifestFileName, "client.jar" }
                .ToDictionary(relative => relative, relative =>
                {
                    var bytes = File.ReadAllBytes(Path.Combine(pack, relative.Replace('/', Path.DirectorySeparatorChar)));
                    return new { sizeBytes = bytes.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
                });
            Write(pack, PortablePackSyncService.SyncStateFileName,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { schemaVersion = 1, revision = "pack-content", files = entries })));
        }
        using var service = new PackInstanceService(paths, new Logger(paths.LogFile));
        var game = service.GetInstanceDirectory("Content fixture");
        var personal = Encoding.UTF8.GetBytes("# Personal config\nserverIP = \"192.0.2.20\"\nserverProxyIP = \"192.0.2.21\"\n");
        const string personalRelative = "config/player/nested/personal.toml";
        Write(game, personalRelative, personal);
        var personalPath = Path.Combine(game, personalRelative.Replace('/', Path.DirectorySeparatorChar));
        var personalStamp = File.GetLastWriteTimeUtc(personalPath);

        await service.PrepareAsync("Content fixture");

        var instanceAuthor = Path.Combine(game, "config", "author.toml");
        Assert.Equal(author, File.ReadAllBytes(instanceAuthor));
        Assert.Equal(author, File.ReadAllBytes(Path.Combine(pack, "config", "author.toml")));
        AssertPersonalUnchanged();
        var edited = Encoding.UTF8.GetBytes("serverIP = \"192.0.2.30\"\nserverProxyIP = \"192.0.2.31\"\nmyPreference = true\n");
        File.WriteAllBytes(instanceAuthor, edited);
        var editedStamp = File.GetLastWriteTimeUtc(instanceAuthor);

        await service.CleanupGeneratedLocalArtifactsAsync("Content fixture", removeSessionLogs: false);
        await service.PrepareAsync("Content fixture");

        Assert.Equal(edited, File.ReadAllBytes(instanceAuthor));
        Assert.Equal(editedStamp, File.GetLastWriteTimeUtc(instanceAuthor));
        AssertPersonalUnchanged();

        void AssertPersonalUnchanged()
        {
            Assert.Equal(personal, File.ReadAllBytes(personalPath));
            Assert.Equal(personalStamp, File.GetLastWriteTimeUtc(personalPath));
        }
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose() => TempTree.Delete(_root);
}
