using System.IO;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class LauncherMaintenancePassTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "launcher-maintenance-pass-" + Guid.NewGuid().ToString("N")));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ARunningPackIsDeferredWhileAnUnrelatedPackIsCleaned()
    {
        _paths.Ensure();
        var packs = PortablePackSyncService.KnownPacks.Take(2).ToArray();
        Assert.Equal(2, packs.Length);
        var runningFile = WritePack(packs[0]);
        var otherFile = WritePack(packs[1]);
        var logger = new Logger(_paths.LogFile);
        var sync = new PortablePackSyncService(_paths, logger);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = new LauncherMaintenance(_paths, logger, () => false, run =>
        {
            try { LauncherMaintenancePass.Run(_paths, logger, sync, run); }
            finally { finished.TrySetResult(); }
        }, gameIsRunning: () => true, isPackInUse: path => path.Equals(packs[0].RelativePath, StringComparison.OrdinalIgnoreCase));

        try
        {
            maintenance.ObserveIdle(true);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(runningFile));
            Assert.False(File.Exists(otherFile));
            var marker = Path.Combine(_paths.Launcher, LauncherMaintenance.CompletionFileName);
            var saved = JsonSerializer.Deserialize<LauncherMaintenanceProgress>(File.ReadAllText(marker));
            Assert.NotNull(saved);
            Assert.Contains(packs[0].RelativePath, saved.DeferredPacks);
        }
        finally { maintenance.Stop(); }
    }

    private string WritePack(PortablePackSyncService.KnownPack pack)
    {
        var directory = _paths.CombineUnderPacks(pack.RelativePath);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PackManifestService.ManifestFileName), "{\"schemaVersion\":1}");
        var source = pack.Source;
        File.WriteAllText(Path.Combine(directory, PortablePackSyncService.SourceMarkerFileName),
            JsonSerializer.Serialize(new { schemaVersion = 1, source.Owner, source.Repo, source.Tag }, JsonOptions));
        File.WriteAllText(Path.Combine(directory, PortablePackSyncService.SyncStateFileName), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            revision = "installed",
            files = new Dictionary<string, object>
            {
                [PackManifestService.ManifestFileName] = new { sizeBytes = 1L, sha256 = new string('0', 64) }
            }
        }, JsonOptions));
        var unknown = Path.Combine(directory, "unlisted.txt");
        File.WriteAllText(unknown, "unused");
        return unknown;
    }

    public void Dispose() => TempTree.Delete(_paths.Root);
}
