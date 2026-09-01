using System.Reflection;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackRuntimeStateTests
{
    [Fact]
    public void RuntimeStateSchema_MatchesTheDiagnosticSnapshotReader()
    {
        // The snapshot reader parses the same file, so a bump has to land in both
        // places or support bundles silently stop reporting the runtime.
        var snapshotSchema = typeof(SupportDiagnosticSnapshotBuilder)
            .GetField("RuntimeStateCacheGeneration", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue();

        Assert.Equal(5, PackRuntimeService.RuntimeCacheGeneration);
        Assert.Equal(PackRuntimeService.RuntimeCacheGeneration, snapshotSchema);
    }
}
