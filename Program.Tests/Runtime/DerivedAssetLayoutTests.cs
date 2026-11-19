using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class DerivedAssetLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "derived-assets-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(_root);

    [Fact]
    public void FlatSoundFilesRepairOfflineAndSurviveTheSharedStoreSweep()
    {
        Paths.Ensure();
        var (index, source, destination) = Index("sound/random/click.ogg");
        var assets = SharedRuntimeStore.Assets(Paths);
        Assert.Equal(2, DerivedAssetLayout.Restore(index, assets, SharedRuntimeStore.Resources(Paths), default));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
        File.WriteAllText(destination, "wrong-bytes");
        Assert.Equal(1, DerivedAssetLayout.Restore(index, assets, SharedRuntimeStore.Resources(Paths), default));
        Assert.Equal(0, DerivedAssetLayout.Restore(index, assets, SharedRuntimeStore.Resources(Paths), default));
        var runtime = Path.Combine(Paths.Runtimes, "fixture");
        Directory.CreateDirectory(runtime);
        var files = new Dictionary<string, object>();
        foreach (var path in new[] { index, source })
            files[Path.GetRelativePath(SharedRuntimeStore.Anchor(Paths), path).Replace('\\', '/')] = new { };
        File.WriteAllText(Path.Combine(runtime, ".portable-runtime.json"), JsonSerializer.Serialize(new { files }));
        var orphan = Path.Combine(assets, "virtual", "unused-index", "old.ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!); File.WriteAllText(orphan, "unused");
        SharedRuntimeStore.Sweep(Paths);
        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(orphan));
        File.Delete(destination);
        Assert.Equal(1, DerivedAssetLayout.Restore(index, assets, SharedRuntimeStore.Resources(Paths), default));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
    }

    [Theory]
    [InlineData("../outside.ogg")]
    [InlineData("sound/../../outside.ogg")]
    [InlineData("C:/outside.ogg")]
    [InlineData("/outside.ogg")]
    [InlineData("sound\\..\\outside.ogg")]
    public void DeclaredPathsCannotEscapeTheAssetLayout(string name)
    {
        var (index, _, _) = Index(name);
        Assert.Throws<InvalidDataException>(() => DerivedAssetLayout.Restore(index, SharedRuntimeStore.Assets(Paths), SharedRuntimeStore.Resources(Paths), default));
    }

    [Fact]
    public void ModernIndexDoesNotCreateFlatAssets()
    {
        var (index, _, destination) = Index("sound/click.ogg", flat: false);
        Assert.Empty(DerivedAssetLayout.Read(index, SharedRuntimeStore.Assets(Paths), SharedRuntimeStore.Resources(Paths)));
        Assert.Equal(0, DerivedAssetLayout.Restore(index, SharedRuntimeStore.Assets(Paths), SharedRuntimeStore.Resources(Paths), default));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void DamagedObjectCannotReplaceAnExistingFlatFile()
    {
        var (index, source, destination) = Index("sound/click.ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllText(destination, "keep");
        File.WriteAllText(source, "wrong-bytes");
        Assert.Throws<InvalidDataException>(() => DerivedAssetLayout.Restore(index, SharedRuntimeStore.Assets(Paths), SharedRuntimeStore.Resources(Paths), default));
        Assert.Equal("keep", File.ReadAllText(destination));
    }

    [Fact]
    public void ALinkedDestinationCannotWriteIntoAnUnrelatedDirectory()
    {
        var (index, _, destination) = Index("sound/click.ogg");
        var unrelated = Path.Combine(_root, "unrelated"); Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(destination))!);
        var link = Path.GetDirectoryName(destination)!;
        SavesFolderService.CreateJunction(link, unrelated);
        try
        {
            Assert.Throws<IOException>(() => DerivedAssetLayout.Restore(index, SharedRuntimeStore.Assets(Paths), SharedRuntimeStore.Resources(Paths), default));
            Assert.Single(Directory.EnumerateFileSystemEntries(unrelated));
        }
        finally { Directory.Delete(link); }
    }

    private (string Index, string Source, string Destination) Index(string name, bool flat = true)
    {
        var assets = SharedRuntimeStore.Assets(Paths);
        var bytes = Encoding.UTF8.GetBytes("known-bytes");
        var hash = Convert.ToHexStringLower(SHA1.HashData(bytes));
        var source = Path.Combine(assets, "objects", hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!); File.WriteAllBytes(source, bytes);
        var index = Path.Combine(assets, "indexes", "fixture.json"); Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        File.WriteAllText(index, JsonSerializer.Serialize(new { @virtual = flat, map_to_resources = flat,
            objects = new Dictionary<string, object> { [name] = new { hash, size = bytes.Length } } }));
        return (index, source, Path.Combine(assets, "virtual", "fixture", name));
    }

    public void Dispose() => TempTree.Delete(_root);
}
