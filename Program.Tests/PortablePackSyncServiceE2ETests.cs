using System.Security.Cryptography;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// End-to-end sync against the real MarkZamore/Infinity pack-latest
/// release. Downloads the full pack (~1.2 GB), so it only runs when opted in:
///   PACKSYNC_E2E=1 dotnet test --filter PortablePackSyncServiceE2ETests
/// PACKSYNC_E2E_REPO can point at a local checkout of the pack repository to
/// compare the synced tree against (defaults to the sibling Infinity dir);
/// when the path does not exist the tree comparison is skipped.
/// </summary>
public sealed class PortablePackSyncServiceE2ETests : IDisposable
{
    // Mirrors SCAN_ROOTS in the pack repo's tools/generate_manifest.py.
    private static readonly string[] ManifestRoots =
    [
        "mods", "config", "kubejs", "scripts", "defaultconfigs", "data",
        "resourcepacks", "shaderpacks", "configureddefaults"
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-pack-sync-e2e-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task FullInstall_TreeMatchesRepository_IncrementalRepairsSingleFile()
    {
        if (Environment.GetEnvironmentVariable("PACKSYNC_E2E") != "1")
        {
            return; // opt-in only: full pack download
        }

        var paths = new AppPaths(_root);
        var service = new PortablePackSyncService(paths, new Logger(paths.LogFile));
        var packDir = paths.CombineUnderPacks(PortablePackSyncService.DefaultPackRelativePath);

        // 1. First install into an empty Packs tree downloads everything.
        var installed = await service.SyncAsync(
            PortablePackSyncService.DefaultPackRelativePath, null, CancellationToken.None);
        Assert.Equal(PackSyncOutcome.Installed, installed.Outcome);
        Assert.NotNull(installed.Revision);
        Assert.Null(installed.Warning);
        Assert.True(File.Exists(Path.Combine(packDir, "portable-pack.json")));
        Assert.True(File.Exists(Path.Combine(packDir, PortablePackSyncService.SourceMarkerFileName)));

        // 2. The synced tree mirrors the repository's manifest-covered files.
        var repo = Environment.GetEnvironmentVariable("PACKSYNC_E2E_REPO")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "Infinity");
        if (Directory.Exists(repo))
        {
            var expected = EnumerateManifestFiles(repo)
                .ToDictionary(rel => rel, rel => Sha256(Path.Combine(repo, rel)));
            var actual = EnumerateManifestFiles(packDir)
                .ToDictionary(rel => rel, rel => Sha256(Path.Combine(packDir, rel)));
            Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
            foreach (var (rel, hash) in expected)
            {
                Assert.Equal(hash, actual[rel]);
            }
        }

        // 3. Re-sync with nothing changed rewrites nothing.
        var probe = Directory.EnumerateFiles(Path.Combine(packDir, "mods"), "*.jar").First();
        var probeWrite = File.GetLastWriteTimeUtc(probe);
        var upToDate = await service.SyncAsync(
            PortablePackSyncService.DefaultPackRelativePath, null, CancellationToken.None);
        Assert.Equal(PackSyncOutcome.UpToDate, upToDate.Outcome);
        Assert.Equal(0, upToDate.FilesChanged);
        Assert.Equal(probeWrite, File.GetLastWriteTimeUtc(probe));

        // 4. Corrupting one jar repairs exactly that jar without touching peers.
        var corrupt = Directory.EnumerateFiles(Path.Combine(packDir, "mods"), "*.jar")
            .OrderBy(p => new FileInfo(p).Length)
            .First();
        var peer = Directory.EnumerateFiles(Path.Combine(packDir, "mods"), "*.jar")
            .First(p => p != corrupt);
        var peerWrite = File.GetLastWriteTimeUtc(peer);
        var originalSize = new FileInfo(corrupt).Length;
        await File.WriteAllBytesAsync(corrupt, [1, 2, 3]);
        var repaired = await service.SyncAsync(
            PortablePackSyncService.DefaultPackRelativePath, null, CancellationToken.None);
        Assert.Equal(PackSyncOutcome.Updated, repaired.Outcome);
        Assert.Equal(1, repaired.FilesChanged);
        Assert.Equal(originalSize, new FileInfo(corrupt).Length);
        // mods/ ships as hash-bucket chunks, so repairing one jar re-downloads
        // its chunk - still a small fraction of the pack, never the whole tree.
        var packBytes = Directory.EnumerateFiles(packDir, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        Assert.True(repaired.BytesDownloaded < packBytes / 10,
            $"single-jar repair downloaded {repaired.BytesDownloaded} bytes of {packBytes}");
        Assert.Equal(peerWrite, File.GetLastWriteTimeUtc(peer));

        // 5. An extra file inside a managed root is deleted without downloads.
        var extra = Path.Combine(packDir, "mods", "not-in-manifest.jar");
        await File.WriteAllBytesAsync(extra, [9, 9, 9]);
        var mirrored = await service.SyncAsync(
            PortablePackSyncService.DefaultPackRelativePath, null, CancellationToken.None);
        Assert.Equal(PackSyncOutcome.Updated, mirrored.Outcome);
        Assert.False(File.Exists(extra));
        Assert.Equal(0, mirrored.BytesDownloaded);
    }

    private static IEnumerable<string> EnumerateManifestFiles(string packRoot)
    {
        foreach (var root in ManifestRoots)
        {
            var basePath = Path.Combine(packRoot, root);
            if (!Directory.Exists(basePath)) continue;
            foreach (var file in Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(packRoot, file).Replace('\\', '/');
                if (rel.Split('/').Any(part =>
                        part.StartsWith(".git", StringComparison.Ordinal) ||
                        part is ".portable-pack-sync.json" or "portable-pack-source.json"))
                {
                    continue;
                }
                yield return rel;
            }
        }

        // Root-level manifest members: portable-pack.json plus the client jar.
        yield return "portable-pack.json";
        foreach (var jar in Directory.EnumerateFiles(packRoot, "*.jar", SearchOption.TopDirectoryOnly))
        {
            yield return Path.GetFileName(jar);
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
