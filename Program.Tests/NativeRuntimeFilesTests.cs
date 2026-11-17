using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class NativeRuntimeFilesTests : IDisposable
{
    private const string Executable = "native/client.exe";
    private const string SteamLibrary = "native/steam_api64.dll";
    private const string Resource = "native/resource.txt";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-pack-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void VerifiedPayloadRejectsUnmanagedAndModifiedFilesAndLocksTheActiveSnapshot()
    {
        WriteDescriptor();
        var exe = Under(Executable);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.Copy(Environment.ProcessPath!, exe);
        File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), Under(SteamLibrary));
        File.WriteAllText(Under(Resource), "original");
        var descriptor = PackManifestService.Load(_root);
        Assert.Equal(PackLoaderKind.Native, descriptor.Loader.Type);
        Assert.Equal("26.3", descriptor.MinecraftVersion);
        Assert.Equal("nightly-fixture-1234", descriptor.NativeRuntime!.Version);

        Assert.Throws<InvalidDataException>(() => NativeRuntimeFiles.Open(_root, descriptor, default));
        WriteSyncState([PackManifestService.NativeRuntimeFeature]);
        Assert.Throws<InvalidDataException>(() => NativeRuntimeFiles.Open(_root, descriptor, default));
        WriteSyncState([PackManifestService.NativeRuntimeFeature, PackManifestService.NativeSteamFeature,
            PackManifestService.Minecraft26WorldLayoutFeature]);
        using (var files = NativeRuntimeFiles.Open(_root, descriptor, default))
        {
            Assert.Equal(exe, files.ExecutablePath);
            Assert.Throws<IOException>(() => File.WriteAllText(Under(Resource), "modified"));
        }

        var unmanaged = Under("native/unmanaged.dll");
        File.WriteAllText(unmanaged, "not in the manifest");
        Assert.Throws<InvalidDataException>(() => NativeRuntimeFiles.Open(_root, descriptor, default));
        File.Delete(unmanaged);

        File.WriteAllText(Under(Resource), "modified");
        Assert.Throws<InvalidDataException>(() => NativeRuntimeFiles.Open(_root, descriptor, default));
    }

    [Fact]
    public void RequiredNativeCleanupProtectsAssetsAndReportsProgressOnlyForUnlistedBlockers()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var entry = PackCatalogService.SeedSources().First();
        var pack = paths.CombineUnderPacks(entry.RelativePath);
        WriteDescriptor(directory: pack);
        var exe = Under(Executable, pack);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.Copy(Environment.ProcessPath!, exe);
        File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), Under(SteamLibrary, pack));
        File.WriteAllText(Under(Resource, pack), "original");
        WriteSyncState([PackManifestService.NativeRuntimeFeature, PackManifestService.NativeSteamFeature,
            PackManifestService.Minecraft26WorldLayoutFeature], pack);
        File.WriteAllText(Under(PortablePackSyncService.SourceMarkerFileName, pack), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, entry.Source.Owner, entry.Source.Repo, entry.Source.Tag
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var sync = new PortablePackSyncService(paths, new Logger(paths.LogFile));
        var descriptor = PackManifestService.Load(pack);
        var reports = new RecordedProgress();

        using (NativeRuntimeFiles.Open(pack, descriptor, default, paths, sync, reports)) { }
        Assert.Empty(reports.Reports);
        var unnecessary = Under("native/old.txt", pack);
        var protectedAsset = Under("native/resourcepacks/added.zip", pack);
        Directory.CreateDirectory(Path.GetDirectoryName(protectedAsset)!);
        File.WriteAllText(unnecessary, "retired");
        File.WriteAllText(protectedAsset, "user data");
        Assert.Throws<InvalidDataException>(() => NativeRuntimeFiles.Open(pack, descriptor, default, paths, sync, reports));
        Assert.True(File.Exists(unnecessary));
        Assert.True(File.Exists(protectedAsset));
        Assert.Empty(reports.Reports);

        File.Delete(protectedAsset);
        using var verified = NativeRuntimeFiles.Open(pack, descriptor, default, paths, sync, reports);
        Assert.Equal(exe, verified.ExecutablePath);
        Assert.False(File.Exists(unnecessary));
        Assert.Equal("original", File.ReadAllText(Under(Resource, pack)));
        Assert.Collection(reports.Reports,
            report => Assert.Equal((RuntimePreparationStage.RepairingPackStructure, 0d), (report.Stage, report.Fraction)),
            report => Assert.Equal((RuntimePreparationStage.RepairingPackStructure, 1d), (report.Stage, report.Fraction)));
    }

    [Theory]
    [InlineData("../client.exe")]
    [InlineData("native/../client.exe")]
    [InlineData("native/client.cmd")]
    [InlineData("native/CON.exe")]
    [InlineData("other/client.exe")]
    public void InvalidExecutablesAreRejected(string executable)
    {
        WriteDescriptor(executable);
        Assert.Throws<InvalidDataException>(() => PackManifestService.Load(_root));
    }

    private void WriteDescriptor(string executable = Executable, string? directory = null)
    {
        Directory.CreateDirectory(directory ?? _root);
        File.WriteAllText(Under(PackManifestService.ManifestFileName, directory), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            minecraftVersion = "26.3",
            nativeRuntime = new
            {
                executable, platform = "windows-x64", adapter = PackManifestService.PortableNativeAdapter, version = "nightly-fixture-1234"
            }
        }));
    }

    private void WriteSyncState(string[] features, string? directory = null)
    {
        var files = new[] { PackManifestService.ManifestFileName, Executable, Resource, SteamLibrary }.ToDictionary(path => path, path =>
        {
            var content = File.ReadAllBytes(Under(path, directory));
            return new { sizeBytes = content.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(content)) };
        });
        File.WriteAllText(Under(PortablePackSyncService.SyncStateFileName, directory),
            JsonSerializer.Serialize(new { schemaVersion = 1, revision = "fixture", requiredLauncherFeatures = features, files }));
    }

    private string Under(string path, string? directory = null) => Path.Combine(directory ?? _root, path.Replace('/', Path.DirectorySeparatorChar));

    private sealed class RecordedProgress : IProgress<RuntimePreparationProgress>
    {
        internal List<RuntimePreparationProgress> Reports { get; } = [];
        public void Report(RuntimePreparationProgress value) => Reports.Add(value);
    }
}
