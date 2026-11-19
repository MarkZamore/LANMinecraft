using System.IO;
using System.Text;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class DesktopBootstrapCacheTests : IDisposable
{
    private readonly AppPaths paths = new(Path.Combine(Path.GetTempPath(), "desktop-bootstrap-cache-" + Guid.NewGuid().ToString("N")));

    public DesktopBootstrapCacheTests() => paths.Ensure();

    [Fact]
    public void OnlyCurrentAndPreviousSuccessfulSetsRemain()
    {
        var first = CreateSet(1);
        var second = CreateSet(2);
        var third = CreateSet(3);
        DesktopBootstrapCache.RecordSuccessful(paths, first);
        DesktopBootstrapCache.RecordSuccessful(paths, second);
        DesktopBootstrapCache.RecordSuccessful(paths, third with { Components = third.Components.Reverse().ToArray() });
        DesktopBootstrapCache.RecordSuccessful(paths, third);

        DesktopBootstrapCache.CleanupIdle(paths);

        Assert.All(Roots(first), root => Assert.False(Directory.Exists(root)));
        Assert.All(Roots(second).Concat(Roots(third)), root => Assert.True(Directory.Exists(root)));
        var previous = JsonSerializer.Deserialize<DesktopBootstrapCache.SuccessfulSet>(
            File.ReadAllText(Path.Combine(SetsRoot, "previous.json")), DesktopBootstrapCache.JsonOptions)!;
        Assert.Equal(second.ExeSha256, previous.ExeSha256);
        Assert.All(Roots(first), root => Assert.True(File.Exists(root + ".lease")));
    }

    [Fact]
    public void StagedUpdateProtectsItsComponentsUntilTheStageIsRemoved()
    {
        var current = CreateSet(1);
        var staged = CreateSet(2);
        DesktopBootstrapCache.RecordSuccessful(paths, current);
        var pointer = Path.Combine(SetsRoot, "staged-" + staged.ExeSha256 + ".json");
        File.WriteAllText(pointer, JsonSerializer.Serialize(staged, DesktopBootstrapCache.JsonOptions));

        DesktopBootstrapCache.CleanupIdle(paths);
        Assert.All(Roots(staged), root => Assert.True(Directory.Exists(root)));

        File.Delete(pointer);
        DesktopBootstrapCache.CleanupIdle(paths);
        Assert.All(Roots(staged), root => Assert.False(Directory.Exists(root)));
    }

    [Fact]
    public void AnOlderRunningHostProtectsItsWholeSetWithLeases()
    {
        var current = CreateSet(1);
        var running = CreateSet(2);
        DesktopBootstrapCache.RecordSuccessful(paths, current);
        var held = Roots(running).Select(root => new FileStream(root + ".lease", FileMode.Open, FileAccess.Read, FileShare.Read)).ToArray();
        try
        {
            DesktopBootstrapCache.CleanupIdle(paths);
            Assert.All(Roots(running), root => Assert.True(Directory.Exists(root)));
        }
        finally { foreach (var lease in held) lease.Dispose(); }

        DesktopBootstrapCache.CleanupIdle(paths);
        Assert.All(Roots(running), root => Assert.False(Directory.Exists(root)));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("previous")]
    [InlineData("staged")]
    public void AnUnreadableSetDefersAllSweeping(string name)
    {
        var current = CreateSet(1);
        var old = CreateSet(2);
        DesktopBootstrapCache.RecordSuccessful(paths, current);
        var file = name == "staged" ? "staged-" + Hash(99) + ".json" : name + ".json";
        File.WriteAllText(Path.Combine(SetsRoot, file), "{ unfinished");
        if (name == "previous")
        {
            Assert.ThrowsAny<Exception>(() => DesktopBootstrapCache.RecordSuccessful(paths, old));
            Assert.Equal("{ unfinished", File.ReadAllText(Path.Combine(SetsRoot, file)));
            var savedCurrent = JsonSerializer.Deserialize<DesktopBootstrapCache.SuccessfulSet>(
                File.ReadAllText(Path.Combine(SetsRoot, "current.json")), DesktopBootstrapCache.JsonOptions)!;
            Assert.Equal(current.ExeSha256, savedCurrent.ExeSha256);
        }

        DesktopBootstrapCache.CleanupIdle(paths);

        Assert.All(Roots(old), root => Assert.True(Directory.Exists(root)));
    }

    [Fact]
    public void ALeaseWithAnotherOwnershipMarkerDoesNotAuthorizeDeletion()
    {
        var current = CreateSet(1);
        var unknown = CreateSet(2);
        DesktopBootstrapCache.RecordSuccessful(paths, current);
        File.WriteAllText(unknown.AppRoot + ".lease", "LMHOST01\n" + unknown.AppSha256 + "\n", Encoding.ASCII);

        DesktopBootstrapCache.CleanupIdle(paths);

        Assert.True(Directory.Exists(unknown.AppRoot));
    }

    [Fact]
    public void SuccessfulMigrationCollectsOldFlatGenerationsAfterTheirLeasesEnd()
    {
        var current = CreateSet(1);
        DesktopBootstrapCache.RecordSuccessful(paths, current);
        var flatRoot = Path.Combine(paths.Launcher, LauncherStorageLayout.LegacyLauncherRuntime);
        var old = Path.Combine(flatRoot, Hash(200));
        var active = Path.Combine(flatRoot, Hash(201));
        foreach (var directory in new[] { old, active })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "payload.dll"), "owned old host bytes");
            File.WriteAllText(directory + ".lease", "LMHOST01\n" + Path.GetFileName(directory) + "\n", Encoding.ASCII);
        }
        using (var lease = new FileStream(active + ".lease", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(PortableRuntimeCache.TryImportLegacy(paths));
            PortableRuntimeCache.Cleanup(paths);
            Assert.True(Directory.Exists(old));
            Assert.True(Directory.Exists(active));
        }
        Assert.True(PortableRuntimeCache.TryImportLegacy(paths));
        PortableRuntimeCache.Cleanup(paths);
        Assert.False(Directory.Exists(flatRoot));
        var migrated = Path.Combine(paths.Launcher, LauncherStorageLayout.LauncherRuntime);
        Assert.False(Directory.Exists(Path.Combine(migrated, Hash(200))));
        Assert.False(Directory.Exists(Path.Combine(migrated, Hash(201))));
        Assert.True(File.Exists(Path.Combine(migrated, Hash(201) + ".lease")));
    }

    private DesktopBootstrapCache.SuccessfulSet CreateSet(int index)
    {
        var executable = Path.Combine(paths.Root, "LANMinecraft.exe");
        File.WriteAllText(executable, "native fixture");
        var appHash = Hash(index);
        var app = CreateObject("app", appHash);
        var components = new[] { "desktop-runtime", "assets", "steam-native" }
            .Select((kind, offset) =>
            {
                var hash = Hash(index * 10 + offset);
                var root = CreateObject(kind, hash);
                return new DesktopBootstrapCache.ComponentReference(kind, kind + "-test", root,
                    "payload.bin", hash, root + ".manifest", Hash(999));
            }).ToArray();
        return new DesktopBootstrapCache.SuccessfulSet(1, executable, Hash(index + 100), app, appHash, components);
    }

    private string CreateObject(string kind, string hash)
    {
        var root = Path.Combine(paths.Launcher, "Bootstrap", kind, hash);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "payload.bin"), "small owned cache fixture");
        File.WriteAllText(root + ".lease", "LMDESK01\n" + kind + "\n" + hash + "\n", Encoding.ASCII);
        if (kind != "app") File.WriteAllText(root + ".manifest", "small fixture manifest");
        return root;
    }

    private static IEnumerable<string> Roots(DesktopBootstrapCache.SuccessfulSet set) =>
        new[] { set.AppRoot }.Concat(set.Components.Select(component => component.Root));
    private static string Hash(int value) => value.ToString("x64");
    private string SetsRoot => Path.Combine(paths.Launcher, "Bootstrap", "sets");
    public void Dispose() => TempTree.Delete(paths.Root);
}
