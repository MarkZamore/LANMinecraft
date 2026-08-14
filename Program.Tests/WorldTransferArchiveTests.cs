using System.IO.Compression;
using Minecraft;

namespace Minecraft.Tests;

public sealed class WorldTransferArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-world-archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static void WriteRandomFile(string path, int length, Random random)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public async Task ArchiveHash_MatchesDirectoryHashOfExtractedWorld()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "region"));
        Directory.CreateDirectory(Path.Combine(source, "data", "nested"));
        var random = new Random(20260814);
        WriteRandomFile(Path.Combine(source, "level.dat"), 64 * 1024, random);
        WriteRandomFile(Path.Combine(source, "region", "r.0.0.mca"), 3 * 1024 * 1024, random);
        WriteRandomFile(Path.Combine(source, "region", "r.0.1.mca"), 1536, random);
        WriteRandomFile(Path.Combine(source, "data", "nested", "raids.dat"), 512, random);
        File.WriteAllText(Path.Combine(source, "data", "notes.json"), "{\"key\":\"значение\"}");
        File.WriteAllBytes(Path.Combine(source, "data", "empty.bin"), []);

        var archivePath = Path.Combine(_root, "world.zip");
        var progressReports = new List<(long Current, long Total)>();
        var archiveHash = await WorldTransferService.CreateWorldArchiveWithHashAsync(
            source,
            archivePath,
            (current, total) =>
            {
                progressReports.Add((current, total));
                return Task.CompletedTask;
            },
            heartbeat: null,
            CancellationToken.None);

        var extracted = Path.Combine(_root, "extracted");
        var extractReports = new List<(long Current, long Total)>();
        await WorldTransferService.ExtractWorldArchiveAsync(
            archivePath,
            extracted,
            (current, total) =>
            {
                extractReports.Add((current, total));
                return Task.CompletedTask;
            },
            checkDiskSpace: null,
            CancellationToken.None);

        var extractedHash = await WorldTransferService.HashDirectoryAsync(
            extracted, progress: null, CancellationToken.None);
        Assert.Equal(archiveHash, extractedHash);

        var sourceHash = await WorldTransferService.HashDirectoryAsync(
            source, progress: null, CancellationToken.None);
        Assert.Equal(sourceHash, archiveHash);

        Assert.NotEmpty(progressReports);
        Assert.Equal(progressReports[^1].Total, progressReports[^1].Current);
        Assert.NotEmpty(extractReports);
        Assert.Equal(extractReports[^1].Total, extractReports[^1].Current);

        using var archive = ZipFile.OpenRead(archivePath);
        var regionEntry = archive.Entries.Single(entry => entry.FullName == "region/r.0.0.mca");
        Assert.Equal(regionEntry.Length, regionEntry.CompressedLength);
    }

    [Fact]
    public async Task ExtractWorldArchive_RejectsEntriesOutsideTheWorldDirectory()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "escape.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escape.txt");
            await using var stream = entry.Open();
            stream.WriteByte(1);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WorldTransferService.ExtractWorldArchiveAsync(
                archivePath,
                Path.Combine(_root, "extracted"),
                (_, _) => Task.CompletedTask,
                checkDiskSpace: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveRoundTrip_PreservesEmptyDirectories()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "datapacks"));
        Directory.CreateDirectory(Path.Combine(source, "region"));
        File.WriteAllBytes(Path.Combine(source, "level.dat"), [1, 2, 3, 4]);

        var archivePath = Path.Combine(_root, "world.zip");
        var archiveHash = await WorldTransferService.CreateWorldArchiveWithHashAsync(
            source, archivePath, (_, _) => Task.CompletedTask, heartbeat: null, CancellationToken.None);

        var extracted = Path.Combine(_root, "extracted");
        await WorldTransferService.ExtractWorldArchiveAsync(
            archivePath, extracted, (_, _) => Task.CompletedTask, checkDiskSpace: null, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(extracted, "datapacks")));
        Assert.True(Directory.Exists(Path.Combine(extracted, "region")));
        Assert.Equal(
            archiveHash,
            await WorldTransferService.HashDirectoryAsync(extracted, progress: null, CancellationToken.None));
    }

    [Fact]
    public async Task ExtractWorldArchive_BudgetsDiskSpaceUpFront_AndNeverWritesMoreThanDeclared()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "bomb.zip");
        var honest = Path.Combine(_root, "honest.zip");
        using (var archive = ZipFile.Open(honest, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("level.dat", CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await stream.WriteAsync(new byte[512 * 1024]);
        }

        // Keep the compressed payload but shrink the declared uncompressed size,
        // which is exactly what a zip bomb does.
        var bytes = File.ReadAllBytes(honest);
        RewriteDeclaredUncompressedSizes(bytes, 4096);
        File.WriteAllBytes(archivePath, bytes);

        var budgets = new List<long>();
        var extracted = Path.Combine(_root, "extracted");
        await WorldTransferService.ExtractWorldArchiveAsync(
            archivePath,
            extracted,
            (_, _) => Task.CompletedTask,
            budgets.Add,
            CancellationToken.None);

        // The declared total is checked against free space before any write,
        // and extraction is capped at that same declared size.
        Assert.Equal([4096L], budgets);
        Assert.Equal(4096, new FileInfo(Path.Combine(extracted, "level.dat")).Length);
    }

    [Fact]
    public async Task ExtractWorldArchive_RejectsDeclaredSizesThatOverflow()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "overflow.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "a.dat", "b.dat" })
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                await using var stream = entry.Open();
                stream.WriteByte(7);
            }
        }

        var bytes = File.ReadAllBytes(archivePath);
        RewriteDeclaredUncompressedSizes(bytes, uint.MaxValue);
        File.WriteAllBytes(archivePath, bytes);

        var budgets = new List<long>();
        await WorldTransferService.ExtractWorldArchiveAsync(
            archivePath,
            Path.Combine(_root, "extracted"),
            (_, _) => Task.CompletedTask,
            budgets.Add,
            CancellationToken.None);

        // Two entries of ~4 GiB each: the receiver must weigh 8 GiB of disk
        // against free space before extracting, not the archive's tiny size.
        Assert.Equal([2L * uint.MaxValue], budgets);
    }

    [Theory]
    [InlineData(long.MaxValue, 0)]
    [InlineData(long.MaxValue / 2 + 1, 0)]
    [InlineData(1024, long.MaxValue)]
    [InlineData(-1, 0)]
    public void EnsureSufficientDiskSpace_RejectsSizesThatWouldOverflowTheBudget(long archiveSize, long extractedSize)
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<InvalidOperationException>(() =>
            WorldTransferService.EnsureSufficientDiskSpace(_root, archiveSize, extractedSize));
    }

    private static void RewriteDeclaredUncompressedSizes(byte[] zip, uint declaredSize)
    {
        // Local file headers (PK\x03\x04) store the uncompressed size at +22,
        // central directory headers (PK\x01\x02) at +24.
        for (var i = 0; i + 30 < zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B) continue;
            var offset = (zip[i + 2], zip[i + 3]) switch
            {
                (0x03, 0x04) => i + 22,
                (0x01, 0x02) => i + 24,
                _ => -1
            };
            if (offset < 0 || offset + 4 > zip.Length) continue;
            BitConverter.GetBytes(declaredSize).CopyTo(zip, offset);
        }
    }
}
