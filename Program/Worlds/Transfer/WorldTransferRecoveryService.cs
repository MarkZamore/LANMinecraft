using System.IO;

namespace Minecraft;

public static class WorldTransferRecoveryService
{
    public static void Recover(AppPaths paths, Logger logger)
    {
        var storage = new WorldTransferStorage(paths, logger);
        var root = storage.TransfersRoot;
        WorldTransferStorage.EnsureOrdinaryDirectoryPath(root);
        if (!Directory.Exists(root)) return;

        var failures = new List<string>();
        foreach (var transactionRoot in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            if (!Guid.TryParseExact(Path.GetFileName(transactionRoot), "N", out _)) continue;
            try
            {
                var journal = storage.ReadJournal(transactionRoot);
                if (journal is null)
                {
                    // An escrow with no journal is evidence, not abandoned staging.
                    if (Directory.Exists(Path.Combine(transactionRoot, "escrow")))
                        throw new InvalidDataException("An escrow directory has no transaction journal.");
                    storage.DeleteTransaction(transactionRoot);
                    continue;
                }

                if (string.Equals(journal.Role, "Sender", StringComparison.Ordinal))
                {
                    RecoverSender(storage, logger, transactionRoot, journal);
                    continue;
                }

                RecoverReceiver(storage, logger, transactionRoot, journal);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(transactionRoot)}: {ex.Message}");
                logger.Warn($"Could not recover transfer transaction {Path.GetFileName(transactionRoot)}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Minecraft cannot continue because a world transfer transaction could not be recovered:\n" +
                string.Join("\n", failures));
        }

        WorldTransferStorage.EnsureOrdinaryDirectoryPath(root);
        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    private static void RecoverReceiver(
        WorldTransferStorage storage,
        Logger logger,
        string transactionRoot,
        WorldTransferJournal journal)
    {
        if (journal.State is "Receiving" or "Ready")
        {
            storage.DeleteTransaction(transactionRoot);
            return;
        }

        if (journal.State is not ("CommitReceived" or "Installed" or "Committed"))
            throw new InvalidDataException("Unknown receiver transaction state.");

        // A planned destination may already have been created by another writer,
        // so never overwrite or remove it. Retain staging when the move is uncertain.
        if (Directory.Exists(storage.GetStagingPath(transactionRoot)) ||
            !Directory.Exists(journal.InstalledWorldPath))
        {
            logger.Warn($"Приём мира не подтверждён. Данные проверки сохранены в {transactionRoot}.");
            return;
        }

        logger.Info($"Received world destination found during recovery: {journal.InstalledWorldPath}.");
        storage.DeleteTransaction(transactionRoot);
    }

    private static void RecoverSender(
        WorldTransferStorage storage,
        Logger logger,
        string transactionRoot,
        WorldTransferJournal journal)
    {
        if (string.Equals(journal.State, "Committed", StringComparison.Ordinal))
        {
            storage.DeleteTransaction(transactionRoot);
            return;
        }

        if (string.Equals(journal.State, "CommitSent", StringComparison.Ordinal))
        {
            if (!Directory.Exists(journal.EscrowPath))
            {
                throw new DirectoryNotFoundException($"Uncertain committed transfer has no escrow world: {journal.EscrowPath}");
            }
            // The receiver may or may not have installed it. Restoring would
            // risk two copies of one world diverging, so the copy stays put -
            // but the player is told where it is and how to take it back.
            WriteEscrowReadme(logger, journal);
            logger.Warn(
                $"Мир «{Path.GetFileName(journal.SourceWorldPath)}» остался в папке {journal.EscrowPath}: " +
                "получатель не подтвердил приём. Если мир ему не дошёл, перенесите эту папку в Minecraft\\Worlds.");
            return;
        }

        if (journal.State is not ("Preparing" or "Escrowed"))
            throw new InvalidDataException("Unknown sender transaction state.");
        storage.RestoreEscrowWorld(transactionRoot, journal);
        storage.DeleteTransaction(transactionRoot);
    }

    /// <summary>
    /// Explains, next to the world itself, what this folder is - the one place
    /// a player looking for their missing world will actually read.
    /// </summary>
    private static void WriteEscrowReadme(Logger logger, WorldTransferJournal journal)
    {
        try
        {
            var readme = Path.Combine(
                Path.GetDirectoryName(journal.EscrowPath) ?? journal.EscrowPath,
                "ЧТО-ЭТО-ЗА-ПАПКА.txt");
            WorldTransferStorage.EnsureOrdinaryDirectoryPath(Path.GetDirectoryName(readme)!);
            if (File.Exists(readme)) return;
            var world = Path.GetFileName(journal.SourceWorldPath);
            var text = string.Join(
                Environment.NewLine,
                $"Здесь лежит мир «{world}».",
                "",
                "Он передавался другому игроку, и связь оборвалась ровно в тот момент, когда",
                "уже нельзя было понять, дошёл он или нет. Лаунчер не стал возвращать его сам:",
                "если у друга мир открылся, две копии начали бы расходиться.",
                "",
                "Спросите друга, появился ли у него этот мир.",
                "  • Появился — эту папку можно удалить.",
                "  • Не появился — перенесите папку с миром в Minecraft\\Worlds,",
                "    и он вернётся в список миров.",
                "");
            using var stream = new FileStream(readme, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true));
            writer.Write(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"Escrow note could not be written: {ex.Message}");
        }
    }

}
