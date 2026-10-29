using System.Diagnostics;
using System.Text.RegularExpressions;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Handing a guest his chunks a few at a time, on the versions that will not.
///
/// From 1.20.2 the game does it itself: PlayerChunkSender watches how fast a
/// client acknowledges a batch and sends the next at that rate. Before that the
/// server writes chunks to the connection as fast as it can, and everything
/// else on that connection waits behind them - which is what a guest feels as
/// food that never finishes, a blow that lands late, and being thrown back for
/// moving too quickly.
///
/// Which of the two a runtime is, is read out of its own mappings: the class
/// that does the pacing either exists or it does not.
/// </summary>
public sealed class IdentityAdapterChunkPacingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-chunk-pacing-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    /// <summary>
    /// The obfuscated names Mojang published for 1.20.1 - the last version that
    /// paces nothing - with the two members this feature adds to the ones the
    /// narrowing already needed.
    /// </summary>
    private static readonly string PacesNothingMappings = string.Join('\n', new[]
    {
        "tsrg2 obf mojmap",
        "ahr net/minecraft/server/level/ChunkMap",
        "\tO viewDistance",
        "\ta (Laig;)V move",
        "\ta (Laig;Z)V updatePlayerStatus",
        "\ta (Laig;Lorg/apache/commons/lang3/mutable/MutableObject;Ldei;)V playerLoadedChunk",
        "\ta (I)V setViewDistance",
        // The pump, and beside it another method of the same shape. A
        // descriptor alone would answer both, which is why the name comes with
        // it: closing the map once a tick would end the world.
        "\tb ()V tick",
        "\tc ()V close",
        "ahr$b net/minecraft/server/level/ChunkMap$TrackedEntity",
        "\ta (Laig;)V removePlayer",
        "\tb (Laig;)V updatePlayer",
        "aig net/minecraft/server/level/ServerPlayer",
        "	e latency",
        "\ta (Lzl;)V updateOptions",
        "\tx ()Laif; serverLevel",
        // And the moment the server tells a client to forget a chunk, which is
        // the only thing that can take a held one back.
        "\tc (Lclt;)V untrackChunk",
        "zl net/minecraft/network/protocol/game/ServerboundClientInformationPacket",
        "\tc ()I viewDistance",
        "bfj net/minecraft/world/entity/Entity",
        "\tct ()Ljava/util/UUID; getUUID",
        "\tdk ()Lclt; chunkPosition",
        "aif net/minecraft/server/level/ServerLevel",
        "\tk ()Laid; getChunkSource",
        "aid net/minecraft/server/level/ServerChunkCache",
        "\ta (I)V setViewDistance",
        "\ta chunkMap",
        "clt net/minecraft/world/level/ChunkPos",
        "\te x",
        "\tf z",
        "ddx net/minecraft/world/level/chunk/ChunkAccess",
        "\tf ()Lclt; getPos",
        "dei net/minecraft/world/level/chunk/LevelChunk"
    });

    /// <summary>
    /// The same excerpt with the class that does the pacing in it.
    ///
    /// No real version looks like this - one that has PlayerChunkSender also
    /// keeps a distance per player, and would be turned away a step earlier -
    /// and that is the point: with everything else still named, the only thing
    /// left to decide is the guard this feature has of its own.
    /// </summary>
    private static readonly string PacesItselfMappings =
        PacesNothingMappings + "\narq net/minecraft/server/network/PlayerChunkSender";

    [Fact]
    public void AMinecraftThatPacesNothing_HoldsChunksBack()
    {
        var (properties, paced) = Build(PacesNothingMappings);

        Assert.True(paced);
        Assert.Equal("true", properties["chunkPacingEnabled"]);
        Assert.Equal("tick,b", properties["chunkMapTickMethods"]);
        Assert.Equal("()V", properties["chunkMapTickDescriptors"]);
        Assert.Equal("untrackChunk,c", properties["untrackChunkMethods"]);
        Assert.Equal("latency,e", properties["playerLatencyFields"]);
        // Both spellings of the position, because a runtime carries whichever
        // one its loader remapped it to and the transformer matches the
        // descriptor exactly.
        Assert.Equal(
            "(Lnet/minecraft/world/level/ChunkPos;)V,(Lclt;)V",
            properties["untrackChunkDescriptors"]);
    }

    [Fact]
    public void AMinecraftThatPacesItself_IsLeftAlone()
    {
        var (properties, paced) = Build(PacesItselfMappings);

        Assert.False(paced);
        Assert.Equal("false", properties["chunkPacingEnabled"]);
    }

    /// <summary>
    /// And it is looked for where it actually lives.
    ///
    /// PlayerChunkSender sits in server.network, beside the connection it
    /// writes to, and not in server.level beside the ChunkMap it takes the work
    /// from - which is where a reader who knows what it does would put it. A
    /// class looked for under the wrong package is never found, the guard then
    /// says every version needs help, and the hold is armed on the versions
    /// that already pace themselves properly.
    /// </summary>
    [Fact]
    public void TheClassTheGuardLooksFor_IsSpelledTheWayMojangSpellsIt()
    {
        var constant = typeof(IdentityAdapterMappingService)
            .GetField("PlayerChunkSender", Hidden)!
            .GetRawConstantValue();

        Assert.Equal("net/minecraft/server/network/PlayerChunkSender", constant);
    }

    /// <summary>
    /// The hold is carried by the narrowing: it holds what the narrowing has
    /// already decided is his, at the one seam the narrowing patches. With the
    /// narrowing off there is nowhere for it to stand.
    /// </summary>
    [Fact]
    public void WhereTheNarrowingIsOff_SoIsTheHold()
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var paced = Pace(Read(PacesNothingMappings), properties, perPlayerChunks: false);

        Assert.False(paced);
        Assert.Equal("false", properties["chunkPacingEnabled"]);
    }

    /// <summary>
    /// A runtime with no mappings at all reaches the same builder, and it still
    /// has to name every list. The preflight throws on a list it was not given,
    /// and the throw takes the whole adapter down - skins, which need no
    /// mappings, lost to the absence of some. That has happened.
    /// </summary>
    [Theory]
    [InlineData("chunkMapTickMethods")]
    [InlineData("chunkMapTickDescriptors")]
    [InlineData("untrackChunkMethods")]
    [InlineData("untrackChunkDescriptors")]
    [InlineData("chunkPacingEnabled")]
    [InlineData("playerLatencyFields")]
    public void EveryListIsNamed_EvenWithNothingToReadThemFrom(string property)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        Pace(null, properties, perPlayerChunks: true);

        Assert.False(string.IsNullOrWhiteSpace(properties.GetValueOrDefault(property)), property);
    }

    /// <summary>
    /// A native-chosen radius32 view exceeds the finite hold. Execute the real
    /// transformer and pacing hook to prove overflow is handed to native code,
    /// every held chunk later arrives and the original distance field survives.
    /// </summary>
    [Fact]
    public async Task NativeChosenView32_IsCompleteWhenTheBoundedHoldOverflows()
    {
        var common = Path.GetDirectoryName(RepositoryFile(
            "Program", "IdentityAdapters", "Common", "PortablePerPlayerChunksHooks.java"))!;
        var classes = Path.Combine(_root, "native-distance-probe");
        Directory.CreateDirectory(classes);
        var asm = string.Join(Path.PathSeparator, Path.Combine(common, "lib", "asm-9.8.jar"),
            Path.Combine(common, "lib", "asm-tree-9.8.jar"));
        await RunJava("javac", "--release", "17", "-cp", asm, "-d", classes,
            Path.Combine(common, "PortableIdentityReflection.java"),
            Path.Combine(common, "PortableLanAutoPublishHooks.java"),
            Path.Combine(common, "PortablePerPlayerChunksHooks.java"),
            Path.Combine(common, "PortablePerPlayerChunksTransformer.java"),
            RepositoryFile("Program.Tests", "Fixtures", "ChunkPacingDistanceProbe.java"));
        foreach (var mode in new[] { "normal", "reflection-miss" })
        {
            var output = await RunJava("java", "-Xverify:all", "-cp", classes + Path.PathSeparator + asm,
                "ChunkPacingDistanceProbe", mode);
            Assert.Contains($"NATIVE_DISTANCE_AND_PACING_PASSED mode={mode}", output, StringComparison.Ordinal);
            Assert.Contains("offered=4225", output, StringComparison.Ordinal);
            Assert.Contains("delivered=4225", output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Resource budgets remain finite and unchanged. They no longer imply a
    /// launcher range policy; the executable probe above covers native overflow.
    /// </summary>
    [Fact]
    public void TheFiniteNetworkBudgets_DoNotBecomeADistanceLimit()
    {
        const int nativeChosenRadius = 32;
        var wholeView = (2 * nativeChosenRadius + 1) * (2 * nativeChosenRadius + 1);
        Assert.Equal(4225, wholeView);
        Assert.Equal(2048, NumberInTheAgent("HOLD_LIMIT"));
        Assert.Equal(4, NumberInTheAgent("CHUNKS_PER_TICK"));
        Assert.Equal(2, NumberInTheAgent("CHUNKS_PER_TICK_WHEN_SLOW"));
        Assert.Equal(1, NumberInTheAgent("CHUNKS_PER_TICK_WHEN_VERY_SLOW"));
        Assert.True(NumberInTheAgent("HOLD_LIMIT") < wholeView);
        var source = File.ReadAllText(RepositoryFile(
            "Program", "IdentityAdapters", "Common", "PortableLanAutoPublishHooks.java"));
        Assert.DoesNotContain("SERVE_DISTANCE_LIMIT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("setChunkViewDistanceMethods", source, StringComparison.Ordinal);
        Assert.DoesNotContain("portable-serve-distance", source, StringComparison.Ordinal);
    }

    private static async Task<string> RunJava(string tool, params string[] arguments)
    {
        var jdk = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(jdk) ? tool + ".exe" : Path.Combine(jdk, "bin", tool + ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        return output;
    }

    private static int NumberInTheAgent(string constant)
    {
        var source = File.ReadAllText(RepositoryFile(
            "Program", "IdentityAdapters", "Common", "PortablePerPlayerChunksHooks.java"));
        var match = Regex.Match(source, constant + @"\s*=\s*(\d+)");

        Assert.True(match.Success, $"The agent no longer names {constant}.");
        return int.Parse(match.Groups[1].Value);
    }

    private static string RepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }

    private const System.Reflection.BindingFlags Hidden =
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Public;

    private (Dictionary<string, string> Properties, bool Paced) Build(string mappings)
    {
        var book = Read(mappings);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var type = typeof(IdentityAdapterMappingService);
        var narrowed = (bool)type
            .GetMethod("AddPerPlayerChunksProperties", Hidden)!
            .Invoke(null, [book, properties])!;
        Assert.True(narrowed, "The fixture no longer earns the narrowing this feature rides on.");
        return (properties, Pace(book, properties, narrowed));
    }

    private static bool Pace(object? book, Dictionary<string, string> properties, bool perPlayerChunks) =>
        (bool)typeof(IdentityAdapterMappingService)
            .GetMethod("AddChunkPacingProperties", Hidden)!
            .Invoke(null, [book, properties, perPlayerChunks])!;

    private object? Read(string mappings)
    {
        var mappingDirectory = Path.Combine(_root, "runtime", "libraries", "neoform");
        Directory.CreateDirectory(mappingDirectory);
        var mappingPath = Path.Combine(mappingDirectory, "neoform-test-mappings-merged.txt");
        File.WriteAllText(mappingPath, mappings);

        var names = typeof(IdentityAdapterMappingService).GetNestedType("RuntimeNames", Hidden)!;
        return names.GetMethod("Read", Hidden)!.Invoke(null, [mappingPath, null]);
    }
}
