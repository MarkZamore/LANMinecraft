using System.IO;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackRuntimeStateTests
{
    [Fact]
    public void RuntimeReceiptUsesTheSameBoundedFormatForPreparationAndDiagnostics()
    {
        var directory = Directory.CreateTempSubdirectory("runtime-receipt-");
        try
        {
            var path = Path.Combine(directory.FullName, PackRuntimeReceipt.FileName);
            PackRuntimeReceipt.WritePreparedState(path, new PackRuntimeReceiptState
            {
                DescriptorHash = new string('a', 64),
                JavaRuntimeId = "fixture-java",
                JavaVersion = "21.0.8",
                ProfileId = "fixture-profile"
            });

            var state = Assert.IsType<PackRuntimeReceiptState>(PackRuntimeReceipt.ReadPreparedState(path));
            using var receipt = PackRuntimeReceipt.TryRead(path);
            Assert.NotNull(receipt);
            var version = Assert.IsType<PackRuntimeReceipt.VersionMetadata>(receipt.ReadVersionMetadata());
            Assert.Equal(PackRuntimeReceipt.CacheGeneration, state.SchemaVersion);
            Assert.Equal(state.SchemaVersion, version.SchemaVersion);
            Assert.Equal(state.DescriptorHash, version.DescriptorHash);
            Assert.Equal(state.ProfileId, version.ProfileId);
            Assert.Equal(state.JavaVersion, version.JavaVersion);
            Assert.Equal(state.JavaRuntimeId, receipt.JavaRuntimeId);
            Assert.Null(PackRuntimeReceipt.TryRead(path, maximumBytes: 1));

            using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                Assert.Null(PackRuntimeReceipt.TryRead(path));
                using var diagnostic = PackRuntimeReceipt.TryRead(path, share: FileShare.ReadWrite | FileShare.Delete);
                Assert.Equal(state.JavaVersion, diagnostic?.ReadVersionMetadata()?.JavaVersion);
            }

            File.WriteAllText(path, """{"javaRuntimeId":"legacy-java"}""");
            using var legacy = PackRuntimeReceipt.TryRead(path);
            Assert.NotNull(legacy);
            Assert.Equal("legacy-java", legacy.JavaRuntimeId);
            Assert.Null(legacy.Generation);
            Assert.False(legacy.TryReadPrivateReferences("Some Build", () => { }, _ => throw new InvalidOperationException()));
        }
        finally { TempTree.Delete(directory.FullName); }
    }
}
