using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Minecraft.Tests;

public sealed class PackArchiveTests : IDisposable
{
    private const string PackName = "Transfer Test";
    private const string Manifest = """{"schemaVersion":1,"minecraftVersion":"1.21.1","loader":{"type":"fabric","version":"0.16.14"}}""";
    private readonly AppPaths _sender = TestPaths();
    private readonly AppPaths _receiver = TestPaths();

    [Fact]
    public async Task PersonalArchiveKeepsNetworkHashButLeavesWorldsAndPlayerFilesOnSender()
    {
        _sender.Ensure();
        _receiver.Ensure();
        var source = _sender.CombineUnderPacks(PackName);
        var archivePath = Path.Combine(_sender.Launcher, "test" + PackArchiveService.Extension);
        Put(source, "mods/test.jar", "mod payload");
        Put(source, "config/gameplay.txt", "pack setting");
        Put(source, "config/jei/world/server/profile.ini", "private data");
        Put(source, "saves/Private World/level.dat", "private world");
        Put(source, "servers.dat", "private addresses");
        Put(source, PortablePackSyncService.SourceMarkerFileName, "private source marker");
        Put(_sender.Worlds, "Private World/level.dat", "my progress");
        Put(_sender.CombineUnderInstances(PackName), "options.txt", "my controls");
        var creator = new PackArchiveService(_sender, _ => true, _ => false, prepareDescriptor: (path, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(PackName, path);
            Put(source, PackManifestService.ManifestFileName, Manifest);
            return Task.CompletedTask;
        });
        await creator.ExportAsync(PackName, archivePath, null, CancellationToken.None);
        Assert.Equal(PackName, (await creator.InspectAsync(archivePath, CancellationToken.None)).PackName);

        Put(_receiver.CombineUnderPacks(PackName), "mods/another.jar", "must not change");
        Put(WorldLocations.ForBuild(_receiver.Worlds, PackName + " (2)"), "Old World/level.dat", "another player's world");
        Put(_receiver.Worlds, "Loose World/level.dat", "legacy placement");
        Put(_receiver.Worlds, "Loose World/" + WorldMetadataService.MetadataFileName,
            """{"buildRelativePath":"Transfer Test (3)"}""");
        var installer = Service(_receiver);
        var installedName = await installer.ImportAsync(archivePath, null, CancellationToken.None, PackName);
        Assert.Equal(PackName + " (4)", installedName);
        var result = _receiver.CombineUnderPacks(installedName);
        Assert.Equal("mod payload", File.ReadAllText(Path.Combine(result, "mods", "test.jar")));
        Assert.Equal("pack setting", File.ReadAllText(Path.Combine(result, "config", "gameplay.txt")));
        Assert.False(Directory.Exists(Path.Combine(result, "saves")));
        Assert.False(Directory.Exists(Path.Combine(result, "config", "jei")));
        Assert.False(File.Exists(Path.Combine(result, "servers.dat")));
        Assert.False(File.Exists(Path.Combine(result, PortablePackSyncService.SourceMarkerFileName)));
        Assert.False(Directory.Exists(_receiver.CombineUnderInstances(installedName)));
        Assert.False(Directory.Exists(WorldLocations.ForBuild(_receiver.Worlds, installedName)));
        Assert.Equal("must not change", File.ReadAllText(Path.Combine(_receiver.CombineUnderPacks(PackName), "mods", "another.jar")));
        Assert.Equal("my progress", File.ReadAllText(Path.Combine(_sender.Worlds, "Private World", "level.dat")));
        using var originalHash = new PackHashService(_sender);
        using var receivedHash = new PackHashService(_receiver);
        Assert.Equal(await originalHash.CalculateAsync(source), await receivedHash.CalculateAsync(result));
        Assert.Equal(installedName, await installer.ImportAsync(archivePath, null, CancellationToken.None));
        Assert.Equal(2, Directory.GetDirectories(_receiver.Packs).Length);
        Assert.Equal("mod payload", File.ReadAllText(Path.Combine(result, "mods", "test.jar")));
    }

    [Fact]
    public async Task ManagedArchiveReconstructsExactReceiptWithoutSourceAndIgnoresUnownedFiles()
    {
        _sender.Ensure();
        _receiver.Ensure();
        var source = _sender.CombineUnderPacks(PackName);
        var archivePath = Path.Combine(_sender.Launcher, "managed" + PackArchiveService.Extension);
        const string featured = "launch-context-v1";
        string[] active = [PackManifestService.ManifestFileName, "mods/original.jar", "launcher/recipe.json"];
        Put(source, active[0], Manifest);
        Put(source, active[1], "original mod");
        Put(source, active[2], "authored launcher data");
        Put(source, "mods/unowned.jar", "do not transfer");
        Put(source, PortablePackSyncService.SourceMarkerFileName, "retired repository");
        PortablePackSyncService.WriteImportedSnapshot(source, active.Select(path => Snapshot(source, path)).ToArray(), [featured]);

        await Service(_sender).ExportAsync(PackName, archivePath, null, CancellationToken.None);
        await Service(_receiver).ImportAsync(archivePath, null, CancellationToken.None);
        var result = _receiver.CombineUnderPacks(PackName);
        Assert.Equal(active.Order(), PortablePackSyncService.ReadManagedFiles(result)!.Select(file => file.Path).Order());
        Assert.Equal(new[] { featured }, PortablePackSyncService.ReadInstalledSnapshotFeatures(result));
        Assert.False(File.Exists(Path.Combine(result, "mods", "unowned.jar")));
        Assert.False(File.Exists(Path.Combine(result, PortablePackSyncService.SourceMarkerFileName)));
        using var originalHash = new PackHashService(_sender);
        using var receivedHash = new PackHashService(_receiver);
        Assert.Equal(await originalHash.CalculateAsync(source), await receivedHash.CalculateAsync(result));

        Put(source, "mods/original.jar", "modified mod");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(_sender).ExportAsync(PackName,
            Path.Combine(_sender.Launcher, "dirty" + PackArchiveService.Extension), null, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsTamperingCancelledImportsAndReservedNamesWithoutPublishingPartials()
    {
        _sender.Ensure();
        _receiver.Ensure();
        var source = _sender.CombineUnderPacks(PackName);
        var archivePath = Path.Combine(_sender.Launcher, "test" + PackArchiveService.Extension);
        Put(source, PackManifestService.ManifestFileName, Manifest);
        Put(source, "mods/test.jar", new string('a', 132_000));
        await Service(_sender).ExportAsync(PackName, archivePath, null, CancellationToken.None);

        var installer = Service(_receiver);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(archivePath, null, CancellationToken.None, "Wrong name"));
        await Assert.ThrowsAsync<IOException>(() => Service(_receiver, reserved: _ => true)
            .ImportAsync(archivePath, null, CancellationToken.None));
        using (var cancellation = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.ImportAsync(archivePath,
                new InlineProgress(cancellation), cancellation.Token));
        }

        var invalidZip = Path.Combine(_sender.Launcher, "invalid" + PackArchiveService.Extension);
        File.Copy(archivePath, invalidZip);
        using (var archive = ZipFile.Open(invalidZip, ZipArchiveMode.Update))
        {
            using var extra = archive.CreateEntry("pack/../../escape.txt").Open();
            extra.WriteByte(1);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(invalidZip, null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_receiver.Service, "escape.txt")));

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            using var payload = archive.GetEntry("pack/mods/test.jar")!.Open();
            payload.WriteByte((byte)'b');
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(archivePath, null, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_receiver.Packs));
        Assert.Empty(Directory.EnumerateDirectories(_receiver.Launcher, ".pack-sync-stage*"));
    }

    private static PackArchiveService Service(AppPaths paths, Func<string, bool>? reserved = null) =>
        new(paths, _ => true, reserved ?? (_ => false));

    private static AppPaths TestPaths() => new(Path.Combine(Path.GetTempPath(), $"launcher-pack-transfer-{Guid.NewGuid():N}"));

    private static void Put(string directory, string relative, string contents)
    {
        var full = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    private static PortablePackSyncService.ManagedFileSnapshot Snapshot(string directory, string path)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, path));
        return new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private sealed class InlineProgress(CancellationTokenSource tokenSource) : IProgress<double>
    {
        public void Report(double value) => tokenSource.Cancel();
    }

    public void Dispose()
    {
        TempTree.Delete(_receiver.Root);
        TempTree.Delete(_sender.Root);
    }
}
