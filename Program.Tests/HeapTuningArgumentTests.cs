using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The collector flags the game is started with.
///
/// A JVM does not warn about an option it dislikes - it refuses to start, and
/// the player gets "Could not create the Java Virtual Machine" with no game
/// behind it. That happened: G1NewSizePercent is an experimental option and
/// needs -XX:+UnlockExperimentalVMOptions written before it, so a release went
/// out that no one could launch. These pin the list so the next flag added has
/// to be looked at.
/// </summary>
public sealed class HeapTuningArgumentTests
{
    /// <summary>
    /// Experimental and diagnostic options need an unlock flag ahead of them.
    /// None is worth that here, so none may appear.
    /// </summary>
    [Fact]
    public void NoFlagNeedsAnUnlockOptionBeforeIt()
    {
        // Every G1 option that HotSpot 21 marks experimental, plus the two
        // unlock switches themselves - if one of those ever becomes necessary,
        // this test is the place to think about ordering rather than discover
        // it from a player.
        string[] locked =
        [
            "G1NewSizePercent", "G1MaxNewSizePercent", "G1MixedGCLiveThresholdPercent",
            "G1MixedGCCountTarget", "G1OldCSetRegionThresholdPercent", "G1EagerReclaimRemSetThreshold",
            "UnlockExperimentalVMOptions", "UnlockDiagnosticVMOptions"
        ];

        foreach (var argument in MinecraftProcessService.HeapTuningArguments)
        {
            foreach (var name in locked)
            {
                Assert.False(
                    argument.Contains(name, StringComparison.Ordinal),
                    $"{argument} needs an unlock option before it; the JVM will not start without one.");
            }
        }
    }

    /// <summary>
    /// The list itself, so adding to it is a decision somebody made on purpose
    /// and checked against the pinned runtime first.
    /// </summary>
    [Fact]
    public void TheListIsWhatWasChecked()
    {
        Assert.Equal(
            [
                "-XX:MaxGCPauseMillis=40",
                "-XX:G1ReservePercent=15",
                "-XX:G1HeapRegionSize=32M",
                "-XX:+ExplicitGCInvokesConcurrent"
            ],
            MinecraftProcessService.HeapTuningArguments);
    }

    /// <summary>Every one of them is a flag, not an empty string or a stray word.</summary>
    [Fact]
    public void EveryEntryIsAnOption()
    {
        Assert.All(MinecraftProcessService.HeapTuningArguments, argument =>
        {
            Assert.StartsWith("-XX:", argument, StringComparison.Ordinal);
            Assert.DoesNotContain(' ', argument);
        });
    }
}
