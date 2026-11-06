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

    private void WriteDescriptor(string executable = Executable)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Under(PackManifestService.ManifestFileName), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            minecraftVersion = "26.3",
            nativeRuntime = new
            {
                executable, platform = "windows-x64", adapter = PackManifestService.PortableNativeAdapter, version = "nightly-fixture-1234"
            }
        }));
    }

    private void WriteSyncState(string[] features)
    {
        var files = new[] { PackManifestService.ManifestFileName, Executable, Resource, SteamLibrary }.ToDictionary(path => path, path =>
        {
            var content = File.ReadAllBytes(Under(path));
            return new { sizeBytes = content.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(content)) };
        });
        File.WriteAllText(Under(PortablePackSyncService.SyncStateFileName),
            JsonSerializer.Serialize(new { schemaVersion = 1, requiredLauncherFeatures = features, files }));
    }

    private string Under(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
}
