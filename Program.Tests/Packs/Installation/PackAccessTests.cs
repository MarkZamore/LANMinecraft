namespace Minecraft.Tests;

public sealed class PackAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanminecraft-pack-access-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task SamePackWaitsAndCanCancelWhileAnotherPackRemainsAvailable()
    {
        var paths = new AppPaths(_root);
        using var owner = await PackAccess.AcquireAsync(paths, "Better Vanilla", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = PackAccess.AcquireAsync(new AppPaths(_root), "better vanilla", cancellation.Token);
        Assert.False(waiting.IsCompleted);
        using var other = PackAccess.TryAcquire(paths, "Another Pack");
        Assert.NotNull(other);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Null(PackAccess.TryAcquire(paths, "BETTER VANILLA"));
        owner.Dispose();
        using var next = PackAccess.TryAcquire(paths, "better vanilla");
        Assert.NotNull(next);
    }

    [Fact]
    public void BusyMemberDoesNotReserveAnotherPackInTheBatch()
    {
        var paths = new AppPaths(_root);
        using var busy = PackAccess.TryAcquire(paths, "Second");
        Assert.NotNull(busy);
        Assert.Null(PackAccess.TryAcquire(paths, ["First", "second"]));

        using var unrelated = PackAccess.TryAcquire(paths, "First");
        Assert.NotNull(unrelated);
        busy.Dispose();
        unrelated.Dispose();
        using var batch = PackAccess.TryAcquire(paths, ["Second", "FIRST", "second"]);
        Assert.NotNull(batch);
        Assert.Null(PackAccess.TryAcquire(paths, "First"));
        Assert.Null(PackAccess.TryAcquire(paths, "second"));
    }

    [Fact]
    public async Task RemovalCannotChangeThePackBetweenDownloadPlanningAndApply()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        const string pack = "A locally prepared pack";
        var directory = paths.CombineUnderPacks(pack);
        Directory.CreateDirectory(directory);
        var payload = Path.Combine(directory, "keep.txt");
        File.WriteAllText(payload, "installed files");
        var sync = new PortablePackSyncService(paths, new Logger(paths.LogFile));
        var removal = new BuildRemovalService(paths);
        var approved = removal.Plan(pack, worldsToo: false);

        using (var plan = await sync.PrepareDownloadPlanAsync(pack, null, CancellationToken.None))
        {
            Assert.Throws<IOException>(() => removal.Remove(approved));
            Assert.Equal("installed files", File.ReadAllText(payload));
            Assert.Equal(PackSyncOutcome.SkippedNoSource,
                (await sync.SyncAsync(plan, null, CancellationToken.None)).Outcome);
        }

        Assert.True(removal.Remove(approved).Complete);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ARedirectedPacksRootDoesNotGrantAccessToAChild()
    {
        var paths = new AppPaths(Path.Combine(_root, "portable"));
        paths.Ensure();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "A pack"));
        Directory.Delete(paths.Packs);
        SavesFolderService.CreateJunction(paths.Packs, outside);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => PackAccess.AcquireAsync(paths, "A pack", CancellationToken.None));
        }
        finally { Directory.Delete(paths.Packs); }
    }
}
