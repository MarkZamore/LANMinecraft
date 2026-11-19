using Minecraft;

namespace Minecraft.Tests;

public sealed class WorldTransferStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lanminecraft-transfer-storage-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly WorldTransferStorage _storage;

    public WorldTransferStorageTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
        _logger = new Logger(_paths.LogFile);
        _storage = new WorldTransferStorage(_paths, _logger);
    }

    [Fact]
    public void ReceivePlansCaseInsensitiveCollisionBeforeMovingTheVerifiedWorld()
    {
        var original = Path.Combine(_paths.Worlds, "Existing World");
        WriteWorld(original, "the existing world");
        var (transaction, staging, journal) = ReadyReceiver();

        _storage.PlanReceivedInstall(transaction, journal, "existing world", null, default);

        var planned = Assert.IsType<WorldTransferJournal>(_storage.ReadJournal(transaction));
        Assert.Equal("CommitReceived", planned.State);
        Assert.Equal(Path.Combine(_paths.Worlds, "existing world (2)"), planned.InstalledWorldPath);
        Assert.False(Directory.Exists(planned.InstalledWorldPath));
        Assert.True(Directory.Exists(staging));

        Assert.Equal(planned.InstalledWorldPath, _storage.ApplyReceivedInstall(transaction, journal, default));
        Assert.Equal("Installed", _storage.ReadJournal(transaction)?.State);
        Assert.Equal("the existing world", ReadWorld(original));
        Assert.Equal("staged world", ReadWorld(planned.InstalledWorldPath));
    }

    [Fact]
    public void DestinationCreatedAfterPlanningIsNeverOverwrittenAndStagingIsRecovered()
    {
        var (transaction, staging, journal) = ReadyReceiver();
        _storage.PlanReceivedInstall(transaction, journal, "Island", null, default);
        WriteWorld(journal.InstalledWorldPath, "appeared after planning");

        Assert.ThrowsAny<IOException>(() => _storage.ApplyReceivedInstall(transaction, journal, default));
        WorldTransferRecoveryService.Recover(_paths, _logger);

        Assert.Equal("appeared after planning", ReadWorld(journal.InstalledWorldPath));
        Assert.Equal("staged world", ReadWorld(staging));
        Assert.Equal("CommitReceived", _storage.ReadJournal(transaction)?.State);
    }

    [Fact]
    public void RecoveryAfterTheDirectoryMoveKeepsTheWorld()
    {
        var (transaction, staging, journal) = ReadyReceiver();
        _storage.PlanReceivedInstall(transaction, journal, "Island", null, default);
        Directory.Move(staging, journal.InstalledWorldPath); // Stop before writing Installed.

        WorldTransferRecoveryService.Recover(_paths, _logger);

        Assert.Equal("staged world", ReadWorld(journal.InstalledWorldPath));
        Assert.False(Directory.Exists(transaction));
    }

    [Fact]
    public void TransferNeverInstallsThroughARedirectedWorldsDirectory()
    {
        var (transaction, staging, journal) = ReadyReceiver();
        _storage.PlanReceivedInstall(transaction, journal, "Island", null, default);
        var outside = Path.Combine(_root, "non-world-directory");
        Directory.CreateDirectory(outside);
        Directory.Delete(_paths.Worlds);
        SavesFolderService.CreateJunction(_paths.Worlds, outside);

        try
        {
            Assert.Throws<InvalidDataException>(() => _storage.ApplyReceivedInstall(transaction, journal, default));
            Assert.Throws<InvalidOperationException>(() => WorldTransferRecoveryService.Recover(_paths, _logger));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            Assert.Equal("staged world", ReadWorld(staging));
        }
        finally
        {
            Directory.Delete(_paths.Worlds);
        }
    }

    [Fact]
    public void RecoveryPreservesSenderEscrowWhenTheCommitHasNoAcknowledgement()
    {
        var source = Path.Combine(_paths.Worlds, "Island");
        var transferId = Guid.NewGuid().ToString("N");
        var transaction = _storage.CreateTransactionDirectory(transferId);
        var escrow = Path.Combine(transaction, "escrow", "Island");
        WriteWorld(escrow, "original world");
        _storage.WriteJournal(transaction, new WorldTransferJournal
        {
            TransferId = transferId,
            Role = "Sender",
            State = "CommitSent",
            SourceWorldPath = source,
            EscrowPath = escrow
        });

        WorldTransferRecoveryService.Recover(_paths, _logger);

        Assert.False(Directory.Exists(source));
        Assert.Equal("original world", ReadWorld(escrow));
        Assert.Equal("CommitSent", _storage.ReadJournal(transaction)?.State);
    }

    private (string Transaction, string Staging, WorldTransferJournal Journal) ReadyReceiver()
    {
        var transferId = Guid.NewGuid().ToString("N");
        var transaction = _storage.CreateTransactionDirectory(transferId);
        var staging = _storage.CreateStagingDirectory(transaction);
        WriteWorld(staging, "staged world");
        var journal = new WorldTransferJournal { TransferId = transferId, Role = "Receiver", State = "Ready" };
        _storage.WriteJournal(transaction, journal);
        return (transaction, staging, journal);
    }

    private static void WriteWorld(string path, string contents)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "level.dat"), contents);
    }

    private static string ReadWorld(string path) => File.ReadAllText(Path.Combine(path, "level.dat"));

    public void Dispose() => TempTree.Delete(_root);
}
