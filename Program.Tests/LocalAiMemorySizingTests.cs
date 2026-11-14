namespace Minecraft.Tests;

public sealed class LocalAiMemorySizingTests
{
    [Fact]
    public void AutomaticRecommendation_ReservesAiOnceAcrossMachineSizesWithoutChangingManualHeap()
    {
        var pack = new PackMemoryProfile(600, 100L * 1024 * 1024, 0, "1.20.1");
        var withAi = pack with { LocalAiReserveMb = 3072 };
        var measured = new MeasuredMemoryProfile(2048, 1024, 1);
        Assert.Equal(4, MemorySizingService.GetNativeReserveGb(pack));
        Assert.Equal(7, MemorySizingService.GetNativeReserveGb(withAi));
        // A Java-only measurement must not absorb or add the separate AI twice.
        Assert.Equal(2, MemorySizingService.GetNativeReserveGb(pack, measured: measured));
        Assert.Equal(5, MemorySizingService.GetNativeReserveGb(withAi, measured: measured));

        foreach (var (installed, expected, expectedAi, unknown, unknownAi) in new[]
                 { (16, 7, 5, 6, 3), (32, 7, 7, 13, 13), (64, 7, 7, 20, 20) })
        {
            var physical = (ulong)installed * 1024 * 1024 * 1024;
            var allowance = MemorySizingService.GetWholeGameAllowanceGb(physical);
            Assert.Equal(expected, MemorySizingService.GetRecommendedMemoryGb(pack, physical));
            Assert.Equal(expectedAi, MemorySizingService.GetRecommendedMemoryGb(withAi, physical));
            Assert.Equal(7, MemorySizingService.GetRecommendedMemoryGb(withAi, physical, measured: measured));
            Assert.Equal(unknown, MemorySizingService.GetRecommendedMemoryGb(PackMemoryProfile.Unknown, physical));
            var unknownWithAi = PackMemoryProfile.Unknown with { LocalAiReserveMb = 3072 };
            Assert.Equal(unknownAi, MemorySizingService.GetRecommendedMemoryGb(unknownWithAi, physical));
            Assert.Equal(MemorySizingService.GetNativeReserveGb(PackMemoryProfile.Unknown, allowance) + 3,
                MemorySizingService.GetNativeReserveGb(unknownWithAi, allowance));
            foreach (var profile in new[] { pack, withAi, PackMemoryProfile.Unknown, unknownWithAi })
            {
                var heap = MemorySizingService.GetRecommendedMemoryGb(profile, physical);
                var outsideHeap = MemorySizingService.GetNativeReserveGb(profile, allowance);
                Assert.True(heap + outsideHeap <= allowance);
                Assert.True(heap + outsideHeap + MemorySizingService.GetOperatingSystemReserveGb(installed) <= installed);
            }
            // The manual ceiling remains a property of the machine, not AI.
            Assert.Equal(9, MemorySizingService.ClampHeapGb(9, physical));
        }

        var root = Path.Combine(Path.GetTempPath(), "local-ai-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.Ensure();
            var service = new SettingsService(paths);
            Assert.True(service.UsePackMemory(withAi));
            var chosen = Math.Min(9, MemorySizingService.GetAllowedHeapGb());
            var settings = new AppSettings { ClientRelativePath = "MemoryFixture", MaxHeapGb = chosen };
            service.Save(settings);
            Assert.Equal(chosen, service.Load().MaxHeapGb);
        }
        finally { TempTree.Delete(root); }
    }
}
