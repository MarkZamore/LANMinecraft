using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>
/// Crash recovery for the files a launch rewrites inside a world. It used to be
/// dead code: the journal lived under Personal\Temp, and startup cleanup wiped
/// that directory before anything read it, so an interrupted launch left a
/// half-converted world behind with no way back.
///
/// The cases below are the ones that decide whether recovery helps or hurts: it
/// must survive cleanup, it must not touch a transaction another launcher is
/// still writing, it must not replay something ancient over a world that has
/// been played since, and a damaged journal must never stop the launcher.
/// </summary>
public sealed class ProfileFileTransactionRecoveryTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-profile-transactions-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            TempTree.Delete(_root);
        }
        catch
        {
        }
    }

    [Fact]
    public void AnInterruptedTransaction_SurvivesStartupCleanupAndIsRolledBack()
    {
        var paths = CreatePaths();
        var world = Path.Combine(paths.Worlds, "Chebupeli");
        Directory.CreateDirectory(world);
        var levelDat = Path.Combine(world, "level.dat");
        File.WriteAllText(levelDat, "the state the player left behind");

        // A launch starts rewriting the world and the process dies mid-way.
        var journal = PlantTransaction(paths, levelDat, ownerProcessId: 0);
        File.WriteAllText(levelDat, "half-converted by an interrupted launch");

        // Startup order: cleanup first, recovery second - which is exactly what
        // used to destroy the journal before it could be read.
        LogCleanupService.RunCleanup(paths);
        ProfileFileTransaction.RecoverPending(paths, null);

        Assert.Equal("the state the player left behind", File.ReadAllText(levelDat));
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public void ATransactionOwnedByARunningLauncher_IsLeftAlone()
    {
        var paths = CreatePaths();
        var world = Path.Combine(paths.Worlds, "Chebupeli");
        Directory.CreateDirectory(world);
        var levelDat = Path.Combine(world, "level.dat");
        File.WriteAllText(levelDat, "original");
        var journal = PlantTransaction(paths, levelDat, ownerProcessId: Environment.ProcessId);
        File.WriteAllText(levelDat, "being written right now by the owner");

        ProfileFileTransaction.RecoverPending(paths, null);

        // Rolling this back would undo what the other launcher is doing.
        Assert.Equal("being written right now by the owner", File.ReadAllText(levelDat));
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void AnAncientTransaction_IsSetAsideInsteadOfReplayed()
    {
        var paths = CreatePaths();
        var world = Path.Combine(paths.Worlds, "Chebupeli");
        Directory.CreateDirectory(world);
        var levelDat = Path.Combine(world, "level.dat");
        File.WriteAllText(levelDat, "original");
        var journal = PlantTransaction(
            paths,
            levelDat,
            ownerProcessId: 0,
            createdAtUtc: DateTimeOffset.UtcNow.AddDays(-30));
        File.WriteAllText(levelDat, "a month of play since that crash");

        ProfileFileTransaction.RecoverPending(paths, null);

        Assert.Equal("a month of play since that crash", File.ReadAllText(levelDat));
        Assert.False(Directory.Exists(journal));
        // The backup is kept - it may be the only copy of that older profile.
        var quarantine = Path.Combine(paths.ProfileTransactions, "Quarantine");
        Assert.True(Directory.Exists(quarantine));
        Assert.Single(Directory.GetDirectories(quarantine));
    }

    [Fact]
    public void ADamagedJournal_DoesNotStopTheLauncher()
    {
        var paths = CreatePaths();
        var transaction = Path.Combine(paths.ProfileTransactions, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        File.WriteAllText(Path.Combine(transaction, "transaction.json"), "{ this is not json");

        ProfileFileTransaction.RecoverPending(paths, null);

        Assert.False(Directory.Exists(transaction));
        Assert.Single(Directory.GetDirectories(Path.Combine(paths.ProfileTransactions, "Quarantine")));
    }

    [Fact]
    public void AJournalFromAnotherSchema_IsNeverReplayed()
    {
        var paths = CreatePaths();
        var world = Path.Combine(paths.Worlds, "Chebupeli");
        Directory.CreateDirectory(world);
        var levelDat = Path.Combine(world, "level.dat");
        File.WriteAllText(levelDat, "current");
        var transaction = PlantTransaction(paths, levelDat, ownerProcessId: 0, schemaVersion: 99);
        File.WriteAllText(levelDat, "written by the newer build");

        ProfileFileTransaction.RecoverPending(paths, null);

        Assert.Equal("written by the newer build", File.ReadAllText(levelDat));
        Assert.True(Directory.Exists(transaction));
    }

    /// <summary>A committed transaction has nothing to undo; it is just swept up.</summary>
    [Fact]
    public void ACommittedTransaction_IsRemovedWithoutRestoring()
    {
        var paths = CreatePaths();
        var world = Path.Combine(paths.Worlds, "Chebupeli");
        Directory.CreateDirectory(world);
        var levelDat = Path.Combine(world, "level.dat");
        File.WriteAllText(levelDat, "original");
        var transaction = PlantTransaction(paths, levelDat, ownerProcessId: 0, state: "Committed");
        File.WriteAllText(levelDat, "the committed result");

        ProfileFileTransaction.RecoverPending(paths, null);

        Assert.Equal("the committed result", File.ReadAllText(levelDat));
        Assert.False(Directory.Exists(transaction));
    }

    private AppPaths CreatePaths()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        return paths;
    }

    /// <summary>Writes the journal a crashed launch would have left behind.</summary>
    private static string PlantTransaction(
        AppPaths paths,
        string trackedFile,
        int ownerProcessId,
        DateTimeOffset? createdAtUtc = null,
        int schemaVersion = 2,
        string state = "Active")
    {
        var transaction = Path.Combine(paths.ProfileTransactions, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        File.Copy(trackedFile, Path.Combine(transaction, "000000.backup"));
        var journal = new
        {
            schemaVersion,
            operation = "PrepareWorldsForLaunch",
            state,
            createdAtUtc = createdAtUtc ?? DateTimeOffset.UtcNow,
            ownerProcessId,
            ownerProcessStartedAtUtc = ownerProcessId == Environment.ProcessId
                ? System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
                : default,
            entries = new[]
            {
                new
                {
                    path = trackedFile,
                    existed = true,
                    backupFile = "000000.backup",
                    lastWriteUtc = File.GetLastWriteTimeUtc(trackedFile),
                    attributes = FileAttributes.Normal
                }
            }
        };
        File.WriteAllText(
            Path.Combine(transaction, "transaction.json"),
            JsonSerializer.Serialize(journal, JsonOptions));
        return transaction;
    }
}
